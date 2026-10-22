using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class AgentRunSteerImageTests
{
    [Fact]
    public void TemporarySteerImages_AreRegisteredOnceAndDrainedForCleanup()
    {
        var run = NewRun();

        Assert.True(run.TryAddTemporarySteerImagePaths(["first.png", "second.png"]));
        Assert.Equal(["first.png", "second.png"], run.DrainTemporarySteerImagePaths());
        Assert.False(run.TryAddTemporarySteerImagePaths(["late.png"]));
        Assert.Empty(run.DrainTemporarySteerImagePaths());
    }

    [Fact]
    public void TemporarySteerImages_AreRejectedAfterRunCompletion()
    {
        var run = NewRun();
        run.Status = AgentRunStatus.Completed;

        Assert.False(run.TryAddTemporarySteerImagePaths(["late.png"]));
        Assert.Empty(run.DrainTemporarySteerImagePaths());
    }

    private static AgentRun NewRun() => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        ProjectSlug = "images",
        TicketId = null,
        AgentName = "owner-chat",
        SkillFile = "chat",
        ConcurrencyGroup = "chat:images:owner-chat",
        StartedAt = DateTime.UtcNow,
    };
}
