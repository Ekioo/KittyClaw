using KittyClaw.Core.Services;
using KittyClaw.Web.Components.Pages;

namespace KittyClaw.Core.Tests.Web;

public sealed class CostPipelineFilterTests
{
    private static readonly CostPipelineOption[] Pipelines =
    [
        new("alpha:1", "Alpha / Build"),
        new("alpha:unknown", "Alpha"),
        new("beta:2", "Beta / Release")
    ];

    [Fact]
    public void Filter_ReturnsOnlyPipelinesOwnedBySelectedProjects()
    {
        var result = CostPipelineFilter.Filter(Pipelines, new HashSet<string> { "alpha" });

        Assert.Equal(["alpha:1", "alpha:unknown"], result.Select(option => option.Key));
    }

    [Fact]
    public void Filter_WithNoProjectSelection_ReturnsAllPipelines()
    {
        var result = CostPipelineFilter.Filter(Pipelines, new HashSet<string>());

        Assert.Same(Pipelines, result);
    }

    [Fact]
    public void KeepValidSelection_ClearsPipelineThatIsNoLongerAvailable()
    {
        var available = CostPipelineFilter.Filter(Pipelines, new HashSet<string> { "alpha" });

        Assert.Equal("alpha:1", CostPipelineFilter.KeepValidSelection("alpha:1", available));
        Assert.Null(CostPipelineFilter.KeepValidSelection("beta:2", available));
    }
}
