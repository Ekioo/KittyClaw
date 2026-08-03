using System.Text.Json;

namespace KittyClaw.Core.Tests.Web;

public sealed class ColumnProcessorDialogTests
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
    public void Unsaved_processor_defaults_are_identified_as_a_draft()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "ColumnProcessorDialog.razor"));

        Assert.Contains("if (!_processorExists)", source);
        Assert.Contains("ProcessorDraftNotice", source);
        Assert.Contains("_processorExists = Column is not null && processor is not null", source);
    }

    [Fact]
    public void Column_editor_centralizes_general_processor_routing_and_deletion()
    {
        var dialog = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "ColumnProcessorDialog.razor"));
        var board = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Board.razor"));
        var workflows = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Workflows.razor"));
        var settings = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "ProjectSettings.razor"));

        Assert.Contains("ColumnGeneralTab", dialog);
        Assert.Contains("ColumnProcessorTab", dialog);
        Assert.Contains("ColumnActionsTab", dialog);
        Assert.Contains("ColumnRoutingTab", dialog);
        Assert.Contains("_columnColor", dialog);
        Assert.Contains("_columnRole", dialog);
        Assert.Contains("_positionIndex", dialog);
        Assert.Contains("DeleteColumnAsync", dialog);
        Assert.Contains("processor?.BeforeActions", dialog);
        Assert.Contains("processor?.AfterActions", dialog);
        Assert.Contains("ProcessorMode=\"true\"", dialog);
        Assert.Contains("column-insert-slot", board);
        Assert.Contains("AddColumnFromBoard", board);
        Assert.Contains("InsertColumnBeforeFromMenu", board);
        Assert.Contains("DuplicateColumnFromMenu", board);
        Assert.DoesNotContain("WorkflowColumnsHint", workflows);
        Assert.DoesNotContain("NewColumnPlaceholder", settings);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    public void Draft_notice_is_localized(string language)
    {
        var path = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", $"Board.{language}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        Assert.False(string.IsNullOrWhiteSpace(
            document.RootElement.GetProperty("ProcessorDraftNotice").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            document.RootElement.GetProperty("ColumnProcessorActive").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            document.RootElement.GetProperty("ColumnProcessorInactive").GetString()));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    public void Every_column_editor_field_help_is_localized(string language)
    {
        string[] keys =
        [
            "ColumnNameHelp", "ColumnColorHelp", "ColumnRoleHelp", "ColumnPositionHelp",
            "ColumnDeleteDestinationHelp", "ProcessorEnabledHelp", "ProcessorNameHelp",
            "ProcessorMissionHelp", "ProcessorPromptHelp", "ProcessorTicketOrderHelp",
            "ProcessorMaxAttemptsHelp", "ProcessorRetryDelayHelp", "ProcessorTurnLimitHelp",
            "ProcessorAvailableHelp", "ProcessorRecommendedHelp", "ProcessorRequiredHelp",
            "ProcessorDefaultDestinationHelp", "ProcessorFailureDestinationHelp",
            "ProcessorOutcomeHelp", "ProcessorRouteDestinationHelp",
        ];
        var path = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", $"Board.{language}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var key in keys)
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty(key).GetString()), key);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    public void Processor_action_editor_help_is_localized(string language)
    {
        string[] keys =
        [
            "ActionEditorAddComment", "ActionEditorExecutePowerShell", "ActionTypeHelp",
            "LabelsToAddHelp", "LabelsToRemoveHelp", "CommentContentHelp",
            "CommentAuthorHelp", "CtTitleHelp", "CtDescriptionHelp", "CtStatusHelp",
            "CtPriorityHelp", "CtLabelsHelp", "CtCreatedByHelp", "PsScriptHelp",
            "PsScriptFileHelp", "PsArgumentsHelp", "PsTimeoutHelp", "HttpMethodHelp",
            "HttpUrlHelp", "HttpBodyHelp", "HttpContentTypeHelp", "HttpTimeoutHelp",
        ];
        var path = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", $"ActionEditor.{language}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var key in keys)
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty(key).GetString()), key);
    }

    [Fact]
    public void Processor_action_editor_uses_user_facing_labels()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "ActionEditor.razor"));

        Assert.Contains("ActionEditorAddComment", source);
        Assert.Contains("ActionEditorExecutePowerShell", source);
        Assert.Contains("PriorityNiceToHave", source);
        Assert.DoesNotContain(">NiceToHave<", source);
    }

    [Fact]
    public void Column_editor_explains_all_fields_and_preserves_the_processor_model()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "ColumnProcessorDialog.razor"));

        Assert.Contains("<FieldHelp", source);
        Assert.Contains("ProcessorMissionHelp", source);
        Assert.Contains("ProcessorPromptHelp", source);
        Assert.Contains("_model = processor?.Model", source);
        Assert.Contains("_name, _mission, _model", source);
        Assert.DoesNotContain("_name, _mission, null", source);

        var help = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "FieldHelp.razor"));
        Assert.Contains("title=\"@Help\"", help);
        Assert.DoesNotContain("data-tooltip", help);
    }

    [Fact]
    public void Boards_replace_the_decorative_dot_with_processor_state()
    {
        var board = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Board.razor"));
        var unified = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "UnifiedBoard.razor"));
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "wwwroot", "app.css"));

        Assert.Contains("column-processor-indicator", board);
        Assert.Contains("_activeProcessorColumnIds.Contains(col.Id)", board);
        Assert.Contains("lane.ActiveProcessorColumnIds.Contains(col.Id)", unified);
        Assert.Contains("ProcessorService.ListAsync", board);
        Assert.Contains("ProcessorService.ListAsync", unified);
        Assert.DoesNotContain(".status-pill::before", css);
    }

    [Fact]
    public void Project_pipeline_tabs_are_left_aligned_and_vertically_balanced()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "wwwroot", "app.css"));

        Assert.Contains("margin: .75rem 1rem;", css);
    }

    [Fact]
    public void New_workflow_controls_follow_the_dark_application_theme()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "wwwroot", "app.css"));

        Assert.Matches(@"(?s)\.column-context-menu\s*\{[^}]*background:\s*var\(--surface2\);[^}]*border:\s*1px solid var\(--border-strong\);", css);
        Assert.Matches(@"(?s)\.processor-dialog select,\s*\.workflow-settings select\s*\{[^}]*background:\s*var\(--surface2\);[^}]*color:\s*var\(--text\);", css);
        Assert.Contains(".workflow-edit-row > .settings-hint { margin: 0;", css);
        Assert.Contains(".workflow-settings .skill-editor summary::before", css);
        Assert.Contains("content: \"▸\";", css);
    }

    [Fact]
    public void Ticket_timeline_resolves_legacy_column_ids_to_processor_names()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "TicketPanel.razor"));

        Assert.Contains("ActivityAuthor(a.Author)", source);
        Assert.Contains("_processorNames.TryGetValue(columnId, out var processorName)", source);
    }

    [Fact]
    public void Column_engine_uses_processor_name_for_new_activity_authors()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Core", "Automation", "ColumnProcessingEngine.cs"));

        Assert.Contains("var activityAuthor = processor.Name", source);
        Assert.DoesNotContain("dispatch.Result, $\"column-{processor.ColumnId}\"", source);
    }

    [Fact]
    public void Scheduled_tasks_use_the_shared_visual_schedule_builder_instead_of_raw_cron_input()
    {
        var dialog = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "ColumnProcessorDialog.razor"));
        var trigger = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "TriggerEditor.razor"));
        var builder = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "CronScheduleEditor.razor"));

        Assert.Contains("<CronScheduleEditor", dialog);
        Assert.Contains("<CronScheduleEditor", trigger);
        Assert.DoesNotContain("@bind=\"task.Cron\"", dialog);
        Assert.Contains("type=\"time\"", builder);
        Assert.Contains("weekday-picker", builder);
        Assert.Contains("ToCron()", builder);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    public void OwnerAction_role_and_visual_schedule_are_localized(string language)
    {
        using var workflow = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Core", "Localization", $"Workflows.{language}.json")));
        using var board = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Core", "Localization", $"Board.{language}.json")));

        Assert.False(string.IsNullOrWhiteSpace(workflow.RootElement.GetProperty("WorkflowRoleOwnerAction").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(board.RootElement.GetProperty("ScheduleCalendar").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(board.RootElement.GetProperty("ScheduleCalendarHelp").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(board.RootElement.GetProperty("OwnerActionRequired").GetString()));
    }

    [Fact]
    public void OwnerAction_tickets_are_emphasized_on_project_and_unified_boards()
    {
        var board = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Board.razor"));
        var unified = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "UnifiedBoard.razor"));
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "wwwroot", "app.css"));

        Assert.Contains("col.Role == ColumnRole.OwnerAction", board);
        Assert.Contains("owner-action-badge", board);
        Assert.Contains("col.Role == ColumnRole.OwnerAction", unified);
        Assert.Contains("owner-action-badge", unified);
        Assert.Contains(".ticket-card.ticket-owner-action", css);
    }
}
