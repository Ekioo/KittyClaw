using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapTicketQuestions(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/tickets/{id}/questions", HandleListTicketQuestions)
            .Produces<List<TicketQuestion>>().WithTags("Tickets");
        api.MapPost("/projects/{slug}/tickets/{id}/questions", HandleAskTicketQuestion)
            .Produces<TicketQuestion>().WithTags("Tickets");
        api.MapPost("/projects/{slug}/tickets/{id}/questions/{questionId}/answer", HandleAnswerTicketQuestion)
            .Produces<TicketQuestion>().WithTags("Tickets");
    }

    internal static async Task<IResult> HandleListTicketQuestions(string slug, int id, ColumnExecutionService executions,
        TicketService tickets)
    {
        if (await tickets.GetTicketAsync(slug, id) is null) return Results.NotFound();
        return Results.Ok(await executions.ListQuestionsAsync(slug, id));
    }

    internal static async Task<IResult> HandleAskTicketQuestion(string slug, int id, AskTicketQuestionRequest request,
        ColumnExecutionService executions, BoardUpdateNotifier notifier)
    {
        try
        {
            var question = await executions.AskQuestionAsync(slug, id, request);
            notifier.NotifyProjectUpdated(slug);
            return Results.Ok(question);
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    internal static async Task<IResult> HandleAnswerTicketQuestion(string slug, int id, string questionId,
        AnswerTicketQuestionRequest request, ColumnExecutionService executions, ColumnProcessingEngine engine,
        BoardUpdateNotifier notifier)
    {
        try
        {
            var question = await executions.AnswerQuestionAsync(slug, id, questionId, request);
            engine.Signal(slug);
            notifier.NotifyProjectUpdated(slug);
            return Results.Ok(question);
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }
}
