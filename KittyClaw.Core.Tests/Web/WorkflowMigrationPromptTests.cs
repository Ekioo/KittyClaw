using System.Text.Json;

namespace KittyClaw.Core.Tests.Web;

public sealed class WorkflowMigrationPromptTests
{
    private static string RepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (directory is not null && !File.Exists(Path.Combine(directory, "KittyClaw.slnx")))
            directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        return directory!;
    }

    [Fact]
    public void Workflow_page_opens_the_same_visual_migration_wizard_as_the_board()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Workflows.razor"));

        Assert.Contains("<WorkflowMigrationWizard", source);
        Assert.Contains("_showMigrationWizard = true", source);
        Assert.DoesNotContain("<ChatDrawer", source);
    }

    [Fact]
    public void Workflow_page_delegates_all_column_configuration_to_the_board()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Workflows.razor"));

        Assert.DoesNotContain("WorkflowAddColumn", source);
        Assert.DoesNotContain("WorkflowColumnsHint", source);
        Assert.DoesNotContain("ColumnService", source);
        Assert.DoesNotContain("Configure processor", source);
        Assert.DoesNotContain("ColumnProcessorService ProcessorService", source);
    }

    [Fact]
    public void Workflow_page_uses_localization_keys_instead_of_english_ui_copy()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Workflows.razor"));

        Assert.Contains("@inject LocalizationService L", source);
        Assert.Contains("WorkflowMigrateTitle", source);
        Assert.Contains("WorkflowProjectSkills", source);
        Assert.DoesNotContain("<h2>Migrate the existing workflow</h2>", source);
        Assert.DoesNotContain("placeholder=\"New pipeline name\"", source);
        Assert.DoesNotContain(">Save skill</button>", source);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    public void Workflow_localizations_have_the_same_keys_as_english(string language)
    {
        static HashSet<string> Keys(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet();
        }

        var localization = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization");
        Assert.Equal(
            Keys(Path.Combine(localization, "Workflows.en.json")).Order(),
            Keys(Path.Combine(localization, $"Workflows.{language}.json")).Order());
    }

    [Fact]
    public void Chat_drawer_prefills_but_does_not_automatically_send_initial_message()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "ChatDrawer.razor"));

        Assert.Contains("[Parameter] public string? InitialMessage", source);
        Assert.Contains("_inputText = InitialMessage ?? \"\";", source);
        Assert.DoesNotContain("Send(InitialMessage", source);
    }

    [Fact]
    public void Legacy_board_opens_the_visual_migration_wizard_without_interrupting_deep_links()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Board.razor"));

        Assert.Contains("legacyConfig.Automations.Any(automation => automation.Enabled)", source);
        Assert.Contains("TicketId is null && ParentId is null", source);
        Assert.Contains("<WorkflowMigrationWizard", source);
    }

    [Fact]
    public void Migration_wizard_supports_graphical_review_refinement_and_confirmed_launch()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "WorkflowMigrationWizard.razor"));

        Assert.Contains("migration-pipeline-grid", source);
        Assert.Contains("migration-column-flow", source);
        Assert.Contains("RefineAsync", source);
        Assert.Contains("private void Back()", source);
        Assert.Contains("AutoSendInitialMessage=\"true\"", source);
        Assert.Contains("new System.Text.Json.Serialization.JsonStringEnumConverter()", source);
        Assert.Contains("GetFromJsonAsync<WorkflowMigrationJob>", source);
        Assert.Contains("Disable a legacy automation only after its replacement is configured and verified", source);
    }

    [Fact]
    public void Automatic_chat_send_remains_an_explicit_opt_in()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "ChatDrawer.razor"));

        Assert.Contains("[Parameter] public bool AutoSendInitialMessage", source);
        Assert.Contains("if (AutoSendInitialMessage", source);
        Assert.Contains("_inputText = InitialMessage ?? \"\";", source);
    }
}
