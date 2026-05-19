using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Services;

public class UpdateCheckService : BackgroundService
{
    private readonly AppSettingsService _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<UpdateCheckService> _logger;
    private string? _latestVersion;

    public UpdateCheckService(AppSettingsService settings, IHttpClientFactory httpFactory, ILogger<UpdateCheckService> logger)
    {
        _settings = settings;
        _httpFactory = httpFactory;
        _logger = logger;
        CurrentVersion = ResolveCurrentVersion();
    }

    public string CurrentVersion { get; }

    public string? LatestVersion => _latestVersion;

    public bool HasUpdate =>
        !string.IsNullOrEmpty(_latestVersion)
        && VersionCompare.IsNewer(CurrentVersion, _latestVersion)
        && !string.Equals(NormalizeTag(_latestVersion), NormalizeTag(_settings.UpdateDismissedVersion), StringComparison.OrdinalIgnoreCase);

    public event Action? OnChange;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                await CheckIfDueAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckIfDueAsync(CancellationToken ct)
    {
        var last = _settings.UpdateCheckLastRun;
        if (last.HasValue && DateTime.UtcNow - last.Value < TimeSpan.FromHours(24)) return;

        try
        {
            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KittyClaw-UpdateCheck");
            var resp = await client.GetFromJsonAsync<GitHubRelease>(
                "https://api.github.com/repos/Ekioo/KittyClaw/releases/latest", ct);
            if (resp is not null && !string.IsNullOrWhiteSpace(resp.TagName))
            {
                _latestVersion = resp.TagName;
                _settings.UpdateCheckLastRun = DateTime.UtcNow;
                OnChange?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed");
        }
    }

    public void Dismiss()
    {
        if (string.IsNullOrEmpty(_latestVersion)) return;
        _settings.UpdateDismissedVersion = _latestVersion;
        OnChange?.Invoke();
    }

    public void SimulateUpdate(string fakeVersion)
    {
        _latestVersion = fakeVersion;
        _settings.UpdateDismissedVersion = null;
        OnChange?.Invoke();
    }

    public void ResetSimulation()
    {
        _latestVersion = null;
        _settings.UpdateDismissedVersion = null;
        _settings.UpdateCheckLastRun = null;
        OnChange?.Invoke();
    }

    private static string ResolveCurrentVersion()
    {
        var info = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return VersionFormatter.Format(info);
    }

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return tag;
        return tag.StartsWith("v") || tag.StartsWith("V") ? tag[1..] : tag;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
    }
}
