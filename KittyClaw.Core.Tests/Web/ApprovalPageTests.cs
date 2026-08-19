namespace KittyClaw.Core.Tests.Web;

using System.Text.Json;

public sealed class ApprovalPageTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "KittyClaw.Web", "Components", "Pages", "Approvals.razor"));
    private static readonly string BoardSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "KittyClaw.Web", "Components", "Pages", "Board.razor"));

    [Fact]
    public void ApprovalPage_ShowsRequiredContextAndOnlyTemporaryChoices()
    {
        Assert.Contains("@inject LocalizationService L", Source);
        foreach (var key in new[] { "ApprovalAction", "ApprovalDestinationResource", "ApprovalReason", "ApprovalScope", "ApprovalDuration", "ApprovalProvider", "ApprovalRun", "ApprovalTicket" })
            Assert.Contains($"L[\"{key}\"]", Source);
        Assert.Contains("L[\"ApprovalAllowOnce\"]", Source);
        Assert.Contains("L[\"ApprovalAllowForTicket\"]", Source);
        Assert.Contains("L[\"ApprovalDeny\"]", Source);
        Assert.DoesNotContain("Allow globally", Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApprovalPage_ExposesAuditHistoryAndPendingStateGate()
    {
        Assert.Contains("L.Get(\"ApprovalAuditHistory\"", Source);
        Assert.Contains("request.State == \"pending\"", Source);
        Assert.Contains("IntegrityHash", Source);
    }

    [Fact]
    public void ApprovalPage_AllStaticLabelsAreLocalizedInEverySupportedLanguage()
    {
        Assert.DoesNotContain(">Runtime approvals<", Source, StringComparison.Ordinal);
        Assert.DoesNotContain(">Refresh<", Source, StringComparison.Ordinal);
        Assert.Contains("L[\"RuntimeApprovalsTitle\"]", BoardSource, StringComparison.Ordinal);
        Assert.Contains("L[\"Approvals\"]", BoardSource, StringComparison.Ordinal);
        Assert.DoesNotContain("&#128737; Approvals</a>", BoardSource, StringComparison.Ordinal);

        var requiredKeys = new[]
        {
            "Approvals", "RuntimeApprovalsTitle", "Refresh", "ApprovalBackToBoard", "ApprovalLoading",
            "ApprovalEmpty", "ApprovalAction", "ApprovalDestinationResource", "ApprovalReason", "ApprovalScope",
            "ApprovalSingleEffect", "ApprovalDuration", "ApprovalProvider", "ApprovalRun", "ApprovalTicket",
            "ApprovalAllowOnce", "ApprovalAllowForTicket", "ApprovalDeny", "ApprovalAuditHistory",
            "ApprovalDecisionAudit", "ApprovalReceiptAudit", "ApprovalUntil"
        };

        foreach (var language in new[] { "en", "fr", "de", "es", "it" })
        {
            var path = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", $"Approvals.{language}.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var key in requiredKeys)
                Assert.True(document.RootElement.TryGetProperty(key, out _), $"{language} is missing {key}");
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
