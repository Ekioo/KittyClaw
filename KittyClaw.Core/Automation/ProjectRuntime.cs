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
}
