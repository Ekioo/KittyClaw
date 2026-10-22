using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public sealed class MinimalWorkflowService(
    string dataDirectory, PipelineService pipelines, ColumnService columns,
    ColumnProcessorService processors, TicketService tickets)
{
    private readonly string _eventDirectory = Path.Combine(dataDirectory, "activation");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MinimalWorkflowResult> EnsureAsync(
        string projectSlug, int firstTicketId, string journeyId, string? model = null,
        Func<CancellationToken, Task>? beforeTicketActivation = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        var stopwatch = Stopwatch.StartNew();
        await AppendEventAsync(new MinimalWorkflowEvent(journeyId, "minimal_workflow_started", DateTimeOffset.UtcNow), ct);
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        var step = "pipeline";
        try
        {
            var pipeline = (await pipelines.ListAsync(projectSlug))
                .FirstOrDefault(p => p.Name.Equals("First success", StringComparison.OrdinalIgnoreCase))
                ?? await pipelines.CreateAsync(projectSlug, "First success");
            step = "columns";
            var qualify = await columns.CreateColumnAsync(projectSlug, "Qualify", "#4a9eff", pipeline.Id);
            var implement = await columns.CreateColumnAsync(projectSlug, "Implement", "#f59e42", pipeline.Id);
            var verify = await columns.CreateColumnAsync(projectSlug, "Verify", "#a78bfa", pipeline.Id);
            var human = await columns.CreateColumnAsync(projectSlug, "Human decision", "#3ecf8e", pipeline.Id, ColumnRole.OwnerAction);

            step = "processors";
            await SaveProcessorAsync(projectSlug, qualify, "Qualifier", "Clarify the ticket and make its acceptance criteria executable.", implement.Id, model);
            await SaveProcessorAsync(projectSlug, implement, "Implementer", "Implement the ticket and provide concrete test evidence.", verify.Id, model);
            await SaveProcessorAsync(projectSlug, verify, "Verifier", "Verify the implementation independently and summarize the evidence for the owner.", human.Id, model);

            stopwatch.Stop();
            await AppendEventAsync(new MinimalWorkflowEvent(journeyId, "minimal_workflow_ready", DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds), ct);
            if (beforeTicketActivation is not null)
                await beforeTicketActivation(ct);

            step = "first-ticket";
            var ticket = await tickets.UpdateTicketAsync(projectSlug, firstTicketId, author: "automation",
                status: qualify.Name, pipelineId: pipeline.Id, columnId: qualify.Id)
                ?? throw new InvalidOperationException($"The first ticket #{firstTicketId} does not exist.");
            return new MinimalWorkflowResult(pipeline.Id, qualify.Id, implement.Id, verify.Id, human.Id, ticket.Id);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await AppendEventAsync(new MinimalWorkflowEvent(journeyId, "minimal_workflow_failed", DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds, step, ex.GetType().Name), ct);
            throw;
        }
        finally { gate.Release(); }
    }

    private Task<ColumnProcessor> SaveProcessorAsync(
        string projectSlug, BoardColumn column, string name, string mission, int targetColumnId, string? model) =>
        processors.SaveAsync(projectSlug, column.Id, name, mission, model, enabled: true, maxTurns: 100,
            availableSkills: [], recommendedSkills: [], requiredSkills: [], maxAttempts: 3,
            retryBackoffSeconds: 60, defaultTargetColumnId: targetColumnId);

    private async Task AppendEventAsync(MinimalWorkflowEvent activationEvent, CancellationToken ct)
    {
        Directory.CreateDirectory(_eventDirectory);
        await File.AppendAllTextAsync(Path.Combine(_eventDirectory, "minimal-workflow-events.jsonl"),
            JsonSerializer.Serialize(activationEvent, JsonOptions) + Environment.NewLine, ct);
    }
}

public sealed record MinimalWorkflowResult(
    int PipelineId, int QualifyColumnId, int ImplementColumnId, int VerifyColumnId,
    int HumanDecisionColumnId, int FirstTicketId);

public sealed record MinimalWorkflowEvent(
    string JourneyId, string Name, DateTimeOffset OccurredAt, long? DurationMilliseconds = null,
    string? FailedStep = null, string? ErrorCode = null);
