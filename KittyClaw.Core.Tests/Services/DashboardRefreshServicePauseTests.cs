using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Services;

// Pause-aware dashboard refresh — ticket #184.
// All assertions are RED on dev: ManualRefreshAsync currently runs for paused/unknown projects
// and the startup catch-up loop does not consult Project.IsPaused.
public sealed class DashboardRefreshServicePauseTests
{
    private const string TileSlug = "pause-tile";

    private static DashboardRefreshService BuildService(
        ProjectService projects,
        out DashboardService dashboard)
    {
        dashboard = new DashboardService(projects);
        var gate = new DashboardTileGate(projects);
        var scriptRunner = new DashboardScriptRunner(NullLogger<DashboardScriptRunner>.Instance);
        var runner = new ClaudeRunner(
            new SessionRegistry(),
            new AgentRunRegistry(),
            new RunConcurrencyGate(1),
            NullLogger<ClaudeRunner>.Instance);
        return new DashboardRefreshService(
            projects, dashboard, runner, gate, scriptRunner,
            NullLogger<DashboardRefreshService>.Instance);
    }

    private static void SeedTile(string workspace)
    {
        var tileDir = Path.Combine(workspace, ".dashboard", TileSlug);
        Directory.CreateDirectory(tileDir);
        File.WriteAllText(
            Path.Combine(tileDir, "tile.yaml"),
            "template: markdown\nrefresh: 60\nprompt: \"\"\nmodel: \"\"\ntitle: \"Pause tile\"\n");
        File.WriteAllText(
            Path.Combine(tileDir, "script.ps1"),
            "Write-Output 'hello from pause test'");
    }

    [Fact]
    public async Task ManualRefresh_PausedProject_DoesNotPersistLastRefreshed()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("paused-manual");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        SeedTile(workspace);

        var paused = await projects.TogglePauseAsync(project.Slug);
        Assert.NotNull(paused);
        Assert.True(paused!.IsPaused);

        var svc = BuildService(projects, out var dashboard);

        await svc.ManualRefreshAsync(project.Slug, workspace, TileSlug, CancellationToken.None);

        var ts = await dashboard.GetLastRefreshedAtAsync(project.Slug, TileSlug);
        Assert.Null(ts);
    }

    [Fact]
    public async Task ManualRefresh_ActiveProject_PersistsLastRefreshed()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("active-manual");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        SeedTile(workspace);

        var svc = BuildService(projects, out var dashboard);

        await svc.ManualRefreshAsync(project.Slug, workspace, TileSlug, CancellationToken.None);

        var ts = await dashboard.GetLastRefreshedAtAsync(project.Slug, TileSlug);
        Assert.NotNull(ts);
    }

    [Fact]
    public async Task ManualRefresh_UnknownProjectSlug_IsNoOp()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        // Create a workspace + tile but never register the project in the registry.
        var workspace = Path.Combine(tmp.Path, "orphan-workspace");
        Directory.CreateDirectory(workspace);
        SeedTile(workspace);

        var svc = BuildService(projects, out var dashboard);

        await svc.ManualRefreshAsync("ghost-project", workspace, TileSlug, CancellationToken.None);

        var ts = await dashboard.GetLastRefreshedAtAsync("ghost-project", TileSlug);
        Assert.Null(ts);
    }

    [Fact]
    public void StartupCatchUp_GuardsAgainstPausedProjects()
    {
        // Structural contract: the startup catch-up flow in DashboardRefreshService MUST consult
        // Project.IsPaused. On dev, the only IsPaused reference is inside TickAsync — so any
        // additional reference proves the startup catch-up / manual-refresh paths now check it too.
        var path = ResolveServiceSourcePath();
        var src = File.ReadAllText(path);

        var count = System.Text.RegularExpressions.Regex.Matches(src, @"\bIsPaused\b").Count;
        Assert.True(
            count >= 2,
            $"Expected >=2 IsPaused references in DashboardRefreshService.cs (TickAsync + startup catch-up / manual refresh). Found {count}.");
    }

    private static string ResolveServiceSourcePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "KittyClaw.Core", "Services", "DashboardRefreshService.cs");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Unable to locate DashboardRefreshService.cs by walking up from the test assembly.");
    }
}
