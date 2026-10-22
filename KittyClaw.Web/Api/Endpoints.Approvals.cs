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

        approvals.MapGet("/requests", HandleListApprovalRequests)
            .Produces<IReadOnlyList<ApprovalRequestRecord>>();

        approvals.MapPost("/decisions", HandleDecideApproval)
            .Produces<ApprovalDecisionRecord>().ProducesProblem(StatusCodes.Status400BadRequest);

        approvals.MapGet("/decisions", HandleListApprovalDecisions)
            .Produces<IReadOnlyList<ApprovalDecisionRecord>>();

        approvals.MapPost("/receipts", async (string slug, ApprovalReceiptInput input, ApprovalRegistryService registry) =>
            await MapWrite(() => registry.AddReceiptAsync(slug, input)))
            .Produces<ApprovalReceiptRecord>().ProducesProblem(StatusCodes.Status400BadRequest);

        approvals.MapPost("/consume-once", async (string slug, ApprovalReceiptInput input, ApprovalRegistryService registry) =>
            await MapWrite(() => registry.ConsumeOnceAsync(slug, input)))
            .Produces<ApprovalReceiptRecord>().ProducesProblem(StatusCodes.Status400BadRequest);

        approvals.MapGet("/receipts", HandleListApprovalReceipts)
            .Produces<IReadOnlyList<ApprovalReceiptRecord>>();

        // Pre-effect hook callback: the provider-native hook posts its raw payload here BEFORE the
        // tool effect runs and blocks on the verdict. Never throws — the gate answers deny on any
        // internal failure so the boundary fails closed rather than open.
        approvals.MapPost("/gate", async (string slug, string runId, bool? finalize, HttpRequest request,
            RuntimeBoundaryGateService gate) =>
        {
            using var reader = new StreamReader(request.Body);
            var payload = await reader.ReadToEndAsync();
            return Results.Ok(await gate.EvaluateHookAsync(slug, runId, payload, finalize ?? false));
        }).Produces<RuntimeBoundaryGateVerdict>();

        // Enforceability source shared by UI claims and dispatch policy: which provider × boundary
        // pairs are actually intercepted pre-effect, and why the others are excluded.
        api.MapGet("/runtime-enforcement/capabilities",
            HandleGetRuntimeCapabilities)
            .WithTags("Approvals")
            .Produces<IReadOnlyList<KittyClaw.Core.Automation.RuntimeEnforcementCapability>>();
    }

    private static async Task<IResult> MapWrite<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        { return Results.BadRequest(new { error = "Approval record conflicts with existing immutable history." }); }
    }

    // Shared application handlers used by REST endpoints and MCP tools.
    internal static async Task<IResult> HandleListApprovalRequests(string slug, string? runId, int? ticketId, string? provider, ApprovalRegistryService registry)
    {
        return Results.Ok(await registry.QueryRequestsAsync(slug, new(runId, ticketId, provider)));
    }

    internal static async Task<IResult> HandleListApprovalDecisions(string slug, string? requestId, ApprovalRegistryService registry)
    {
        return Results.Ok(await registry.QueryDecisionsAsync(slug, requestId));
    }

    internal static async Task<IResult> HandleListApprovalReceipts(string slug, string? requestId, ApprovalRegistryService registry)
    {
        return Results.Ok(await registry.QueryReceiptsAsync(slug, requestId));
    }

    internal static async Task<IResult> HandleDecideApproval(string slug, ApprovalDecisionInput input, ApprovalWorkflowService workflow)
    {
        return await MapWrite(() => workflow.DecideAsync(slug, input));
    }

    internal static IResult HandleGetRuntimeCapabilities()
    {
        return Results.Ok(KittyClaw.Core.Automation.RuntimeEnforcementCapabilities.Catalogue);
    }
}
