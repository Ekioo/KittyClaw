using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KittyClaw.Core.Tests.Api;

public sealed class PipelineExportHttpTests : IClassFixture<PipelineExportHttpTests.ApiFactory>
{
    private readonly HttpClient _client;

    public PipelineExportHttpTests(ApiFactory factory) => _client = factory.CreateClient();

    private async Task<(string Slug, int PipelineId, int SourceColumnId, int TargetColumnId)> CreatePipelineAsync(string projectName)
    {
        var create = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(projectName));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;

        var pipeline = await _client.PostAsJsonAsync($"/api/projects/{slug}/pipelines", new { name = "Kit" });
        pipeline.EnsureSuccessStatusCode();
        var pipelineId = (await pipeline.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        async Task<int> CreateColumn(string name)
        {
            var response = await _client.PostAsJsonAsync($"/api/projects/{slug}/columns", new { name, pipelineId });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }
        var source = await CreateColumn("Entrée");
        var target = await CreateColumn("Sortie");
        return (slug, pipelineId, source, target);
    }

    private async Task SaveProcessorAsync(string slug, int columnId, int targetColumnId, string prompt)
    {
        var save = await _client.PutAsJsonAsync($"/api/projects/{slug}/columns/{columnId}/processor", new
        {
            name = "Worker",
            mission = "Process tickets.",
            prompt,
            routes = new[] { new { outcome = "completed", targetColumnId } },
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
    }

    [Fact]
    public async Task Export_endpoint_returns_a_named_zip_attachment()
    {
        var (slug, pipelineId, source, target) = await CreatePipelineAsync("Export http ok");
        await SaveProcessorAsync(slug, source, target, "Publish with {{input.CHANNEL}}.");

        var response = await _client.GetAsync($"/api/projects/{slug}/pipelines/{pipelineId}/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".kittyclaw-pipeline", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.Contains(zip.Entries, entry => entry.FullName == "manifest.json");
        Assert.Contains(zip.Entries, entry => entry.FullName == "pipeline.json");
    }

    [Fact]
    public async Task Blocked_export_returns_409_with_findings_and_never_the_raw_value()
    {
        var (slug, pipelineId, source, target) = await CreatePipelineAsync("Export http blocked");
        const string secret = "sk-live-abcdefgh12345678xyz";
        await SaveProcessorAsync(slug, source, target, $"Use key {secret} to publish.");

        var response = await _client.GetAsync($"/api/projects/{slug}/pipelines/{pipelineId}/export");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("export_blocked", body);
        Assert.Contains("probable-secret", body);
        Assert.DoesNotContain(secret, body);
    }

    [Fact]
    public async Task Unknown_pipeline_returns_404()
    {
        var (slug, _, _, _) = await CreatePipelineAsync("Export http 404");

        var response = await _client.GetAsync($"/api/projects/{slug}/pipelines/999999/export");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir = Path.Combine(
            Path.GetTempPath(), "kittyclaw-pipeline-export-api-" + Guid.NewGuid().ToString("N"));

        public ApiFactory()
        {
            Directory.CreateDirectory(_dataDir);
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
