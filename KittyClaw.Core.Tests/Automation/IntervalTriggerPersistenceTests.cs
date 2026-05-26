using KittyClaw.Core.Automation;
using KittyClaw.Core.Automation.Triggers;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Tests for IntervalTrigger persistence: LastRunAt is loaded from ITriggerStateStore
/// on boot and saved on CommitFiringAsync, enabling missed-run catchup.
/// </summary>
public class IntervalTriggerPersistenceTests
{
    private sealed class FakeStateStore : ITriggerStateStore
    {
        private readonly Dictionary<(string, string), DateTime> _data = new();

        public Task<DateTime?> GetLastRunAtAsync(string slug, string automationId)
        {
            _data.TryGetValue((slug, automationId), out var dt);
            return Task.FromResult(dt == default ? (DateTime?)null : dt);
        }

        public Task SetLastRunAtAsync(string slug, string automationId, DateTime lastRunAt)
        {
            _data[(slug, automationId)] = lastRunAt;
            return Task.CompletedTask;
        }

        public DateTime? Peek(string slug, string automationId) =>
            _data.TryGetValue((slug, automationId), out var dt) ? dt : null;
    }

    private static TriggerContext MakeCtx(DateTime now) => new()
    {
        ProjectSlug = "test",
        WorkspacePath = "/",
        Automation = new KittyClaw.Core.Automation.Automation { Id = "a1", Trigger = new IntervalTriggerSpec { Seconds = 60 } },
        Tickets = null!,
        Members = null!,
        Sessions = null!,
        Runs = null!,
        Now = now,
    };

    [Fact]
    public async Task CommitFiring_persists_lastFired_to_store()
    {
        var store = new FakeStateStore();
        var now = DateTime.UtcNow;
        var trigger = new IntervalTrigger(
            new IntervalTriggerSpec { Seconds = 60 },
            DateTime.MinValue, store, "test", "a1");

        var firings = await trigger.EvaluateAsync(MakeCtx(now), CancellationToken.None);
        Assert.Single(firings);

        await trigger.CommitFiringAsync(MakeCtx(now), firings[0]);

        var persisted = store.Peek("test", "a1");
        Assert.NotNull(persisted);
        Assert.Equal(now, persisted!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task OnRestart_seededFromStore_triggersImmediatelyIfIntervalElapsed()
    {
        var lastRun = DateTime.UtcNow.AddHours(-2);
        var trigger = new IntervalTrigger(
            new IntervalTriggerSpec { Seconds = 60 },
            lastRun, new FakeStateStore(), "test", "a1");

        var firings = await trigger.EvaluateAsync(MakeCtx(DateTime.UtcNow), CancellationToken.None);
        Assert.Single(firings);
    }

    [Fact]
    public async Task OnRestart_seededFromStore_doesNotFireIfIntervalNotElapsed()
    {
        var lastRun = DateTime.UtcNow.AddSeconds(-10);
        var trigger = new IntervalTrigger(
            new IntervalTriggerSpec { Seconds = 60 },
            lastRun, new FakeStateStore(), "test", "a1");

        var firings = await trigger.EvaluateAsync(MakeCtx(DateTime.UtcNow), CancellationToken.None);
        Assert.Empty(firings);
    }

    [Fact]
    public async Task OnRestart_cronSeededFromStore_triggersOnceIfOccurrenceMissed()
    {
        // "every minute" cron; last run was >1 minute ago — one occurrence missed
        var lastRun = DateTime.UtcNow.AddMinutes(-2).AddSeconds(-5);
        var trigger = new IntervalTrigger(
            new IntervalTriggerSpec { Cron = "* * * * *" },
            lastRun, new FakeStateStore(), "test", "cron1");

        var firings = await trigger.EvaluateAsync(MakeCtx(DateTime.UtcNow), CancellationToken.None);
        Assert.Single(firings); // exactly one catchup, not two
    }

    [Fact]
    public async Task OnRestart_cronSeededFromStore_doesNotFireIfNoOccurrenceMissed()
    {
        // Use a fixed reference time to avoid flakiness from test-run timing
        // lastRun at :30, next cron occurrence at :00 next minute, "now" is :40 — still before next minute
        var reference = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var lastRun = reference; // fired at exactly :00
        var now = reference.AddSeconds(40); // now at :40 — next cron occurrence is 12:01:00

        var trigger = new IntervalTrigger(
            new IntervalTriggerSpec { Cron = "* * * * *" },
            lastRun, new FakeStateStore(), "test", "cron1");

        var firings = await trigger.EvaluateAsync(MakeCtx(now), CancellationToken.None);
        Assert.Empty(firings);
    }
}
