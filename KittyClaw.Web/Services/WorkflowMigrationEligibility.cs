namespace KittyClaw.Web.Services;

public static class WorkflowMigrationEligibility
{
    public static bool ShouldOffer(bool hasLegacyAutomations) => hasLegacyAutomations;
}
