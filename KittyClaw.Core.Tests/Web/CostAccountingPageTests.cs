using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KittyClaw.Core.Tests.Web;

public sealed class CostAccountingPageTests
{
    [Fact]
    public async Task Page_SeparatesApiEquivalent_SubscriptionAndUnpricedUsage()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """{"Language":"en","OnboardingSeen":true}""");
        var previous = Environment.GetEnvironmentVariable("KITTYCLAW_DATA_DIR");
        var previousUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        var previousDesktop = Environment.GetEnvironmentVariable("KITTYCLAW_DESKTOP");
        Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", temp.Path);
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
        Environment.SetEnvironmentVariable("KITTYCLAW_DESKTOP", "false");
        try
        {
            await using var factory = new Factory();
            using var client = factory.CreateClient();
            var projects = factory.Services.GetRequiredService<ProjectService>();
            var project = await projects.CreateProjectAsync("Subscription usage");
            var tracker = factory.Services.GetRequiredService<CostTracker>();
            var now = DateTime.UtcNow;
            tracker.LogRun(projects.ResolveWorkspacePath(project), new(now, "test", null, "gpt-6.1-sol",
                1000000, 0, 0, 0, 2m, 1, 0, CostEstimated: true, RunId: "priced", UsageVersion: 2,
                BillingPlan: "pro", RateCardVersion: ModelCostEstimator.RateCardVersion));
            tracker.LogRun(projects.ResolveWorkspacePath(project), new(now, "test", null, "future-model",
                100, 0, 0, 0, 0, 1, 0, CostKnown: false, RunId: "unknown", UsageVersion: 2));
            await factory.Services.GetRequiredService<CostReportService>().RefreshAsync();
            var html = await client.GetStringAsync("/costs");
            Assert.Contains("Token usage value (API rates)", html);
            Assert.Contains("$2.00", html);
            Assert.Contains("ChatGPT pro", html);
            Assert.Contains("subscription detected", html);
            Assert.Contains("actual subscription fee", html);
            Assert.Contains("records cannot be priced reliably", html);
            Assert.DoesNotContain("$200", html);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", previous);
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", previousUrls);
            Environment.SetEnvironmentVariable("KITTYCLAW_DESKTOP", previousDesktop);
        }
    }

    private sealed class Factory : WebApplicationFactory<CreateProjectRequest>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                // Render the actual page with no automation, recovery or provider dispatch.
                foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                             && descriptor.ImplementationType?.Assembly.GetName().Name?.StartsWith("KittyClaw", StringComparison.Ordinal) == true).ToList())
                    services.Remove(descriptor);
                foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                             && descriptor.ImplementationFactory is not null).ToList()) services.Remove(descriptor);
            });
        }
    }
}
