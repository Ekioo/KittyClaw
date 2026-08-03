using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public sealed class WorkflowMigrationEligibilityTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Offer_until_every_legacy_definition_has_been_removed(
        bool hasLegacyAutomations,
        bool expected)
    {
        Assert.Equal(expected, WorkflowMigrationEligibility.ShouldOffer(hasLegacyAutomations));
    }
}
