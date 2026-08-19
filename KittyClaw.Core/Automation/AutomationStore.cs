using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using KittyClaw.Core.Services;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

public sealed class AutomationStore : IDisposable
{
    /// <summary>Stamp value used when automations.json does not exist yet.</summary>
    public const string AbsentStamp = "absent";

    private readonly ProjectService _projectService;
    private readonly ILogger<AutomationStore>? _logger;
    private readonly ConcurrentDictionary<string, ProjectEntry> _cache = new();

    public event Action<string>? OnConfigChangedOnDisk;

    public AutomationStore(ProjectService projectService, ILogger<AutomationStore>? logger = null)
    {
        _projectService = projectService;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonSerializerOptions JsonOptions => Json;

    /// <summary>Outcome of <see cref="SaveAsync"/>: the config actually written (after any merge),
    /// its file stamp, the IDs of automations that were preserved from disk because they were
    /// missing from the saved payload, and whether the file had diverged from the caller's base.</summary>
    public sealed record SaveResult(AutomationConfig Config, string FileStamp, IReadOnlyList<string> PreservedIds, bool Diverged);

    public sealed record TargetedMutationResult(bool Found, AutomationConfig Config, string FileStamp);

    public async Task<(AutomationConfig Config, string WorkspacePath, string ConfigPath)> LoadAsync(string slug)
    {
        var (config, workspace, configPath, _) = await LoadWithStampAsync(slug);
        return (config, workspace, configPath);
    }

    /// <summary>Same as <see cref="LoadAsync"/> but also returns a content stamp of the file as
    /// read. Pass it back to <see cref="SaveAsync"/> as <c>baseStamp</c> to get optimistic
    /// concurrency: a save whose base no longer matches the disk is merged instead of overwriting.</summary>
    public async Task<(AutomationConfig Config, string WorkspacePath, string ConfigPath, string FileStamp)> LoadWithStampAsync(string slug)
    {
        var project = await _projectService.GetProjectAsync(slug)
            ?? throw new InvalidOperationException($"Projet '{slug}' introuvable.");
        var workspace = _projectService.ResolveWorkspacePath(project);
        var agentsDir = Path.Combine(workspace, ".agents");
        var configPath = Path.Combine(agentsDir, "automations.json");

        var entry = _cache.GetOrAdd(slug, s => new ProjectEntry(s));
        lock (entry.Lock)
        {
            if (entry.ConfigPath != configPath)
            {
                entry.DisposeWatcher();
                entry.ConfigPath = configPath;
                entry.WorkspacePath = workspace;
                if (Directory.Exists(agentsDir))
                    entry.AttachWatcher(agentsDir, configPath, () => OnConfigChangedOnDisk?.Invoke(slug));
            }
        }

        AutomationConfig? config;
        string stamp;
        await entry.IoLock.WaitAsync();
        try
        {
            (config, stamp) = await ReadDiskAsync(configPath, slug);
        }
        finally
        {
            entry.IoLock.Release();
        }
        config ??= new AutomationConfig();

        entry.LastLoaded = config;
        return (config, workspace, configPath, stamp);
    }

    public AutomationConfig? GetCached(string slug) =>
        _cache.TryGetValue(slug, out var e) ? e.LastLoaded : null;

    /// <summary>
    /// Persists <paramref name="config"/> with a re-read-and-merge pass so a concurrent edit of
    /// automations.json (agents editing the file directly, another UI session, …) can never be
    /// silently erased (ticket #115). Under a per-project lock the file is re-read; any automation
    /// present on disk but absent from the payload is preserved — unless <paramref name="baseStamp"/>
    /// matches the current disk content, which proves the caller edited the latest version and the
    /// omission is an intentional delete. Every divergence is logged.
    /// </summary>
    public async Task<SaveResult> SaveAsync(string slug, AutomationConfig config, string? baseStamp = null)
    {
        var (_, _, configPath, _) = await LoadWithStampAsync(slug);
        var entry = _cache[slug];
        await entry.IoLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            var (diskConfig, diskStamp) = await ReadDiskAsync(configPath, slug);
            var diverged = baseStamp is not null && baseStamp != diskStamp;
            var preserved = new List<string>();
            if (diskConfig is not null && (baseStamp is null || diverged))
            {
                var incomingIds = new HashSet<string>(config.Automations.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var a in diskConfig.Automations)
                {
                    if (incomingIds.Contains(a.Id)) continue;
                    config.Automations.Add(a);
                    preserved.Add(a.Id);
                }
            }

            if (diverged)
                _logger?.LogWarning(
                    "automations.json for '{Slug}' changed on disk since the caller loaded it (base stamp mismatch); preserved {Count} automation(s) missing from the saved payload: [{Ids}]",
                    slug, preserved.Count, string.Join(", ", preserved));
            else if (preserved.Count > 0)
                _logger?.LogWarning(
                    "automations.json save for '{Slug}' carried no base stamp; preserved {Count} automation(s) present on disk but missing from the payload: [{Ids}]",
                    slug, preserved.Count, string.Join(", ", preserved));

            var bytes = JsonSerializer.SerializeToUtf8Bytes(config, Json);
            entry.SuppressWatcher = true;
            try
            {
                // Atomic replace: never leave a truncated automations.json behind. Retried because
                // an external reader (agent, editor) can briefly hold the destination open.
                var tmpPath = configPath + ".tmp";
                await File.WriteAllBytesAsync(tmpPath, bytes);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Move(tmpPath, configPath, overwrite: true);
                        break;
                    }
                    catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
                    {
                        await Task.Delay(25);
                    }
                }
            }
            finally
            {
                entry.SuppressWatcher = false;
                entry.LastLoaded = config;
            }
            return new SaveResult(config, ComputeStamp(bytes), preserved, diverged);
        }
        finally
        {
            entry.IoLock.Release();
        }
    }

    public Task<TargetedMutationResult> DisableAsync(string slug, string automationId) =>
        MutateAsync(slug, automationId, automation => automation.Enabled = false, remove: false);

    public Task<TargetedMutationResult> DeleteAsync(string slug, string automationId) =>
        MutateAsync(slug, automationId, _ => { }, remove: true);

    private async Task<TargetedMutationResult> MutateAsync(
        string slug, string automationId, Action<Automation> mutate, bool remove)
    {
        var (_, _, configPath, _) = await LoadWithStampAsync(slug);
        var entry = _cache[slug];
        await entry.IoLock.WaitAsync();
        try
        {
            var (config, stamp) = await ReadDiskAsync(configPath, slug);
            config ??= new AutomationConfig();
            var automation = config.Automations.FirstOrDefault(a =>
                string.Equals(a.Id, automationId, StringComparison.OrdinalIgnoreCase));
            if (automation is null)
                return new TargetedMutationResult(false, config, stamp);

            if (remove)
                config.Automations.Remove(automation);
            else
                mutate(automation);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(config, Json);
            entry.SuppressWatcher = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                var tmpPath = configPath + ".tmp";
                await File.WriteAllBytesAsync(tmpPath, bytes);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Move(tmpPath, configPath, overwrite: true);
                        break;
                    }
                    catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
                    {
                        await Task.Delay(25);
                    }
                }
            }
            finally
            {
                entry.SuppressWatcher = false;
                entry.LastLoaded = config;
            }

            return new TargetedMutationResult(true, config, ComputeStamp(bytes));
        }
        finally
        {
            entry.IoLock.Release();
        }
    }

    private async Task<(AutomationConfig? Config, string Stamp)> ReadDiskAsync(string configPath, string slug)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(configPath);
        }
        catch (IOException)
        {
            return (null, AbsentStamp);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, AbsentStamp);
        }
        try
        {
            var config = JsonSerializer.Deserialize<AutomationConfig>(bytes, Json);
            return (config, ComputeStamp(bytes));
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "automations.json for '{Slug}' is not valid JSON; keeping the previous runtime", slug);
            throw new InvalidDataException($"automations.json for '{slug}' is not valid JSON.", ex);
        }
    }

    private static string ComputeStamp(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose()
    {
        foreach (var e in _cache.Values) { e.DisposeWatcher(); e.IoLock.Dispose(); }
        _cache.Clear();
    }

    private sealed class ProjectEntry
    {
        public ProjectEntry(string slug) { }
        public string ConfigPath { get; set; } = "";
        public string WorkspacePath { get; set; } = "";
        public AutomationConfig? LastLoaded { get; set; }
        public bool SuppressWatcher { get; set; }
        public FileSystemWatcher? Watcher { get; set; }
        public readonly object Lock = new();
        /// <summary>Serializes all file IO (read and write) on automations.json within this process.</summary>
        public readonly SemaphoreSlim IoLock = new(1, 1);

        public void AttachWatcher(string dir, string path, Action onChange)
        {
            var w = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            void fire(object _, FileSystemEventArgs __) { if (!SuppressWatcher) onChange(); }
            w.Changed += fire;
            w.Created += fire;
            w.Renamed += (s, e) => { if (!SuppressWatcher) onChange(); };
            Watcher = w;
        }

        public void DisposeWatcher() { Watcher?.Dispose(); Watcher = null; }
    }
}
