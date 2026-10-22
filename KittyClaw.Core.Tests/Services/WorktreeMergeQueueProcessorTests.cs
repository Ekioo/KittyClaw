using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class WorktreeMergeQueueProcessorTests
{
    [Fact]
    public void ShouldProcess_SkipsPausedProjects()
    {
        var paused = new Project { Name = "Paused", Slug = "paused", IsPaused = true };
        var active = new Project { Name = "Active", Slug = "active", IsPaused = false };

        Assert.False(WorktreeMergeQueueProcessor.ShouldProcess(paused));
        Assert.True(WorktreeMergeQueueProcessor.ShouldProcess(active));
    }
}
