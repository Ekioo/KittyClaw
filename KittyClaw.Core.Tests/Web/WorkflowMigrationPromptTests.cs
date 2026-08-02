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
        Assert.DoesNotContain("InitialAgent=", source);
    }

    [Fact]
    public void Workflow_page_keeps_structure_editing_but_delegates_processor_configuration_to_the_board()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Workflows.razor"));

        Assert.Contains("Add column", source);
        Assert.Contains("right-click its header on the board", source);
        Assert.DoesNotContain("Configure processor", source);
        Assert.DoesNotContain("ColumnProcessorService ProcessorService", source);
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
