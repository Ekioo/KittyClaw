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
    public void Workflow_page_opens_new_instruction_with_editable_migration_draft()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Workflows.razor"));

        Assert.Contains("InitialMessage=\"@MigrationPrompt\"", source);
        Assert.Contains("Wait for my explicit approval before applying the migration.", source);
        Assert.Contains("Treat the current board as potentially denormalized", source);
        Assert.Contains("Do not preserve a single mixed pipeline", source);
        Assert.Contains("Map every existing ticket to exactly one proposed pipeline", source);
        Assert.Contains("Use OwnerAction when a human decision", source);
        Assert.Contains("replaced by scheduled tasks", source);
        Assert.DoesNotContain("InitialAgent=", source);
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
}
