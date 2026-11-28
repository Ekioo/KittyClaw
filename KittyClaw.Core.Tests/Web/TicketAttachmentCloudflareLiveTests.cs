using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace KittyClaw.Core.Tests.Web;

/// <summary>Real Cloudflare transport; synthetic account authorization and no production installation.</summary>
public sealed class TicketAttachmentCloudflareLiveTests(ITestOutputHelper output)
{
    [AttachmentCloudflareFact]
    public async Task Near_100_MB_upload_retry_download_integrity_and_deletion_through_Cloudflare()
    {
        var binary = Environment.GetEnvironmentVariable("KITTYCLAW_CLOUDFLARED_BIN")
            ?? throw new InvalidOperationException("Set KITTYCLAW_CLOUDFLARED_BIN to the pinned cloudflared binary.");
        Assert.True(OperatingSystem.IsWindows(), "This opt-in fixture currently verifies the pinned Windows asset.");
        await CloudflaredRuntime.VerifyAsync(binary, CloudflaredRuntime.CurrentAsset().Sha256, default);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var evidence = Path.Combine(directory.FullName, ".artifacts", "attachment-cloudflare-live", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(evidence, "data");
        Directory.CreateDirectory(data);
        var projects = new ProjectService(data);
        var project = await projects.CreateProjectAsync("Attachment live QA");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Synthetic transport test");
        var token = "kca_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        using var central = new HttpClient(new SyntheticPortal(token, project.Slug));
        using var accounts = new AccountAssociationService(data, new EphemeralDataProtectionProvider(), central);
        await accounts.BeginAssociationAsync(default); await accounts.RefreshAsync(default);
        await accounts.ConfirmAsync(SyntheticPortal.AccountId, default); await accounts.RefreshAsync(default);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration["AllowedHosts"] = "attachment-live.kittyclaw.dev";
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(projects); builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(new RemoteTunnelOptions(true, port, "attachment-live.kittyclaw.dev"));
        builder.Services.AddSingleton<AgentApiAccessService>(); builder.Services.AddSingleton<TicketAttachmentService>();
        builder.Services.AddSingleton<TicketAttachmentLocationService>();
        builder.Services.AddSingleton<BoardUpdateNotifier>();
        await using var app = builder.Build();
        app.UseMiddleware<RemoteTunnelMiddleware>();
        // The fixture has no browser login or UI. Only a production-validated AgentApiGrant
        // may reach the production attachment endpoints, including browser download exceptions.
        app.Use(async (context, next) =>
        {
            if (context.Features.Get<AgentApiGrant>() is not { Valid: true }) { context.Response.StatusCode = 401; return; }
            await next(context);
        });
        Endpoints.MapAttachments(app.MapGroup("/api"));
        await app.StartAsync();
        using (var local = new HttpClient())
        {
            using var check = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/projects/{project.Slug}/tickets/{ticket.Id}/attachments");
            check.Headers.Host = "attachment-live.kittyclaw.dev"; check.Headers.Authorization = new("Bearer", token);
            var checkedResponse = await local.SendAsync(check);
            Assert.True(checkedResponse.StatusCode == HttpStatusCode.OK, $"Isolated origin preflight returned HTTP {(int)checkedResponse.StatusCode}.");
        }
        var config = Path.Combine(evidence, "quick-tunnel.yml"); await File.WriteAllTextAsync(config, "{}\n");
        var info = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "tunnel", "--config", config, "--no-autoupdate", "--url", $"http://127.0.0.1:{port}",
            "--http-host-header", "attachment-live.kittyclaw.dev", "--protocol", "http2", "--edge-ip-version", "4" }) info.ArgumentList.Add(arg);
        info.Environment.Remove("TUNNEL_TOKEN"); info.Environment.Remove("TUNNEL_TOKEN_FILE");
        var url = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connector = Process.Start(info)!;
        var diagnostics = new List<string>();
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var match = Regex.Match(line, @"https://[a-z0-9-]+\.trycloudflare\.com");
                if (match.Success) url.TrySetResult(match.Value);
                // Retain diagnostics only in this private ignored fixture directory.
                lock (diagnostics) diagnostics.Add(line);
            }
        }
        var stdout = Drain(connector.StandardOutput); var stderr = Drain(connector.StandardError);
        try
        {
            var origin = await url.Task.WaitAsync(TimeSpan.FromSeconds(60));
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var path = $"/api/projects/{project.Slug}/tickets/{ticket.Id}/attachments";
            var ready = false;
            var lastStatus = "no response";
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { using var response = await http.GetAsync(path); lastStatus = "HTTP " + (int)response.StatusCode; if (response.StatusCode == HttpStatusCode.OK) { ready = true; break; } }
                catch (HttpRequestException ex) { lastStatus = ex.HttpRequestError.ToString(); }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            Assert.True(ready, "The isolated Cloudflare tunnel did not become ready: " + lastStatus);
            output.WriteLine("Temporary Cloudflare HTTPS transport ready; isolated synthetic project only.");
            const long size = 99_000_000;
            var payload = Path.Combine(evidence, "synthetic-99mb.bin");
            await using (var file = File.Create(payload))
            {
                var buffer = new byte[1024 * 1024];
                long left = size;
                while (left > 0) { RandomNumberGenerator.Fill(buffer); var count = (int)Math.Min(left, buffer.Length); await file.WriteAsync(buffer.AsMemory(0, count)); left -= count; }
            }
            string expected;
            await using (var file = File.OpenRead(payload)) expected = Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
            var key = Guid.NewGuid().ToString("N");
            async Task<HttpResponseMessage> Upload()
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StreamContent(File.OpenRead(payload)), "file", "synthetic-99mb.bin");
                form.Add(new StringContent("qa-agent"), "author");
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
                request.Headers.Add("Idempotency-Key", key);
                return await http.SendAsync(request);
            }
            var timer = Stopwatch.StartNew();
            using var uploaded = await Upload();
            Assert.True(uploaded.StatusCode == HttpStatusCode.Created, $"Live upload returned HTTP {(int)uploaded.StatusCode}.");
            var attachment = await uploaded.Content.ReadFromJsonAsync<TicketAttachmentResponse>(); Assert.NotNull(attachment);
            Assert.Equal(size, attachment.Size); Assert.Equal(expected, attachment.Sha256);
            var seconds = timer.Elapsed.TotalSeconds;
            output.WriteLine($"Uploaded {size} bytes through Cloudflare: HTTP 201, {seconds:F1}s, matching SHA-256.");
            using var replay = await Upload(); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(attachment.Id, (await replay.Content.ReadFromJsonAsync<TicketAttachmentResponse>())!.Id);
            Assert.Single((await http.GetFromJsonAsync<TicketAttachmentResponse[]>(path))!);
            using var downloaded = await http.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead); downloaded.EnsureSuccessStatusCode();
            await using (var bytes = await downloaded.Content.ReadAsStreamAsync())
                Assert.Equal(expected, Convert.ToHexStringLower(await SHA256.HashDataAsync(bytes)));
            using (var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
                Assert.Contains((await anonymous.GetAsync(origin + attachment.Url)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized });
            using var delete = new HttpRequestMessage(HttpMethod.Delete, attachment.Url) { Content = JsonContent.Create(new DeleteTicketAttachmentRequest("qa-agent")) };
            Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(delete)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(attachment.Url)).StatusCode);
            Assert.False(File.Exists(app.Services.GetRequiredService<TicketAttachmentService>().BlobPath(attachment.Id)));
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                transport = "real-cloudflare-quick-tunnel", authorization = "synthetic-first-party-portal-response",
                size, sha256 = expected, uploadStatus = 201, replayStatus = 200, deleteStatus = 204,
                downloadIntegrity = true, anonymousDenied = true, uploadSeconds = seconds, completedUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Delete(payload);
            output.WriteLine("Retry returned HTTP 200 with the same ID; download matched; anonymous access denied; deletion returned 204 and removed bytes.");
            output.WriteLine("Evidence: " + Path.Combine(evidence, "result.json"));
        }
        finally
        {
            if (!connector.HasExited) connector.Kill(entireProcessTree: true);
            await connector.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(stdout, stderr);
            await File.WriteAllLinesAsync(Path.Combine(evidence, "connector.log"), diagnostics);
            await app.StopAsync();
        }
    }

    private sealed class SyntheticPortal(string token, string slug) : HttpMessageHandler
    {
        internal const string AccountId = "28f8d3c3-51f9-4278-a355-4c7329202715";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var data = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            object account = new { id = AccountId, name = "Synthetic QA owner", email = "qa@example.test" };
            object result = request.RequestUri!.AbsolutePath switch
            {
                "/v1/installations/associate" => new { authorizationUrl = AccountAssociationService.PortalOrigin + "/associate?request=" + new string('a', 64), expiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() },
                "/v1/installations/status" => new { account, proposedAccount = account, revoked = false, mfaEnabled = true },
                "/v1/installations/confirm" => new { associated = true },
                "/v1/installations/agents/check" => new { valid = data.GetProperty("token").GetString() == token, projects = new[] { slug }, write = true, expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), id = "synthetic-live-grant" },
                _ => throw new InvalidOperationException("Unexpected synthetic portal action.")
            };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
}

public sealed class AttachmentCloudflareFactAttribute : FactAttribute
{
    public AttachmentCloudflareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KITTYCLAW_ATTACHMENT_CLOUDFLARE_LIVE") != "1")
            Skip = "Requires explicit KITTYCLAW_ATTACHMENT_CLOUDFLARE_LIVE=1 and a pinned cloudflared binary.";
    }
}
