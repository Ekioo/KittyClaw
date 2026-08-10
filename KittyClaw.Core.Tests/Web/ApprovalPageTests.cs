namespace KittyClaw.Core.Tests.Web;

public sealed class ApprovalPageTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "KittyClaw.Web", "Components", "Pages", "Approvals.razor"));

    [Fact]
    public void ApprovalPage_ShowsRequiredContextAndOnlyTemporaryChoices()
    {
        foreach (var label in new[] { "Action", "Destination / resource", "Reason", "Scope", "Duration", "Provider", "Run", "Ticket" })
            Assert.Contains(label, Source);
        Assert.Contains("Allow once", Source);
        Assert.Contains("Allow for this ticket", Source);
        Assert.Contains("Deny", Source);
        Assert.DoesNotContain("Allow globally", Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApprovalPage_ExposesAuditHistoryAndPendingStateGate()
    {
        Assert.Contains("Audit history", Source);
        Assert.Contains("request.State == \"pending\"", Source);
        Assert.Contains("IntegrityHash", Source);
    }
}
