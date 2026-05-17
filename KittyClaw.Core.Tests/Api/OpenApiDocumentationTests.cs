using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Api;

/// <summary>
/// Integration tests for OpenAPI documentation correctness.
/// Use WebApplicationFactory so tests are self-contained — no live dev server required.
/// Tests are RED until Endpoints.cs has .Produces&lt;T&gt;() annotations and
/// OpenApiMarkdownGenerator.GetExampleValue special-cases the "author" field.
/// </summary>
public sealed class OpenApiDocumentationTests : IClassFixture<OpenApiDocumentationTests.ApiFactory>, IDisposable
{
    private readonly HttpClient _client;

    public OpenApiDocumentationTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    // Case 1: author example value in the generated docs markdown
    // /api/docs self-calls the server via HttpClient (breaks in in-process test host),
    // so we exercise the generator directly with the spec from /openapi/v1.json.
    [Fact]
    public async Task ApiDocs_AuthorField_ShowsOwnerNotEllipsis()
    {
        using var doc = await FetchOpenApiDoc();
        var markdown = OpenApiMarkdownGenerator.Generate(doc);
        Assert.Contains("\"author\": \"owner\"", markdown);
        Assert.DoesNotContain("\"author\": \"...\"", markdown);
    }

    // Case 2: GET /tickets/{id} 200 response has content schema
    [Fact]
    public async Task OpenApiSpec_GetTicketById_Has200ContentSchema()
    {
        using var doc = await FetchOpenApiDoc();
        var op = FindOperation(doc, "/tickets/{id}", "get");
        Assert.NotNull(op);
        var responses = op!.Value.GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out var r200), "200 response must be declared");
        Assert.True(r200.TryGetProperty("content", out _), "200 response must have a content schema");
    }

    // Case 3: POST /tickets lists 201 with schema and 400
    [Fact]
    public async Task OpenApiSpec_PostTickets_Has201WithSchemaAnd400()
    {
        using var doc = await FetchOpenApiDoc();
        var op = FindOperation(doc, "/tickets", "post");
        Assert.NotNull(op);
        var responses = op!.Value.GetProperty("responses");
        Assert.True(responses.TryGetProperty("201", out var r201), "201 response must be declared");
        Assert.True(r201.TryGetProperty("content", out _), "201 response must have a content schema");
        Assert.True(responses.TryGetProperty("400", out _), "400 response must be declared");
    }

    // Case 4: GET /tickets (list) 200 response has content schema
    [Fact]
    public async Task OpenApiSpec_GetTicketsList_Has200ContentSchema()
    {
        using var doc = await FetchOpenApiDoc();
        var op = FindOperation(doc, "/tickets", "get");
        Assert.NotNull(op);
        var responses = op!.Value.GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out var r200), "200 response must be declared");
        Assert.True(r200.TryGetProperty("content", out _), "200 response must have a content schema");
    }

    // Case 5: DELETE /tickets/{id} lists 204 and 404
    [Fact]
    public async Task OpenApiSpec_DeleteTicket_Has204And404()
    {
        using var doc = await FetchOpenApiDoc();
        var op = FindOperation(doc, "/tickets/{id}", "delete");
        Assert.NotNull(op);
        var responses = op!.Value.GetProperty("responses");
        Assert.True(responses.TryGetProperty("204", out _), "204 NoContent must be declared on DELETE");
        Assert.True(responses.TryGetProperty("404", out _), "404 NotFound must be declared on DELETE");
    }

    private async Task<JsonDocument> FetchOpenApiDoc()
    {
        var json = await _client.GetStringAsync("/openapi/v1.json");
        return JsonDocument.Parse(json);
    }

    private static JsonElement? FindOperation(JsonDocument doc, string pathSuffix, string method)
    {
        if (!doc.RootElement.TryGetProperty("paths", out var paths))
            return null;
        foreach (var path in paths.EnumerateObject())
        {
            if (path.Name.EndsWith(pathSuffix) && path.Value.TryGetProperty(method, out var op))
                return op;
        }
        return null;
    }

    // Use CreateProjectRequest as the anchor type — it's a public non-static type in
    // KittyClaw.Web, which lets WebApplicationFactory locate the assembly's entry point
    // via reflection without requiring Program to be public.
    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir;

        public ApiFactory()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-oapi-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(Path.Combine(_dataDir, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
            // Must be set before the host starts — Program.cs reads this at line 33
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
