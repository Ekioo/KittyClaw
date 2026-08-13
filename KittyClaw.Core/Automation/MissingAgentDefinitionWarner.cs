using System.Collections.Concurrent;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Surfaces the silent-skip failure mode of assignee dispatch (ticket kittyclaw-front#211):
/// a board member can exist without an agent definition on disk, in which case a Todo ticket
/// assigned to it never dispatches and freezes with no trace. For each column-poll firing whose
/// automation runs the "{assignee}" agent, this warner probes .agents/{assignee}/SKILL.md and,
/// when it is missing, writes a throttled SKIPPED line to .agents/channel/debug.log plus a
/// one-time activity on the ticket so the gap is visible from the board UI.
/// </summary>
internal sealed class MissingAgentDefinitionWarner(
    TicketService tickets, LocalizationService loc, ILogger logger)
{
    internal static readonly TimeSpan WarnInterval = TimeSpan.FromHours(1);

    // Keyed by "{project}|{ticketId}|{agent}"; value = last time a SKIPPED line was written.
    // In-memory on purpose: a process restart re-warns once, which is acceptable (and refreshes
    // the signal). The key is dropped as soon as the definition appears so a later deletion of
    // the agent folder produces a fresh activity, not just a throttled log line.
    private readonly ConcurrentDictionary<string, DateTime> _lastWarned = new(StringComparer.OrdinalIgnoreCase);

    public async Task ObserveColumnFiringAsync(
        ProjectRuntime rt, Automation automation, TriggerFiring firing, DateTime nowUtc)
    {
        if (firing.TicketId is not int ticketId || rt.Workspace is null) return;
        if (!DispatchesAssigneeAgent(automation)) return;

        string? assignee;
        try { assignee = (await tickets.GetTicketAsync(rt.Slug, ticketId))?.AssignedTo; }
        catch { return; }
        if (string.IsNullOrWhiteSpace(assignee)) return;

        var key = $"{rt.Slug}|{ticketId}|{assignee}";
        if (HasAgentDefinition(rt.Workspace, assignee))
        {
            _lastWarned.TryRemove(key, out _);
            return;
        }

        var first = !_lastWarned.TryGetValue(key, out var last);
        if (!first && nowUtc - last < WarnInterval) return;
        _lastWarned[key] = nowUtc;

        var line = $"SKIPPED #{ticketId}: no agent definition for '{assignee}' " +
                   $"(.agents/{assignee}/SKILL.md not found; automation '{automation.Id}')";
        AppendDebugLog(rt.Workspace, line);
        logger.LogWarning("{Project}: {Line}", rt.Slug, line);

        if (first)
        {
            try
            {
                await tickets.AddActivityAsync(rt.Slug, ticketId,
                    loc.Get("ActAgentDefinitionMissing", assignee), "automation");
            }
            catch { /* activity is best-effort — the debug log line is the primary signal */ }
        }
    }

    /// <summary>True when the automation dispatches the firing ticket's assignee as an agent.</summary>
    internal static bool DispatchesAssigneeAgent(Automation automation) =>
        automation.Actions.OfType<RunAgentActionSpec>().Any(a => a.Agent.Contains("{assignee}"));

    /// <summary>True when .agents/{agent}/SKILL.md exists in the workspace.</summary>
    internal static bool HasAgentDefinition(string workspace, string agent)
    {
        if (agent.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || agent.Contains("..")) return false;
        try { return File.Exists(Path.Combine(workspace, ".agents", agent, "SKILL.md")); }
        catch { return false; }
    }

    private static void AppendDebugLog(string workspace, string line)
    {
        try
        {
            var dir = Path.Combine(workspace, ".agents", "channel");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "debug.log"), $"[{DateTime.UtcNow:o}] {line}\n");
        }
        catch { /* best-effort debug log — disk errors must not break the tick */ }
    }
}
