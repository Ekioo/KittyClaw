using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private sealed record BoundaryReviewRequest(bool IsFalsePositive);

    private static void MapBoundaryObservations(RouteGroupBuilder api)
    {
        var observations = api.MapGroup("/projects/{slug}/boundary-observations").WithTags("Boundary observations");

        observations.MapGet("/", async (string slug, string? runId, BoundaryObservationService service) =>
            Results.Ok(await service.QueryAsync(slug, runId)));

        observations.MapGet("/metrics", async (string slug, int? runWindow, BoundaryObservationService service) =>
            Results.Ok(await service.MetricsAsync(slug, runWindow ?? 20)));

        observations.MapPut("/{observationId}/review", async (
            string slug, string observationId, BoundaryReviewRequest request, BoundaryObservationService service) =>
        {
            try
            {
                await service.ReviewAsync(slug, observationId, request.IsFalsePositive);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.NotFound(new { error = ex.Message }); }
        });
    }
}
