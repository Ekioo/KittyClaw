using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace KittyClaw.Core.Tests.Services;

public sealed class ApprovalRegistryServiceTests
{
    [Fact]
    public async Task RegisterRequest_DeduplicatesMateriallyIdenticalRequests()
    {
        using var temp = new TempDir();
        var registry = CreateRegistry(temp);
        var first = await registry.RegisterRequestAsync("project", Request("first", "same"));
        var duplicate = await registry.RegisterRequestAsync("project", Request("second", "same"));

        Assert.Equal("first", duplicate.RequestId);
        Assert.Single(await registry.QueryRequestsAsync("project", new()));
        Assert.Equal(first, duplicate);
    }

    [Fact]
    public async Task ExpiredRequest_CannotBeApproved_AndIsReportedExpired()
    {
        using var temp = new TempDir();
        var registry = CreateRegistry(temp);
        var now = DateTime.UtcNow;
        await registry.RegisterRequestAsync("project", Request("request", "dedupe", now.AddMinutes(-2), now.AddMinutes(-1)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.DecideAsync("project",
            Decision("decision", "request", now, now.AddMinutes(5))));

        Assert.Contains("expired", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("expired", (await registry.QueryRequestsAsync("project", new())).Single().State);
    }

    [Fact]
    public async Task Restart_PreservesHistory_AndMarksInterruptedAttemptUnknown()
    {
        using var temp = new TempDir();
        var now = DateTime.UtcNow;
        var first = CreateRegistry(temp);
        await first.RegisterRequestAsync("project", Request("request", "dedupe", now, now.AddMinutes(10)));
        await first.DecideAsync("project", Decision("decision", "request", now, now.AddMinutes(5)));
        await first.ConsumeOnceAsync("project", Receipt("initial", now));

        var restarted = CreateRegistry(temp);
        await restarted.RecoverInterruptedAttemptsAsync("project", now.AddMinutes(1));

        var receipts = await restarted.QueryReceiptsAsync("project", "request");
        Assert.Equal(2, receipts.Count);
        Assert.Equal(ApprovalReceiptOutcome.Unknown, receipts[1].Outcome);
        Assert.Equal(receipts[0].IntegrityHash, receipts[1].PreviousHash);
        Assert.Equal("consumed", (await restarted.QueryRequestsAsync("project", new())).Single().State);
    }

    [Fact]
    public async Task ConcurrentAllowOnceConsumption_SucceedsExactlyOnce()
    {
        using var temp = new TempDir();
        var now = DateTime.UtcNow;
        var registry = CreateRegistry(temp);
        await registry.RegisterRequestAsync("project", Request("request", "dedupe", now, now.AddMinutes(10)));
        await registry.DecideAsync("project", Decision("decision", "request", now, now.AddMinutes(5)));

        var attempts = Enumerable.Range(0, 8).Select(async i =>
        {
            try { await registry.ConsumeOnceAsync("project", Receipt($"receipt-{i}", now.AddSeconds(i))); return true; }
            catch (InvalidOperationException) { return false; }
        });
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, x => x);
        Assert.Single(await registry.QueryReceiptsAsync("project", "request"));
    }

    [Fact]
    public async Task Query_FiltersByRunTicketAndProvider()
    {
        using var temp = new TempDir();
        var registry = CreateRegistry(temp);
        await registry.RegisterRequestAsync("project", Request("one", "one"));
        await registry.RegisterRequestAsync("project", Request("two", "two") with { RunId = "other", TicketId = 99, ProviderName = "codex" });

        Assert.Equal("one", (await registry.QueryRequestsAsync("project", new(RunId: "run"))).Single().RequestId);
        Assert.Equal("two", (await registry.QueryRequestsAsync("project", new(TicketId: 99, Provider: "codex"))).Single().RequestId);
    }

    [Fact]
    public async Task Adversarial_ReceiptWithEndBeforeStart_IsRejected()
    {
        using var temp = new TempDir();
        var now = DateTime.UtcNow;
        var registry = CreateRegistry(temp);
        await registry.RegisterRequestAsync("project", Request("request", "dedupe", now, now.AddMinutes(10)));
        await registry.DecideAsync("project", Decision("decision", "request", now, now.AddMinutes(5)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddReceiptAsync("project",
            Receipt("backwards", now) with { EndedAt = now.AddSeconds(-1) }));
    }

    [Fact]
    public async Task Adversarial_InvalidConsumeOnceReceipt_RollsBackConsumption()
    {
        using var temp = new TempDir();
        var now = DateTime.UtcNow;
        var registry = CreateRegistry(temp);
        await registry.RegisterRequestAsync("project", Request("request", "dedupe", now, now.AddMinutes(10)));
        await registry.DecideAsync("project", Decision("decision", "request", now, now.AddMinutes(5)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ConsumeOnceAsync("project",
            Receipt("backwards", now) with { EndedAt = now.AddSeconds(-1) }));
        var accepted = await registry.ConsumeOnceAsync("project",
            Receipt("valid-after-rejection", now) with { EndedAt = now });

        Assert.Equal(now, accepted.StartedAt);
        Assert.Equal(now, accepted.EndedAt);
        Assert.Single(await registry.QueryReceiptsAsync("project", "request"));
    }

    [Fact]
    public async Task Adversarial_ConcurrentDecisions_CreateExactlyOneActiveDecision()
    {
        using var temp = new TempDir();
        var now = DateTime.UtcNow;
        var registry = CreateRegistry(temp);
        await registry.RegisterRequestAsync("project", Request("request", "dedupe", now, now.AddMinutes(10)));

        var attempts = Enumerable.Range(0, 8).Select(async i =>
        {
            try
            {
                await registry.DecideAsync("project", Decision($"decision-{i}", "request", now, now.AddMinutes(5)));
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(attempts);
        Assert.Single(results, x => x);
        Assert.Single(await registry.QueryDecisionsAsync("project", "request"));
    }

    [Fact]
    public async Task Adversarial_HttpRejectsReceiptWithEndBeforeStart()
    {
        using var factory = new ApprovalApiFactory();
        using var client = factory.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/projects", new { name = "Approval QA" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var slug = project.GetProperty("slug").GetString();
        var now = DateTime.UtcNow;

        (await client.PostAsJsonAsync($"/api/projects/{slug}/approvals/requests",
            Request("request", "dedupe", now, now.AddMinutes(10)))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/projects/{slug}/approvals/decisions",
            Decision("decision", "request", now, now.AddMinutes(5)))).EnsureSuccessStatusCode();
        var response = await client.PostAsJsonAsync($"/api/projects/{slug}/approvals/receipts",
            Receipt("backwards", now) with { EndedAt = now.AddSeconds(-1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class ApprovalApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir;

        public ApprovalApiFactory()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-approval-qa-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(Path.Combine(_dataDir, "settings.json"), "{\"OnboardingSeen\":true,\"Language\":\"en\"}");
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", _dataDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", null);
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }

    private static ApprovalRegistryService CreateRegistry(TempDir temp) => new(new ProjectService(temp.Path));

    private static ApprovalRequestInput Request(string id, string dedupe, DateTime? observed = null, DateTime? expires = null)
    {
        var now = observed ?? DateTime.UtcNow;
        return new(id, dedupe, 1, "source_publication", "git_push", "repository", "example/repo", "example/repo",
            "Publish changes", null, "repository", 600, "claude", "1.0", "1.0", "observation", "run", 167,
            "programmer", now, expires ?? now.AddMinutes(10), "shell", "SHA256:redacted", "provider_event");
    }

    private static ApprovalDecisionInput Decision(string id, string requestId, DateTime created, DateTime expires) =>
        new(id, requestId, ApprovalDecisionKind.AllowOnce, "owner", created, expires, "repository", "Approved", null);

    private static ApprovalReceiptInput Receipt(string id, DateTime started) =>
        new(id, "decision", "request", "test-gate", "1.0", "observation", started, started.AddSeconds(1),
            ApprovalReceiptOutcome.Allowed, "repository:example/repo");
}
