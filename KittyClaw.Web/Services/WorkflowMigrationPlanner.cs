using System.Collections.Concurrent;
using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Web.Services;

public sealed class WorkflowMigrationPlan
{
    public string Summary { get; set; } = "";
    public List<WorkflowMigrationPipeline> Pipelines { get; set; } = [];
    public List<string> Risks { get; set; } = [];
}

public sealed class WorkflowMigrationPipeline
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<WorkflowMigrationColumn> Columns { get; set; } = [];
}

public sealed class WorkflowMigrationColumn
{
    public string Name { get; set; } = "";
    public ColumnRole Role { get; set; } = ColumnRole.Normal;
    public string Description { get; set; } = "";
    public string UserGuidance { get; set; } = "";
}

public sealed record WorkflowMigrationJob(
    string Id,
    string ProjectSlug,
    string Status,
    WorkflowMigrationPlan? Plan,
    string? Error,
    string? RunId,
    string? ErrorCode = null,
    string ProgressCode = "queued",
    DateTime? StartedAt = null,
    DateTime? LastActivityAt = null,
    string? Result = null);

/// <summary>
/// Runs workflow planning and explicitly confirmed application turns, retaining their state long
/// enough for the visual wizard to remain the single source of progress and errors.
/// </summary>
public sealed class WorkflowMigrationPlanner(
    ProjectService projects,
    PipelineService pipelines,
    ColumnService columns,
    TicketService tickets,
    AutomationStore automations,
    AgentRunner runner,
    LocalizationService localization,
    ILogger<WorkflowMigrationPlanner>? logger = null)
{
    private readonly ConcurrentDictionary<string, WorkflowMigrationJob> _jobs = [];
    private readonly object _applicationSync = new();
    private readonly Dictionary<string, string> _activeApplications = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public WorkflowMigrationJob? Get(string projectSlug, string jobId) =>
        _jobs.TryGetValue(jobId, out var job) && job.ProjectSlug == projectSlug ? job : null;

    public string StartAnalysis(string projectSlug, string mode = "migration", string? brief = null)
    {
        var id = Guid.NewGuid().ToString("N");
        _jobs[id] = new WorkflowMigrationJob(id, projectSlug, "queued", null, null, null);
        _ = RunAsync(id, projectSlug, null, null, null, mode, brief);
        return id;
    }

    public string StartRefinement(string projectSlug, WorkflowMigrationPlan plan, string instruction,
        string phase, int? pipelineIndex)
    {
        var id = Guid.NewGuid().ToString("N");
        _jobs[id] = new WorkflowMigrationJob(id, projectSlug, "queued", null, null, null);
        _ = RunAsync(id, projectSlug, plan, instruction, (phase, pipelineIndex), "migration", null);
        return id;
    }

    public string StartApplication(string projectSlug, WorkflowMigrationPlan plan, bool isProjectOnboarding)
    {
        lock (_applicationSync)
        {
            if (_activeApplications.TryGetValue(projectSlug, out var existingId)
                && _jobs.TryGetValue(existingId, out var existing)
                && existing.Status is "queued" or "running")
                return existingId;

            var id = Guid.NewGuid().ToString("N");
            _jobs[id] = new WorkflowMigrationJob(id, projectSlug, "queued", plan, null, null);
            _activeApplications[projectSlug] = id;
            _ = RunApplicationAsync(id, projectSlug, plan, isProjectOnboarding);
            return id;
        }
    }

    private async Task RunAsync(string jobId, string slug, WorkflowMigrationPlan? currentPlan,
        string? instruction, (string Phase, int? PipelineIndex)? refinement, string mode, string? brief)
    {
        try
        {
            var project = await projects.GetProjectAsync(slug)
                ?? throw new InvalidOperationException($"Project '{slug}' was not found.");
            var workspace = projects.ResolveWorkspacePath(project);
            var prompt = currentPlan is null
                ? mode == "onboarding"
                    ? BuildWorkspaceAnalysisPrompt(workspace, brief, localization.Lang, logger)
                    : await BuildAnalysisPromptAsync(slug, localization.Lang)
                : BuildRefinementPrompt(currentPlan, instruction!, refinement!.Value, localization.Lang);

            var assistantText = new List<string>();
            var runId = Guid.NewGuid().ToString("N");
            var startedAt = DateTime.UtcNow;
            _jobs[jobId] = _jobs[jobId] with
            {
                Status = "running",
                RunId = runId,
                ProgressCode = "preparing",
                StartedAt = startedAt,
                LastActivityAt = startedAt,
            };
            var target = CodexCli.IsInstalled
                ? new AgentDispatchTarget("gpt-5.6-sol", CliProvider.Codex, new Dictionary<string, string>())
                : AgentDispatchTarget.ClaudeDefault;
            var run = await runner.RunAsync(new AgentRunContext
            {
                ProjectSlug = slug,
                WorkspacePath = workspace,
                AgentName = "workflow-migration-planner",
                SkillFile = "(inline)",
                InlineSkillContent = PlannerInstructions,
                ExtraContext = prompt,
                Target = target,
                MaxTurns = 30,
                MaxRunDuration = TimeSpan.FromMinutes(10),
                PersistSession = false,
                PresetRunId = runId,
                ConcurrencyGroup = $"workflow-migration:{slug}",
                OnEventHook = ev =>
                {
                    if (ev.Kind == "assistant") assistantText.Add(ev.Text);
                    var progressCode = ev.Kind switch
                    {
                        "command" or "tool" or "stderr" => "inspecting",
                        "assistant" or "result" => "finalizing",
                        _ => "analyzing",
                    };
                    _jobs.AddOrUpdate(jobId,
                        _ => new WorkflowMigrationJob(jobId, slug, "running", null, null, runId,
                            ProgressCode: progressCode, StartedAt: startedAt, LastActivityAt: ev.At),
                        (_, current) => current with
                        {
                            ProgressCode = progressCode,
                            LastActivityAt = ev.At,
                        });
                },
            }, CancellationToken.None);

            if (run.Status != AgentRunStatus.Completed)
            {
                var diagnostic = run.SnapshotBuffer()
                    .LastOrDefault(ev => ev.Kind is "stderr" or "error")?.Text;
                var code = diagnostic?.Contains("input_too_large", StringComparison.OrdinalIgnoreCase) == true
                    ? "input-too-large"
                    : run.Status == AgentRunStatus.Stopped ? "interrupted" : "agent-failed";
                throw new WorkflowMigrationPlanningException(code, diagnostic ?? "The planning agent did not complete successfully.");
            }

            var plan = ParsePlan(string.Join("\n", assistantText));
            Validate(plan);
            _jobs[jobId] = _jobs[jobId] with
            {
                Status = "completed",
                Plan = plan,
                ProgressCode = "completed",
                LastActivityAt = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            var code = ex switch
            {
                WorkflowMigrationPlanningException planning => planning.Code,
                JsonException => "invalid-plan",
                _ when ex.Message.Contains("structured plan", StringComparison.OrdinalIgnoreCase) => "invalid-plan",
                _ => "analysis-failed",
            };
            logger?.LogError(ex, "Workflow migration planning job {JobId} failed for project {ProjectSlug} (code: {ErrorCode})",
                jobId, slug, code);
            _jobs[jobId] = _jobs[jobId] with
            {
                Status = "failed",
                Error = ex.Message,
                ErrorCode = code,
                ProgressCode = "failed",
                LastActivityAt = DateTime.UtcNow,
            };
        }
    }

    private async Task RunApplicationAsync(string jobId, string slug, WorkflowMigrationPlan plan,
        bool isProjectOnboarding)
    {
        try
        {
            Validate(plan);
            var existingPipelines = isProjectOnboarding ? [] : (await pipelines.ListAsync(slug)).ToList();
            var existingColumns = new List<BoardColumn>();
            foreach (var pipeline in existingPipelines)
                existingColumns.AddRange(await columns.ListColumnsAsync(slug, pipeline.Id));
            var existingAutomationConfig = isProjectOnboarding
                ? new AutomationConfig()
                : (await automations.LoadAsync(slug)).Config;
            var extendExistingWorkflow = existingPipelines.Count > 0;
            List<CompletedTicketSnapshot> completedTickets = isProjectOnboarding
                ? []
                : CaptureCompletedTickets(existingColumns, await tickets.ListTicketsAsync(slug));
            var project = await projects.GetProjectAsync(slug)
                ?? throw new InvalidOperationException($"Project '{slug}' was not found.");
            var workspace = projects.ResolveWorkspacePath(project);
            var assistantText = new List<string>();
            var runId = Guid.NewGuid().ToString("N");
            var startedAt = DateTime.UtcNow;
            _jobs[jobId] = _jobs[jobId] with
            {
                Status = "running",
                RunId = runId,
                ProgressCode = "applying",
                StartedAt = startedAt,
                LastActivityAt = startedAt,
            };

            var target = CodexCli.IsInstalled
                ? new AgentDispatchTarget("gpt-5.6-sol", CliProvider.Codex, new Dictionary<string, string>())
                : AgentDispatchTarget.ClaudeDefault;
            var run = await runner.RunAsync(new AgentRunContext
            {
                ProjectSlug = slug,
                WorkspacePath = workspace,
                AgentName = "workflow-migration-applier",
                SkillFile = "(inline)",
                InlineSkillContent = ApplicationInstructions,
                ExtraContext = BuildApplicationPrompt(plan, isProjectOnboarding,
                    existingPipelines, existingColumns, existingAutomationConfig),
                Target = target,
                MaxTurns = 100,
                MaxRunDuration = TimeSpan.FromMinutes(30),
                PersistSession = false,
                PresetRunId = runId,
                ConcurrencyGroup = $"workflow-migration:{slug}",
                OnEventHook = ev =>
                {
                    if (ev.Kind == "assistant") assistantText.Add(ev.Text);
                    var progressCode = ev.Kind switch
                    {
                        "assistant" or "result" => "verifying",
                        "command" or "tool" or "tool_use" => "configuring",
                        _ => "applying",
                    };
                    _jobs.AddOrUpdate(jobId,
                        _ => new WorkflowMigrationJob(jobId, slug, "running", plan, null, runId,
                            ProgressCode: progressCode, StartedAt: startedAt, LastActivityAt: ev.At),
                        (_, current) => current with
                        {
                            ProgressCode = progressCode,
                            LastActivityAt = ev.At,
                        });
                },
            }, CancellationToken.None);

            if (!isProjectOnboarding && completedTickets.Count > 0)
            {
                var repaired = await PreserveCompletedTicketsAsync(slug, completedTickets);
                if (repaired > 0)
                    assistantText.Add($"KittyClaw preserved the completed state of {repaired} ticket(s) that the migration had reopened.");
            }

            if (run.Status != AgentRunStatus.Completed)
            {
                var events = run.SnapshotBuffer();
                var diagnostic = events.LastOrDefault(ev => ev.Kind is "stderr" or "error" or "rate_limit_event")?.Text;
                var code = events.Any(ev => ev.Kind == "rate_limit_event")
                    ? "provider-rate-limited"
                    : run.Status == AgentRunStatus.Stopped ? "interrupted" : "application-failed";
                throw new WorkflowMigrationPlanningException(code,
                    diagnostic ?? "The migration agent did not complete successfully.");
            }

            if (!isProjectOnboarding)
            {
                var (legacyConfig, _, _) = await automations.LoadAsync(slug);
                EnsureNoLegacyAutomations(legacyConfig);
                if (extendExistingWorkflow)
                    EnsureExistingWorkflowWasExtended(existingPipelines, existingColumns,
                        await pipelines.ListAsync(slug), await ListAllColumnsAsync(slug));
            }

            _jobs[jobId] = _jobs[jobId] with
            {
                Status = "completed",
                ProgressCode = "completed",
                LastActivityAt = DateTime.UtcNow,
                Result = string.Join("\n", assistantText)
                    .Replace("[assistant] ", "", StringComparison.Ordinal)
                    .Trim(),
            };
        }
        catch (Exception ex)
        {
            var code = ex is WorkflowMigrationPlanningException known ? known.Code : "application-failed";
            logger?.LogError(ex, "Workflow migration application job {JobId} failed for project {ProjectSlug} (code: {ErrorCode})",
                jobId, slug, code);
            _jobs[jobId] = _jobs[jobId] with
            {
                Status = "failed",
                Error = ex.Message,
                ErrorCode = code,
                ProgressCode = "failed",
                LastActivityAt = DateTime.UtcNow,
            };
        }
        finally
        {
            lock (_applicationSync)
            {
                if (_activeApplications.TryGetValue(slug, out var activeId) && activeId == jobId)
                    _activeApplications.Remove(slug);
            }
        }
    }

    private async Task<List<BoardColumn>> ListAllColumnsAsync(string slug)
    {
        var result = new List<BoardColumn>();
        foreach (var pipeline in await pipelines.ListAsync(slug))
            result.AddRange(await columns.ListColumnsAsync(slug, pipeline.Id));
        return result;
    }

    internal static List<CompletedTicketSnapshot> CaptureCompletedTickets(
        IReadOnlyCollection<BoardColumn> columnRows,
        IReadOnlyCollection<TicketSummary> ticketRows)
    {
        var byId = columnRows.ToDictionary(column => column.Id);
        var result = new List<CompletedTicketSnapshot>();
        foreach (var ticket in ticketRows)
        {
            ColumnRole? role = null;
            if (ticket.ColumnId is int columnId)
            {
                if (byId.TryGetValue(columnId, out var column) && IsTerminalRole(column.Role))
                    role = column.Role;
            }
            else
            {
                var match = columnRows.FirstOrDefault(column => IsTerminalRole(column.Role)
                    && column.PipelineId == ticket.PipelineId
                    && string.Equals(column.Name, ticket.Status, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    role = match.Role;
            }
            if (role.HasValue)
                result.Add(new CompletedTicketSnapshot(ticket.Id, ticket.PipelineId, ticket.Status, role.Value));
        }
        return result;
    }

    private static bool IsTerminalRole(ColumnRole role) => role is ColumnRole.Success or ColumnRole.Failure;

    private async Task<int> PreserveCompletedTicketsAsync(string slug,
        IReadOnlyCollection<CompletedTicketSnapshot> completedTickets)
    {
        var currentTickets = (await tickets.ListTicketsAsync(slug)).ToDictionary(ticket => ticket.Id);
        var currentColumns = await ListAllColumnsAsync(slug);
        var columnsById = currentColumns.ToDictionary(column => column.Id);
        var repaired = 0;

        foreach (var snapshot in completedTickets)
        {
            if (!currentTickets.TryGetValue(snapshot.TicketId, out var ticket))
                throw new InvalidOperationException(
                    $"Migration removed completed ticket #{snapshot.TicketId}; completion state cannot be preserved.");

            if (ticket.ColumnId is int currentColumnId
                && columnsById.TryGetValue(currentColumnId, out var currentColumn)
                && currentColumn.Role == snapshot.OriginalRole)
                continue;

            var destination = SelectCompletionDestination(snapshot, ticket, currentColumns)
                ?? throw new InvalidOperationException(
                    $"Migration created no terminal column in which completed ticket #{snapshot.TicketId} can remain closed.");

            await tickets.UpdateTicketAsync(slug, snapshot.TicketId,
                author: "KittyClaw",
                status: destination.Name,
                pipelineId: destination.PipelineId,
                columnId: destination.Id,
                enforceRouting: false);
            repaired++;
        }

        return repaired;
    }

    internal static BoardColumn? SelectCompletionDestination(CompletedTicketSnapshot snapshot,
        TicketSummary ticket, IReadOnlyCollection<BoardColumn> currentColumns)
    {
        var preferredRole = snapshot.OriginalRole;
        return currentColumns
            .Where(column => column.Role == preferredRole && column.PipelineId == ticket.PipelineId)
            .OrderBy(column => column.SortOrder)
            .ThenBy(column => column.Id)
            .FirstOrDefault()
        ?? currentColumns
            .Where(column => column.Role == preferredRole && column.PipelineId == snapshot.PipelineId)
            .OrderBy(column => column.SortOrder)
            .ThenBy(column => column.Id)
            .FirstOrDefault()
        ?? currentColumns
            .Where(column => column.Role == preferredRole)
            .OrderBy(column => column.PipelineId)
            .ThenBy(column => column.SortOrder)
            .ThenBy(column => column.Id)
            .FirstOrDefault()
        // Failure tickets fall back to any Success column when no Failure column exists after migration.
        ?? (preferredRole == ColumnRole.Failure
            ? currentColumns
                .Where(column => column.Role == ColumnRole.Success && column.PipelineId == ticket.PipelineId)
                .OrderBy(column => column.SortOrder)
                .ThenBy(column => column.Id)
                .FirstOrDefault()
              ?? currentColumns
                .Where(column => column.Role == ColumnRole.Success)
                .OrderBy(column => column.PipelineId)
                .ThenBy(column => column.SortOrder)
                .ThenBy(column => column.Id)
                .FirstOrDefault()
            : null);
    }

    private async Task<string> BuildAnalysisPromptAsync(string slug, string language)
    {
        var pipelineRows = await pipelines.ListAsync(slug);
        var columnRows = new List<BoardColumn>();
        foreach (var pipeline in pipelineRows)
            columnRows.AddRange(await columns.ListColumnsAsync(slug, pipeline.Id));
        var ticketRows = await tickets.ListTicketsAsync(slug);
        var (automationConfig, _, _) = await automations.LoadAsync(slug);

        return BuildAnalysisPrompt(pipelineRows, columnRows, ticketRows, automationConfig, language);
    }

    internal static string BuildAnalysisPrompt(IReadOnlyCollection<Pipeline> pipelineRows,
        IReadOnlyCollection<BoardColumn> columnRows, IReadOnlyCollection<TicketSummary> ticketRows,
        AutomationConfig automationConfig, string language)
    {
        var snapshot = new
        {
            pipelines = pipelineRows,
            columns = columnRows,
            ticketGroups = ticketRows
                .GroupBy(ticket => new { ticket.PipelineId, ticket.Status })
                .Select(group => new
                {
                    group.Key.PipelineId,
                    group.Key.Status,
                    count = group.Count(),
                    rootTickets = group.Count(ticket => ticket.ParentId is null),
                    commonLabels = group.SelectMany(ticket => ticket.Labels.Select(label => label.Name))
                        .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(labels => labels.Count())
                        .ThenBy(labels => labels.Key)
                        .Take(12)
                        .Select(labels => new { name = labels.Key, count = labels.Count() }),
                    recentExamples = group.OrderByDescending(ticket => ticket.UpdatedAt).Take(20).Select(ticket => new
                    {
                        ticket.Id,
                        ticket.Title,
                        description = Truncate(ticket.Description, 180),
                        ticket.ParentId,
                        labels = ticket.Labels.Select(label => label.Name).Take(8),
                    }),
                })
                .OrderBy(group => group.PipelineId)
                .ThenBy(group => group.Status),
            legacyAutomations = automationConfig.Automations.Select(automation => new
            {
                automation.Id,
                automation.Name,
                automation.Enabled,
                trigger = SummarizeTrigger(automation.Trigger),
                conditions = automation.Conditions.Select(SummarizeCondition),
                actions = automation.Actions.Select(SummarizeAction),
            }),
        };
        var migrationMode = pipelineRows.Count > 0 && automationConfig.Automations.Count > 0
            ? "completeExistingPipelines"
            : "designWorkflow";
        return $"Analyse this current project snapshot and propose the migration plan. Write every user-facing value in language '{language}'.\n" +
               $"Migration mode: {migrationMode}. When the mode is completeExistingPipelines, preserve every existing pipeline and column; propose only the missing processors, action chains, routes, skills, schedules, guidance, or strictly necessary columns that complete them. Never propose replacement pipelines or a regenerated workflow.\n\n" +
               JsonSerializer.Serialize(snapshot, Json);
    }

    private static object SummarizeTrigger(TriggerSpec trigger) => trigger switch
    {
        IntervalTriggerSpec value => new { type = "interval", value.Cron, value.Seconds },
        TicketInColumnTriggerSpec value => new { type = "ticketInColumn", value.Columns, value.AssigneeSlug },
        StatusChangeTriggerSpec value => new { type = "statusChange", value.From, value.To },
        SubTicketStatusTriggerSpec value => new { type = "subTicketStatus", value.ParentColumn },
        BoardIdleTriggerSpec value => new { type = "boardIdle", value.IdleColumns },
        AgentInactivityTriggerSpec value => new { type = "agentInactivity", value.MinutesIdle },
        TicketCommentAddedTriggerSpec value => new { type = "ticketCommentAdded", value.Authors },
        GitCommitTriggerSpec => new { type = "gitCommit" },
        _ => new { type = trigger.UiTypeKey },
    };

    private static object SummarizeCondition(ConditionSpec condition) => new
    {
        type = condition.UiTypeKey,
        condition.Negate,
        details = Truncate(JsonSerializer.Serialize(condition, condition.GetType(), Json), 500),
    };

    private static object SummarizeAction(ActionSpec action) => action switch
    {
        RunAgentActionSpec value => new { type = "runAgent", value.Agent, value.Model, value.MaxTurns, context = Truncate(value.Context ?? "", 400) },
        MoveTicketStatusActionSpec value => new { type = "moveTicketStatus", value.To },
        SetLabelsActionSpec value => new { type = "setLabels", value.Add, value.Remove },
        AssignTicketActionSpec value => new { type = "assignTicket", value.Slug },
        AddCommentActionSpec value => new { type = "addComment", value.Author, content = Truncate(value.Content, 220) },
        CommitAgentMemoryActionSpec value => new { type = "commitAgentMemory", value.Agent },
        ConsolidateAgentMemoryActionSpec value => new { type = "consolidateAgentMemory", value.Agent, value.Model },
        ExecutePowerShellActionSpec value => new { type = "executePowerShell", value.ScriptFile, script = Truncate(value.Script, 300), value.Arguments },
        CreateTicketActionSpec value => new { type = "createTicket", value.Title, description = Truncate(value.Description, 220), value.Status, value.AssignedTo, value.Labels },
        HttpRequestActionSpec value => new { type = "httpRequest", value.Method, value.Url, body = Truncate(value.Body, 220) },
        _ => new { type = action.UiTypeKey },
    };

    private static string BuildRefinementPrompt(WorkflowMigrationPlan plan, string instruction,
        (string Phase, int? PipelineIndex) refinement, string language) =>
        $"""
        Refine the current migration plan according to the user's request. Return the complete updated plan.
        Current wizard phase: {refinement.Phase}
        Current pipeline index: {refinement.PipelineIndex?.ToString() ?? "none"}
        Write every user-facing value in language: {language}
        User request: {instruction}

        Current plan:
        {JsonSerializer.Serialize(plan, Json)}
        """;

    internal static string BuildWorkspaceAnalysisPrompt(string workspace, string? brief, string language, ILogger? logger = null)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".agents", "node_modules", "bin", "obj", ".next", "dist", "build", ".venv", "vendor",
        };
        var files = new List<string>();
        var manifests = new List<object>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(workspace, path);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ignored.Contains))
                    continue;
                files.Add(relative.Replace('\\', '/'));
                var name = Path.GetFileName(path);
                if (name.Equals("README.md", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase))
                {
                    try { manifests.Add(new { path = relative, content = Truncate(File.ReadAllText(path), 6_000) }); }
                    catch (Exception ex) { logger?.LogDebug(ex, "Skipping manifest {Path} — file could not be read", relative); }
                }
                if (files.Count >= 1_500) break;
            }
        }
        catch (Exception ex) { logger?.LogWarning(ex, "Workspace file scan failed or incomplete for {Workspace}", workspace); }

        var snapshot = new
        {
            ownerBrief = brief,
            fileCount = files.Count,
            extensions = files.GroupBy(Path.GetExtension, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count()).Take(20)
                .Select(group => new { extension = string.IsNullOrEmpty(group.Key) ? "(none)" : group.Key, count = group.Count() }),
            representativeFiles = files.Take(300),
            manifests,
        };
        return $"Analyse this new project's workspace and owner brief, then propose the pipelines it needs. Write every user-facing value in language '{language}'.\n\n"
               + JsonSerializer.Serialize(snapshot, Json);
    }

    internal static WorkflowMigrationPlan ParsePlan(string text)
    {
        const string prefix = "[assistant] ";
        text = text.Replace(prefix, "", StringComparison.Ordinal);
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("The planning agent returned no structured plan.");
        return JsonSerializer.Deserialize<WorkflowMigrationPlan>(text[start..(end + 1)], Json)
            ?? throw new InvalidOperationException("The planning agent returned an empty plan.");
    }

    private static void Validate(WorkflowMigrationPlan plan)
    {
        if (plan.Pipelines.Count == 0)
            throw new InvalidOperationException("The proposed plan contains no pipeline.");
        if (plan.Pipelines.Any(pipeline => string.IsNullOrWhiteSpace(pipeline.Name) || pipeline.Columns.Count == 0))
            throw new InvalidOperationException("Every proposed pipeline must have a name and at least one column.");
        if (plan.Pipelines.Select(p => p.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Pipelines.Count)
            throw new InvalidOperationException("Proposed pipeline names must be unique.");
    }

    internal static void EnsureNoLegacyAutomations(AutomationConfig config)
    {
        if (config.Automations.Count == 0) return;

        var ids = string.Join(", ", config.Automations.Select(automation => automation.Id));
        throw new WorkflowMigrationPlanningException(
            "legacy-automations-remain",
            $"Migration incomplete: legacy automations remain ({ids}). Convert or explicitly retire every legacy behavior, then remove its automation definition.");
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    internal static void EnsureExistingWorkflowWasExtended(
        IReadOnlyCollection<Pipeline> originalPipelines,
        IReadOnlyCollection<BoardColumn> originalColumns,
        IReadOnlyCollection<Pipeline> currentPipelines,
        IReadOnlyCollection<BoardColumn> currentColumns)
    {
        var originalPipelineIds = originalPipelines.Select(pipeline => pipeline.Id).ToHashSet();
        var currentPipelineIds = currentPipelines.Select(pipeline => pipeline.Id).ToHashSet();
        if (!originalPipelineIds.SetEquals(currentPipelineIds))
            throw new WorkflowMigrationPlanningException(
                "existing-workflow-replaced",
                "Migration replaced the existing pipeline set instead of extending it. Existing pipelines must keep their stable identifiers.");

        var currentPipelinesById = currentPipelines.ToDictionary(pipeline => pipeline.Id);
        var changedPipeline = originalPipelines.FirstOrDefault(original =>
        {
            var current = currentPipelinesById[original.Id];
            return current.Name != original.Name
                || current.Slug != original.Slug
                || current.SortOrder != original.SortOrder
                || current.IsDefault != original.IsDefault;
        });
        if (changedPipeline is not null)
            throw new WorkflowMigrationPlanningException(
                "existing-workflow-replaced",
                $"Migration changed existing pipeline '{changedPipeline.Name}' (#{changedPipeline.Id}) instead of extending it in place.");

        var currentColumnsById = currentColumns.ToDictionary(column => column.Id);
        var changedColumn = originalColumns.FirstOrDefault(original =>
            !currentColumnsById.TryGetValue(original.Id, out var current)
            || current.PipelineId != original.PipelineId
            || current.Name != original.Name);
        if (changedColumn is not null)
            throw new WorkflowMigrationPlanningException(
                "existing-workflow-replaced",
                $"Migration removed or recreated existing column '{changedColumn.Name}' (#{changedColumn.Id}) instead of extending its pipeline.");

        foreach (var pipelineId in originalPipelineIds)
        {
            var originalOrder = originalColumns
                .Where(column => column.PipelineId == pipelineId)
                .OrderBy(column => column.SortOrder)
                .ThenBy(column => column.Id)
                .Select(column => column.Id);
            var originalIds = originalColumns
                .Where(column => column.PipelineId == pipelineId)
                .Select(column => column.Id)
                .ToHashSet();
            var currentOriginalOrder = currentColumns
                .Where(column => column.PipelineId == pipelineId && originalIds.Contains(column.Id))
                .OrderBy(column => column.SortOrder)
                .ThenBy(column => column.Id)
                .Select(column => column.Id);
            if (!originalOrder.SequenceEqual(currentOriginalOrder))
                throw new WorkflowMigrationPlanningException(
                    "existing-workflow-replaced",
                    $"Migration reordered existing columns in pipeline #{pipelineId} instead of extending it in place.");
        }
    }

    internal static string BuildApplicationPrompt(WorkflowMigrationPlan plan, bool isProjectOnboarding,
        IReadOnlyCollection<Pipeline>? existingPipelines = null,
        IReadOnlyCollection<BoardColumn>? existingColumns = null,
        AutomationConfig? legacyConfig = null)
    {
        existingPipelines ??= [];
        existingColumns ??= [];
        legacyConfig ??= new AutomationConfig();
        var extendExistingWorkflow = !isProjectOnboarding
            && existingPipelines.Count > 0;
        var existingWorkflow = new
        {
            pipelines = existingPipelines.Select(pipeline => new
            {
                pipeline.Id,
                pipeline.Name,
                pipeline.Slug,
                pipeline.SortOrder,
                pipeline.IsDefault,
            }),
            columns = existingColumns.Select(column => new
            {
                column.Id,
                column.PipelineId,
                column.Name,
                column.Role,
                column.SortOrder,
            }),
            legacyAutomations = legacyConfig.Automations.Select(automation => new
            {
                automation.Id,
                automation.Name,
                automation.Enabled,
                trigger = SummarizeTrigger(automation.Trigger),
                conditions = automation.Conditions.Select(SummarizeCondition),
                actions = automation.Actions.Select(SummarizeAction),
            }),
        };
        var extensionContract = extendExistingWorkflow
            ? $"""

        EXISTING WORKFLOW EXTENSION MODE IS ACTIVE.
        The following workflow structure already exists and is authoritative:
        {JsonSerializer.Serialize(existingWorkflow, Json)}

        Do not create a replacement pipeline and do not delete, recreate, rename, or reorder any
        existing pipeline. Preserve every existing pipeline ID. Preserve every existing column ID
        in its current pipeline; add a column only when no existing column can safely host the
        required behavior. Complete the workflow by adding or updating only missing processors,
        action chains, routes, skills, schedules, and user guidance on the existing structure.
        Treat the approved plan as intent, not permission to regenerate the structure above.

        Migrate legacy automations sequentially. For each automation: inspect its complete trigger,
        conditions, and actions; configure an equivalent action in the most appropriate existing
        pipeline/column; verify that replacement and its routing; then delete only that automation
        definition. Never clear the legacy file in bulk before every replacement is verified. If
        one automation cannot be represented safely, stop and leave that definition in place so
        the migration fails visibly. Inspect current processors, routes, skills, and schedules
        before writing, and reuse equivalent configuration so rerunning the migration is idempotent.
        """
            : "";

        return $"""
        {(isProjectOnboarding ? "Configure the new project's workflow that the owner has just reviewed and explicitly approved in the visual onboarding wizard." : "Apply the workflow migration that the owner has just reviewed and explicitly approved in the visual migration wizard.")}

        Approved plan:
        {JsonSerializer.Serialize(plan, Json)}

        Implement this plan completely using KittyClaw's API and the project's file-backed processor and skill configuration. Preserve stable identifiers whenever an existing pipeline, column, processor, or skill can be reused. Map every existing ticket and child ticket to exactly one pipeline by purpose. A ticket that is already in a Success or Failure column is completed historical work: it must remain in a terminal (Success or Failure) column after migration and must never be routed back into an actionable, waiting, or owner-action stage. Configure processors, action chains, routing, retry/failure destinations, scheduled tasks and project skills needed by the approved flow. Use OwnerAction for every stage requiring a human decision or missing input, and save concrete userGuidance explaining the exact comment or destination-column action that unblocks each Waiting or OwnerAction stage. Do not create In Progress columns for agent execution.

        You are already running inside the confirmed application job. Never call any /workflow-migrations/analyze, /workflow-migrations/refine, or /workflow-migrations/apply endpoint. Apply the plan directly through the granular pipeline, column, ticket, processor, skill, scheduled-task and automation APIs.

        {extensionContract}

        {(isProjectOnboarding ? "Replace the placeholder default workflow with the approved organization. Inspect the workspace again when useful, but do not invent operational requirements that contradict the approved plan." : "Convert or explicitly retire every legacy automation behavior. Translate interval triggers into scheduled tasks that create or process tickets through a column; absorb status-change work into the responsible processor stage; replace owner-comment relaunches with an explicit OwnerAction hand-off and resubmission; and fold repository-event duties into the responsible delivery processor or a scheduled ticket when exact event semantics are unnecessary. Do not leave any legacy automation definition, enabled or disabled: remove each definition only after its replacement or intentional retirement is configured and verified. If any behavior cannot be represented safely, fail the migration instead of silently preserving it.")} Verify that all tickets remain accessible, that processors and schedules reload successfully, and summarize every applied change and remaining risk when finished.
        """;
    }

    private sealed class WorkflowMigrationPlanningException(string code, string message) : InvalidOperationException(message)
    {
        public string Code { get; } = code;
    }

    internal sealed record CompletedTicketSnapshot(int TicketId, int PipelineId, string Status, ColumnRole OriginalRole = ColumnRole.Success);

    private const string PlannerInstructions = """
        You are KittyClaw's read-only workflow planning agent. Never modify files, databases,
        tickets, automations, or project state during this planning turn. Infer genuinely distinct
        ticket lifecycles from either an existing workspace or a legacy board that mixed them into
        one set of columns.

        Return only one JSON object, without Markdown fences or commentary, matching this schema:
        {
          "summary": "short explanation of the proposed organization",
          "pipelines": [{
            "name": "user-friendly pipeline name",
            "description": "purpose and routing of this lifecycle",
            "columns": [{
              "name": "user-friendly column name",
              "role": "Normal|Waiting|OwnerAction|Success|Failure",
              "description": "what happens here and what moves the ticket onward",
              "userGuidance": "for Waiting or OwnerAction: concrete Markdown instructions shown on the ticket"
            }]
          }],
          "risks": ["ambiguity or migration risk"]
        }

        Rules: create separate pipelines for genuinely distinct operational goals, actors, routing,
        schedules, or terminal outcomes; do not preserve one mixed pipeline merely because the old
        board shared columns. Do not invent an In Progress column for agent execution. Use
        OwnerAction for human decisions or missing input, Waiting only for non-human pauses, and
        include explicit Success and Failure destinations. Every Waiting or OwnerAction column must
        have userGuidance that says whether to comment, which exact column validates, which exact
        column refuses, or why no manual action is required. Keep the plan concise and user-facing.
        """;

    private const string ApplicationInstructions = """
        You are KittyClaw's workflow migration agent. The owner has explicitly approved the plan
        provided in the task. Apply it completely and safely inside the current KittyClaw project.
        Consult http://localhost:5230/api/docs before using the API. Prefer stable identifiers and
        file-backed workflow, processor, prompt, memory and skill configuration. Keep the existing
        board usable throughout the migration. You are already the application job: never invoke
        any workflow-migrations endpoint or launch another migration agent. Use only the granular
        project APIs. When pipelines already exist, extend them in place and preserve their stable
        IDs; never regenerate the workflow. Convert legacy behaviors sequentially, verify each
        replacement, then remove only that legacy automation definition. A successful migration must leave zero legacy
        automations, including disabled definitions. If that cannot be achieved safely, report failure
        instead of claiming completion. Verify API reloads, ticket accessibility, routing and scheduled
        work before reporting completion. End with a concise user-facing summary of changes and risks.
        """;
}
