using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Web.Api;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KittyClaw.Core.Tests.Api;

/// <summary>
/// End-to-end coverage of the embedded MCP endpoint (ticket #217): a real MCP client
/// speaking Streamable HTTP against the full app pipeline, exactly like
/// `claude mcp add --transport http kittyclaw http://localhost:5230/mcp` would.
/// Also covers the opt-in KITTYCLAW_MCP_ENABLED feature flag.
/// </summary>
[Collection("MCP endpoint environment")]
public sealed class McpEndpointTests : IClassFixture<McpEndpointTests.ApiFactory>, IAsyncLifetime
{
    private static readonly string[] V1Tools =
    [
        "board_overview", "comment_ticket", "create_ticket",
        "get_ticket", "list_projects", "list_tickets", "move_ticket",
    ];

    private readonly ApiFactory _factory;
    private readonly HttpClient _http;
    private McpClient? _mcp;

    public McpEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_mcp is not null) await _mcp.DisposeAsync();
        _http.Dispose();
    }

    private async Task<McpClient> ConnectAsync()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, _http, loggerFactory: null, ownsHttpClient: false);
        return _mcp = await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task ToolsList_ExposesExactlyTheSevenV1Tools()
    {
        var client = await ConnectAsync();

        var tools = await client.ListToolsAsync();

        Assert.Equal(V1Tools, tools.Select(t => t.Name).Order().ToArray());
    }

    [Fact]
    public async Task InitializeProbe_Succeeds()
    {
        // Same raw probe the KITTYCLAW_MCP_DISABLED test uses — proving here that it
        // returns 200 when the endpoint is up makes its failure there meaningful.
        using var response = await McpInitializeProbe.SendAsync(_http);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CallTool_ListProjects_SeesProjectsCreatedViaRest()
    {
        var name = "mcp-e2e-" + Guid.NewGuid().ToString("N")[..8];
        var create = await _http.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("list_projects");

        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains(slug, text);
    }

    [Fact]
    public async Task CallTool_CreateThenMove_RoundTripsThroughTheBoard()
    {
        var create = await _http.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("mcp-e2e-" + Guid.NewGuid().ToString("N")[..8]));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var client = await ConnectAsync();

        var created = await client.CallToolAsync("create_ticket", new Dictionary<string, object?>
        {
            ["project"] = slug,
            ["title"] = "Born over MCP",
            ["status"] = "Todo",
            ["priority"] = "Required",
            ["author"] = "lain",
        });
        Assert.NotEqual(true, created.IsError);
        var ticket = JsonDocument.Parse(((TextContentBlock)created.Content[0]).Text).RootElement;
        var id = ticket.GetProperty("id").GetInt32();
        Assert.Equal("Required", ticket.GetProperty("priority").GetString());

        var moved = await client.CallToolAsync("move_ticket", new Dictionary<string, object?>
        {
            ["project"] = slug,
            ["id"] = id,
            ["status"] = "InProgress",
        });
        Assert.NotEqual(true, moved.IsError);

        // The move is observable through the REST API — one board, two protocols.
        var viaRest = await _http.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Equal("InProgress", viaRest.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CallTool_Comment_RoundTripsThroughTheBoard()
    {
        var create = await _http.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("mcp-e2e-" + Guid.NewGuid().ToString("N")[..8]));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var ticketResponse = await _http.PostAsJsonAsync($"/api/projects/{slug}/tickets", new
        {
            title = "Comment target", createdBy = "owner", status = "Todo",
        });
        ticketResponse.EnsureSuccessStatusCode();
        var id = (await ticketResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var client = await ConnectAsync();

        var commented = await client.CallToolAsync("comment_ticket", new Dictionary<string, object?>
        {
            ["project"] = slug,
            ["id"] = id,
            ["content"] = "Added over MCP",
            ["author"] = "mcp-test",
        });

        Assert.NotEqual(true, commented.IsError);
        var viaRest = await _http.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Contains(viaRest.GetProperty("comments").EnumerateArray(), comment =>
            comment.GetProperty("content").GetString() == "Added over MCP"
            && comment.GetProperty("author").GetString() == "mcp-test");
    }

    [Fact]
    public async Task CallTool_UnknownProject_ReturnsToolErrorWithGuidance()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("get_ticket", new Dictionary<string, object?>
        {
            ["project"] = "no-such-project",
            ["id"] = 1,
        });

        Assert.Equal(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("list_projects", text);

        // A business error is scoped to one tools/call request; the MCP session remains usable.
        var recovery = await client.CallToolAsync("list_projects");
        Assert.NotEqual(true, recovery.IsError);
    }

    [Fact]
    public async Task InvalidStatus_IsRejectedByBothRestAndMcp()
    {
        var create = await _http.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("mcp-e2e-" + Guid.NewGuid().ToString("N")[..8]));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var client = await ConnectAsync();

        var rest = await _http.PostAsJsonAsync($"/api/projects/{slug}/tickets", new
        {
            title = "Invalid REST", createdBy = "owner", status = "NoSuchColumn",
        });
        var mcp = await client.CallToolAsync("create_ticket", new Dictionary<string, object?>
        {
            ["project"] = slug,
            ["title"] = "Invalid MCP",
            ["status"] = "NoSuchColumn",
        });

        Assert.Equal(HttpStatusCode.BadRequest, rest.StatusCode);
        Assert.Equal(true, mcp.IsError);
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir;

        public ApiFactory()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-mcp-endpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(Path.Combine(_dataDir, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseSetting("KITTYCLAW_DATA_DIR", _dataDir)
            .UseSetting("KITTYCLAW_MCP_ENABLED", "1");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// A raw MCP `initialize` request against /mcp. 200 when the endpoint is live; anything
/// else (Blazor fallback: 400 antiforgery / 404) when the feature flag removed the route.
/// </summary>
internal static class McpInitializeProbe
{
    public static Task<HttpResponseMessage> SendAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1.0"}}}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return client.SendAsync(request);
    }
}

/// <summary>
/// The default configuration must omit the endpoint entirely — no MCP route, no tools.
/// Separate class: the flag is read once at startup, so it needs its own host.
/// </summary>
[Collection("MCP endpoint environment")]
public sealed class McpDisabledByDefaultTests : IDisposable
{
    private sealed class DisabledFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir;

        public DisabledFactory()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-mcp-disabled-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(Path.Combine(_dataDir, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseSetting("KITTYCLAW_DATA_DIR", _dataDir)
            .UseSetting("KITTYCLAW_MCP_ENABLED", "0");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }

    private readonly DisabledFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task DisabledFlag_RemovesTheMcpEndpoint_ButKeepsTheRestApi()
    {
        using var client = _factory.CreateClient();

        using var mcpResponse = await McpInitializeProbe.SendAsync(client);
        Assert.False(mcpResponse.IsSuccessStatusCode,
            $"MCP initialize succeeded ({(int)mcpResponse.StatusCode}) without KITTYCLAW_MCP_ENABLED=1");

        var rest = await client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
    }
}

[CollectionDefinition("MCP endpoint environment", DisableParallelization = true)]
public sealed class McpEndpointEnvironmentCollection;
