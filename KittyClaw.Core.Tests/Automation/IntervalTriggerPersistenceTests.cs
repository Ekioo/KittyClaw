using KittyClaw.Core.Automation;
using KittyClaw.Core.Automation.Triggers;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Tests for IntervalTrigger persistence: NextRunAt is computed once and persisted via
/// ITriggerStateStore, so a restart before or after the scheduled moment doesn't lose it —
/// it fires immediately on catch-up rather than silently rescheduling from "now".
/// </summary>
public class IntervalTriggerPersistenceTests
{
    private sealed class FakeStateStore : ITriggerStateStore
    {
        private readonly Dictionary<(string, string), DateTime> _data = new();

        public Task<DateTime?> GetNextRunAtAsync(string slug, string automationId)
        {
            _data.TryGetValue((slug, automationId), out var dt);
            return Task.FromResult(dt == default ? (DateTime?)null : dt);
        }

        public Task SetNextRunAtAsync(string slug, string automationId, DateTime nextRunAt)
        {
            _data[(slug, automationId)] = nextRunAt;
            return Task.CompletedTask;
        }

        public Task<DateTime?> GetLegacyLastRunAtAsync(string slug, string automationId) =>
            Task.FromResult<DateTime?>(null);

        public DateTime? Peek(string slug, string automationId) =>
            _data.TryGetValue((slug, automationId), out var dt) ? dt : null;
    }

    private static TriggerContext MakeCtx(DateTime now, string cron = "* * * * *") => new()
    {
        ProjectSlug = "test",
        WorkspacePath = "/",
        Automation = new KittyClaw.Core.Automation.Automation { Id = "a1", Trigger = new IntervalTriggerSpec { Cron = cron } },
        Tickets = null!,
        Members = null!,
        Sessions = null!,
        Runs = null!,
        Now = now,
    };

    [Fact]
    public async Task CommitFiring_persists_nextRunAt_to_store()
    {
        var store = new FakeStateStore();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc); // due (every-minute cron)
        var trigger = new IntervalTrigger(
            new IntervalTriggerSpec { Cron = "* * * * *" },
            now, store, "test", "a1");

        var firings = await trigger.EvaluateAsync(MakeCtx(now), CancellationToken.None);
        Assert.Single(firings);

        await trigger.CommitFiringAsync(MakeCtx(now), firings[0]);

        var persisted = store.Peek("test", "a1");
        Assert.NotNull(persisted);
        Assert.True(persisted > now); // re-anchored to the *next* occurrence after firing
    }

    [Fact]
    public async Task DoesNotFire_beforeNextRunAt()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var nextRunAt = now.AddHours(1);
        var trigger = new IntervalTrigger(
            new IntervalTriggerSpec { Cron = "0 * * * *" },
            nextRunAt, new FakeStateStore(), "test", "a1");

        var firings = await trigger.EvaluateAsync(MakeCtx(now), CancellationToken.None);
        Assert.Empty(firings);
    }

    [Fact]
    public async Task OnRestart_seededNextRunAt_firesImmediately_ifOverdue()
    {
        // The trigger was scheduled to fire 2 hours ago but the engine wasn't running — catch up now.
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var overdueNextRunAt = now.AddHours(-2);
        var trigger = new IntervalTrigger(
            new IntervalTriggerSpec { Cron = "0 * * * *" },
            overdueNextRunAt, new FakeStateStore(), "test", "a1");

        var firings = await trigger.EvaluateAsync(MakeCtx(now), CancellationToken.None);
        Assert.Single(firings);
    }

    [Fact]
    public async Task OnRestart_neverFiredBefore_missedItsFirstScheduledMoment_firesImmediately()
    {
        // Regression: the trigger was registered (NextRunAt persisted) for a Monday 9am cron, but the
        // engine process wasn't running at that exact moment — it comes back up an hour later. Because
        // NextRunAt was persisted at registration time (not recomputed from "now" on restart), the
        // missed occurrence is still detected and fires immediately instead of silently jumping to
        // next Monday.
        var monday9am = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc); // Monday
        var restartTime = monday9am.AddHours(1); // engine only comes back up an hour later

        var trigger = new IntervalTrigger(
            new IntervalTriggerSpec { Cron = "0 9 * * 1" },
            monday9am, new FakeStateStore(), "test", "a1");

        var firings = await trigger.EvaluateAsync(MakeCtx(restartTime), CancellationToken.None);
        Assert.Single(firings);
    }

    [Fact]
    public async Task RepeatedTicks_onlyFireOnce_perScheduledOccurrence()
    {
        var start = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc); // Monday 9am — due immediately
        var trigger = new IntervalTrigger(
            new IntervalTriggerSpec { Cron = "0 9 * * 1" },
            start, new FakeStateStore(), "test", "a1");

        int fireCount = 0;
        for (var now = start; now < start.AddDays(8); now = now.AddHours(1))
        {
            var firings = await trigger.EvaluateAsync(MakeCtx(now), CancellationToken.None);
            if (firings.Count > 0) fireCount++;
        }

        Assert.Equal(2, fireCount); // this Monday 9am + next Monday 9am, nothing in between
    }

    [Fact]
    public void ComputeInitialNextRunAt_returnsFutureOccurrence_forFreshRegistration()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc); // Thursday
        var next = IntervalTrigger.ComputeInitialNextRunAt(new IntervalTriggerSpec { Cron = "0 9 * * 1" }, now);

        Assert.True(next > now);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
    }

    [Fact]
    public void LegacySecondsField_migratesTo_equivalentCron()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var next = IntervalTrigger.ComputeInitialNextRunAt(new IntervalTriggerSpec { Seconds = 86400 }, now);

        Assert.Equal(now.AddDays(1), next); // 86400s legacy spec maps to a "daily at midnight" cron
    }

    [Fact]
    public void ComputeInitialNextRunAt_fromLegacyBaseline_catchesUpOverdueOccurrence()
    {
        // Regression: ProjectRuntimeManager seeds a pre-existing install's migrated row (NextRunAt =
        // NULL) using ComputeInitialNextRunAt(spec, legacyLastRunAt) instead of DateTime.UtcNow, so an
        // automation that had a genuinely missed occurrence at the moment of upgrade still catches up
        // rather than silently jumping to the next future occurrence.
        var legacyLastRunAt = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc); // last real fire, Monday
        var now = legacyLastRunAt.AddDays(10); // engine was down; a week has passed since

        var seeded = IntervalTrigger.ComputeInitialNextRunAt(new IntervalTriggerSpec { Cron = "0 9 * * 1" }, legacyLastRunAt);

        Assert.True(seeded < now); // overdue relative to "now" — the next tick's EvaluateAsync will fire immediately
    }
}
