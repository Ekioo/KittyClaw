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
    string? RunId);

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
    AgentRunner runner)
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

    public string StartAnalysis(string projectSlug)
    {
        var id = Guid.NewGuid().ToString("N");
        _jobs[id] = new WorkflowMigrationJob(id, projectSlug, "queued", null, null, null);
        _ = RunAsync(id, projectSlug, null, null, null);
        return id;
    }

    public string StartRefinement(string projectSlug, WorkflowMigrationPlan plan, string instruction,
        string phase, int? pipelineIndex)
    {
        var id = Guid.NewGuid().ToString("N");
        _jobs[id] = new WorkflowMigrationJob(id, projectSlug, "queued", null, null, null);
        _ = RunAsync(id, projectSlug, plan, instruction, (phase, pipelineIndex));
        return id;
    }

    private async Task RunAsync(string jobId, string slug, WorkflowMigrationPlan? currentPlan,
        string? instruction, (string Phase, int? PipelineIndex)? refinement)
    {
        try
        {
            var project = await projects.GetProjectAsync(slug)
                ?? throw new InvalidOperationException($"Project '{slug}' was not found.");
            var workspace = projects.ResolveWorkspacePath(project);
            var prompt = currentPlan is null
                ? await BuildAnalysisPromptAsync(slug)
                : BuildRefinementPrompt(currentPlan, instruction!, refinement!.Value);

            var assistantText = new List<string>();
            var runId = Guid.NewGuid().ToString("N");
            _jobs[jobId] = _jobs[jobId] with { Status = "running", RunId = runId };
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
                },
            }, CancellationToken.None);

            if (run.Status != AgentRunStatus.Completed)
                throw new InvalidOperationException("The planning agent did not complete successfully.");

            var plan = ParsePlan(string.Join("\n", assistantText));
            Validate(plan);
            _jobs[jobId] = _jobs[jobId] with { Status = "completed", Plan = plan };
        }
        catch (Exception ex)
        {
            _jobs[jobId] = _jobs[jobId] with { Status = "failed", Error = ex.Message };
        }
    }

    private async Task<string> BuildAnalysisPromptAsync(string slug)
    {
        var pipelineRows = await pipelines.ListAsync(slug);
        var columnRows = new List<BoardColumn>();
        foreach (var pipeline in pipelineRows)
            columnRows.AddRange(await columns.ListColumnsAsync(slug, pipeline.Id));
        var ticketRows = await tickets.ListTicketsAsync(slug);
        var (automationConfig, _, _) = await automations.LoadAsync(slug);

        var snapshot = new
        {
            pipelines = pipelineRows,
            columns = columnRows,
            tickets = ticketRows.Select(ticket => new
            {
                ticket.Id,
                ticket.Title,
                description = Truncate(ticket.Description, 500),
                ticket.Status,
                ticket.PipelineId,
                ticket.ParentId,
                ticket.BlocksParent,
                labels = ticket.Labels.Select(label => label.Name),
            }),
            legacyAutomations = automationConfig.Automations,
        };
        return "Analyse this current project snapshot and propose the migration plan.\n\n" +
               JsonSerializer.Serialize(snapshot, Json);
    }

    private static string BuildRefinementPrompt(WorkflowMigrationPlan plan, string instruction,
        (string Phase, int? PipelineIndex) refinement) =>
        $"""
        Refine the current migration plan according to the user's request. Return the complete updated plan.
        Current wizard phase: {refinement.Phase}
        Current pipeline index: {refinement.PipelineIndex?.ToString() ?? "none"}
        User request: {instruction}

        Current plan:
        {JsonSerializer.Serialize(plan, Json)}
        """;

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

    private const string PlannerInstructions = """
        You are KittyClaw's read-only workflow migration planner. Never modify files, databases,
        tickets, automations, or project state during this planning turn. Infer genuinely distinct
        ticket lifecycles even when the legacy board mixed them into one set of columns.

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
