using KittyClaw.Core.Models;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapApprovals(RouteGroupBuilder api)
    {
        var approvals = api.MapGroup("/projects/{slug}/approvals").WithTags("Approvals");

        approvals.MapPost("/requests", async (string slug, ApprovalRequestInput input, ApprovalWorkflowService workflow) =>
            await MapWrite(() => workflow.RegisterRequestAsync(slug, input)))
            .Produces<ApprovalRequestRecord>().ProducesProblem(StatusCodes.Status400BadRequest);

        approvals.MapGet("/requests", async (string slug, string? runId, int? ticketId, string? provider, ApprovalRegistryService registry) =>
            Results.Ok(await registry.QueryRequestsAsync(slug, new(runId, ticketId, provider))))
            .Produces<IReadOnlyList<ApprovalRequestRecord>>();

        approvals.MapPost("/decisions", async (string slug, ApprovalDecisionInput input, ApprovalWorkflowService workflow) =>
            await MapWrite(() => workflow.DecideAsync(slug, input)))
            .Produces<ApprovalDecisionRecord>().ProducesProblem(StatusCodes.Status400BadRequest);

        approvals.MapGet("/decisions", async (string slug, string? requestId, ApprovalRegistryService registry) =>
            Results.Ok(await registry.QueryDecisionsAsync(slug, requestId)))
            .Produces<IReadOnlyList<ApprovalDecisionRecord>>();

        approvals.MapPost("/receipts", async (string slug, ApprovalReceiptInput input, ApprovalRegistryService registry) =>
            await MapWrite(() => registry.AddReceiptAsync(slug, input)))
            .Produces<ApprovalReceiptRecord>().ProducesProblem(StatusCodes.Status400BadRequest);

        approvals.MapPost("/consume-once", async (string slug, ApprovalReceiptInput input, ApprovalRegistryService registry) =>
            await MapWrite(() => registry.ConsumeOnceAsync(slug, input)))
            .Produces<ApprovalReceiptRecord>().ProducesProblem(StatusCodes.Status400BadRequest);

        approvals.MapGet("/receipts", async (string slug, string? requestId, ApprovalRegistryService registry) =>
            Results.Ok(await registry.QueryReceiptsAsync(slug, requestId)))
            .Produces<IReadOnlyList<ApprovalReceiptRecord>>();
    }

    private static async Task<IResult> MapWrite<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        { return Results.BadRequest(new { error = "Approval record conflicts with existing immutable history." }); }
    }
}
