using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapAgentAccess(RouteGroupBuilder api)
    {
        // The dedicated tunnel middleware handles these before the browser session gate.
        // Ordinary listeners cannot accidentally expose a second pairing surface.
        api.MapPost("/agent-access/start", (AgentPairRequest request) => Results.Problem(
            "Use the enabled managed HTTPS tunnel to pair an agent.", statusCode: 403))
            .WithTags("Agent access")
            .WithSummary("Request agent access. Display only verification_uri and user_code to the owner; keep device_code private. The verification URI never contains codes or tokens.")
            .Produces<AgentAccessStartResponse>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(429);
        api.MapPost("/agent-access/token", (AgentPollRequest request) => Results.Problem(
            "Use the enabled managed HTTPS tunnel to poll an agent request.", statusCode: 403))
            .WithTags("Agent access")
            .WithSummary("Poll with deviceCode every 5 seconds until approved, denied or expired. The bearer access_token is returned only in the JSON response body.")
            .Produces<AgentAccessTokenResponse>().Produces<AgentAccessTokenResponse>(400).ProducesProblem(403).ProducesProblem(429);
    }
}
