using System.Collections.Concurrent;
using System.Text;
using KittyClaw.Core.Automation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

/// <summary>
/// Background service that periodically refreshes dashboard files whose front-matter
/// declares a <c>refresh</c> interval and a <c>prompt</c>. On each scheduled refresh the
/// configured LLM prompt is executed via the claude CLI and the result is written back to
/// the file, preserving the header.
/// </summary>
public sealed class DashboardRefreshService : BackgroundService
{
    private readonly ProjectService _projects;
    private readonly DashboardService _dashboard;
    private readonly ClaudeRunner _runner;
    private readonly ILogger<DashboardRefreshService> _logger;

    // key = "{slug}:{fileName}", value = last refresh UTC
    private readonly ConcurrentDictionary<string, DateTime> _lastRefreshed = new();

    public DashboardRefreshService(
        ProjectService projects,
        DashboardService dashboard,
        ClaudeRunner runner,
        ILogger<DashboardRefreshService> logger)
    {
        _projects = projects;
        _dashboard = dashboard;
        _runner = runner;
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
            var content = await _dashboard.ReadFileContentAsync(workspace, fileName);
            if (content is null) return;

            var header = DashboardService.ParseHeader(content);
            if (header is null) return; // static file — skip

            var key = $"{slug}:{fileName}";
            var now = DateTime.UtcNow;
            if (_lastRefreshed.TryGetValue(key, out var last)
                && (now - last).TotalSeconds < header.RefreshSeconds)
                return;

            _logger.LogInformation("Refreshing dashboard file {Slug}/{File}", slug, fileName);
            _lastRefreshed[key] = now;

            var newBody = await RunPromptAsync(slug, workspace, fileName, header, ct);
            if (newBody is null) return;

            var updated = DashboardService.BuildContent(header, newBody);
            await _dashboard.WriteFileAsync(workspace, fileName, updated);
            _logger.LogInformation("Dashboard file {Slug}/{File} updated ({Chars} chars)", slug, fileName, newBody.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh dashboard file {Slug}/{File}", slug, fileName);
        }
    }

    private async Task<string?> RunPromptAsync(
        string slug, string workspace, string fileName,
        DashboardFileHeader header, CancellationToken ct)
    {
        var output = new StringBuilder();

        var ctx = new ClaudeRunContext
        {
            ProjectSlug = slug,
            WorkspacePath = workspace,
            AgentName = "dashboard",
            SkillFile = "dashboard/SKILL.md", // intentionally non-existent; InlineSkillContent used instead
            InlineSkillContent = header.Prompt,
            MaxTurns = 5,
            ConcurrencyGroup = $"dashboard-{slug}-{SanitizeFileName(fileName)}",
            Model = header.Model,
            SessionScope = "dashboard",
            OnEventHook = ev =>
            {
                if (ev.Kind == "assistant" && !string.IsNullOrWhiteSpace(ev.Text))
                    lock (output) { output.Append(ev.Text); }
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
