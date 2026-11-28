using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Services;

internal sealed record AgentApiGrant(bool Valid, string[]? Projects = null, bool Write = false, long ExpiresAt = 0, string? Id = null);
internal sealed record AgentPairRequest(string Name, string[] Projects, bool Write = false);
internal sealed record AgentPollRequest(string DeviceCode);
internal sealed record AgentAccessStartResponse(
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    int Interval);
internal sealed record AgentAccessTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    string[]? Projects, string? Scope, string? Error);

/// <summary>Explicit, fail-closed route capabilities for headless clients. No browser session is issued.</summary>
internal static class AgentApiPolicy
{
    internal static bool Allowed(string method, string path, AgentApiGrant grant)
    {
        if (!grant.Valid || grant.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        if (method == "GET" && path is "/api/projects" or "/api/docs" or "/openapi/v1.json") return true;
        var match = Regex.Match(path, @"^/api/projects/([a-zA-Z0-9_-]{1,80})(.*)$");
        if (!match.Success || grant.Projects?.Contains(match.Groups[1].Value, StringComparer.Ordinal) != true) return false;
        var suffix = match.Groups[2].Value;
        if (method == "GET")
            return Regex.IsMatch(suffix, @"^(|/columns|/labels|/members|/tickets(?:/[0-9]+(?:/(?:activity|evidence|brief))?)?)$");
        if (!grant.Write) return false;
        // Cross-project transfers, provider execution, secrets and configuration are never implicit ticket rights.
        return method switch
        {
            "POST" => Regex.IsMatch(suffix, @"^/tickets(?:/[0-9]+/(?:comments|dependencies|acceptance-criteria|focus))?$"),
            "PATCH" => Regex.IsMatch(suffix, @"^/tickets/[0-9]+(?:/(?:status|schedule|reorder|comments/[0-9]+))?$"),
            "PUT" => Regex.IsMatch(suffix, @"^/tickets/[0-9]+/(?:parent|acceptance-criteria/[a-zA-Z0-9_-]+)$"),
            "DELETE" => Regex.IsMatch(suffix, @"^/tickets/[0-9]+(?:/(?:parent|comments/[0-9]+|dependencies/[0-9]+|acceptance-criteria/[a-zA-Z0-9_-]+))?$"),
            _ => false
        };
    }
}

public sealed class AgentApiAccessService(AccountAssociationService accounts) : IDisposable
{
    // Bound the unauthenticated surface before parsing bodies or contacting the portal.
    private readonly TokenBucketRateLimiter _start = new(new TokenBucketRateLimiterOptions
    { TokenLimit = 10, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromMinutes(1), AutoReplenishment = true });
    private readonly TokenBucketRateLimiter _poll = new(new TokenBucketRateLimiterOptions
    { TokenLimit = 30, TokensPerPeriod = 10, ReplenishmentPeriod = TimeSpan.FromSeconds(5), AutoReplenishment = true });

    internal RateLimitLease Acquire(bool start) => (start ? _start : _poll).AttemptAcquire();
    internal Task<JsonElement> StartAsync(AgentPairRequest request, CancellationToken ct)
        => accounts.AgentAccessAsync<JsonElement>("start", new { name = request.Name, projects = request.Projects, write = request.Write }, ct);
    internal Task<JsonElement> PollAsync(string deviceCode, CancellationToken ct)
        => accounts.AgentAccessAsync<JsonElement>("token", new { deviceCode }, ct);
    internal Task<AgentApiGrant> CheckAsync(string token, CancellationToken ct)
        => accounts.AgentAccessAsync<AgentApiGrant>("check", new { token }, ct);
    public void Dispose() { _start.Dispose(); _poll.Dispose(); }
}

/// <summary>Invoked only after the tunnel origin has established its transport and host boundary.</summary>
internal static class AgentApiAccess
{
    internal static async Task<bool> HandleAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path.Value ?? "";
        var pairing = path is "/api/agent-access/start" or "/api/agent-access/token";
        var bearer = context.Request.Headers.Authorization.ToString();
        if (!pairing && !bearer.StartsWith("Bearer kca_", StringComparison.Ordinal)) return false;
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps || context.Request.Headers.ContainsKey("Origin")
            || context.Request.Headers.ContainsKey("Cookie") || context.Request.Headers.ContainsKey("Sec-Fetch-Site")
            || context.Request.Query.Keys.Any(k => k.Contains("token", StringComparison.OrdinalIgnoreCase) || k.Contains("code", StringComparison.OrdinalIgnoreCase)))
        { context.Response.StatusCode = 403; return true; }
        var service = context.RequestServices.GetRequiredService<AgentApiAccessService>();
        try
        {
            if (pairing)
            {
                if (!HttpMethods.IsPost(context.Request.Method)) { context.Response.StatusCode = 405; return true; }
                using var lease = service.Acquire(path.EndsWith("/start"));
                if (!lease.IsAcquired) { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "5"; return true; }
                // Read a bounded request even when Content-Length is absent (chunked requests).
                var buffer = new byte[4097];
                var length = 0;
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(length, buffer.Length - length), context.RequestAborted)) > 0)
                {
                    length += read;
                    if (length > 4096) { context.Response.StatusCode = 413; return true; }
                }
                if (!context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return true; }
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                JsonElement result;
                if (path.EndsWith("/start"))
                {
                    var request = JsonSerializer.Deserialize<AgentPairRequest>(buffer.AsSpan(0, length), options);
                    if (request is null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80
                        || request.Name.Any(char.IsControl) || request.Projects is not { Length: > 0 and <= 50 }
                        || request.Projects.Any(p => p is null || !Regex.IsMatch(p, "^[a-zA-Z0-9_-]{1,80}$"))
                        || request.Projects.Distinct(StringComparer.Ordinal).Count() != request.Projects.Length)
                    { context.Response.StatusCode = 400; return true; }
                    var projects = context.RequestServices.GetRequiredService<ProjectService>();
                    var available = (await projects.ListProjectsAsync()).Select(p => p.Slug).ToHashSet(StringComparer.Ordinal);
                    if (request.Projects.Any(p => !available.Contains(p))) { context.Response.StatusCode = 400; return true; }
                    result = await service.StartAsync(request, context.RequestAborted);
                    if (result.GetProperty("verification_uri").GetString() != AccountAssociationService.PortalOrigin + "/agents")
                        throw new AccountAssociationException();
                }
                else
                {
                    var request = JsonSerializer.Deserialize<AgentPollRequest>(buffer.AsSpan(0, length), options);
                    if (request?.DeviceCode is null || !Regex.IsMatch(request.DeviceCode, "^[a-f0-9]{64}$"))
                    { context.Response.StatusCode = 400; return true; }
                    result = await service.PollAsync(request.DeviceCode, context.RequestAborted);
                }
                if (result.TryGetProperty("error", out _)) context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(result, context.RequestAborted);
                return true;
            }
            if (!Regex.IsMatch(bearer, "^Bearer kca_[a-f0-9]{64}$"))
            { context.Response.StatusCode = 401; return true; }
            // No positive cache: revocation applies to the next API request.
            var grant = await service.CheckAsync(bearer[7..], context.RequestAborted);
            if (!grant.Valid || grant.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            { context.Response.StatusCode = 401; return true; }
            if (!AgentApiPolicy.Allowed(context.Request.Method, path, grant))
            { context.Response.StatusCode = 403; return true; }
            context.Features.Set(grant);
            if (path == "/api/projects")
            {
                var projects = await context.RequestServices.GetRequiredService<ProjectService>().ListProjectsAsync();
                await context.Response.WriteAsJsonAsync(projects.Where(p => grant.Projects?.Contains(p.Slug, StringComparer.Ordinal) == true)
                    .Select(p => new { p.Slug, p.Name }), context.RequestAborted);
            }
            else await next(context);
            return true;
        }
        catch (JsonException) { context.Response.StatusCode = 400; return true; }
        catch (AccountAssociationException error)
        {
            context.Response.StatusCode = error.Code == "AgentRateLimited" ? 429 : 503;
            if (error.Code == "AgentRateLimited") context.Response.Headers.RetryAfter = "60";
            return true;
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { context.Response.StatusCode = 503; return true; }
    }
}
