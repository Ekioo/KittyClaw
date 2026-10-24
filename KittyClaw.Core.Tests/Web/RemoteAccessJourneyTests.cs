using System.Net;
using System.Net.Http.Json;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

public sealed class RemoteAccessJourneyTests
{
    [RemoteBrowserFact]
    public async Task Local_owner_enrolls_activates_pauses_and_resumes_without_cloud_resources()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "settings.json"), """{"OnboardingSeen":true,"Language":"en"}""");
        var changes = new Dictionary<string, string?>
        {
            ["KITTYCLAW_DATA_DIR"] = dir.Path, ["KITTYCLAW_DESKTOP"] = "false",
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0", ["RemoteTunnel__Port"] = "25834"
        };
        var original = changes.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        foreach (var pair in changes) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        try
        {
            await using var factory = new Factory();
            factory.UseKestrel(0);
            using var client = factory.CreateClient();
            var baseUrl = client.BaseAddress!.ToString().TrimEnd('/');
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true,
                Channel = Environment.GetEnvironmentVariable("KITTYCLAW_TEST_BROWSER_CHANNEL") ?? "msedge" });
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1200, Height = 1000 } });
            await page.GotoAsync(baseUrl + "/settings");
            await Expect(page.GetByTestId("remote-global-settings")).ToBeVisibleAsync();
            await Expect(page.GetByTestId("remote-url-qr")).ToHaveCountAsync(0);
            await page.GetByText("Configure Internet access", new() { Exact = true }).ClickAsync();
            await Expect(page.GetByText("First configure an authenticator", new() { Exact = false })).ToBeVisibleAsync();
            await page.GetByText("Configure network access", new() { Exact = true }).ClickAsync();
            var secret = await page.Locator("main code").InnerTextAsync();
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bits = string.Concat(secret.Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
            var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
            await page.Locator("input[name=code]").FillAsync(NetworkAccessService.Totp(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30));
            await Expect(page.GetByText("SHA-256:", new() { Exact = false })).ToBeVisibleAsync();
            await page.GotoAsync(baseUrl + "/remote-setup");
            await page.GetByLabel("Public address name (optional)").FillAsync("my-office");
            await page.GetByRole(AriaRole.Checkbox).CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Activate Internet access" }).ClickAsync();
            await Expect(page.GetByText("Your address is reserved.", new() { Exact = false })).ToBeVisibleAsync();
            var publicLink = page.Locator("a[href^='https://'][href$='.kittyclaw.dev']").First;
            var url = await publicLink.GetAttributeAsync("href");
            Assert.Equal("https://my-office.kittyclaw.dev", url);
            await Expect(page.GetByTestId("remote-url-qr")).ToBeVisibleAsync();
            await Expect(page.GetByText("Open the camera on your smartphone", new() { Exact = false })).ToBeVisibleAsync();
            var setupQr = await page.GetByTestId("remote-url-qr").GetAttributeAsync("src");
            Assert.StartsWith("data:image/png;base64,", setupQr);
            var screenshots = Path.Combine(RepoRoot(), ".artifacts", "remote-access-ui");
            Directory.CreateDirectory(screenshots);
            await File.WriteAllBytesAsync(Path.Combine(screenshots, "setup-qr.png"), Convert.FromBase64String(setupQr!["data:image/png;base64,".Length..]));
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "mobile.png"), FullPage = true });
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Stop Internet access" }).ClickAsync();
            await Expect(page.GetByText("Internet access is stopped.", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(page.Locator("input[name=invitation]")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Checkbox).CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Resume Internet access" }).ClickAsync();
            await Expect(page.Locator("a[href^='https://'][href$='.kittyclaw.dev']").First).ToHaveAttributeAsync("href", url!);
            await Expect(page.GetByTestId("remote-url-qr")).ToHaveAttributeAsync("src", setupQr!);
            await page.GotoAsync(baseUrl + "/settings");
            await Expect(page.Locator(".mobile-section-navigation select")).ToBeEnabledAsync();
            await page.Locator(".mobile-section-navigation select").SelectOptionAsync("remote");
            var qrCard = page.GetByTestId("remote-access-qr");
            await Expect(qrCard).ToBeVisibleAsync();
            await Expect(qrCard.GetByRole(AriaRole.Link, new() { Name = url, Exact = true })).ToHaveAttributeAsync("href", url!);
            await Expect(qrCard.GetByTestId("remote-url-qr")).ToHaveAttributeAsync("src", setupQr!);
            await page.SetViewportSizeAsync(1200, 1000);
            await Expect(page.GetByTestId("jev-api-key")).ToBeEnabledAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Language", Exact = true }).SelectOptionAsync("fr");
            await Expect(qrCard).ToContainTextAsync("Ouvrir sur ton smartphone");
            await page.SetViewportSizeAsync(390, 844);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "qr-settings-mobile.png"), FullPage = true });
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
            await page.GetByTestId("remote-url-qr").ScreenshotAsync(new() { Path = Path.Combine(screenshots, "settings-qr-rendered.png") });
            await page.SetViewportSizeAsync(1200, 1000);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, "qr-settings-desktop.png"), FullPage = true });
            var content = await page.ContentAsync();
            Assert.DoesNotContain(factory.Services.GetRequiredService<RemoteAccessStore>().Identity!.Credential, content);
        }
        finally { foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }

    private static string RepoRoot()
    {
        var path = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(path, "KittyClaw.slnx"))) path = Path.GetDirectoryName(path)!;
        return path;
    }
    private sealed class Factory : WebApplicationFactory<CreateProjectRequest>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                // No automation, providers, telemetry or real connector can run.
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray()) services.Remove(descriptor);
                services.RemoveAll<RemoteAccessBroker>();
                services.AddSingleton(sp => new RemoteAccessBroker(new HttpClient(new BrokerHandler(sp.GetRequiredService<RemoteAccessStore>())),
                    new Uri("https://remote-api.kittyclaw.dev")));
            });
        }
    }
    private sealed class BrokerHandler(RemoteAccessStore store) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://remote-api.kittyclaw.dev/v1/instances", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = JsonContent.Create(new
            {
                instanceId = store.Identity!.InstanceId, url = $"https://{RemoteAccessName.Hostname(store.Identity)}",
                state = "pending", originPort = store.Identity.Port
            }) });
        }
    }
}

public sealed class RemoteBrowserFactAttribute : FactAttribute
{
    public RemoteBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KITTYCLAW_REMOTE_DEBUG_E2E") != "1")
            Skip = "Opt-in isolated remote access UI journey: set KITTYCLAW_REMOTE_DEBUG_E2E=1.";
    }
}
