using KittyClaw.Core.Services;

namespace KittyClaw.Web.Components.Pages;

internal static class CostPipelineFilter
{
    public static IReadOnlyList<CostPipelineOption> Filter(
        IReadOnlyList<CostPipelineOption>? pipelines,
        IReadOnlySet<string> selectedProjects)
    {
        if (pipelines is null || pipelines.Count == 0)
            return [];
        if (selectedProjects.Count == 0)
            return pipelines;

        return pipelines
            .Where(pipeline => selectedProjects.Any(slug =>
                pipeline.Key.StartsWith($"{slug}:", StringComparison.Ordinal)))
            .ToArray();
    }

    public static string? KeepValidSelection(
        string? selectedPipelineKey,
        IReadOnlyList<CostPipelineOption> availablePipelines)
        => selectedPipelineKey is not null
            && availablePipelines.Any(pipeline => pipeline.Key == selectedPipelineKey)
                ? selectedPipelineKey
                : null;
}
