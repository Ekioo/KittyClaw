using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Api;

/// <summary>
/// Guard tests for ticket #158: split Endpoints.cs into per-domain partial-class files.
/// Encodes the architect's contract:
///   - Route inventory (path, verb) set must remain identical to the current 65-route baseline.
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
        // Projects
        "GET /api/projects",
        "POST /api/projects",
        "GET /api/projects/{slug}",
        "DELETE /api/projects/{slug}",
        "PATCH /api/projects/{slug}",
        "POST /api/projects/{slug}/pause",
        // Tickets
        "GET /api/projects/{slug}/tickets",
        "POST /api/projects/{slug}/tickets",
        "PATCH /api/projects/{slug}/tickets/{id}",
        "GET /api/projects/{slug}/tickets/{id}",
        "PATCH /api/projects/{slug}/tickets/{id}/status",
        "DELETE /api/projects/{slug}/tickets/{id}",
        "PUT /api/projects/{slug}/tickets/{id}/parent",
        "DELETE /api/projects/{slug}/tickets/{id}/parent",
        "POST /api/projects/{slug}/tickets/{id}/comments",
        "PATCH /api/projects/{slug}/tickets/{id}/comments/{commentId}",
        "DELETE /api/projects/{slug}/tickets/{id}/comments/{commentId}",
        "GET /api/projects/{slug}/tickets/{id}/activity",
        "PATCH /api/projects/{slug}/tickets/{id}/reorder",
        // Labels
        "GET /api/projects/{slug}/labels",
        "POST /api/projects/{slug}/labels",
        "DELETE /api/projects/{slug}/labels/{labelId}",
        "PATCH /api/projects/{slug}/labels/{labelId}",
        "GET /api/projects/{slug}/tickets/{id}/labels",
        "PUT /api/projects/{slug}/tickets/{id}/labels",
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
        // Automations
        "GET /api/projects/{slug}/automations",
        "PUT /api/projects/{slug}/automations",
        "POST /api/projects/{slug}/automations/reload",
        // Runs
        "GET /api/projects/{slug}/runs",
        "GET /api/projects/{slug}/runs/{runId}",
        "GET /api/projects/{slug}/runs/{runId}/stream",
        "POST /api/projects/{slug}/runs/{runId}/steer",
        "POST /api/projects/{slug}/runs/{runId}/stop",
        "POST /api/projects/{slug}/runs/{runId}/retry",
        // Chat
        "GET /api/projects/{slug}/chat/targets",
        "GET /api/projects/{slug}/chat/messages",
        "GET /api/projects/{slug}/chat/active",
        "DELETE /api/projects/{slug}/chat/session",
        "POST /api/projects/{slug}/chat/start",
        // Images
        "POST /api/images",
        // Dashboard
        "GET /api/projects/{slug}/dashboard/tiles",
        "POST /api/projects/{slug}/dashboard/tiles",
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
        ["GET /api/projects/{slug}/tickets"] = "Tickets",
        ["POST /api/projects/{slug}/tickets"] = "Tickets",
        ["PATCH /api/projects/{slug}/tickets/{id}"] = "Tickets",
        ["GET /api/projects/{slug}/tickets/{id}"] = "Tickets",
        ["PATCH /api/projects/{slug}/tickets/{id}/status"] = "Tickets",
        ["DELETE /api/projects/{slug}/tickets/{id}"] = "Tickets",
        ["PUT /api/projects/{slug}/tickets/{id}/parent"] = "Tickets",
        ["DELETE /api/projects/{slug}/tickets/{id}/parent"] = "Tickets",
        ["POST /api/projects/{slug}/tickets/{id}/comments"] = "Comments",
        ["PATCH /api/projects/{slug}/tickets/{id}/comments/{commentId}"] = "Comments",
        ["DELETE /api/projects/{slug}/tickets/{id}/comments/{commentId}"] = "Comments",
        ["GET /api/projects/{slug}/tickets/{id}/activity"] = "Activity",
        ["PATCH /api/projects/{slug}/tickets/{id}/reorder"] = "Tickets",
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
        ["GET /api/projects/{slug}/automations"] = "Automations",
        ["PUT /api/projects/{slug}/automations"] = "Automations",
        ["POST /api/projects/{slug}/automations/reload"] = "Automations",
        ["GET /api/projects/{slug}/runs"] = "Runs",
        ["GET /api/projects/{slug}/runs/{runId}"] = "Runs",
        ["GET /api/projects/{slug}/runs/{runId}/stream"] = "Runs",
        ["POST /api/projects/{slug}/runs/{runId}/steer"] = "Runs",
        ["POST /api/projects/{slug}/runs/{runId}/stop"] = "Runs",
        ["POST /api/projects/{slug}/runs/{runId}/retry"] = "Runs",
        ["GET /api/projects/{slug}/chat/targets"] = "Chat",
        ["GET /api/projects/{slug}/chat/messages"] = "Chat",
        ["GET /api/projects/{slug}/chat/active"] = "Chat",
        ["DELETE /api/projects/{slug}/chat/session"] = "Chat",
        ["POST /api/projects/{slug}/chat/start"] = "Chat",
        ["POST /api/images"] = "Images",
        ["GET /api/projects/{slug}/dashboard/tiles"] = "Dashboard",
        ["POST /api/projects/{slug}/dashboard/tiles"] = "Dashboard",
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
        })
        {
            var resp = await _client.GetAsync(path);
            Assert.True(resp.IsSuccessStatusCode,
                $"GET {path} returned {(int)resp.StatusCode} {resp.StatusCode}");
        }
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
