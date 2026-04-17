using Todo.Core.Automation;

namespace Todo.Web.Services;

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

    public AgentRun? Get(string runId) => _registry.Get(runId);
}
