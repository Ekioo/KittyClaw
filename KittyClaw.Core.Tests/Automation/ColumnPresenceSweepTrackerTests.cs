using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ColumnPresenceSweepTrackerTests
{
    [Fact]
    public void IdleProject_IsSweptAtStartupAndThenOnlyAfterSafetyInterval()
    {
        var tracker = new ColumnPresenceSweepTracker(TimeSpan.FromSeconds(30));
        var now = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(tracker.TryClaimSweep("project", now));
        Assert.False(tracker.TryClaimSweep("project", now.AddSeconds(1)));
        Assert.False(tracker.TryClaimSweep("project", now.AddSeconds(29)));
        Assert.True(tracker.TryClaimSweep("project", now.AddSeconds(30)));
    }

    [Fact]
    public void TicketMutation_ForcesOneImmediateCoalescedSweep()
    {
        var tracker = new ColumnPresenceSweepTracker(TimeSpan.FromMinutes(1));
        var now = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(tracker.TryClaimSweep("project", now));

        tracker.MarkDirty("project");
        tracker.MarkDirty("PROJECT");

        Assert.True(tracker.TryClaimSweep("project", now.AddSeconds(1)));
        Assert.False(tracker.TryClaimSweep("project", now.AddSeconds(2)));
    }
}
