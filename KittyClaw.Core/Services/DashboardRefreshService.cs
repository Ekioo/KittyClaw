using System.Collections.Concurrent;
using System.Text;
using KittyClaw.Core.Automation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

/// <summary>
/// Background service that periodically refreshes dashboard tiles whose sidecar declares a
/// <c>refresh</c> interval &gt; 0 and a non-empty <c>prompt</c>. The configured LLM prompt is
/// executed via the claude CLI and its output is written back to the result file.
/// The sidecar is left untouched.
/// </summary>
public sealed class DashboardRefreshService : BackgroundService
{
    private readonly ProjectService _projects;
    private readonly DashboardService _dashboard;
    private readonly ClaudeRunner _runner;
    private readonly DashboardTileGate _gate;
    private readonly ILogger<DashboardRefreshService> _logger;

    // key = "{slug}:{fileName}", value = last refresh UTC
    private readonly ConcurrentDictionary<string, DateTime> _lastRefreshed = new();

    public DashboardRefreshService(
        ProjectService projects,
        DashboardService dashboard,
        ClaudeRunner runner,
        DashboardTileGate gate,
        ILogger<DashboardRefreshService> logger)
    {
        _projects = projects;
        _dashboard = dashboard;
        _runner = runner;
        _gate = gate;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DashboardRefreshService started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "DashboardRefreshService tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var projects = await _projects.ListProjectsAsync();
        foreach (var project in projects)
        {
            if (ct.IsCancellationRequested) return;
            if (project.IsPaused) continue;

            var workspace = _projects.ResolveWorkspacePath(project);
            if (!Directory.Exists(workspace)) continue;

            var files = _dashboard.GetAvailableFiles(workspace);
            foreach (var fileName in files)
            {
                if (ct.IsCancellationRequested) return;
                await MaybeRefreshFileAsync(project.Slug, workspace, fileName, ct);
            }
        }
    }

    private async Task MaybeRefreshFileAsync(string slug, string workspace, string fileName, CancellationToken ct)
    {
        try
        {
            var sidecar = await _dashboard.ReadSidecarAsync(workspace, fileName);
            if (sidecar is null) return;
            if (sidecar.Refresh <= 0 || string.IsNullOrWhiteSpace(sidecar.Prompt)) return;

            var key = $"{slug}:{fileName}";
            var now = DateTime.UtcNow;
            if (_lastRefreshed.TryGetValue(key, out var last)
                && (now - last).TotalSeconds < sidecar.Refresh)
                return;

            _logger.LogInformation("Refreshing dashboard tile {Slug}/{File} (template={Template})",
                slug, fileName, sidecar.Template);
            _lastRefreshed[key] = now;

            await _gate.RunAsync(slug, fileName, manual: false, async gct =>
            {
                var newBody = await RunPromptAsync(slug, workspace, fileName, sidecar, gct);
                if (newBody is null) return;
                await _dashboard.WriteFileAsync(workspace, fileName, newBody);
                _logger.LogInformation("Dashboard tile {Slug}/{File} updated ({Chars} chars)", slug, fileName, newBody.Length);
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh dashboard tile {Slug}/{File}", slug, fileName);
        }
    }

    private async Task<string?> RunPromptAsync(
        string slug, string workspace, string fileName,
        TileSidecar sidecar, CancellationToken ct)
    {
        var output = new StringBuilder();

        var ctx = new ClaudeRunContext
        {
            ProjectSlug = slug,
            WorkspacePath = workspace,
            AgentName = "dashboard",
            SkillFile = "dashboard/SKILL.md",
            InlineSkillContent = sidecar.Prompt + TileTemplate.SchemaPrompt(sidecar.Template),
            MaxTurns = 5,
            ConcurrencyGroup = $"dashboard-{slug}-{SanitizeFileName(fileName)}",
            Model = sidecar.Model,
            SessionScope = "dashboard",
            PersistSession = false,
            OnEventHook = ev =>
            {
                if (ev.Kind != "assistant" || string.IsNullOrWhiteSpace(ev.Text)) return;
                // FlattenJson prefixes assistant text with "[assistant] " — strip it before
                // we write the tile body, otherwise it shows up literally in the rendered output.
                var text = ev.Text;
                const string prefix = "[assistant] ";
                if (text.StartsWith(prefix, StringComparison.Ordinal)) text = text[prefix.Length..];
                lock (output) { output.Append(text); }
            },
        };

        var run = await _runner.RunAsync(ctx, ct);
        if (run.Status == AgentRunStatus.Failed)
        {
            _logger.LogWarning("Dashboard prompt run failed for {Slug}/{File} (exit {Exit})", slug, fileName, run.ExitCode);
            return null;
        }

        var text = output.ToString().Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
