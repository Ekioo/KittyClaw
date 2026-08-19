using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Handles outbound-network automation actions: httpRequest and executePowerShell.
/// </summary>
internal sealed class NetworkActionHandler(
    TicketService tickets,
    ILogger logger,
    ProjectSecretVault? projectSecrets = null)
{
    // Returns true when AbortOnFailure is set and the request failed.
    public async Task<bool> ExecuteHttpRequestAsync(
        HttpRequestActionSpec spec, ProjectRuntime rt, TriggerFiring firing, CancellationToken ct)
    {
        try
        {
            var url = await ResolveHttpPlaceholdersAsync(spec.Url, rt, firing);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                logger.LogWarning("httpRequest: invalid or non-http(s) URL — request refused");
                return spec.AbortOnFailure;
            }
            var method = spec.Method.ToUpperInvariant() switch
            {
                "GET" => HttpMethod.Get,
                "POST" => HttpMethod.Post,
                "PUT" => HttpMethod.Put,
                "PATCH" => HttpMethod.Patch,
                "DELETE" => HttpMethod.Delete,
                _ => null,
            };
            if (method is null)
            {
                logger.LogWarning("httpRequest: unsupported method '{Method}' — request refused", spec.Method);
                return spec.AbortOnFailure;
            }

            using var request = new HttpRequestMessage(method, uri);
            if (!string.IsNullOrEmpty(spec.Body))
                request.Content = new StringContent(
                    await ResolveHttpPlaceholdersAsync(spec.Body, rt, firing),
                    System.Text.Encoding.UTF8, spec.ContentType);
            foreach (var (name, value) in spec.Headers)
            {
                var resolved = await ResolveHttpPlaceholdersAsync(value, rt, firing);
                if (!request.Headers.TryAddWithoutValidation(name, resolved))
                    request.Content?.Headers.TryAddWithoutValidation(name, resolved);
            }

            var client = spec.AllowLocalTargets ? HttpActionClient.Unguarded : HttpActionClient.Guarded;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, spec.TimeoutSeconds)));

            var started = DateTime.UtcNow;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            // Drain at most MaxResponseBytes; the body is never stored.
            await using (var stream = await response.Content.ReadAsStreamAsync(cts.Token))
            {
                var buffer = new byte[8192];
                var remaining = HttpActionClient.MaxResponseBytes;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cts.Token);
                    if (read == 0) break;
                    remaining -= read;
                }
            }
            logger.LogInformation("httpRequest {Method} {Host} -> {Status} in {Ms}ms",
                method, uri.Host, (int)response.StatusCode,
                (int)(DateTime.UtcNow - started).TotalMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("httpRequest non-2xx ({Status}) from {Host}; abortOnFailure={Abort}",
                    (int)response.StatusCode, uri.Host, spec.AbortOnFailure);
                return spec.AbortOnFailure;
            }
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("httpRequest timed out after {Timeout}s", spec.TimeoutSeconds);
            return spec.AbortOnFailure;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "httpRequest failed");
            return spec.AbortOnFailure;
        }
    }

    // Returns true when AbortOnFailure is set and the process exited with a non-zero code.
    public async Task<bool> ExecutePowerShellAsync(
        ExecutePowerShellActionSpec spec, string workspacePath, string slug, TriggerFiring firing, CancellationToken ct)
    {
        try
        {
            string Render(string s) => (s ?? string.Empty)
                .Replace("{ticketId}", firing.TicketId?.ToString() ?? "")
                .Replace("{ticketTitle}", firing.TicketTitle ?? "")
                .Replace("{slug}", slug ?? "");

            string scriptArg;
            if (!string.IsNullOrWhiteSpace(spec.ScriptFile))
            {
                var rendered = Render(spec.ScriptFile);
                var path = Path.IsPathRooted(rendered)
                    ? rendered
                    : Path.Combine(workspacePath, rendered);
                scriptArg = $"-File \"{path}\"";
            }
            else
            {
                var bytes = System.Text.Encoding.Unicode.GetBytes(Render(spec.Script));
                scriptArg = $"-EncodedCommand {Convert.ToBase64String(bytes)}";
            }

            var extraArgs = spec.Arguments.Count > 0
                ? " " + string.Join(" ", spec.Arguments.Select(a => $"\"{Render(a)}\""))
                : "";

            var pwshBin = ShellResolver.ResolvePowerShell();
            var secrets = projectSecrets is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(
                    await projectSecrets.ReadForInjectionAsync(slug, ct),
                    StringComparer.OrdinalIgnoreCase);
            var environment = new Dictionary<string, string>(spec.Env, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in secrets) environment[name] = value;
            var res = await ProcessRunner.RunAsync(
                pwshBin,
                $"-NonInteractive -NoProfile {scriptArg}{extraArgs}",
                workspacePath,
                TimeSpan.FromSeconds(spec.TimeoutSeconds),
                environment,
                ct);
            var stdout = SecretRedactor.Redact(res.Stdout.Trim(), secrets.Values);
            var stderr = SecretRedactor.Redact(res.Stderr.Trim(), secrets.Values);

            if (res.TimedOut)
            {
                logger.LogWarning(
                    "executePowerShell timed out after {Timeout}s; process tree killed", spec.TimeoutSeconds);
                return spec.AbortOnFailure;
            }

            logger.LogInformation("executePowerShell exited {Code}. stdout={Stdout} stderr={Stderr}",
                res.ExitCode, stdout, stderr);

            if (res.ExitCode != 0)
            {
                logger.LogWarning("executePowerShell non-zero exit ({Code}); abortOnFailure={Abort}",
                    res.ExitCode, spec.AbortOnFailure);
                return spec.AbortOnFailure;
            }
        }
        catch (OperationCanceledException)
        {
            // Engine shutdown / chain cancellation — the process tree was already killed.
            logger.LogWarning("executePowerShell cancelled");
            if (spec.AbortOnFailure) return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "executePowerShell failed");
            if (spec.AbortOnFailure) return true;
        }
        return false;
    }

    private async Task<string> ResolveHttpPlaceholdersAsync(string template, ProjectRuntime rt, TriggerFiring firing)
    {
        var s = template.Replace("{ticketId}", firing.TicketId?.ToString() ?? "");
        // Signal-path firings carry only the ticket id (no title/status snapshot) — resolve
        // whatever the template needs from the live ticket, like the condition path does (#135).
        var needsLookup = s.Contains("{assignee}") || s.Contains("{ticketStatus}")
            || (s.Contains("{ticketTitle}") && firing.TicketTitle is null);
        Models.Ticket? ticket = null;
        if (needsLookup && firing.TicketId is int id)
            ticket = await tickets.GetTicketAsync(rt.Slug, id);
        return s
            .Replace("{ticketTitle}", firing.TicketTitle ?? ticket?.Title ?? "")
            .Replace("{ticketStatus}", firing.TicketStatus ?? ticket?.Status ?? "")
            .Replace("{assignee}", ticket?.AssignedTo ?? "");
    }
}
