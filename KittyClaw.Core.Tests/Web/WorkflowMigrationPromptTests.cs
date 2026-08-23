using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public sealed class WorkflowMigrationPromptTests
{
    [Fact]
    public void Migration_analysis_compacts_large_boards_below_the_cli_input_limit()
    {
        var pipeline = new Pipeline { Id = 1, Name = "Main", Slug = "main", IsDefault = true };
        var columns = new[] { new BoardColumn { Id = 1, PipelineId = 1, Name = "Todo" } };
        var labels = new List<Label> { new() { Id = 1, Name = "editorial", Color = "#fff" } };
        var tickets = Enumerable.Range(1, 1_500).Select(id => new TicketSummary(
            id, $"Representative ticket {id}", new string('d', 2_000), id % 2 == 0 ? "Todo" : "Review",
            TicketPriority.NiceToHave, id, null, "owner", DateTime.UtcNow.AddDays(-id), DateTime.UtcNow.AddMinutes(-id),
            labels, 0, null, id % 5 == 0 ? 1 : null, []) { PipelineId = 1 }).ToList();
        var config = new AutomationConfig
        {
            Automations = Enumerable.Range(1, 50).Select(id => new KittyClaw.Core.Automation.Automation
            {
                Id = $"automation-{id}",
                Name = $"Automation {id}",
                Trigger = new TicketInColumnTriggerSpec { Columns = ["Todo"] },
                Actions = [new ExecutePowerShellActionSpec { Script = new string('x', 20_000) }],
            }).ToList(),
        };

        var prompt = WorkflowMigrationPlanner.BuildAnalysisPrompt([pipeline], columns, tickets, config, "fr");

        Assert.True(prompt.Length < 250_000, $"Compacted prompt is still too large: {prompt.Length} characters.");
        Assert.Contains("ticketGroups", prompt);
        Assert.Contains("legacyAutomations", prompt);
    }

    [Fact]
    public void Migration_analysis_with_existing_pipeline_requests_additive_completion()
    {
        var pipeline = new Pipeline { Id = 42, Name = "Delivery", Slug = "delivery", IsDefault = true };
        var columns = new[]
        {
            new BoardColumn { Id = 7, PipelineId = 42, Name = "Ready", SortOrder = 0 },
            new BoardColumn { Id = 8, PipelineId = 42, Name = "Done", SortOrder = 1, Role = ColumnRole.Success },
        };
        var config = new AutomationConfig
        {
            Automations =
            [
                new KittyClaw.Core.Automation.Automation
                {
                    Id = "legacy-dispatch",
                    Trigger = new TicketInColumnTriggerSpec { Columns = ["Ready"] },
                    Actions = [new RunAgentActionSpec { Agent = "programmer" }],
                },
            ],
        };

        var prompt = WorkflowMigrationPlanner.BuildAnalysisPrompt([pipeline], columns, [], config, "en");

        Assert.Contains("Migration mode: completeExistingPipelines", prompt);
        Assert.Contains("preserve every existing pipeline and column", prompt);
        Assert.Contains("Never propose replacement pipelines or a regenerated workflow", prompt);
        Assert.Contains("legacy-dispatch", prompt);
    }

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

        Assert.Contains("legacyConfig.Automations.Count > 0", source);
        Assert.Contains("TicketId is null && ParentId is null", source);
        Assert.Contains("<WorkflowMigrationWizard", source);
    }

    [Fact]
    public void Migration_wizard_supports_graphical_review_refinement_and_in_wizard_application()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "WorkflowMigrationWizard.razor"));

        Assert.Contains("migration-pipeline-grid", source);
        Assert.Contains("migration-column-flow", source);
        Assert.Contains("RefineAsync", source);
        Assert.Contains("private void Back()", source);
        Assert.Contains("LaunchMigrationAsync", source);
        Assert.Contains("StartJobAsync(\"apply\"", source);
        Assert.Contains("MigrationWizardApplying", source);
        Assert.Contains("MigrationWizardCompletedTitle", source);
        Assert.DoesNotContain("<ChatDrawer", source);
        Assert.Contains("new System.Text.Json.Serialization.JsonStringEnumConverter()", source);
        Assert.Contains("GetFromJsonAsync<WorkflowMigrationJob>", source);
        Assert.Contains("migration-progress-track", source);
        Assert.Contains("MigrationWizardElapsed", source);
        Assert.Contains("job.ProgressCode", source);
        Assert.Contains("job.StartedAt", source);
        Assert.Contains("StateHasChanged", source);
        var planner = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Services", "WorkflowMigrationPlanner.cs"));
        Assert.Contains("localization.Lang", planner);
        Assert.Contains("Write every user-facing value in language", planner);
        Assert.Contains("ProgressCode = progressCode", planner);
        Assert.Contains("LastActivityAt = ev.At", planner);
        Assert.Contains("StartApplication", planner);
        Assert.Contains("workflow-migration-applier", planner);
        Assert.Contains("_activeApplications", planner);
    }

    [Fact]
    public void Project_onboarding_wizard_uses_setup_copy_instead_of_migration_copy()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "WorkflowMigrationWizard.razor"));

        Assert.Contains("ContextText(\"MigrationWizardLaunch\", \"ProjectOnboardingLaunch\")", source);
        Assert.Contains("ContextText(\"MigrationWizardApplying\", \"ProjectOnboardingApplying\")", source);
        Assert.Contains("ContextText(\"MigrationWizardCompletedTitle\", \"ProjectOnboardingCompletedTitle\")", source);
        Assert.Contains("ContextText(\"MigrationWizardLongRunningHint\", \"ProjectOnboardingLongRunningHint\")", source);

        foreach (var language in new[] { "en", "fr", "de", "es", "it", "pt-BR", "ja" })
        {
            var path = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", $"Workflows.{language}.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var onboardingCopy = document.RootElement.EnumerateObject()
                .Where(property => property.Name.StartsWith("ProjectOnboarding", StringComparison.Ordinal))
                .Select(property => property.Value.GetString() ?? string.Empty);

            Assert.DoesNotContain(onboardingCopy, value => value.Contains("migration", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Approved_application_prompt_preserves_the_migration_safety_contract()
    {
        var prompt = WorkflowMigrationPlanner.BuildApplicationPrompt(new WorkflowMigrationPlan
        {
            Pipelines =
            [
                new WorkflowMigrationPipeline
                {
                    Name = "Editorial",
                    Columns = [new WorkflowMigrationColumn { Name = "Owner review", Role = ColumnRole.OwnerAction }],
                },
            ],
        }, isProjectOnboarding: false);

        Assert.Contains("Editorial", prompt);
        Assert.Contains("OwnerAction", prompt);
        Assert.Contains("Do not leave any legacy automation definition, enabled or disabled", prompt);
        Assert.Contains("fail the migration instead of silently preserving it", prompt);
        Assert.Contains("Map every existing ticket and child ticket to exactly one pipeline", prompt);
        Assert.Contains("already in a Success or Failure column is completed historical work", prompt);
        Assert.Contains("must remain in a terminal (Success or Failure) column", prompt);
        Assert.Contains("Never call any /workflow-migrations/analyze", prompt);
        Assert.Contains("Apply the plan directly through the granular pipeline", prompt);
    }

    [Fact]
    public void Approved_application_extends_existing_pipelines_and_migrates_automations_sequentially()
    {
        var pipeline = new Pipeline { Id = 42, Name = "Delivery", Slug = "delivery", IsDefault = true };
        var column = new BoardColumn { Id = 7, PipelineId = 42, Name = "Ready", SortOrder = 0 };
        var config = new AutomationConfig
        {
            Automations =
            [
                new KittyClaw.Core.Automation.Automation
                {
                    Id = "legacy-dispatch",
                    Trigger = new TicketInColumnTriggerSpec { Columns = ["Ready"] },
                    Actions = [new RunAgentActionSpec { Agent = "programmer" }],
                },
            ],
        };

        var prompt = WorkflowMigrationPlanner.BuildApplicationPrompt(
            new WorkflowMigrationPlan
            {
                Pipelines =
                [
                    new WorkflowMigrationPipeline
                    {
                        Name = "Delivery",
                        Columns = [new WorkflowMigrationColumn { Name = "Ready" }],
                    },
                ],
            },
            isProjectOnboarding: false,
            [pipeline],
            [column],
            config);

        Assert.Contains("EXISTING WORKFLOW EXTENSION MODE IS ACTIVE", prompt);
        Assert.Contains("Preserve every existing pipeline ID", prompt);
        Assert.Contains("Preserve every existing column ID", prompt);
        Assert.Contains("Migrate legacy automations sequentially", prompt);
        Assert.Contains("then delete only that automation", prompt);
        Assert.Contains("rerunning the migration is idempotent", prompt);
        Assert.Contains("stop and leave that definition in place", prompt);
        Assert.Contains("legacy-dispatch", prompt);
        Assert.Contains("\"id\": 42", prompt);
        Assert.Contains("\"id\": 7", prompt);
    }

    [Fact]
    public void Second_application_with_no_legacy_automations_still_preserves_existing_workflow()
    {
        var pipeline = new Pipeline { Id = 42, Name = "Delivery", Slug = "delivery", IsDefault = true };
        var column = new BoardColumn { Id = 7, PipelineId = 42, Name = "Ready", SortOrder = 0 };
        var prompt = WorkflowMigrationPlanner.BuildApplicationPrompt(
            new WorkflowMigrationPlan
            {
                Pipelines =
                [
                    new WorkflowMigrationPipeline
                    {
                        Name = "Delivery",
                        Columns = [new WorkflowMigrationColumn { Name = "Ready" }],
                    },
                ],
            },
            isProjectOnboarding: false,
            [pipeline],
            [column],
            new AutomationConfig());

        Assert.Contains("EXISTING WORKFLOW EXTENSION MODE IS ACTIVE", prompt);
        Assert.Contains("Preserve every existing pipeline ID", prompt);
        Assert.Contains("\"legacyAutomations\": []", prompt);

        var regeneratedPipeline = new Pipeline
        {
            Id = 99, Name = "Delivery", Slug = "delivery", IsDefault = true,
        };
        var error = Assert.ThrowsAny<InvalidOperationException>(() =>
            WorkflowMigrationPlanner.EnsureExistingWorkflowWasExtended(
                [pipeline], [column], [regeneratedPipeline], []));

        Assert.Contains("instead of extending it", error.Message);
    }

    [Fact]
    public void Existing_workflow_guard_accepts_added_columns_without_replacing_original_structure()
    {
        var originalPipelines = new[]
        {
            new Pipeline { Id = 42, Name = "Delivery", Slug = "delivery" },
        };
        var originalColumns = new[]
        {
            new BoardColumn { Id = 7, PipelineId = 42, Name = "Ready" },
        };
        var currentPipelines = new[]
        {
            new Pipeline { Id = 42, Name = "Delivery", Slug = "delivery" },
        };
        var currentColumns = new[]
        {
            new BoardColumn { Id = 7, PipelineId = 42, Name = "Ready" },
            new BoardColumn { Id = 8, PipelineId = 42, Name = "Owner review", Role = ColumnRole.OwnerAction },
        };

        WorkflowMigrationPlanner.EnsureExistingWorkflowWasExtended(
            originalPipelines, originalColumns, currentPipelines, currentColumns);
    }

    [Fact]
    public void Existing_workflow_guard_accepts_semantic_column_enrichment()
    {
        var pipeline = new Pipeline { Id = 42, Name = "Delivery", Slug = "delivery" };
        var originalColumn = new BoardColumn
        {
            Id = 7, PipelineId = 42, Name = "Published", Color = "#999", Role = ColumnRole.Normal,
        };
        var enrichedColumn = new BoardColumn
        {
            Id = 7,
            PipelineId = 42,
            Name = "Published",
            Color = "#0f0",
            Role = ColumnRole.Success,
            UserGuidance = "Publication verified.",
        };

        WorkflowMigrationPlanner.EnsureExistingWorkflowWasExtended(
            [pipeline], [originalColumn], [pipeline], [enrichedColumn]);
    }

    [Fact]
    public void Existing_workflow_guard_accepts_a_new_column_inserted_between_existing_columns()
    {
        var pipeline = new Pipeline { Id = 42, Name = "Delivery", Slug = "delivery" };
        var originalColumns = new[]
        {
            new BoardColumn { Id = 7, PipelineId = 42, Name = "Ready", SortOrder = 0 },
            new BoardColumn { Id = 8, PipelineId = 42, Name = "Published", SortOrder = 1 },
        };
        var extendedColumns = new[]
        {
            new BoardColumn { Id = 7, PipelineId = 42, Name = "Ready", SortOrder = 0 },
            new BoardColumn { Id = 9, PipelineId = 42, Name = "Review", SortOrder = 1 },
            new BoardColumn { Id = 8, PipelineId = 42, Name = "Published", SortOrder = 2 },
        };

        WorkflowMigrationPlanner.EnsureExistingWorkflowWasExtended(
            [pipeline], originalColumns, [pipeline], extendedColumns);
    }

    [Fact]
    public void Existing_workflow_guard_rejects_reordered_existing_columns()
    {
        var pipeline = new Pipeline { Id = 42, Name = "Delivery", Slug = "delivery" };
        var originalColumns = new[]
        {
            new BoardColumn { Id = 7, PipelineId = 42, Name = "Ready", SortOrder = 0 },
            new BoardColumn { Id = 8, PipelineId = 42, Name = "Published", SortOrder = 1 },
        };
        var reorderedColumns = new[]
        {
            new BoardColumn { Id = 8, PipelineId = 42, Name = "Published", SortOrder = 0 },
            new BoardColumn { Id = 7, PipelineId = 42, Name = "Ready", SortOrder = 1 },
        };

        var error = Assert.ThrowsAny<InvalidOperationException>(() =>
            WorkflowMigrationPlanner.EnsureExistingWorkflowWasExtended(
                [pipeline], originalColumns, [pipeline], reorderedColumns));

        Assert.Contains("reordered existing columns", error.Message);
    }

    [Fact]
    public void Existing_workflow_guard_rejects_regenerated_pipelines()
    {
        var originalPipelines = new[]
        {
            new Pipeline { Id = 42, Name = "Delivery", Slug = "delivery" },
        };
        var originalColumns = new[]
        {
            new BoardColumn { Id = 7, PipelineId = 42, Name = "Ready" },
        };
        var regeneratedPipelines = new[]
        {
            new Pipeline { Id = 99, Name = "Delivery", Slug = "delivery" },
        };
        var regeneratedColumns = new[]
        {
            new BoardColumn { Id = 70, PipelineId = 99, Name = "Ready" },
        };

        var error = Assert.ThrowsAny<InvalidOperationException>(() =>
            WorkflowMigrationPlanner.EnsureExistingWorkflowWasExtended(
                originalPipelines, originalColumns, regeneratedPipelines, regeneratedColumns));

        Assert.Contains("instead of extending it", error.Message);
    }

    [Fact]
    public void Existing_workflow_guard_rejects_rewritten_pipeline_metadata()
    {
        var original = new Pipeline
        {
            Id = 42, Name = "Delivery", Slug = "delivery", SortOrder = 1, IsDefault = true,
        };
        var rewritten = new Pipeline
        {
            Id = 42, Name = "New delivery", Slug = "delivery", SortOrder = 1, IsDefault = true,
        };

        var error = Assert.ThrowsAny<InvalidOperationException>(() =>
            WorkflowMigrationPlanner.EnsureExistingWorkflowWasExtended(
                [original], [], [rewritten], []));

        Assert.Contains("changed existing pipeline", error.Message);
    }

    [Fact]
    public void Existing_workflow_guard_rejects_recreated_original_columns()
    {
        var pipeline = new Pipeline { Id = 42, Name = "Delivery", Slug = "delivery" };
        var originalColumn = new BoardColumn { Id = 7, PipelineId = 42, Name = "Ready" };
        var recreatedColumn = new BoardColumn { Id = 70, PipelineId = 42, Name = "Ready" };

        var error = Assert.ThrowsAny<InvalidOperationException>(() =>
            WorkflowMigrationPlanner.EnsureExistingWorkflowWasExtended(
                [pipeline], [originalColumn], [pipeline], [recreatedColumn]));

        Assert.Contains("removed or recreated existing column", error.Message);
    }

    [Fact]
    public void Completed_ticket_snapshot_uses_semantic_success_role_including_legacy_rows()
    {
        var columns = new[]
        {
            new BoardColumn { Id = 10, PipelineId = 1, Name = "Published", Color = "#0f0", Role = ColumnRole.Success },
            new BoardColumn { Id = 11, PipelineId = 1, Name = "Review", Color = "#999", Role = ColumnRole.Normal },
        };
        var tickets = new[]
        {
            Ticket(1, "Published", 1, 10),
            Ticket(2, "Review", 1, 11),
            Ticket(3, "Published", 1, null),
        };

        var completed = WorkflowMigrationPlanner.CaptureCompletedTickets(columns, tickets);

        Assert.Equal([1, 3], completed.Select(ticket => ticket.TicketId));
    }

    [Fact]
    public void Completion_guard_prefers_the_success_column_in_the_tickets_migrated_pipeline()
    {
        var snapshot = new WorkflowMigrationPlanner.CompletedTicketSnapshot(7, 1, "Done");
        var migratedTicket = Ticket(7, "Drafting", 2, 20);
        var columns = new[]
        {
            new BoardColumn { Id = 12, PipelineId = 1, Name = "Done", Color = "#0f0", Role = ColumnRole.Success },
            new BoardColumn { Id = 20, PipelineId = 2, Name = "Drafting", Color = "#999", Role = ColumnRole.Normal },
            new BoardColumn { Id = 21, PipelineId = 2, Name = "Published", Color = "#0f0", Role = ColumnRole.Success },
        };

        var destination = WorkflowMigrationPlanner.SelectCompletionDestination(snapshot, migratedTicket, columns);

        Assert.NotNull(destination);
        Assert.Equal(21, destination.Id);
        Assert.Equal(ColumnRole.Success, destination.Role);
    }

    private static TicketSummary Ticket(int id, string status, int pipelineId, int? columnId) =>
        new(id, $"Ticket {id}", "Description", status, TicketPriority.Required, 0,
            null, "owner", DateTime.UtcNow, DateTime.UtcNow, [], 0, null, null, [])
        {
            PipelineId = pipelineId,
            ColumnId = columnId,
        };

    [Fact]
    public void Completed_ticket_snapshot_captures_failure_tickets_by_column_id()
    {
        var columns = new[]
        {
            new BoardColumn { Id = 10, PipelineId = 1, Name = "Done", Color = "#0f0", Role = ColumnRole.Success },
            new BoardColumn { Id = 11, PipelineId = 1, Name = "Abandoned", Color = "#f00", Role = ColumnRole.Failure },
            new BoardColumn { Id = 12, PipelineId = 1, Name = "In Progress", Color = "#999", Role = ColumnRole.Normal },
        };
        var tickets = new[]
        {
            Ticket(1, "Done", 1, 10),
            Ticket(2, "Abandoned", 1, 11),
            Ticket(3, "In Progress", 1, 12),
        };

        var completed = WorkflowMigrationPlanner.CaptureCompletedTickets(columns, tickets);

        Assert.Equal([1, 2], completed.Select(t => t.TicketId));
        Assert.Equal(ColumnRole.Success, completed.Single(t => t.TicketId == 1).OriginalRole);
        Assert.Equal(ColumnRole.Failure, completed.Single(t => t.TicketId == 2).OriginalRole);
    }

    [Fact]
    public void Completed_ticket_snapshot_captures_failure_tickets_via_legacy_name_fallback()
    {
        var columns = new[]
        {
            new BoardColumn { Id = 10, PipelineId = 1, Name = "Abandonné ou échec", Color = "#f00", Role = ColumnRole.Failure },
            new BoardColumn { Id = 11, PipelineId = 1, Name = "Validation éditoriale", Color = "#fa0", Role = ColumnRole.OwnerAction },
        };
        var tickets = new[]
        {
            Ticket(99, "Abandonné ou échec", 1, null),
            Ticket(100, "Validation éditoriale", 1, null),
        };

        var completed = WorkflowMigrationPlanner.CaptureCompletedTickets(columns, tickets);

        Assert.Equal([99], completed.Select(t => t.TicketId));
        Assert.Equal(ColumnRole.Failure, completed[0].OriginalRole);
    }

    [Fact]
    public void Completed_ticket_snapshot_captures_localized_success_via_legacy_name_fallback()
    {
        var columns = new[]
        {
            new BoardColumn { Id = 10, PipelineId = 1, Name = "Publié et vérifié", Color = "#0f0", Role = ColumnRole.Success },
        };
        var tickets = new[]
        {
            Ticket(101, "Publié et vérifié", 1, null),
        };

        var completed = WorkflowMigrationPlanner.CaptureCompletedTickets(columns, tickets);

        var snapshot = Assert.Single(completed);
        Assert.Equal(101, snapshot.TicketId);
        Assert.Equal(ColumnRole.Success, snapshot.OriginalRole);
    }

    [Fact]
    public void Completed_ticket_snapshot_excludes_scheduled_waiting_ticket()
    {
        var columns = new[]
        {
            new BoardColumn { Id = 10, PipelineId = 1, Name = "Programmé", Color = "#fa0", Role = ColumnRole.Waiting },
        };
        var tickets = new[]
        {
            Ticket(102, "Programmé", 1, 10),
        };

        var completed = WorkflowMigrationPlanner.CaptureCompletedTickets(columns, tickets);

        Assert.Empty(completed);
    }

    [Fact]
    public void Completed_ticket_snapshot_excludes_authentic_owner_action_ticket()
    {
        var columns = new[]
        {
            new BoardColumn { Id = 10, PipelineId = 1, Name = "Validation éditoriale", Color = "#fa0", Role = ColumnRole.OwnerAction },
        };
        var tickets = new[]
        {
            Ticket(103, "Validation éditoriale", 1, 10),
        };

        var completed = WorkflowMigrationPlanner.CaptureCompletedTickets(columns, tickets);

        Assert.Empty(completed);
    }

    [Fact]
    public void Completion_guard_prefers_failure_column_for_failure_snapshot()
    {
        var snapshot = new WorkflowMigrationPlanner.CompletedTicketSnapshot(5, 1, "Abandoned", ColumnRole.Failure);
        var migratedTicket = Ticket(5, "In Progress", 2, 30);
        var columns = new[]
        {
            new BoardColumn { Id = 30, PipelineId = 2, Name = "In Progress", Color = "#999", Role = ColumnRole.Normal },
            new BoardColumn { Id = 31, PipelineId = 2, Name = "Done", Color = "#0f0", Role = ColumnRole.Success },
            new BoardColumn { Id = 32, PipelineId = 2, Name = "Abandoned", Color = "#f00", Role = ColumnRole.Failure },
        };

        var destination = WorkflowMigrationPlanner.SelectCompletionDestination(snapshot, migratedTicket, columns);

        Assert.NotNull(destination);
        Assert.Equal(32, destination.Id);
        Assert.Equal(ColumnRole.Failure, destination.Role);
    }

    [Fact]
    public void Completion_guard_falls_back_to_success_when_no_failure_column_exists_after_migration()
    {
        var snapshot = new WorkflowMigrationPlanner.CompletedTicketSnapshot(5, 1, "Abandoned", ColumnRole.Failure);
        var migratedTicket = Ticket(5, "In Progress", 2, 30);
        var columns = new[]
        {
            new BoardColumn { Id = 30, PipelineId = 2, Name = "In Progress", Color = "#999", Role = ColumnRole.Normal },
            new BoardColumn { Id = 31, PipelineId = 2, Name = "Done", Color = "#0f0", Role = ColumnRole.Success },
        };

        var destination = WorkflowMigrationPlanner.SelectCompletionDestination(snapshot, migratedTicket, columns);

        Assert.NotNull(destination);
        Assert.Equal(31, destination.Id);
        Assert.Equal(ColumnRole.Success, destination.Role);
    }

    [Fact]
    public void Completed_migration_rejects_even_disabled_legacy_definitions()
    {
        var config = new AutomationConfig
        {
            Automations =
            [
                new KittyClaw.Core.Automation.Automation
                {
                    Id = "old-worker",
                    Enabled = false,
                    Trigger = new TicketInColumnTriggerSpec { Columns = ["Todo"] },
                },
            ],
        };

        var error = Assert.ThrowsAny<InvalidOperationException>(
            () => WorkflowMigrationPlanner.EnsureNoLegacyAutomations(config));

        Assert.Contains("old-worker", error.Message);
    }

    [Fact]
    public void Completed_migration_accepts_an_empty_legacy_configuration()
    {
        WorkflowMigrationPlanner.EnsureNoLegacyAutomations(new AutomationConfig());
    }

    [Fact]
    public void Automatic_chat_send_remains_an_explicit_opt_in()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "ChatDrawer.razor"));

        Assert.Contains("[Parameter] public bool AutoSendInitialMessage", source);
        Assert.Contains("if (AutoSendInitialMessage", source);
        Assert.Contains("_inputText = InitialMessage ?? \"\";", source);
    }

    [Fact]
    public void ParsePlan_throws_when_agent_response_contains_no_json()
    {
        var ex = Assert.ThrowsAny<InvalidOperationException>(
            () => WorkflowMigrationPlanner.ParsePlan("I could not generate a migration plan."));
        Assert.Contains("no structured plan", ex.Message);
    }

    [Fact]
    public void ParsePlan_extracts_plan_from_response_with_surrounding_commentary()
    {
        var json = """{"summary":"two pipelines","pipelines":[{"name":"Dev","description":"dev","columns":[{"name":"Todo","role":"Normal","description":"open","userGuidance":""}]}],"risks":[]}""";
        var plan = WorkflowMigrationPlanner.ParsePlan($"Here is your plan:\n{json}\nLet me know if changes are needed.");
        Assert.Equal("two pipelines", plan.Summary);
        Assert.Single(plan.Pipelines);
    }

    [Fact]
    public void ParsePlan_strips_assistant_prefix_before_extracting_json()
    {
        var json = """{"summary":"onboarding","pipelines":[{"name":"Delivery","description":"d","columns":[{"name":"Todo","role":"Normal","description":"open","userGuidance":""}]}],"risks":[]}""";
        var plan = WorkflowMigrationPlanner.ParsePlan($"[assistant] {json}");
        Assert.Equal("onboarding", plan.Summary);
    }

    [Fact]
    public void Workspace_analysis_prompt_does_not_throw_when_workspace_path_does_not_exist()
    {
        var prompt = WorkflowMigrationPlanner.BuildWorkspaceAnalysisPrompt(
            Path.Combine(Path.GetTempPath(), "nonexistent-workspace-" + Guid.NewGuid().ToString("N")), null, "en");
        Assert.Contains("fileCount", prompt);
    }

    [Fact]
    public void Migration_failures_are_logged_not_swallowed()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Services", "WorkflowMigrationPlanner.cs"));
        Assert.DoesNotContain("catch { }", source);
        Assert.Contains("LogWarning", source);
        Assert.Contains("LogError", source);
        Assert.Contains("LogDebug", source);
    }
}
