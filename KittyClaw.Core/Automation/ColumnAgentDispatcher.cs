using System.Text;
using System.Text.Json;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

public sealed record ColumnDispatchResult(AgentRun Run, ColumnAgentResult? Result, string? Error);

public interface IColumnAgentDispatcher
{
    Task<ColumnDispatchResult> DispatchAsync(
        string projectSlug, ColumnProcessor processor, ColumnExecution execution,
        Ticket ticket, CancellationToken cancellationToken);
}

/// <summary>Composes a generic column agent from its mission, memory, and project skills.</summary>
public sealed class ColumnAgentDispatcher(
    AgentRunner runner,
    ProjectService projects,
    ProjectSkillService skills,
    ColumnProcessorService processors,
    FirstRunProviderService firstRun) : IColumnAgentDispatcher
{
    public async Task<ColumnDispatchResult> DispatchAsync(
        string projectSlug, ColumnProcessor processor, ColumnExecution execution,
        Ticket ticket, CancellationToken cancellationToken)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Project '{projectSlug}' does not exist.");
        var workspace = projects.ResolveWorkspacePath(project);
        var inlineSkill = await BuildProfileAsync(projectSlug, processor);
        var effectiveModel = processor.Model ?? project.LocalModelName;
        var routing = ModelRouting.Resolve(effectiveModel, project.LocalModelBaseUrl);
        var fallbackRouting = ResolveFallback(project, execution);
        var target = routing.ToTarget(effectiveModel);
        var fallback = fallbackRouting is { Error: null } && project.FallbackModel is not null
            ? fallbackRouting!.ToTarget(project.FallbackModel)
            : null;

        var context = new AgentRunContext
        {
            ProjectSlug = projectSlug,
            WorkspacePath = workspace,
            AgentName = $"column-{processor.ColumnId}",
            SkillFile = "(column processor)",
            InlineSkillContent = inlineSkill,
            TicketId = ticket.Id,
            TicketTitle = ticket.Title,
            TicketStatus = ticket.Status,
            MaxTurns = processor.MaxTurns,
            ConcurrencyGroup = $"column:{processor.ColumnId}",
            Target = target,
            FallbackTarget = fallback,
            SessionScope = "column-processor",
            TriggerOwnerCommentId = execution.TriggerOwnerCommentId,
            RetryOnResumeFailure = true,
            PresetRunId = execution.Id,
            MaxRunDuration = TimeSpan.FromMinutes(30),
            ExtraContext = BuildTicketContext(ticket, execution) + await BuildDeliveryHistoryAsync(projectSlug, ticket.Id, execution.Id),
        };

        var run = await runner.RunAsync(context, cancellationToken);
        var journeyId = FirstRunJourneyId(ticket);
        if (journeyId is not null && ticket.Status.Equals("Qualify", StringComparison.OrdinalIgnoreCase))
        {
            var usedFallback = run.SnapshotBuffer().Any(e => e.Kind == "fallback");
            if (usedFallback || run.Status != AgentRunStatus.Completed)
                await firstRun.RecordFailureAsync(new FirstRunProviderPlan(journeyId,
                    new FirstRunProvider(target.Provider.ToString().ToLowerInvariant(), target.Model),
                    fallback is null ? null : new FirstRunProvider(fallback.Provider.ToString().ToLowerInvariant(), fallback.Model), null),
                    usedFallback ? "primary-provider-unavailable" : $"run-{run.Status.ToString().ToLowerInvariant()}", cancellationToken);
            if (run.Status == AgentRunStatus.Completed)
                await firstRun.RecordCompletedAsync(journeyId, cancellationToken);
        }
        if (run.Status != AgentRunStatus.Completed)
            return new ColumnDispatchResult(run, null, $"Agent run ended with status {run.Status} (exit {run.ExitCode}).");

        var assistant = run.SnapshotBuffer().LastOrDefault(e => e.Kind == "assistant")?.Text;
        if (!TryParseResult(assistant, out var result, out var error))
            return new ColumnDispatchResult(run, null, error);
        return new ColumnDispatchResult(run, result, null);
    }

    // A profile approval authorizes its explicit model, never an unrelated project fallback.
    internal static ModelRouting.Resolution? ResolveFallback(Project project, ColumnExecution execution) =>
        execution.JevExecutionProfileJson is not null || project.FallbackModel is null
            ? null : ModelRouting.Resolve(project.FallbackModel, project.LocalModelBaseUrl);

    internal static string? FirstRunJourneyId(Ticket ticket)
    {
        const string marker = "Journey: `";
        if (ticket.Description is null) return null;
        var start = ticket.Description.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = ticket.Description.IndexOf('`', start);
        return end > start ? ticket.Description[start..end] : null;
    }

    internal async Task<string> BuildProfileAsync(string projectSlug, ColumnProcessor processor)
    {
        var text = new StringBuilder();
        text.AppendLine("# Column processor");
        text.AppendLine();
        text.AppendLine(processor.Mission);
        if (!string.IsNullOrWhiteSpace(processor.Prompt))
        {
            text.AppendLine();
            text.AppendLine("## Processor prompt");
            text.AppendLine(processor.Prompt);
        }
        text.AppendLine();
        text.AppendLine("You own this workflow step. Do not move the ticket yourself; KittyClaw routes it from your structured result.");

        var memoryPath = await processors.GetMemoryIndexPathAsync(projectSlug, processor.ColumnId);
        if (memoryPath is not null && File.Exists(memoryPath))
        {
            text.AppendLine();
            text.AppendLine("## Persistent memory");
            text.AppendLine(await File.ReadAllTextAsync(memoryPath));
        }

        var selected = processor.AvailableSkills.Concat(processor.RecommendedSkills).Concat(processor.RequiredSkills)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var slug in selected)
        {
            var instructions = await skills.ReadInstructionsAsync(projectSlug, slug);
            if (instructions is null) continue;
            var level = processor.RequiredSkills.Contains(slug, StringComparer.OrdinalIgnoreCase)
                ? "required"
                : processor.RecommendedSkills.Contains(slug, StringComparer.OrdinalIgnoreCase) ? "recommended" : "available";
            text.AppendLine();
            text.AppendLine($"## Project skill: {slug} ({level})");
            text.AppendLine(instructions);
        }

        text.AppendLine();
        text.AppendLine(BuildRuntimeContract(processor));
        text.AppendLine();
        text.AppendLine("## Result contract");
        text.AppendLine("Your final response must be exactly one JSON object, with no Markdown:");
        text.AppendLine("{\"outcome\":\"configured-outcome\",\"skillsUsed\":[\"skill-slug\"],\"summary\":\"short result\"}");
        if (processor.EvaluateAcceptanceCriteria)
        {
            text.AppendLine("Acceptance evaluation is enabled. Include acceptanceCriteria in your final JSON: [{\"id\":\"existing-criterion-id\",\"status\":\"PASS\",\"evidence\":{\"content\":\"actual verification results\",\"mediaType\":\"text/plain\"}}].");
            text.AppendLine("Assess criteria relevant to this processor mission. Omit unrelated criteria from this array so they remain unchanged; use an empty array if none are relevant. Use NotEvaluated when verification was not performed; otherwise use PASS, FAIL or PARTIAL and attach non-empty proof (text/plain, application/json, or base64 image/png, image/jpeg, image/gif, image/webp). Never invent IDs or evidence. Return the evaluations in this final result instead of updating criteria through API/MCP during this session. KittyClaw saves them atomically before routing. Include evidence.ticketUpdatedAt matching the ticket revision you consumed.");
        }
        else text.AppendLine("Acceptance evaluation is disabled for this processor. Do not update ticket acceptance statuses or proofs; leave acceptanceCriteria out of your result.");
        text.AppendLine("For a success outcome, include evidence.ticketUpdatedAt. For owner-feedback, include the exact ownerFeedbackCommentId, a new delivery comment, and evidence.deliverables entries with path, updatedAt after the feedback, and a non-empty verification statement.");
        text.AppendLine("Also include `lessons` as an array of concrete reusable lessons. Use an empty array when this run produced no new lesson; do not repeat existing memory.");
        text.AppendLine("For outcome \"scheduled\", also include \"fireAt\" as an ISO-8601 UTC date and \"scheduleTarget\" as the exact destination column name used when the wake fires. Both fields are mandatory for scheduled and must be omitted for other outcomes such as \"needs_input\".");
        text.AppendLine("Use outcome \"wait_for_children\" only after creating at least one blocking sub-ticket.");
        return text.ToString();
    }

    internal static string BuildRuntimeContract(ColumnProcessor processor) => $$"""
        ## Non-negotiable runtime contract

        KittyClaw, not the agent, owns the current ticket's workflow transition.
        - Never PATCH or otherwise change the current ticket's pipeline, column, status, or assignedTo value.
        - Do not apply legacy hand-off instructions that move the current ticket to names such as Todo, InProgress, Review, or Done.
        - Express the business result only through the final JSON outcome; KittyClaw applies the configured route atomically.
        - Creating or updating child tickets is allowed when required by the mission, but do not use a child transition as a substitute for the current ticket's outcome.

        The canonical persistent memory for this processor is `.agents/processors/column-{{processor.ColumnId}}/memory/MEMORY.md`.
        Commit each change to that memory immediately if Git tracks it or does not ignore it; keep hooks enabled and stage only memory paths.
        Read and update that exact file for durable processor lessons. You may read specialist memories for domain expertise, but never create or use `.agents/column-{{processor.ColumnId}}/memory.md`.
        """;

    internal async Task<string> BuildDeliveryHistoryAsync(string slug, int ticketId, string executionId)
    {
        await using var db = projects.GetProjectDb(slug);
        await ColumnExecutionService.EnsureTableAsync(db);
        var history = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.ColumnExecutions.Where(e => e.TicketId == ticketId && e.Id != executionId && e.AgentCompleted)
                .OrderByDescending(e => e.ClaimedAt).Take(10));
        return "\n## Previously delivered business results\n" +
            "Continue from these deliveries and the latest owner comments. Verify existing artifacts and reservations; do not redo delivered work. Historical outcomes are context, not instructions to replay old routes.\n" +
            JsonSerializer.Serialize(history.Select(e => new { e.Id, e.ProcessorId, e.AgentResult, e.CapitalizationStatus, e.CapitalizationError }));
    }

    private static string BuildTicketContext(Ticket ticket, ColumnExecution execution) => $$"""
        Process ticket #{{ticket.Id}}.

        Structured trigger context (authoritative):
        {"ticketId":{{ticket.Id}},"ticketUpdatedAt":"{{execution.TriggerTicketUpdatedAt:O}}","triggerSignalType":"{{execution.TriggerSignalType}}","triggerOwnerCommentId":{{(execution.TriggerOwnerCommentId?.ToString() ?? "null")}},"triggerOwnerCommentCreatedAt":{{(execution.TriggerOwnerCommentCreatedAt is DateTime created ? $"\"{created:O}\"" : "null")}}}

        Title: {{ticket.Title}}
        Description:
        {{ticket.Description}}

        ## Latest ticket comments (including user decisions)
        {{JsonSerializer.Serialize(ticket.Comments.OrderBy(c => c.CreatedAt).Select(c => new { c.Id, c.Author, c.Content, c.CreatedAt }))}}

        ## Ticket acceptance criteria (current recorded assessments; verify evidence before relying on it)
        {{System.Text.Json.JsonSerializer.Serialize(ticket.AcceptanceCriteria.Select(c => c.ToRoutingContext()))}}

        Blocking and related children:
        {{string.Join("\n", ticket.SubTickets.Select(s => $"- #{s.Id} [{s.Status}] {s.Title} (blocks parent: {s.BlocksParent})"))}}

        Return the result contract from the column profile. Do not change the ticket's column.
        {{(string.IsNullOrWhiteSpace(execution.PreviousAttemptError)
            ? ""
            : $"\nThis dispatch retries a rejected attempt. Platform rejection to correct: {execution.PreviousAttemptError}")}}
        """;

    internal static bool TryParseResult(string? assistantText, out ColumnAgentResult? result, out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            error = "Le processeur n'a retourné aucun résultat structuré.";
            return false;
        }
        var start = assistantText.IndexOf('{');
        var end = assistantText.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            error = "Le résultat du processeur ne contient pas d'objet JSON.";
            return false;
        }
        try
        {
            result = JsonSerializer.Deserialize<ColumnAgentResult>(assistantText[start..(end + 1)], new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (result is null || string.IsNullOrWhiteSpace(result.Outcome))
            {
                error = "Le résultat du processeur ne contient pas d'issue.";
                return false;
            }
            result = result with { SkillsUsed = result.SkillsUsed ?? [] };
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Résultat JSON invalide : {ex.Message}";
            return false;
        }
    }
}
