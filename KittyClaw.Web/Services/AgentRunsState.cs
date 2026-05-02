using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Services;

/// <summary>
/// Bridges the in-process AgentRunRegistry to Blazor components so the board
/// can display a spinner on tickets with an active run and open the drawer.
/// </summary>
public sealed class AgentRunsState
{
    private readonly AgentRunRegistry _registry;
    public event Action? OnChange;

    public AgentRunsState(AgentRunRegistry registry)
    {
        _registry = registry;
        _registry.OnRunStarted += _ => OnChange?.Invoke();
        _registry.OnRunEnded += _ => OnChange?.Invoke();
    }

    public IEnumerable<AgentRun> ActiveForProject(string slug) => _registry.ActiveForProject(slug);

    public AgentRun? ActiveForTicket(string slug, int ticketId) =>
        _registry.ActiveForTicket(slug, ticketId).FirstOrDefault();

    /// <summary>
    /// Most recent run for a ticket (any status — used to offer a "log" button
    /// after a run has completed so the user can inspect what happened).
    /// </summary>
    public AgentRun? LastForTicket(string slug, int ticketId) =>
        _registry.AllForTicket(slug, ticketId)
            .OrderByDescending(r => r.EndedAt ?? r.StartedAt)
            .FirstOrDefault();

    public AgentRun? Get(string runId) => _registry.Get(runId);

    public IEnumerable<AgentRun> AllForProject(string slug) => _registry.AllForProject(slug);
}
