using System.Collections.Concurrent;
using System.Text;
using KittyClaw.Core.Automation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

/// <summary>
/// Background service that periodically refreshes dashboard tiles whose sidecar declares a
/// refresh interval > 0 and a script or prompt. The pipeline is:
/// script (optional) -> prompt (optional) -> write output file.
/// Script is detected by convention: script.* inside the tile folder.
/// </summary>
public sealed class DashboardRefreshService : BackgroundService
{
    private readonly ProjectService _projects;
    private readonly DashboardService _dashboard;
    private readonly ClaudeRunner _runner;
    private readonly DashboardTileGate _gate;
    private readonly DashboardScriptRunner _scriptRunner;
    private readonly ILogger<DashboardRefreshService> _logger;

    // key = "{projectSlug}:{tileSlug}", value = last refresh UTC
    private readonly ConcurrentDictionary<string, DateTime> _lastRefreshed = new();

    public DashboardRefreshService(
        ProjectService projects,
        DashboardService dashboard,
        ClaudeRunner runner,
        DashboardTileGate gate,
        DashboardScriptRunner scriptRunner,
        ILogger<DashboardRefreshService> logger)
    {
        _projects = projects;
        _dashboard = dashboard;
        _runner = runner;
        _gate = gate;
        _scriptRunner = scriptRunner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DashboardRefreshService started — running startup migration");
        await RunStartupMigrationAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "DashboardRefreshService tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunStartupMigrationAsync(CancellationToken ct)
    {
        try
        {
            var projects = await _projects.ListProjectsAsync();
            foreach (var project in projects)
            {
                if (ct.IsCancellationRequested) return;
                var workspace = _projects.ResolveWorkspacePath(project);
                if (!Directory.Exists(workspace)) continue;
                await _dashboard.MigrateAsync(project.Slug, workspace,
                    msg => _logger.LogInformation("{Msg}", msg));
                if (project.IsPaused) continue;
                await LoadAndCatchUpAsync(project.Slug, workspace, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard startup migration encountered an error");
        }
    }

    private async Task LoadAndCatchUpAsync(string projectSlug, string workspace, CancellationToken ct)
    {
        var slugs = _dashboard.GetAvailableSlugs(workspace);
        foreach (var tileSlug in slugs)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var persisted = await _dashboard.GetLastRefreshedAtAsync(projectSlug, tileSlug);
                if (persisted.HasValue)
                    _lastRefreshed[$"{projectSlug}:{tileSlug}"] = persisted.Value;

                var sidecar = await _dashboard.ReadSidecarAsync(workspace, tileSlug);
                if (sidecar is null) continue;
                var hasDailyAt = !string.IsNullOrWhiteSpace(sidecar.RefreshAt);
                if (!hasDailyAt && sidecar.Refresh <= 0) continue;
                var (scriptPath, _) = _dashboard.FindScript(workspace, tileSlug);
                if (scriptPath is null && string.IsNullOrWhiteSpace(sidecar.Prompt)) continue;

                var shouldFire = hasDailyAt
                    ? DashboardRefreshScheduling.ShouldFireDailyAt(DateTime.Now, persisted?.ToLocalTime(), sidecar.RefreshAt)
                    : (DateTime.UtcNow - (persisted ?? DateTime.MinValue)).TotalSeconds >= sidecar.Refresh;
                if (shouldFire)
                {
                    _logger.LogInformation(
                        "Dashboard tile {Project}/{Tile} missed refresh — catching up at startup",
                        projectSlug, tileSlug);
                    await MaybeRefreshTileAsync(projectSlug, workspace, tileSlug, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Startup catchup failed for tile {Project}/{Tile}", projectSlug, tileSlug);
            }
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

            var slugs = _dashboard.GetAvailableSlugs(workspace);
            foreach (var tileSlug in slugs)
            {
                if (ct.IsCancellationRequested) return;
                await MaybeRefreshTileAsync(project.Slug, workspace, tileSlug, ct);
            }
        }
    }

    private async Task MaybeRefreshTileAsync(string projectSlug, string workspace, string tileSlug, CancellationToken ct)
    {
        try
        {
            var sidecar = await _dashboard.ReadSidecarAsync(workspace, tileSlug);
            if (sidecar is null) return;

            var (scriptPath, scriptConfigError) = _dashboard.FindScript(workspace, tileSlug);
            var hasScript = scriptPath is not null;
            var hasPrompt = !string.IsNullOrWhiteSpace(sidecar.Prompt);
            var hasDailyAt = !string.IsNullOrWhiteSpace(sidecar.RefreshAt);
            if ((sidecar.Refresh <= 0 && !hasDailyAt) || (!hasScript && !hasPrompt)) return;

            var key = $"{projectSlug}:{tileSlug}";
            var now = DateTime.UtcNow;
            _lastRefreshed.TryGetValue(key, out var last);
            var hasLast = last != default;
            if (hasDailyAt)
            {
                if (!DashboardRefreshScheduling.ShouldFireDailyAt(
                        DateTime.Now,
                        hasLast ? last.ToLocalTime() : null,
                        sidecar.RefreshAt))
                    return;
            }
            else if (hasLast && (now - last).TotalSeconds < sidecar.Refresh)
            {
                return;
            }

            _logger.LogInformation("Refreshing dashboard tile {Project}/{Tile} (template={Template})",
                projectSlug, tileSlug, sidecar.Template);
            _lastRefreshed[key] = now;
            await _dashboard.SetLastRefreshedAtAsync(projectSlug, tileSlug, now);

            await _gate.RunAsync(projectSlug, tileSlug, manual: false, async gct =>
            {
                if (scriptConfigError is not null)
                {
                    _logger.LogWarning("Dashboard tile {Project}/{Tile}: {Error}",
                        projectSlug, tileSlug, scriptConfigError);
                    return;
                }

                if (hasScript)
                {
                    if (!DashboardScriptRunner.IsSupported(scriptPath!))
                    {
                        _logger.LogWarning("Dashboard tile {Project}/{Tile}: unsupported script extension '{Script}'",
                            projectSlug, tileSlug, scriptPath);
                        return;
                    }
                    var result = await _scriptRunner.RunAsync(scriptPath!, workspace, gct);
                    if (!result.IsSuccess)
                    {
                        _logger.LogWarning("Dashboard script failed for {Project}/{Tile}: {Error}",
                            projectSlug, tileSlug, result.ConfigError ?? result.Stderr);
                        return;
                    }
                    await _dashboard.WriteOutputAsync(workspace, tileSlug, result.Stdout, sidecar.Template);
                    _logger.LogInformation("Dashboard script {Project}/{Tile} wrote {Chars} chars",
                        projectSlug, tileSlug, result.Stdout.Length);
                }

                if (hasPrompt)
                {
                    var newBody = await RunPromptAsync(projectSlug, workspace, tileSlug, sidecar, gct);
                    if (newBody is null) return;
                    await _dashboard.WriteOutputAsync(workspace, tileSlug, newBody, sidecar.Template);
                    _logger.LogInformation("Dashboard tile {Project}/{Tile} updated ({Chars} chars)",
                        projectSlug, tileSlug, newBody.Length);
                }
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh dashboard tile {Project}/{Tile}", projectSlug, tileSlug);
        }
    }

    private async Task<string?> RunPromptAsync(
        string projectSlug, string workspace, string tileSlug,
        TileSidecar sidecar, CancellationToken ct)
    {
        var output = new StringBuilder();

        var ctx = new ClaudeRunContext
        {
            ProjectSlug = projectSlug,
            WorkspacePath = workspace,
            AgentName = "dashboard",
            SkillFile = "dashboard/SKILL.md",
            InlineSkillContent = sidecar.Prompt + TileTemplate.SchemaPrompt(sidecar.Template),
            MaxTurns = 5,
            ConcurrencyGroup = $"dashboard-{projectSlug}-{SanitizeName(tileSlug)}",
            Model = sidecar.Model,
            SessionScope = "dashboard",
            PersistSession = false,
            OnEventHook = ev =>
            {
                if (ev.Kind != "assistant" || string.IsNullOrWhiteSpace(ev.Text)) return;
                var text = ev.Text;
                const string prefix = "[assistant] ";
                if (text.StartsWith(prefix, StringComparison.Ordinal)) text = text[prefix.Length..];
                lock (output) { output.Append(text); }
            },
        };

        var run = await _runner.RunAsync(ctx, ct);
        if (run.Status == AgentRunStatus.Failed)
        {
            _logger.LogWarning("Dashboard prompt run failed for {Project}/{Tile} (exit {Exit})",
                projectSlug, tileSlug, run.ExitCode);
            return null;
        }

        var text = output.ToString().Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public async Task ManualRefreshAsync(string projectSlug, string workspace, string tileSlug, CancellationToken ct)
    {
        var project = await _projects.GetProjectAsync(projectSlug);
        if (project is null || project.IsPaused) return;
        var sidecar = await _dashboard.ReadSidecarAsync(workspace, tileSlug);
        if (sidecar is null) return;
        var (scriptPath, scriptConfigError) = _dashboard.FindScript(workspace, tileSlug);
        var hasScript = scriptPath is not null;
        var hasPrompt = !string.IsNullOrWhiteSpace(sidecar.Prompt);
        if (!hasScript && !hasPrompt) return;

        var key = $"{projectSlug}:{tileSlug}";
        var now = DateTime.UtcNow;
        _lastRefreshed[key] = now;
        await _dashboard.SetLastRefreshedAtAsync(projectSlug, tileSlug, now);

        await _gate.RunAsync(projectSlug, tileSlug, manual: true, async gct =>
        {
            if (scriptConfigError is not null)
            {
                _logger.LogWarning("Dashboard tile {Project}/{Tile}: {Error}", projectSlug, tileSlug, scriptConfigError);
                return;
            }
            if (hasScript)
            {
                var result = await _scriptRunner.RunAsync(scriptPath!, workspace, gct);
                if (!result.IsSuccess)
                {
                    _logger.LogWarning("Dashboard manual script failed {Project}/{Tile}: {Error}",
                        projectSlug, tileSlug, result.ConfigError ?? result.Stderr);
                    return;
                }
                await _dashboard.WriteOutputAsync(workspace, tileSlug, result.Stdout, sidecar.Template);
            }
            if (hasPrompt)
            {
                var body = await RunPromptAsync(projectSlug, workspace, tileSlug, sidecar, gct);
                if (body is not null)
                    await _dashboard.WriteOutputAsync(workspace, tileSlug, body, sidecar.Template);
            }
        }, ct);
    }

    private static string SanitizeName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}