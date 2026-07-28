using KittyClaw.Core.Automation.Triggers;

namespace KittyClaw.Core.Automation;

internal sealed class ProjectRuntime
{
    public ProjectRuntime(string slug) { Slug = slug; }
    public string Slug { get; }
    public string? Workspace { get; set; }
    public AutomationConfig? Config { get; set; }
    public Dictionary<string, ITrigger> Triggers { get; set; } = new();
    public bool ConfigDirty { get; set; }

    /// <summary>Last time an automation of this project was dispatched (in-memory, since process
    /// start). Surfaced by GET /api/engine/health so a silent engine outage is visible (ticket #114).</summary>
    public DateTime? LastFiredAt { get; set; }
    public string? LastFiredAutomationId { get; set; }
}
