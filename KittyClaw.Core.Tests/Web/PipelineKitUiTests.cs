using System.Text.Json;

namespace KittyClaw.Core.Tests.Web;

public sealed class PipelineKitUiTests
{
    private static string RepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (directory is not null && !File.Exists(Path.Combine(directory, "KittyClaw.slnx")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException();
    }

    [Fact]
    public void Workflow_page_exposes_the_secure_pipeline_kit_dialog()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Workflows.razor"));
        Assert.Contains("<PipelineKitDialog", source);
        Assert.Contains("PipelineId=\"@_selectedPipelineId\"", source);
        Assert.Contains("PipelineImportedAsync", source);
    }

    [Fact]
    public void Import_is_analyze_then_confirm_with_separate_approvals_and_masked_secret_fields()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "PipelineKitDialog.razor"));
        Assert.Contains("Importer.AnalyzeAsync", source);
        Assert.Contains("Importer.InstallAsync", source);
        Assert.True(source.IndexOf("Importer.AnalyzeAsync", StringComparison.Ordinal) < source.IndexOf("Importer.InstallAsync", StringComparison.Ordinal));
        Assert.Contains("type=\"password\"", source);
        Assert.Contains("RequiredApprovals", source);
        Assert.Contains("SetApproval", source);
        Assert.DoesNotContain("overwrite", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_requires_review_and_displays_blocking_findings_before_download()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "PipelineKitDialog.razor"));
        Assert.Contains("ReviewExportAsync", source);
        Assert.Contains("PipelineExportBlockedException", source);
        Assert.Contains("ex.Findings", source);
        Assert.Contains("DownloadAsync", source);
        Assert.True(source.IndexOf("ReviewExportAsync", StringComparison.Ordinal) < source.LastIndexOf("DownloadAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void Pipeline_kit_copy_is_localized_in_every_supported_workflow_catalog()
    {
        var localization = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization");
        var english = Keys(Path.Combine(localization, "Workflows.en.json"));
        foreach (var language in new[] { "fr", "de", "es", "it", "pt-BR", "ja" })
            Assert.Equal(english.Order(), Keys(Path.Combine(localization, $"Workflows.{language}.json")).Order());
    }

    private static HashSet<string> Keys(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet();
    }
}
