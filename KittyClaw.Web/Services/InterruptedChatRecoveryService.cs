using System.Net.Http.Json;
using KittyClaw.Core.Automation;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace KittyClaw.Web.Services;

/// <summary>
/// Restarts chat turns that were still running when the KittyClaw server stopped.
/// The normal chat endpoint rebuilds the agent context and resumes the persisted CLI session;
/// the drawer's recovery path remains as a fallback if this startup pass cannot reach the API.
/// </summary>
public sealed class InterruptedChatRecoveryService(
    AgentRunRegistry runs,
    IHttpClientFactory clients,
    IServer server,
    IHostApplicationLifetime lifetime,
    ILogger<InterruptedChatRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(started.SetResult);
        await started.Task.WaitAsync(stoppingToken);

        var interrupted = runs.InterruptedChats();
        if (interrupted.Count == 0) return;

        var address = ResolveLoopbackAddress(server.Features.Get<IServerAddressesFeature>()?.Addresses);
        if (address is null)
        {
            logger.LogWarning("Could not recover {Count} interrupted chat sessions because the local server address is unavailable", interrupted.Count);
            return;
        }

        using var client = clients.CreateClient();
        client.BaseAddress = address;
        foreach (var run in interrupted)
        {
            try
            {
                var response = await client.PostAsJsonAsync(
                    $"/api/projects/{Uri.EscapeDataString(run.ProjectSlug)}/chat/start",
                    new
                    {
                        message = "",
                        target = run.ChatTarget,
                        ticketId = run.TicketId,
                        resumeInterrupted = true,
                    }, stoppingToken);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("Resumed interrupted chat for project {ProjectSlug}, target {ChatTarget}", run.ProjectSlug, run.ChatTarget);
                }
                else
                {
                    logger.LogWarning("Interrupted chat recovery returned HTTP {StatusCode} for project {ProjectSlug}, target {ChatTarget}",
                        (int)response.StatusCode, run.ProjectSlug, run.ChatTarget);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not resume interrupted chat for project {ProjectSlug}, target {ChatTarget}", run.ProjectSlug, run.ChatTarget);
            }
        }
    }

    internal static Uri? ResolveLoopbackAddress(IEnumerable<string>? addresses)
    {
        var value = addresses?.FirstOrDefault(address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var builder = new UriBuilder(uri);
        if (builder.Host is "0.0.0.0" or "::" or "[::]") builder.Host = "localhost";
        return builder.Uri;
    }
}
