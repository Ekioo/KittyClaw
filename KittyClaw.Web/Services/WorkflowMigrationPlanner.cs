using System.Collections.Concurrent;
using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;

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
    DateTime? LastActivityAt = null);

/// <summary>
/// Runs read-only, stateless planning turns and retains their structured result long enough for
/// the migration wizard to poll it. Applying the plan is deliberately left to a separately
/// confirmed interactive agent turn.
/// </summary>
public sealed class WorkflowMigrationPlanner(
    ProjectService projects,
    PipelineService pipelines,
    ColumnService columns,
    TicketService tickets,
    AutomationStore automations,
    AgentRunner runner,
    LocalizationService localization)
{
    private readonly ConcurrentDictionary<string, WorkflowMigrationJob> _jobs = [];
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
                    ? BuildWorkspaceAnalysisPrompt(workspace, brief, localization.Lang)
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
        return $"Analyse this current project snapshot and propose the migration plan. Write every user-facing value in language '{language}'.\n\n" +
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

    internal static string BuildWorkspaceAnalysisPrompt(string workspace, string? brief, string language)
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
                    catch { }
                }
                if (files.Count >= 1_500) break;
            }
        }
        catch { }

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

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    private sealed class WorkflowMigrationPlanningException(string code, string message) : InvalidOperationException(message)
    {
        public string Code { get; } = code;
    }

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
              "description": "what happens here and what moves the ticket onward"
            }]
          }],
          "risks": ["ambiguity or migration risk"]
        }

        Rules: create separate pipelines for genuinely distinct operational goals, actors, routing,
        schedules, or terminal outcomes; do not preserve one mixed pipeline merely because the old
        board shared columns. Do not invent an In Progress column for agent execution. Use
        OwnerAction for human decisions or missing input, Waiting only for non-human pauses, and
        include explicit Success and Failure destinations. Keep the plan concise and user-facing.
        """;
}
