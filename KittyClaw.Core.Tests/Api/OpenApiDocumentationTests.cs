using System.Net.Http;
using System.Text.Json;

namespace KittyClaw.Core.Tests.Api;

/// <summary>
/// Integration tests against the live server's OpenAPI spec and /api/docs endpoint.
/// The server must be running (dotnet watch) for these tests to execute.
/// Tests are intentionally RED until Endpoints.cs is annotated with .Produces&lt;T&gt;()
/// and OpenApiMarkdownGenerator.GetExampleValue special-cases the "author" field.
/// </summary>
public class OpenApiDocumentationTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public OpenApiDocumentationTests()
    {
        _baseUrl = Environment.GetEnvironmentVariable("KITTYCLAW_API_URL") ?? "http://localhost:5230";
        _client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public void Dispose() => _client.Dispose();

    // Case 1: author example value
    [Fact]
    public async Task ApiDocs_AuthorField_ShowsOwnerNotEllipsis()
    {
        var response = await _client.GetAsync("/api/docs");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"author\": \"owner\"", body);
        Assert.DoesNotContain("\"author\": \"...\"", body);
    }

    // Case 2: GET /tickets/{id} 200 response has content schema
    [Fact]
    public async Task OpenApiSpec_GetTicketById_Has200ContentSchema()
    {
        var doc = await FetchOpenApiDoc();
        var paths = doc.RootElement.GetProperty("paths");

        JsonElement? getTicketPath = null;
        foreach (var path in paths.EnumerateObject())
        {
            if (path.Name.EndsWith("/tickets/{id}") && path.Value.TryGetProperty("get", out _))
            {
                getTicketPath = path.Value.GetProperty("get");
                break;
            }
        }

        Assert.NotNull(getTicketPath);
        var responses = getTicketPath!.Value.GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out var resp200), "200 response must be declared");
        Assert.True(resp200.TryGetProperty("content", out _), "200 response must have a content schema");
    }

    // Case 3: POST /tickets lists 201 with schema and 400
    [Fact]
    public async Task OpenApiSpec_PostTickets_Has201WithSchemaAnd400()
    {
        var doc = await FetchOpenApiDoc();
        var paths = doc.RootElement.GetProperty("paths");

        JsonElement? postTickets = null;
        foreach (var path in paths.EnumerateObject())
        {
            if (path.Name.EndsWith("/tickets") && path.Value.TryGetProperty("post", out var op))
            {
                postTickets = op;
                break;
            }
        }

        Assert.NotNull(postTickets);
        var responses = postTickets!.Value.GetProperty("responses");

        Assert.True(responses.TryGetProperty("201", out var resp201), "201 response must be declared");
        Assert.True(resp201.TryGetProperty("content", out _), "201 response must have a content schema");
        Assert.True(responses.TryGetProperty("400", out _), "400 response must be declared");
    }

    // Case 4: GET /tickets (list) 200 response has content schema
    [Fact]
    public async Task OpenApiSpec_GetTicketsList_Has200ContentSchema()
    {
        var doc = await FetchOpenApiDoc();
        var paths = doc.RootElement.GetProperty("paths");

        JsonElement? getTickets = null;
        foreach (var path in paths.EnumerateObject())
        {
            // Match the list endpoint: ends with /tickets, not /tickets/{id}
            if (path.Name.EndsWith("/tickets") && !path.Name.Contains("{id}") && path.Value.TryGetProperty("get", out var op))
            {
                getTickets = op;
                break;
            }
        }

        Assert.NotNull(getTickets);
        var responses = getTickets!.Value.GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out var resp200), "200 response must be declared");
        Assert.True(resp200.TryGetProperty("content", out _), "200 response must have a content schema");
    }

    // Case 5: DELETE endpoints list 204 and 404
    [Fact]
    public async Task OpenApiSpec_DeleteTicket_Has204And404()
    {
        var doc = await FetchOpenApiDoc();
        var paths = doc.RootElement.GetProperty("paths");

        JsonElement? deleteTicket = null;
        foreach (var path in paths.EnumerateObject())
        {
            if (path.Name.Contains("/tickets/") && path.Value.TryGetProperty("delete", out var op))
            {
                deleteTicket = op;
                break;
            }
        }

        Assert.NotNull(deleteTicket);
        var responses = deleteTicket!.Value.GetProperty("responses");
        Assert.True(responses.TryGetProperty("204", out _), "204 NoContent must be declared on DELETE");
        Assert.True(responses.TryGetProperty("404", out _), "404 NotFound must be declared on DELETE");
    }

    private async Task<JsonDocument> FetchOpenApiDoc()
    {
        var json = await _client.GetStringAsync("/openapi/v1.json");
        return JsonDocument.Parse(json);
    }
}
