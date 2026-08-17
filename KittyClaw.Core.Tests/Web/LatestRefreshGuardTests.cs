using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public sealed class LatestRefreshGuardTests
{
    [Fact]
    public async Task OlderRefreshCompletingLast_CannotOverwriteNewerResult()
    {
        var guard = new LatestRefreshGuard();
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var displayed = "initial";

        var olderRefresh = guard.ApplyLatestAsync(() => first.Task, value => displayed = value);
        var newerRefresh = guard.ApplyLatestAsync(() => second.Task, value => displayed = value);

        second.SetResult("column C");
        Assert.True(await newerRefresh);
        Assert.Equal("column C", displayed);

        first.SetResult("column B");
        Assert.False(await olderRefresh);
        Assert.Equal("column C", displayed);
    }

    [Fact]
    public async Task FailedLatestRefresh_PreservesLastConsistentStateAndRemainsObservable()
    {
        var guard = new LatestRefreshGuard();
        var displayed = "column B";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.ApplyLatestAsync<string>(
                () => Task.FromException<string>(new InvalidOperationException("refresh failed")),
                value => displayed = value));

        Assert.Equal("refresh failed", error.Message);
        Assert.Equal("column B", displayed);
    }

    [Fact]
    public void BothBoards_ApplyTheLatestRefreshGuardAndLogExternalRefreshFailures()
    {
        var root = FindRepositoryRoot();
        var board = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "Components", "Pages", "Board.razor"));
        var unified = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "Components", "Pages", "UnifiedBoard.razor"));

        Assert.Contains("_ticketRefreshGuard.ApplyLatestAsync", board);
        Assert.Contains("LaneRefreshGuard(project.Slug).ApplyLatestAsync", unified);
        Assert.Contains("Logger.LogError", board);
        Assert.Contains("Logger.LogError", unified);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the KittyClaw repository root.");
    }
}
