using KittyClaw.Core.Automation;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public class RunWorkingFilesTests
{
    private static AgentRun NewRun(string? folder) => new()
    {
        RunId = "run-one", ProjectSlug = "test", AgentName = "agent", SkillFile = "inline",
        ConcurrencyGroup = "agent", TicketId = null, StartedAt = DateTime.UtcNow,
        Status = AgentRunStatus.Completed, WorkingFilesPath = folder
    };

    [Fact]
    public async Task RecordedFolder_SurvivesBothHistoryLoaders()
    {
        using var tmp = new TempDir();
        var folder = Path.Combine(tmp.Path, "custom", "run-one");
        var store = new RunLogStore(tmp.Path);
        store.Save(NewRun(folder));
        Assert.Equal(folder, Assert.Single(store.LoadAll()).WorkingFilesPath);
        var loaded = new List<AgentRun>();
        await foreach (var run in store.LoadAllAsync()) loaded.Add(run);
        Assert.Equal(folder, Assert.Single(loaded).WorkingFilesPath);
    }

    [Fact]
    public void Open_UsesRecordedFolderAsSingleArgumentWithoutShell()
    {
        using var tmp = new TempDir();
        var folder = Path.Combine(tmp.Path, "proofs with spaces");
        Directory.CreateDirectory(folder);
        var info = RunWorkingFilesService.BuildOpenStartInfo(NewRun(folder));
        Assert.Equal(folder, Assert.Single(info.ArgumentList));
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
    }

    [Fact]
    public void Open_RejectsMissingFoldersAndWrongProjects()
    {
        using var tmp = new TempDir();
        Assert.Throws<InvalidOperationException>(() => RunWorkingFilesService.BuildOpenStartInfo(NewRun(null)));
        Assert.Throws<InvalidOperationException>(() => RunWorkingFilesService.BuildOpenStartInfo(NewRun(Path.Combine(tmp.Path, "missing"))));
        var registry = new AgentRunRegistry();
        registry.Register(NewRun(tmp.Path));
        Assert.Throws<InvalidOperationException>(() => new RunWorkingFilesService(registry).Open("other-project", "run-one"));
    }
}
