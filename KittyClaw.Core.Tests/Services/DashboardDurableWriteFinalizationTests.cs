using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Services;

public sealed class DashboardDurableWriteFinalizationTests
{
    [Fact]
    public async Task ManualRefresh_FailedScript_FinalizesMaintenanceRoute()
    {
        using var temp = new TempDir();
        var repository = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "integration");
        const string tileSlug = "failed-script";
        var tileDirectory = Path.Combine(repository, ".dashboard", tileSlug);
        Directory.CreateDirectory(tileDirectory);
        await File.WriteAllTextAsync(Path.Combine(tileDirectory, "tile.yaml"),
            "template: markdown\nrefresh: 60\ntitle: Failed script\n");
        await File.WriteAllTextAsync(Path.Combine(tileDirectory, "script.ps1"),
            "Write-Error 'expected failure'\nexit 1\n");
        Git(repository, "add", ".dashboard");
        Git(repository, "commit", "-m", "add failing dashboard tile");

        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("dashboard-finalization");
        await projects.UpdateProjectAsync(project.Slug, repository);
        project = (await projects.UpdateProjectAsync(project.Slug, null,
            worktreesEnabled: true, integrationBranch: "integration"))!;
        var tickets = new TicketService(projects, new MemberService(projects));
        var worktrees = new TicketWorktreeService(projects, tickets);
        var queue = new WorktreeMergeQueueService(projects, worktrees);
        var durableWrites = new DurableWriteRouter(projects, worktrees, queue);
        var dashboard = new DashboardService(projects);
        var service = new DashboardRefreshService(
            projects,
            dashboard,
            new AgentRunner(
                new SessionRegistry(),
                new AgentRunRegistry(),
                new RunConcurrencyGate(1),
                NullLogger<AgentRunner>.Instance),
            new DashboardTileGate(projects),
            new DashboardScriptRunner(NullLogger<DashboardScriptRunner>.Instance),
            NullLogger<DashboardRefreshService>.Instance,
            durableWrites);

        await service.ManualRefreshAsync(
            project.Slug, repository, tileSlug, CancellationToken.None);

        var request = Assert.Single(await queue.ListAsync(project.Slug));
        Assert.Equal(WorktreeMergeStatus.Completed, request.Status);

        var next = await durableWrites.ResolveAsync(
            project.Slug, null, [Path.Combine(".dashboard", tileSlug)])
            .WaitAsync(TimeSpan.FromSeconds(5));
        await durableWrites.CommitAndQueueAsync(
            project.Slug, next, "chore(dashboard): verify released route");
    }

    private static void Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
