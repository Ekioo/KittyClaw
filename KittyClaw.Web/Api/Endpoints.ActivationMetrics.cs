using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapActivationMetrics(RouteGroupBuilder api)
    {
        api.MapGet("/activation/first-project", async (FirstProjectActivationMetricsService metrics, CancellationToken ct) =>
            Results.Ok(await metrics.GetReportAsync(ct)));
    }
}
