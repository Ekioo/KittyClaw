using System.Net;
using System.Text;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

public sealed record ColumnActionResult(bool Succeeded, string? Error = null)
{
    public static ColumnActionResult Success() => new(true);
    public static ColumnActionResult Failure(string error) => new(false, error);
}

/// <summary>
/// Executes the deterministic actions surrounding a column's single implicit agent.
/// Unlike legacy automation chains, every failure is surfaced to the durable column engine.
/// </summary>
public sealed class ColumnActionExecutor(
    ProjectService projects,
    TicketService tickets,
    ILogger<ColumnActionExecutor> logger)
{
    public Task<ColumnActionResult> ExecuteScheduledAsync(
        string projectSlug,
        ColumnScheduledTask task,
        ColumnScheduledTaskRun run,
        BoardColumn column,
        Ticket? ticket,
        ColumnProcessorAction action,
        CancellationToken cancellationToken)
    {
        if (ticket is null && action.Action is SetLabelsActionSpec or AddCommentActionSpec)
            return Task.FromResult(ColumnActionResult.Failure("Cette action requiert un ticket cible."));

        var target = ticket ?? new Ticket
        {
            Id = 0,
            PipelineId = column.PipelineId,
            ColumnId = column.Id,
            Title = "",
            Status = column.Name,
        };
        var processor = new ColumnProcessor
        {
            Id = 0,
            ColumnId = column.Id,
            Name = task.Name,
        };
        var execution = new ColumnExecution
        {
            Id = run.Id,
            ProcessorId = 0,
            TicketId = target.Id,
        };
        return ExecuteAsync(projectSlug, processor, execution, target, action, null, cancellationToken);
    }

    public async Task<ColumnActionResult> ExecuteAsync(
        string projectSlug,
        ColumnProcessor processor,
        ColumnExecution execution,
        Ticket ticket,
        ColumnProcessorAction processorAction,
        ColumnAgentResult? agentResult,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (processorAction.Action)
            {
                case SetLabelsActionSpec labels:
                    if (labels.Add.Count == 0 && labels.Remove.Count == 0)
                        return ColumnActionResult.Failure("L’action de labels ne contient aucune modification.");
                    if (await tickets.PatchTicketLabelsAsync(
                            projectSlug, ticket.Id, labels.Add, labels.Remove, processor.Name) is null)
                        return ColumnActionResult.Failure($"Le ticket #{ticket.Id} n’existe plus.");
                    return ColumnActionResult.Success();

                case AddCommentActionSpec comment:
                    await tickets.AddCommentAsync(
                        projectSlug,
                        ticket.Id,
                        Render(comment.Content, projectSlug, ticket, agentResult),
                        string.IsNullOrWhiteSpace(comment.Author) ? processor.Name : comment.Author);
                    return ColumnActionResult.Success();

                case CreateTicketActionSpec create:
                    return await CreateTicketAsync(projectSlug, processor, ticket, create, agentResult);

                case HttpRequestActionSpec request:
                    return await SendHttpRequestAsync(
                        projectSlug, execution, ticket, processorAction, request, agentResult, cancellationToken);

                case ExecutePowerShellActionSpec script:
                    return await ExecutePowerShellAsync(
                        projectSlug, execution, ticket, processorAction, script, agentResult, cancellationToken);

                default:
                    return ColumnActionResult.Failure(
                        $"Type d’action interdit dans un processeur : {processorAction.Action.GetType().Name}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Column action {ActionId} failed for ticket #{TicketId}", processorAction.Id, ticket.Id);
            return ColumnActionResult.Failure(ex.Message);
        }
    }

    private async Task<ColumnActionResult> CreateTicketAsync(
        string projectSlug,
        ColumnProcessor processor,
        Ticket currentTicket,
        CreateTicketActionSpec spec,
        ColumnAgentResult? agentResult)
    {
        var now = DateTime.Now;
        string Resolve(string value) => Render(
            ActionExecutor.ResolveCreateTicketPlaceholders(value, now), projectSlug, currentTicket, agentResult);
        var title = Resolve(spec.Title);
        if (string.IsNullOrWhiteSpace(title))
            return ColumnActionResult.Failure("Le titre du ticket créé est vide après résolution des variables.");

        if (spec.SkipIfExists)
        {
            var existing = await tickets.ListTicketsAsync(projectSlug);
            if (existing.Any(candidate =>
                    !string.Equals(candidate.Status, "Done", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(candidate.Status, "Terminé", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Title, title, StringComparison.OrdinalIgnoreCase)))
                return ColumnActionResult.Success();
        }

        var priority = Enum.TryParse<TicketPriority>(spec.Priority, true, out var parsed)
            ? parsed : TicketPriority.NiceToHave;
        var created = await tickets.CreateTicketAsync(
            projectSlug,
            title,
            Resolve(spec.Description),
            string.IsNullOrWhiteSpace(spec.CreatedBy) ? processor.Name : spec.CreatedBy,
            spec.Status,
            priority: priority,
            assignedTo: string.IsNullOrWhiteSpace(spec.AssignedTo) ? null : spec.AssignedTo,
            parentId: spec.ParentId,
            pipelineId: currentTicket.PipelineId);
        if (spec.Labels.Count > 0)
            await tickets.PatchTicketLabelsAsync(projectSlug, created.Id, spec.Labels, [], processor.Name);
        return ColumnActionResult.Success();
    }

    private async Task<ColumnActionResult> SendHttpRequestAsync(
        string projectSlug,
        ColumnExecution execution,
        Ticket ticket,
        ColumnProcessorAction action,
        HttpRequestActionSpec spec,
        ColumnAgentResult? agentResult,
        CancellationToken cancellationToken)
    {
        var resolvedUrl = Render(spec.Url, projectSlug, ticket, agentResult);
        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ColumnActionResult.Failure("L’URL de l’action HTTP est invalide ou n’utilise pas HTTP(S).");
        var method = spec.Method.ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            "DELETE" => HttpMethod.Delete,
            _ => null,
        };
        if (method is null) return ColumnActionResult.Failure($"Méthode HTTP non prise en charge : {spec.Method}.");

        using var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrEmpty(spec.Body))
            request.Content = new StringContent(
                Render(spec.Body, projectSlug, ticket, agentResult), Encoding.UTF8, spec.ContentType);
        foreach (var (name, value) in spec.Headers)
        {
            var rendered = Render(value, projectSlug, ticket, agentResult);
            if (!request.Headers.TryAddWithoutValidation(name, rendered))
                request.Content?.Headers.TryAddWithoutValidation(name, rendered);
        }
        if (!request.Headers.Contains("Idempotency-Key"))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", $"kittyclaw-{execution.Id}-{action.Id}");

        var client = spec.AllowLocalTargets ? HttpActionClient.Unguarded : HttpActionClient.Guarded;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, spec.TimeoutSeconds)));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[8192];
            var remaining = HttpActionClient.MaxResponseBytes;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cts.Token);
                if (read == 0) break;
                remaining -= read;
            }
            return response.IsSuccessStatusCode
                ? ColumnActionResult.Success()
                : ColumnActionResult.Failure($"La requête HTTP a retourné {(int)response.StatusCode} ({response.StatusCode}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ColumnActionResult.Failure($"La requête HTTP a dépassé le délai de {spec.TimeoutSeconds} secondes.");
        }
    }

    private async Task<ColumnActionResult> ExecutePowerShellAsync(
        string projectSlug,
        ColumnExecution execution,
        Ticket ticket,
        ColumnProcessorAction action,
        ExecutePowerShellActionSpec spec,
        ColumnAgentResult? agentResult,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Le projet '{projectSlug}' n’existe pas.");
        var workspace = projects.ResolveWorkspacePath(project);
        string Resolve(string value) => Render(value, projectSlug, ticket, agentResult);
        string scriptArgument;
        if (!string.IsNullOrWhiteSpace(spec.ScriptFile))
        {
            var rendered = Resolve(spec.ScriptFile);
            var path = Path.IsPathRooted(rendered) ? rendered : Path.Combine(workspace, rendered);
            scriptArgument = $"-File \"{path}\"";
        }
        else
        {
            var bytes = Encoding.Unicode.GetBytes(Resolve(spec.Script));
            scriptArgument = $"-EncodedCommand {Convert.ToBase64String(bytes)}";
        }
        var arguments = spec.Arguments.Count == 0
            ? ""
            : " " + string.Join(" ", spec.Arguments.Select(argument => $"\"{Resolve(argument)}\""));
        var env = new Dictionary<string, string>(spec.Env, StringComparer.OrdinalIgnoreCase)
        {
            ["KITTYCLAW_ACTION_EXECUTION_ID"] = $"{execution.Id}:{action.Id}",
            ["KITTYCLAW_TICKET_ID"] = ticket.Id.ToString(),
            ["KITTYCLAW_PROJECT_SLUG"] = projectSlug,
        };
        var result = await ProcessRunner.RunAsync(
            ShellResolver.ResolvePowerShell(),
            $"-NonInteractive -NoProfile {scriptArgument}{arguments}",
            workspace,
            TimeSpan.FromSeconds(Math.Max(1, spec.TimeoutSeconds)),
            env,
            cancellationToken);
        if (result.TimedOut)
            return ColumnActionResult.Failure($"Le script PowerShell a dépassé le délai de {spec.TimeoutSeconds} secondes.");
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
            detail = detail.Trim();
            if (detail.Length > 600) detail = detail[..600] + "…";
            return ColumnActionResult.Failure(
                $"Le script PowerShell s’est terminé avec le code {result.ExitCode}. {detail}".Trim());
        }
        return ColumnActionResult.Success();
    }

    private static string Render(
        string value,
        string projectSlug,
        Ticket ticket,
        ColumnAgentResult? result) => (value ?? "")
        .Replace("{ticketId}", ticket.Id == 0 ? "" : ticket.Id.ToString())
        .Replace("{ticketTitle}", ticket.Title)
        .Replace("{ticketStatus}", ticket.Status)
        .Replace("{assignee}", ticket.AssignedTo ?? "")
        .Replace("{slug}", projectSlug)
        .Replace("{outcome}", result?.Outcome ?? "")
        .Replace("{summary}", result?.Summary ?? "");
}
