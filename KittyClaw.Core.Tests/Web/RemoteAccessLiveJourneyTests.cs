using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

/// <summary>Explicitly opted-in acceptance test. Allocates and retires a real beta tunnel.</summary>
public sealed class RemoteAccessLiveJourneyTests(ITestOutputHelper output)
{
    [LiveRemoteFact]
    public async Task Public_TLS_authentication_Blazor_pause_resume_and_retirement()
    {
        var repo = FindRepo();
        var invitationFile = Environment.GetEnvironmentVariable("KITTYCLAW_REMOTE_LIVE_INVITATION")
            ?? throw new InvalidOperationException("An operator-created private invitation file is required.");
        using var invitationJson = JsonDocument.Parse(await File.ReadAllTextAsync(invitationFile));
        var invitation = invitationJson.RootElement.GetProperty("invitation").GetString()!;
        var requestedSubdomain = "e2e-" + Guid.NewGuid().ToString("N")[..12];
        var dataDir = Path.Combine(repo, ".artifacts", "remote-live", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), """{"OnboardingSeen":true,"Language":"en"}""");
        const string local = "http://localhost:25832";
        const string broker = "https://remote-api.kittyclaw.dev";
        using var localHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = "msedge" });
        // Default certificate validation is deliberately kept enabled.
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1360, Height = 1000 } });
        var owner = await context.NewPageAsync();
        var remote = await context.NewPageAsync();
        var interactiveConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.WebSocket += (_, socket) =>
        {
            if (new Uri(socket.Url).AbsolutePath == "/_blazor")
                socket.FrameReceived += (_, _) => interactiveConnected.TrySetResult();
        };
        remote.Response += (_, response) =>
        {
            if (response.Status >= 400)
                output.WriteLine($"Public browser HTTP {response.Status}: {new Uri(response.Url).AbsolutePath}");
        };
        remote.PageError += (_, error) => output.WriteLine("Public browser script error: " + error);
        Process? app = null;
        Task<string>? appOutput = null, appError = null;
        RemoteAccessIdentity? identity = null;
        string? publicUrl = null;
        var retired = false;
        long lastCodeStep = -1;
        try
        {
            await StartAsync();
            output.WriteLine("Isolated app started with mock provider CLIs.");
            await owner.GotoAsync(local + "/settings");
            await Expect(owner.GetByTestId("jev-api-key")).ToBeEnabledAsync(new() { Timeout = 30000 });
            await owner.GetByRole(AriaRole.Combobox, new() { Name = "Language", Exact = true }).SelectOptionAsync("fr");
            await Expect(owner.GetByTestId("remote-global-settings")).ToContainTextAsync("Accès depuis Internet", new() { Timeout = 15000 });
            await owner.GetByRole(AriaRole.Combobox, new() { Name = "Langue", Exact = true }).SelectOptionAsync("en");
            await Expect(owner.GetByTestId("remote-global-settings")).ToContainTextAsync("Internet access");
            output.WriteLine("Local Blazor language control completed a round trip.");
            await owner.GotoAsync(local + "/network-setup");
            var setupKey = await owner.Locator("main code").InnerTextAsync();
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bits = string.Concat(setupKey.Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
            var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
            await owner.Locator("input[name=code]").FillAsync(await NextCodeAsync());
            await owner.GetByRole(AriaRole.Button, new() { Name = "Validate code and enable network access" }).ClickAsync();
            await Expect(owner.GetByText("SHA-256:", new() { Exact = false })).ToBeVisibleAsync();
            await owner.GotoAsync(local + "/remote-setup");
            await owner.GetByLabel("Public address name (optional)").FillAsync(requestedSubdomain);
            await owner.GetByLabel("Private beta invitation code").FillAsync(invitation);
            await owner.GetByRole(AriaRole.Checkbox).CheckAsync();
            await owner.GetByRole(AriaRole.Button, new() { Name = "Activate Internet access" }).ClickAsync();
            await Expect(owner.GetByText("Your address is reserved.", new() { Exact = false })).ToBeVisibleAsync(new() { Timeout = 100000 });
            publicUrl = await owner.Locator("a[href^='https://'][href$='.kittyclaw.dev']").GetAttributeAsync("href");
            Assert.Equal($"https://{requestedSubdomain}.kittyclaw.dev", publicUrl);
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dataDir, "network-keys")), options =>
            { if (OperatingSystem.IsWindows()) options.ProtectKeysWithDpapi(); });
            identity = new RemoteAccessStore(dataDir, protection).Identity;
            Assert.NotNull(identity);
            output.WriteLine("Real broker registration succeeded; restarting only the isolated app.");
            await StopAsync();
            await StartAsync();
            await WaitUntilAsync(async () =>
            {
                var page = await localHttp.GetStringAsync(local + "/remote-setup");
                return page.Contains("Connected to Cloudflare.", StringComparison.Ordinal);
            }, TimeSpan.FromMinutes(5));
            output.WriteLine("Pinned cloudflared installed, integrity verified, real edge connection ready.");
            using (var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            {
                await WaitUntilAsync(async () =>
                {
                    try { using var response = await anonymous.GetAsync(publicUrl + "/access"); return response.StatusCode == HttpStatusCode.OK; }
                    catch (HttpRequestException) { return false; }
                }, TimeSpan.FromMinutes(3));
                Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(publicUrl + "/remote-setup")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(publicUrl + "/api/projects")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(publicUrl + "/mcp")).StatusCode);
            }
            await LoginAsync();
            interactiveConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await remote.GotoAsync(publicUrl + "/settings");
            await Expect(remote.GetByTestId("remote-global-settings")).ToBeVisibleAsync();
            await interactiveConnected.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Expect(remote.GetByTestId("jev-api-key")).ToBeEnabledAsync(new() { Timeout = 30000 });
            output.WriteLine("Public Blazor WebSocket and interactive settings initialization completed.");
            output.WriteLine("Public language controls: " + await remote.Locator("select.settings-input").CountAsync());
            await remote.GetByRole(AriaRole.Combobox, new() { Name = "Language", Exact = true }).SelectOptionAsync("fr");
            output.WriteLine("Public selected language value: " + await remote.Locator("select.settings-input").First.InputValueAsync());
            await Expect(remote.GetByTestId("remote-global-settings")).ToContainTextAsync("Accès depuis Internet", new() { Timeout = 20000 });
            await remote.GetByRole(AriaRole.Combobox, new() { Name = "Langue", Exact = true }).SelectOptionAsync("en");
            await Expect(remote.GetByTestId("remote-global-settings")).ToContainTextAsync("Internet access");
            output.WriteLine("Public HTTPS login and interactive Blazor round trip succeeded with certificate validation enabled.");
            var cookies = await context.CookiesAsync([publicUrl!]);
            var cookie = string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value));
            using (var authenticated = new HttpClient())
            {
                authenticated.DefaultRequestHeaders.Add("Cookie", cookie);
                Assert.Equal(HttpStatusCode.OK, (await authenticated.GetAsync(publicUrl + "/api/engine/health")).StatusCode);
                using var upload = new HttpRequestMessage(HttpMethod.Post, publicUrl + "/api/images");
                upload.Headers.Add("Origin", publicUrl);
                var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII=");
                using var multipart = new MultipartFormDataContent();
                var file = new ByteArrayContent(png); file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                multipart.Add(file, "file", "probe.png"); upload.Content = multipart;
                using var uploaded = await authenticated.SendAsync(upload);
                Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
                var uploadJson = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
                var imagePath = uploadJson.RootElement.GetProperty("url").GetString();
                Assert.Equal(HttpStatusCode.OK, (await authenticated.GetAsync(publicUrl + imagePath)).StatusCode);
                using var badOrigin = new HttpRequestMessage(HttpMethod.Post, publicUrl + "/api/images");
                badOrigin.Headers.Add("Origin", "https://unrelated.example");
                Assert.Equal(HttpStatusCode.Forbidden, (await authenticated.SendAsync(badOrigin)).StatusCode);
            }
            await remote.SetViewportSizeAsync(390, 844);
            await remote.ScreenshotAsync(new() { Path = Path.Combine(dataDir, "public-mobile.png"), FullPage = true });
            await owner.GotoAsync(local + "/remote-setup");
            await owner.GetByRole(AriaRole.Button, new() { Name = "Stop Internet access" }).ClickAsync();
            await Expect(owner.GetByText("Internet access is stopped.", new() { Exact = true })).ToBeVisibleAsync();
            using (var oldSession = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            {
                oldSession.DefaultRequestHeaders.Add("Cookie", cookie);
                using var stopped = await oldSession.GetAsync(publicUrl + "/api/engine/health");
                Assert.NotEqual(HttpStatusCode.OK, stopped.StatusCode);
            }
            await owner.GetByRole(AriaRole.Checkbox).CheckAsync();
            await owner.GetByRole(AriaRole.Button, new() { Name = "Resume Internet access" }).ClickAsync();
            await Expect(owner.Locator("a[href^='https://'][href$='.kittyclaw.dev']")).ToHaveAttributeAsync("href", publicUrl!);
            await WaitUntilAsync(async () => (await localHttp.GetStringAsync(local + "/remote-setup")).Contains("Connected to Cloudflare."), TimeSpan.FromMinutes(2));
            using (var oldSession = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            {
                oldSession.DefaultRequestHeaders.Add("Cookie", cookie);
                await WaitUntilAsync(async () =>
                {
                    try { using var response = await oldSession.GetAsync(publicUrl + "/api/engine/health"); return response.StatusCode == HttpStatusCode.Unauthorized; }
                    catch (HttpRequestException) { return false; }
                }, TimeSpan.FromMinutes(1));
            }
            await LoginAsync();
            output.WriteLine("Authenticated upload, cross-origin denial, pause, session revocation and same-URL resume succeeded.");
            await RetireAsync();
            retired = true;
            output.WriteLine("Cloudflare accepted secret update, connector disconnection and permanent retirement.");
            await File.WriteAllTextAsync(Path.Combine(dataDir, "result.json"), JsonSerializer.Serialize(new
            { passed = true, publicUrl, retired = true, completedAt = DateTimeOffset.UtcNow, providerMode = "mock" }, new JsonSerializerOptions { WriteIndented = true }));
            output.WriteLine("Acceptance evidence retained under " + dataDir);

            async Task<string> NextCodeAsync()
            {
                while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 <= lastCodeStep) await Task.Delay(500);
                lastCodeStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
                return NetworkAccessService.Totp(key, lastCodeStep);
            }
            async Task LoginAsync()
            {
                await remote.GotoAsync(publicUrl + "/access");
                await remote.Locator("input[name=code]").FillAsync(await NextCodeAsync());
                await remote.GetByRole(AriaRole.Button, new() { Name = "Connect", Exact = true }).ClickAsync();
                await Expect(remote.Locator("input[name=code]")).ToHaveCountAsync(0, new() { Timeout = 20000 });
            }
        }
        finally
        {
            if (identity is null && File.Exists(Path.Combine(dataDir, "remote-access.json")))
            {
                var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dataDir, "network-keys")), options =>
                { if (OperatingSystem.IsWindows()) options.ProtectKeysWithDpapi(); });
                identity = new RemoteAccessStore(dataDir, protection).Identity;
            }
            if (identity is not null && !retired)
            {
                try { await RetireAsync(); retired = true; }
                catch { output.WriteLine("Retirement needs operator retry for this isolated test identity; protected state was retained."); }
            }
            await StopAsync();
        }

        async Task RetireAsync()
        {
            using var control = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            control.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", identity!.Credential);
            using var response = await control.PostAsync(broker + "/v1/instances/" + identity.InstanceId + "/revoke", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("revoked", json.RootElement.GetProperty("state").GetString());
        }
        async Task StartAsync()
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.Combine(repo, "KittyClaw.Web") };
            info.ArgumentList.Add(Path.Combine(repo, "KittyClaw.Web", "bin", "Debug", "net10.0", "KittyClaw.Web.dll"));
            foreach (var argument in new[] { "--urls", local, "--RemoteTunnel:Port=25834", "--NetworkAccess:HttpsPort=25833" }) info.ArgumentList.Add(argument);
            info.Environment["KITTYCLAW_DATA_DIR"] = dataDir;
            info.Environment["KITTYCLAW_DESKTOP"] = "false";
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            info.Environment["ASPNETCORE_URLS"] = local;
            var mock = Path.Combine(repo, "KittyClaw.ClaudeMock", "bin", "Debug", "net10.0", "claude.exe");
            foreach (var variable in new[] { "KITTYCLAW_CLAUDE_BIN", "KITTYCLAW_GROK_BIN", "KITTYCLAW_CODEX_BIN", "KITTYCLAW_MISTRAL_BIN" }) info.Environment[variable] = mock;
            app = Process.Start(info)!;
            appOutput = app.StandardOutput.ReadToEndAsync();
            appError = app.StandardError.ReadToEndAsync();
            await WaitUntilAsync(async () =>
            {
                if (app.HasExited) throw new InvalidOperationException("Isolated app exited before listening.");
                try { using var response = await localHttp.GetAsync(local + "/remote-setup"); return response.StatusCode == HttpStatusCode.OK; }
                catch (HttpRequestException) { return false; }
            }, TimeSpan.FromSeconds(90));
        }
        async Task StopAsync()
        {
            if (app is null) return;
            if (!app.HasExited) app.Kill(entireProcessTree: true);
            await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await File.AppendAllTextAsync(Path.Combine(dataDir, "isolated-app.log"), await appOutput! + await appError!);
            app.Dispose(); app = null;
        }
    }
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Live acceptance condition was not reached.");
            await Task.Delay(2000);
        }
    }
    private static string FindRepo()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(directory, "KittyClaw.slnx"))) directory = Path.GetDirectoryName(directory)!;
        return directory;
    }
}

public sealed class LiveRemoteFactAttribute : FactAttribute
{
    public LiveRemoteFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KITTYCLAW_REMOTE_LIVE_E2E") != "1")
            Skip = "Real Cloudflare allocation requires explicit KITTYCLAW_REMOTE_LIVE_E2E=1 and a private invitation file.";
    }
}
