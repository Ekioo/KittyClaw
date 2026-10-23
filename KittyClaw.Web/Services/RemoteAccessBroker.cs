using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KittyClaw.Web.Services;

public sealed class RemoteAccessBroker(HttpClient http, Uri endpoint)
{
    internal string Origin { get; } = ValidateEndpoint(endpoint);
    private static string ValidateEndpoint(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.UserInfo.Length > 0
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("RemoteAccess:BrokerUrl must be an HTTPS origin.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    internal async Task RegisterAsync(RemoteAccessIdentity identity, string invitation, CancellationToken ct)
    {
        if (!Regex.IsMatch(invitation, "^[a-f0-9]{64}$")) throw new RemoteAccessException("RemoteInvitationInvalid");
        await SendAsync(identity, "v1/instances", new { instanceId = identity.InstanceId, invitation, originPort = identity.Port, subdomain = identity.Subdomain }, false, ct);
    }
    internal async Task<string> ConnectAsync(RemoteAccessIdentity identity, CancellationToken ct)
    {
        var response = await SendAsync(identity, $"v1/instances/{identity.InstanceId}/connect", null, true, ct);
        if (response.State != "active" || string.IsNullOrEmpty(response.TunnelToken)
            || response.TunnelToken.Length > 16384 || response.TunnelToken.Any(char.IsControl))
            throw new RemoteAccessException("RemoteBrokerInvalid");
        return response.TunnelToken;
    }
    private async Task<BrokerResponse> SendAsync(RemoteAccessIdentity identity, string path, object? body, bool connect, CancellationToken ct)
    {
        if (identity.BrokerOrigin != Origin) throw new RemoteAccessException("RemoteIdentityMismatch");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Origin}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                if (!connect && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
                {
                    var failure = await response.Content.ReadFromJsonAsync<BrokerError>(cancellationToken: timeout.Token);
                    if (failure?.Error == "name_unavailable") throw new RemoteAccessException("RemoteNameUnavailable");
                    if (failure?.Error == "invalid_subdomain") throw new RemoteAccessException("RemoteNameInvalid");
                    if (failure?.Error == "name_locked") throw new RemoteAccessException("RemoteNameLocked");
                }
                throw new RemoteAccessException(response.StatusCode switch
                {
                    HttpStatusCode.Forbidden when !connect => "RemoteInvitationInvalid",
                    HttpStatusCode.Unauthorized => "RemoteOwnershipInvalid",
                    HttpStatusCode.Gone => "RemoteRetired",
                    _ => "RemoteBrokerUnavailable"
                });
            }
            var result = await response.Content.ReadFromJsonAsync<BrokerResponse>(cancellationToken: timeout.Token);
            if (result is null || result.InstanceId != identity.InstanceId || result.OriginPort != identity.Port
                || result.Url != $"https://{RemoteAccessName.Hostname(identity)}"
                || result.State is not ("pending" or "active")) throw new RemoteAccessException("RemoteBrokerInvalid");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new RemoteAccessException("RemoteBrokerUnavailable"); }
        catch (HttpRequestException) { throw new RemoteAccessException("RemoteBrokerUnavailable"); }
        catch (JsonException) { throw new RemoteAccessException("RemoteBrokerInvalid"); }
    }
    private sealed record BrokerError(string Error);
    private sealed record BrokerResponse(string InstanceId, string Url, string State, int OriginPort, string? TunnelToken);
}
