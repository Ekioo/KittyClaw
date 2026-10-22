using KittyClaw.Core.Evidence;
using KittyClaw.Core.Services;
using KittyClaw.Web.Components;

namespace KittyClaw.Core.Tests.Evidence;

public sealed class DecisionBriefDiagnosticLocalizerTests
{
    [Fact]
    public void StaleDiagnostics_AreLocalizedInFrench()
    {
        using var data = new TemporaryDirectory();
        var localization = CreateLocalization(data.Path, "fr");
        var brief = Compose(EvidenceStatus.Stale);

        Assert.Equal(
            "Les preuves sont périmées et peuvent ne plus refléter l’état actuel",
            DecisionBriefDiagnosticLocalizer.Finding(localization, Assert.Single(brief.Findings)));
        Assert.Equal(
            "Les preuves sont antérieures au seuil de péremption. Relancez l’agent pour capturer des artefacts récents, ou abaissez le seuil de fraîcheur pour ce contexte.",
            DecisionBriefDiagnosticLocalizer.Recovery(localization, brief));
    }

    [Fact]
    public void StaleDiagnostics_FallBackToEnglishForUnsupportedLanguage()
    {
        using var data = new TemporaryDirectory();
        var localization = CreateLocalization(data.Path, "unsupported");
        var brief = Compose(EvidenceStatus.Stale);

        Assert.Equal(
            "Evidence is stale and may not reflect the current state",
            DecisionBriefDiagnosticLocalizer.Finding(localization, Assert.Single(brief.Findings)));
        Assert.Equal(
            "Evidence predates the staleness threshold. Re-run the agent to capture fresh artifacts, or lower the freshness threshold for this context.",
            DecisionBriefDiagnosticLocalizer.Recovery(localization, brief));
    }

    [Fact]
    public void CommandFinding_PreservesDynamicCommand()
    {
        using var data = new TemporaryDirectory();
        var localization = CreateLocalization(data.Path, "fr");
        var finding = new DecisionFinding("command-failure", "Command failed: dotnet test --filter Evidence", true);

        Assert.Equal(
            "Échec de la commande : dotnet test --filter Evidence",
            DecisionBriefDiagnosticLocalizer.Finding(localization, finding));
    }

    private static LocalizationService CreateLocalization(string dataDir, string language) =>
        new(new AppSettingsService(dataDir) { Language = language });

    private static TicketDecisionBrief Compose(EvidenceStatus status) =>
        DecisionBriefComposer.Compose(new TicketEvidence
        {
            TicketId = "341",
            ProjectSlug = "kittyclaw",
            CapturedAt = DateTime.UtcNow,
            Status = status,
        });

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"kittyclaw-brief-localization-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

