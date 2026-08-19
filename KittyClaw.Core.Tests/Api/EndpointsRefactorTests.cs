using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Core.Automation;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Api;

/// <summary>
/// Guard tests for ticket #158: split Endpoints.cs into per-domain partial-class files.
/// Encodes the architect's contract:
///   - Route inventory (path, verb) set must remain identical to the current 64-route baseline.
///   - Each route keeps its existing OpenAPI tag.
///   - Each domain still answers a representative request through the in-process host.
///   - Endpoints.cs becomes a thin orchestrator and per-domain partial files exist.
/// Source-text assertions are RED on dev (monolith) and GREEN after the refactor.
/// </summary>
public sealed class EndpointsRefactorTests : IClassFixture<EndpointsRefactorTests.ApiFactory>, IDisposable
{
    private readonly HttpClient _client;
    private readonly ApiFactory _factory;

    public EndpointsRefactorTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    // ---------- Case 1: route inventory preserved ----------

    private static readonly HashSet<string> ExpectedRoutes = new(StringComparer.Ordinal)
    {
        // Columns
        "GET /api/projects/{slug}/columns",
        "POST /api/projects/{slug}/columns",
        "PATCH /api/projects/{slug}/columns/{columnId}",
        "DELETE /api/projects/{slug}/columns/{columnId}",
        "PATCH /api/projects/{slug}/columns/reorder",
        // Pipelines
        "GET /api/projects/{slug}/pipelines",
        "POST /api/projects/{slug}/pipelines",
        "PATCH /api/projects/{slug}/pipelines/{pipelineId}",
        "GET /api/projects/{slug}/pipelines/{pipelineId}/export",
        "POST /api/projects/{slug}/pipeline-kits/analyze",
        "POST /api/projects/{slug}/pipeline-kits/confirm",
        // Workflow migrations
        "POST /api/projects/{slug}/workflow-migrations/analyze",
        "POST /api/projects/{slug}/workflow-migrations/apply",
        "POST /api/projects/{slug}/workflow-migrations/refine",
        "GET /api/projects/{slug}/workflow-migrations/jobs/{jobId}",
        // Projects
        "GET /api/projects",
        "POST /api/projects",
        "GET /api/projects/{slug}",
        "DELETE /api/projects/{slug}",
        "PATCH /api/projects/{slug}",
        "POST /api/projects/{slug}/pause",
        "PATCH /api/projects/{slug}/local-model",
        "GET /api/projects/{slug}/rtk",
        "GET /api/projects/{slug}/git",
        "POST /api/projects/{slug}/git/init",
        "PATCH /api/projects/{slug}/rtk",
        "GET /api/projects/{slug}/secrets",
        "PUT /api/projects/{slug}/secrets/{name}",
        "DELETE /api/projects/{slug}/secrets/{name}",
        "GET /api/projects/{slug}/worktree-merges",
        "POST /api/projects/{slug}/worktree-merges",
        "POST /api/projects/{slug}/worktree-merges/process-next",
        "POST /api/projects/{slug}/worktree-merges/{requestId}/resume",
        // Tickets
        "GET /api/projects/{slug}/tickets",
        "POST /api/projects/{slug}/tickets",
        "PATCH /api/projects/{slug}/tickets/{id}",
        "GET /api/projects/{slug}/tickets/{id}",
        "POST /api/projects/{slug}/tickets/{id}/transfer",
        "PATCH /api/projects/{slug}/tickets/{id}/status",
        "DELETE /api/projects/{slug}/tickets/{id}",
        "PUT /api/projects/{slug}/tickets/{id}/parent",
        "DELETE /api/projects/{slug}/tickets/{id}/parent",
        "POST /api/projects/{slug}/tickets/{id}/comments",
        "PATCH /api/projects/{slug}/tickets/{id}/comments/{commentId}",
        "DELETE /api/projects/{slug}/tickets/{id}/comments/{commentId}",
        "GET /api/projects/{slug}/tickets/{id}/activity",
        "PATCH /api/projects/{slug}/tickets/{id}/reorder",
        "PATCH /api/projects/{slug}/tickets/{id}/schedule",
        // Dependencies
        "POST /api/projects/{slug}/tickets/{id}/dependencies",
        "DELETE /api/projects/{slug}/tickets/{id}/dependencies/{depId}",
        // Labels
        "GET /api/projects/{slug}/labels",
        "POST /api/projects/{slug}/labels",
        "DELETE /api/projects/{slug}/labels/{labelId}",
        "PATCH /api/projects/{slug}/labels/{labelId}",
        "GET /api/projects/{slug}/tickets/{id}/labels",
        "PUT /api/projects/{slug}/tickets/{id}/labels",
        "PATCH /api/projects/{slug}/tickets/{id}/labels",
        // Members
        "GET /api/projects/{slug}/members",
        "POST /api/projects/{slug}/members",
        "PATCH /api/projects/{slug}/members/{memberId}",
        "DELETE /api/projects/{slug}/members/{memberId}",
        "GET /api/projects/{slug}/mentions/{handle}",
        // Browse
        "GET /api/browse/capabilities",
        "POST /api/browse/folder",
        // Skills
        "GET /api/projects/{slug}/skills",
        "GET /api/projects/{slug}/project-skills",
        "GET /api/projects/{slug}/project-skills/{skillSlug}",
        "POST /api/projects/{slug}/project-skills",
        "PATCH /api/projects/{slug}/project-skills/{skillSlug}",
        "DELETE /api/projects/{slug}/project-skills/{skillSlug}",
        // Column processors
        "GET /api/projects/{slug}/columns/{columnId}/processor",
        "PUT /api/projects/{slug}/columns/{columnId}/processor",
        "DELETE /api/projects/{slug}/columns/{columnId}/processor",
        // Column execution runtime
        "GET /api/projects/{slug}/column-executions",
        "POST /api/projects/{slug}/column-executions/{executionId}/retry",
        "POST /api/projects/{slug}/column-executions/{executionId}/cancel",
        // Ollama
        "GET /api/projects/{slug}/ollama-models",
        // Grok
        "GET /api/grok-models",
        // Codex
        "GET /api/codex-models",
        // Mistral
        "GET /api/mistral-models",
        // DeepSeek
        "GET /api/deepseek-models",
        // Approval registry
        "GET /api/projects/{slug}/approvals/requests",
        "POST /api/projects/{slug}/approvals/requests",
        "GET /api/projects/{slug}/approvals/decisions",
        "POST /api/projects/{slug}/approvals/decisions",
        "GET /api/projects/{slug}/approvals/receipts",
        "POST /api/projects/{slug}/approvals/receipts",
        "POST /api/projects/{slug}/approvals/consume-once",
        "POST /api/projects/{slug}/approvals/gate",
        "GET /api/runtime-enforcement/capabilities",
        // Observation-only boundary detection
        "GET /api/projects/{slug}/boundary-observations",
        "GET /api/projects/{slug}/boundary-observations/metrics",
        "PUT /api/projects/{slug}/boundary-observations/{observationId}/review",
        // Automations
        "GET /api/projects/{slug}/automations",
        "POST /api/projects/{slug}/automations/{automationId}/disable",
        "DELETE /api/projects/{slug}/automations/{automationId}",
        "GET /api/projects/{slug}/tickets/{ticketId}/automation-queue",
        // Engine
        "GET /api/engine/health",
        // Runs
        "GET /api/projects/{slug}/runs",
        "GET /api/projects/{slug}/runs/{runId}",
        "GET /api/projects/{slug}/runs/{runId}/stream",
        "POST /api/projects/{slug}/runs/{runId}/steer",
        "POST /api/projects/{slug}/runs/{runId}/stop",
        "POST /api/projects/{slug}/runs/{runId}/retry",
        "GET /api/projects/{slug}/concurrency-groups",
        // Evidence
        "GET /api/projects/{slug}/runs/{runId}/evidence",
        "GET /api/projects/{slug}/tickets/{id}/evidence",
        "GET /api/projects/{slug}/tickets/{id}/brief",
        // Chat
        "GET /api/projects/{slug}/chat/targets",
        "GET /api/projects/{slug}/chat/messages",
        "GET /api/projects/{slug}/chat/active",
        "GET /api/projects/{slug}/chat/model",
        "DELETE /api/projects/{slug}/chat/session",
        "POST /api/projects/{slug}/chat/start",
        // Images
        "POST /api/images",
        // Dashboard
        "GET /api/projects/{slug}/dashboard/tiles",
        "DELETE /api/projects/{slug}/dashboard/tiles/{tileSlug}",
        "PATCH /api/projects/{slug}/dashboard/tiles/{tileSlug}/position",
        "PATCH /api/projects/{slug}/dashboard/tiles/{tileSlug}/size",
        "GET /api/projects/{slug}/dashboard/tiles/{tileSlug}/output",
        "GET /api/projects/{slug}/dashboard/tiles/{tileSlug}/output/raw",
        "PUT /api/projects/{slug}/dashboard/tiles/{tileSlug}/output",
        "GET /api/projects/{slug}/dashboard/tiles/{tileSlug}/sidecar",
        "PUT /api/projects/{slug}/dashboard/tiles/{tileSlug}/sidecar",
        "GET /api/projects/{slug}/dashboard/tiles/{tileSlug}/script",
        "POST /api/projects/{slug}/dashboard/tiles/{tileSlug}/refresh",
    };

    [Fact]
    public async Task OpenApi_RouteInventory_MatchesGoldenSet()
    {
        var actual = await ReadRouteInventoryAsync();
        var missing = ExpectedRoutes.Except(actual).OrderBy(x => x).ToList();
        var extra = actual.Except(ExpectedRoutes).OrderBy(x => x).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"Route inventory drift.\nMissing ({missing.Count}):\n  {string.Join("\n  ", missing)}\nExtra ({extra.Count}):\n  {string.Join("\n  ", extra)}");
    }

    // ---------- Case 2: tags preserved ----------

    private static readonly Dictionary<string, string> ExpectedTags = new(StringComparer.Ordinal)
    {
        ["POST /api/projects/{slug}/pipeline-kits/analyze"] = "Pipelines",
        ["POST /api/projects/{slug}/pipeline-kits/confirm"] = "Pipelines",
        ["GET /api/projects/{slug}/columns"] = "Columns",
        ["POST /api/projects/{slug}/columns"] = "Columns",
        ["PATCH /api/projects/{slug}/columns/{columnId}"] = "Columns",
        ["DELETE /api/projects/{slug}/columns/{columnId}"] = "Columns",
        ["PATCH /api/projects/{slug}/columns/reorder"] = "Columns",
        ["GET /api/projects"] = "Projects",
        ["POST /api/projects"] = "Projects",
        ["GET /api/projects/{slug}"] = "Projects",
        ["DELETE /api/projects/{slug}"] = "Projects",
        ["PATCH /api/projects/{slug}"] = "Projects",
        ["POST /api/projects/{slug}/pause"] = "Projects",
        ["PATCH /api/projects/{slug}/local-model"] = "Projects",
        ["GET /api/projects/{slug}/rtk"] = "Projects",
        ["GET /api/projects/{slug}/git"] = "Projects",
        ["POST /api/projects/{slug}/git/init"] = "Projects",
        ["PATCH /api/projects/{slug}/rtk"] = "Projects",
        ["GET /api/projects/{slug}/secrets"] = "Project secrets",
        ["PUT /api/projects/{slug}/secrets/{name}"] = "Project secrets",
        ["DELETE /api/projects/{slug}/secrets/{name}"] = "Project secrets",
        ["GET /api/projects/{slug}/worktree-merges"] = "Worktree merges",
        ["POST /api/projects/{slug}/worktree-merges"] = "Worktree merges",
        ["POST /api/projects/{slug}/worktree-merges/process-next"] = "Worktree merges",
        ["POST /api/projects/{slug}/worktree-merges/{requestId}/resume"] = "Worktree merges",
        ["GET /api/projects/{slug}/tickets"] = "Tickets",
        ["POST /api/projects/{slug}/tickets"] = "Tickets",
        ["PATCH /api/projects/{slug}/tickets/{id}"] = "Tickets",
        ["GET /api/projects/{slug}/tickets/{id}"] = "Tickets",
        ["POST /api/projects/{slug}/tickets/{id}/transfer"] = "Tickets",
        ["PATCH /api/projects/{slug}/tickets/{id}/status"] = "Tickets",
        ["DELETE /api/projects/{slug}/tickets/{id}"] = "Tickets",
        ["PUT /api/projects/{slug}/tickets/{id}/parent"] = "Tickets",
        ["DELETE /api/projects/{slug}/tickets/{id}/parent"] = "Tickets",
        ["POST /api/projects/{slug}/tickets/{id}/comments"] = "Comments",
        ["PATCH /api/projects/{slug}/tickets/{id}/comments/{commentId}"] = "Comments",
        ["DELETE /api/projects/{slug}/tickets/{id}/comments/{commentId}"] = "Comments",
        ["GET /api/projects/{slug}/tickets/{id}/activity"] = "Activity",
        ["PATCH /api/projects/{slug}/tickets/{id}/reorder"] = "Tickets",
        ["PATCH /api/projects/{slug}/tickets/{id}/schedule"] = "Tickets",
        ["POST /api/projects/{slug}/tickets/{id}/dependencies"] = "Dependencies",
        ["DELETE /api/projects/{slug}/tickets/{id}/dependencies/{depId}"] = "Dependencies",
        ["GET /api/projects/{slug}/labels"] = "Labels",
        ["POST /api/projects/{slug}/labels"] = "Labels",
        ["DELETE /api/projects/{slug}/labels/{labelId}"] = "Labels",
        ["PATCH /api/projects/{slug}/labels/{labelId}"] = "Labels",
        ["GET /api/projects/{slug}/tickets/{id}/labels"] = "Labels",
        ["PUT /api/projects/{slug}/tickets/{id}/labels"] = "Labels",
        ["GET /api/projects/{slug}/members"] = "Members",
        ["POST /api/projects/{slug}/members"] = "Members",
        ["PATCH /api/projects/{slug}/members/{memberId}"] = "Members",
        ["DELETE /api/projects/{slug}/members/{memberId}"] = "Members",
        ["GET /api/projects/{slug}/mentions/{handle}"] = "Mentions",
        ["GET /api/browse/capabilities"] = "Browse",
        ["POST /api/browse/folder"] = "Browse",
        ["GET /api/projects/{slug}/skills"] = "Automations",
        ["GET /api/projects/{slug}/ollama-models"] = "Ollama",
        ["GET /api/projects/{slug}/approvals/requests"] = "Approvals",
        ["POST /api/projects/{slug}/approvals/requests"] = "Approvals",
        ["GET /api/projects/{slug}/approvals/decisions"] = "Approvals",
        ["POST /api/projects/{slug}/approvals/decisions"] = "Approvals",
        ["GET /api/projects/{slug}/approvals/receipts"] = "Approvals",
        ["POST /api/projects/{slug}/approvals/receipts"] = "Approvals",
        ["POST /api/projects/{slug}/approvals/consume-once"] = "Approvals",
        ["POST /api/projects/{slug}/approvals/gate"] = "Approvals",
        ["GET /api/runtime-enforcement/capabilities"] = "Approvals",
        ["GET /api/projects/{slug}/boundary-observations"] = "Boundary observations",
        ["GET /api/projects/{slug}/boundary-observations/metrics"] = "Boundary observations",
        ["PUT /api/projects/{slug}/boundary-observations/{observationId}/review"] = "Boundary observations",
        ["GET /api/grok-models"] = "Grok",
        ["GET /api/codex-models"] = "Codex",
        ["GET /api/mistral-models"] = "Mistral",
        ["GET /api/deepseek-models"] = "DeepSeek",
        ["POST /api/projects/{slug}/workflow-migrations/analyze"] = "Workflow migrations",
        ["POST /api/projects/{slug}/workflow-migrations/apply"] = "Workflow migrations",
        ["POST /api/projects/{slug}/workflow-migrations/refine"] = "Workflow migrations",
        ["GET /api/projects/{slug}/workflow-migrations/jobs/{jobId}"] = "Workflow migrations",
        ["GET /api/projects/{slug}/automations"] = "Automations",
        ["POST /api/projects/{slug}/automations/{automationId}/disable"] = "Automations",
        ["DELETE /api/projects/{slug}/automations/{automationId}"] = "Automations",
        ["GET /api/projects/{slug}/tickets/{ticketId}/automation-queue"] = "Automations",
        ["GET /api/engine/health"] = "Engine",
        ["GET /api/projects/{slug}/runs"] = "Runs",
        ["GET /api/projects/{slug}/runs/{runId}"] = "Runs",
        ["GET /api/projects/{slug}/runs/{runId}/stream"] = "Runs",
        ["POST /api/projects/{slug}/runs/{runId}/steer"] = "Runs",
        ["POST /api/projects/{slug}/runs/{runId}/stop"] = "Runs",
        ["POST /api/projects/{slug}/runs/{runId}/retry"] = "Runs",
        ["GET /api/projects/{slug}/concurrency-groups"] = "Runs",
        ["GET /api/projects/{slug}/runs/{runId}/evidence"] = "Runs",
        ["GET /api/projects/{slug}/tickets/{id}/evidence"] = "Evidence",
        ["GET /api/projects/{slug}/tickets/{id}/brief"] = "Evidence",
        ["GET /api/projects/{slug}/chat/targets"] = "Chat",
        ["GET /api/projects/{slug}/chat/messages"] = "Chat",
        ["GET /api/projects/{slug}/chat/active"] = "Chat",
        ["GET /api/projects/{slug}/chat/model"] = "Chat",
        ["DELETE /api/projects/{slug}/chat/session"] = "Chat",
        ["POST /api/projects/{slug}/chat/start"] = "Chat",
        ["POST /api/images"] = "Images",
        ["GET /api/projects/{slug}/dashboard/tiles"] = "Dashboard",
        ["DELETE /api/projects/{slug}/dashboard/tiles/{tileSlug}"] = "Dashboard",
        ["PATCH /api/projects/{slug}/dashboard/tiles/{tileSlug}/position"] = "Dashboard",
        ["PATCH /api/projects/{slug}/dashboard/tiles/{tileSlug}/size"] = "Dashboard",
        ["GET /api/projects/{slug}/dashboard/tiles/{tileSlug}/output"] = "Dashboard",
        ["GET /api/projects/{slug}/dashboard/tiles/{tileSlug}/output/raw"] = "Dashboard",
        ["PUT /api/projects/{slug}/dashboard/tiles/{tileSlug}/output"] = "Dashboard",
        ["GET /api/projects/{slug}/dashboard/tiles/{tileSlug}/sidecar"] = "Dashboard",
        ["PUT /api/projects/{slug}/dashboard/tiles/{tileSlug}/sidecar"] = "Dashboard",
        ["GET /api/projects/{slug}/dashboard/tiles/{tileSlug}/script"] = "Dashboard",
        ["POST /api/projects/{slug}/dashboard/tiles/{tileSlug}/refresh"] = "Dashboard",
    };

    [Fact]
    public async Task OpenApi_Tags_ArePreservedPerRoute()
    {
        var tags = await ReadRouteTagsAsync();
        var mismatches = new List<string>();
        foreach (var kv in ExpectedTags)
        {
            if (!tags.TryGetValue(kv.Key, out var actual))
            {
                mismatches.Add($"missing route: {kv.Key}");
                continue;
            }
            if (!string.Equals(actual, kv.Value, StringComparison.Ordinal))
                mismatches.Add($"{kv.Key}: expected tag '{kv.Value}', got '{actual}'");
        }
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    // ---------- Case 3: per-domain smoke test ----------

    [Theory]
    [InlineData("/api/projects")]
    [InlineData("/api/browse/capabilities")]
    public async Task DomainRoute_IsRegistered_ReturnsSuccess(string path)
    {
        var resp = await _client.GetAsync(path);
        Assert.True(resp.IsSuccessStatusCode,
            $"GET {path} returned {(int)resp.StatusCode} {resp.StatusCode}");
    }

    [Fact]
    public async Task DomainRoutes_PerProjectGet_ReturnSuccess()
    {
        // Create a project so per-slug endpoints have something to read.
        var slug = await CreateProjectAsync("Refactor158QA");
        foreach (var path in new[]
        {
            $"/api/projects/{slug}/columns",
            $"/api/projects/{slug}/tickets",
            $"/api/projects/{slug}/labels",
            $"/api/projects/{slug}/members",
            $"/api/projects/{slug}/automations",
            $"/api/projects/{slug}/skills",
            $"/api/projects/{slug}/runs",
            $"/api/projects/{slug}/chat/targets",
            $"/api/projects/{slug}/dashboard/tiles",
            $"/api/projects/{slug}/secrets",
        })
        {
            var resp = await _client.GetAsync(path);
            Assert.True(resp.IsSuccessStatusCode,
                $"GET {path} returned {(int)resp.StatusCode} {resp.StatusCode}");
        }
    }

    [Fact]
    public async Task Automations_CanBeReadDisabledAndDeletedWithoutChangingOtherDefinitions()
    {
        var (slug, path) = await CreateAutomationProjectAsync("RestrictedAutomationQA");
        var config = new AutomationConfig
        {
            Automations =
            {
                new KittyClaw.Core.Automation.Automation
                {
                    Id = "target",
                    Name = "Target",
                    Trigger = new IntervalTriggerSpec { Seconds = 3600 },
                },
                new KittyClaw.Core.Automation.Automation
                {
                    Id = "preserved",
                    Name = "Preserved",
                    Trigger = new IntervalTriggerSpec { Seconds = 7200 },
                },
            },
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, AutomationStore.JsonOptions));

        var read = await _client.GetAsync($"/api/projects/{slug}/automations");
        read.EnsureSuccessStatusCode();

        var disabled = await _client.PostAsync($"/api/projects/{slug}/automations/target/disable", null);
        disabled.EnsureSuccessStatusCode();
        var afterDisable = JsonSerializer.Deserialize<AutomationConfig>(await File.ReadAllTextAsync(path), AutomationStore.JsonOptions)!;
        var disabledTarget = afterDisable.Automations.Single(a => a.Id == "target");
        Assert.False(disabledTarget.Enabled);
        Assert.Equal("Target", disabledTarget.Name);
        Assert.Equal(3600, Assert.IsType<IntervalTriggerSpec>(disabledTarget.Trigger).Seconds);
        Assert.True(afterDisable.Automations.Single(a => a.Id == "preserved").Enabled);

        var deleted = await _client.DeleteAsync($"/api/projects/{slug}/automations/target");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, deleted.StatusCode);
        var afterDelete = JsonSerializer.Deserialize<AutomationConfig>(await File.ReadAllTextAsync(path), AutomationStore.JsonOptions)!;
        Assert.DoesNotContain(afterDelete.Automations, a => a.Id == "target");
        Assert.Contains(afterDelete.Automations, a => a.Id == "preserved");
    }

    [Fact]
    public async Task AutomationCreationAndArbitraryEditingRoutes_AreNotExposed()
    {
        var (slug, path) = await CreateAutomationProjectAsync("NoAutomationUpsertQA");
        var original = new AutomationConfig
        {
            Automations = { new KittyClaw.Core.Automation.Automation { Id = "existing", Trigger = new IntervalTriggerSpec { Seconds = 3600 } } },
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(original, AutomationStore.JsonOptions));
        var before = await File.ReadAllBytesAsync(path);

        using var payload = JsonContent.Create(new AutomationConfig());
        var response = await _client.PutAsync($"/api/projects/{slug}/automations", payload);

        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AutomationMutation_UnknownIdReturnsNotFoundWithoutChangingFile()
    {
        var (slug, path) = await CreateAutomationProjectAsync("UnknownAutomationQA");
        var original = new AutomationConfig
        {
            Automations = { new KittyClaw.Core.Automation.Automation { Id = "existing", Trigger = new IntervalTriggerSpec { Seconds = 3600 } } },
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(original, AutomationStore.JsonOptions));
        var before = await File.ReadAllBytesAsync(path);

        var disable = await _client.PostAsync($"/api/projects/{slug}/automations/missing/disable", null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, disable.StatusCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));

        var delete = await _client.DeleteAsync($"/api/projects/{slug}/automations/missing");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AutomationMutations_ConcurrentRequestsPreserveUntargetedDefinitions()
    {
        var (slug, path) = await CreateAutomationProjectAsync("ConcurrentAutomationQA");
        var config = new AutomationConfig
        {
            Automations =
            {
                new KittyClaw.Core.Automation.Automation { Id = "disable", Trigger = new IntervalTriggerSpec { Seconds = 3600 } },
                new KittyClaw.Core.Automation.Automation { Id = "delete", Trigger = new IntervalTriggerSpec { Seconds = 3600 } },
                new KittyClaw.Core.Automation.Automation { Id = "keep", Trigger = new IntervalTriggerSpec { Seconds = 3600 } },
            },
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, AutomationStore.JsonOptions));

        var responses = await Task.WhenAll(
            _client.PostAsync($"/api/projects/{slug}/automations/disable/disable", null),
            _client.DeleteAsync($"/api/projects/{slug}/automations/delete"));
        Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode));

        var result = JsonSerializer.Deserialize<AutomationConfig>(await File.ReadAllTextAsync(path), AutomationStore.JsonOptions)!;
        Assert.False(result.Automations.Single(a => a.Id == "disable").Enabled);
        Assert.DoesNotContain(result.Automations, a => a.Id == "delete");
        Assert.True(result.Automations.Single(a => a.Id == "keep").Enabled);
    }

    // ---------- Case 4: structural — Endpoints.cs is split into per-domain partial files ----------
    // These two assertions are RED on dev (monolith 951 lines) and GREEN after refactor.

    [Fact]
    public void EndpointsFile_IsThinOrchestrator()
    {
        var path = LocateRepoFile("KittyClaw.Web/Api/Endpoints.cs");
        var lineCount = File.ReadAllLines(path).Length;
        Assert.True(lineCount <= 200,
            $"Endpoints.cs should be a thin orchestrator (≤200 lines) after the refactor, but has {lineCount} lines.");
    }

    [Fact]
    public void PerDomain_PartialFiles_Exist()
    {
        var apiDir = Path.GetDirectoryName(LocateRepoFile("KittyClaw.Web/Api/Endpoints.cs"))!;
        var expected = new[]
        {
            "Endpoints.Columns.cs",
            "Endpoints.Projects.cs",
            "Endpoints.Tickets.cs",
            "Endpoints.Labels.cs",
            "Endpoints.Members.cs",
            "Endpoints.Browse.cs",
            "Endpoints.Skills.cs",
            "Endpoints.Automations.cs",
            "Endpoints.Runs.cs",
            "Endpoints.Chat.cs",
            "Endpoints.Images.cs",
            "Endpoints.Dashboard.cs",
            "Endpoints.Ollama.cs",
            "Endpoints.DeepSeek.cs",
        };
        var missing = expected.Where(f => !File.Exists(Path.Combine(apiDir, f))).ToList();
        Assert.True(missing.Count == 0,
            $"Missing per-domain partial files under KittyClaw.Web/Api/: {string.Join(", ", missing)}");
    }

    // ---------- helpers ----------

    private async Task<HashSet<string>> ReadRouteInventoryAsync()
    {
        var json = await _client.GetStringAsync("/openapi/v1.json");
        using var doc = JsonDocument.Parse(json);
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("paths", out var paths)) return result;
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject())
            {
                if (!IsHttpMethod(op.Name)) continue;
                result.Add($"{op.Name.ToUpperInvariant()} {path.Name}");
            }
        }
        return result;
    }

    private async Task<Dictionary<string, string>> ReadRouteTagsAsync()
    {
        var json = await _client.GetStringAsync("/openapi/v1.json");
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("paths", out var paths)) return result;
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject())
            {
                if (!IsHttpMethod(op.Name)) continue;
                string? tag = null;
                if (op.Value.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0)
                    tag = tags[0].GetString();
                result[$"{op.Name.ToUpperInvariant()} {path.Name}"] = tag ?? "";
            }
        }
        return result;
    }

    private static bool IsHttpMethod(string name) => name is "get" or "post" or "put" or "patch" or "delete" or "head" or "options";

    private async Task<string> CreateProjectAsync(string name)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name));
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("slug").GetString()!;
    }

    private async Task<(string Slug, string ConfigPath)> CreateAutomationProjectAsync(string name)
    {
        var slug = await CreateProjectAsync(name + Guid.NewGuid().ToString("N"));
        var agentsDir = Path.Combine(_factory.DataDir, "projects", slug, ".agents");
        Directory.CreateDirectory(agentsDir);
        return (slug, Path.Combine(agentsDir, "automations.json"));
    }

    private static string LocateRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} from {AppContext.BaseDirectory}");
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir;
        public string DataDir => _dataDir;
        public string WorkspaceDir { get; }

        public ApiFactory()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-refactor158-" + Guid.NewGuid().ToString("N"));
            WorkspaceDir = Path.Combine(_dataDir, "ws");
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(WorkspaceDir);
            File.WriteAllText(Path.Combine(_dataDir, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", _dataDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", null);
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }
}
