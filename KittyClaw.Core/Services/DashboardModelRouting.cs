using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

/// <summary>
/// Keeps dashboard prompt routing aligned with normal automation dispatches, including the
/// project-level quota fallback.
/// </summary>
public static class DashboardModelRouting
{
    public static (AgentDispatchTarget Primary, AgentDispatchTarget? Fallback) Resolve(
        Project? project,
        string? primaryModel)
    {
        var primaryRouting = ModelRouting.Resolve(primaryModel, project?.LocalModelBaseUrl);
        var primary = primaryRouting.ToTarget(primaryModel);

        var fallbackModel = string.IsNullOrWhiteSpace(project?.FallbackModel)
            ? null
            : project.FallbackModel;
        if (fallbackModel is null)
            return (primary, null);

        var fallbackRouting = ModelRouting.Resolve(fallbackModel, project?.LocalModelBaseUrl);
        var fallback = fallbackRouting.Error is null
            ? fallbackRouting.ToTarget(fallbackModel)
            : null;
        return (primary, fallback);
    }
}
