using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ProjectEngineHealthTests
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    public void EffectiveOverdueCount_SuppressesFalseAlertsForPausedProjects(bool isPaused, int expected)
    {
        var health = new ProjectEngineHealth(
            "project", AutomationCount: 4, EnabledCount: 4, ScheduledCount: 3,
            NextRunAt: DateTime.UtcNow.AddHours(-1), OverdueCount: 3,
            LastFiredAt: null, LastFiredAutomationId: null);

        Assert.Equal(expected, health.EffectiveOverdueCount(isPaused));
    }
}
