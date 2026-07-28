namespace KittyClaw.Core.Automation;

/// <summary>
/// Per-project engine health snapshot exposed by GET /api/engine/health (ticket #114) so a
/// silently dead scheduler is visible: <paramref name="ScheduledCount"/> says how many cron/interval
/// tasks are actually registered, <paramref name="OverdueCount"/> how many of them sit past their
/// scheduled time, and <paramref name="LastFiredAt"/> when an automation last dispatched
/// (in-memory, since process start).
/// </summary>
public sealed record ProjectEngineHealth(
    string Slug,
    int AutomationCount,
    int EnabledCount,
    int ScheduledCount,
    DateTime? NextRunAt,
    int OverdueCount,
    DateTime? LastFiredAt,
    string? LastFiredAutomationId);
