using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using Xunit;

namespace KittyClaw.Core.Tests.Automation;

public class ModelGroupsDefaultTests
{
    private static ModelGroup[] Groups(bool claude) =>
    [
        new("Claude", ClaudeModelCatalog.Models, claude),
        new("Grok", ["grok-4.5", "grok-4"], true),
        new("Copilot", ["copilot:gpt-5"], true),
    ];

    [Fact]
    public void Recommended_UsesClaudeDefault_WhenClaudeInstalled() =>
        Assert.Equal(ClaudeModelCatalog.DefaultModel, ModelGroups.Recommended(Groups(true)));

    [Fact]
    public void Recommended_UsesFirstModelOfFirstInstalledProvider_WhenClaudeMissing() =>
        Assert.Equal("grok-4.5", ModelGroups.Recommended(Groups(false)));

    [Fact]
    public void ResolveDefault_KeepsAvailableModelAndReplacesUnavailableOne()
    {
        Assert.Equal("copilot:gpt-5", ModelGroups.ResolveDefault(Groups(false), "copilot:gpt-5"));
        Assert.Equal("grok-4.5", ModelGroups.ResolveDefault(Groups(false), ClaudeModelCatalog.DefaultModel));
    }

    [Fact]
    public void ResolveDefault_KeepsCurrent_WhenNothingInstalled() =>
        Assert.Equal("x", ModelGroups.ResolveDefault([new("Claude", ClaudeModelCatalog.Models, false)], "x"));
}
