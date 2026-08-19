using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
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
        // Concatenated so the synthetic token never appears verbatim in the repository.
        const string secret = "sk-" + "live-abcdefgh12345678xyz";
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

    [Fact]
    public async Task Analyze_is_write_free_and_confirm_installs_the_reviewed_kit()
    {
        var (sourceSlug, pipelineId, source, target) = await CreatePipelineAsync("Import source");
        await SaveProcessorAsync(sourceSlug, source, target, "Publish to {{input.CHANNEL}}.");
        var kit = await _client.GetByteArrayAsync($"/api/projects/{sourceSlug}/pipelines/{pipelineId}/export");
        var targetCreate = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Import target"));
        var targetSlug = (await targetCreate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var beforePipelines = await _client.GetFromJsonAsync<JsonElement[]>($"/api/projects/{targetSlug}/pipelines");

        using var analyzeBody = new ByteArrayContent(kit);
        analyzeBody.Headers.ContentType = new("application/zip");
        var analyze = await _client.PostAsync($"/api/projects/{targetSlug}/pipeline-kits/analyze", analyzeBody);

        Assert.Equal(HttpStatusCode.OK, analyze.StatusCode);
        var preview = await analyze.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preview.GetProperty("installable").GetBoolean());
        Assert.Equal(2, preview.GetProperty("creation").GetProperty("columnsToCreate").GetInt32());
        Assert.Equal(beforePipelines!.Length,
            (await _client.GetFromJsonAsync<JsonElement[]>($"/api/projects/{targetSlug}/pipelines"))!.Length);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(kit), "kit", "kit.kittyclaw-pipeline");
        form.Add(new StringContent("""{"parameters":{"CHANNEL":"stable"}}""", Encoding.UTF8, "application/json"), "confirmation");
        var confirm = await _client.PostAsync($"/api/projects/{targetSlug}/pipeline-kits/confirm", form);

        Assert.Equal(HttpStatusCode.Created, confirm.StatusCode);
        var installed = await confirm.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(installed.GetProperty("enabled").GetBoolean());
        Assert.Equal(beforePipelines.Length + 1,
            (await _client.GetFromJsonAsync<JsonElement[]>($"/api/projects/{targetSlug}/pipelines"))!.Length);
    }

    [Fact]
    public async Task Tampered_kit_is_rejected_without_creating_a_pipeline()
    {
        var (sourceSlug, pipelineId, source, target) = await CreatePipelineAsync("Tamper source");
        await SaveProcessorAsync(sourceSlug, source, target, "Safe prompt.");
        var kit = await _client.GetByteArrayAsync($"/api/projects/{sourceSlug}/pipelines/{pipelineId}/export");
        var tampered = RewriteEntry(kit, "pipeline.json", bytes => [.. bytes, (byte)' ']);
        var targetCreate = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Tamper target"));
        var targetSlug = (await targetCreate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var beforeCount = (await _client.GetFromJsonAsync<JsonElement[]>($"/api/projects/{targetSlug}/pipelines"))!.Length;

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(tampered), "kit", "tampered.kittyclaw-pipeline");
        form.Add(new StringContent("{}"), "confirmation");
        var response = await _client.PostAsync($"/api/projects/{targetSlug}/pipeline-kits/confirm", form);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("hash-mismatch", await response.Content.ReadAsStringAsync());
        Assert.Equal(beforeCount,
            (await _client.GetFromJsonAsync<JsonElement[]>($"/api/projects/{targetSlug}/pipelines"))!.Length);
    }

    [Fact]
    public async Task Duplicate_submission_never_creates_a_second_pipeline()
    {
        var (sourceSlug, pipelineId, source, target) = await CreatePipelineAsync("Double source");
        await SaveProcessorAsync(sourceSlug, source, target, "Safe prompt.");
        var kit = await _client.GetByteArrayAsync($"/api/projects/{sourceSlug}/pipelines/{pipelineId}/export");
        var targetCreate = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Double target"));
        var targetSlug = (await targetCreate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var beforeCount = (await _client.GetFromJsonAsync<JsonElement[]>($"/api/projects/{targetSlug}/pipelines"))!.Length;

        async Task<HttpResponseMessage> Confirm()
        {
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(kit), "kit", "kit.kittyclaw-pipeline");
            form.Add(new StringContent("{}"), "confirmation");
            return await _client.PostAsync($"/api/projects/{targetSlug}/pipeline-kits/confirm", form);
        }
        Assert.Equal(HttpStatusCode.Created, (await Confirm()).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm()).StatusCode);
        Assert.Equal(beforeCount + 1,
            (await _client.GetFromJsonAsync<JsonElement[]>($"/api/projects/{targetSlug}/pipelines"))!.Length);
    }

    private static byte[] RewriteEntry(byte[] archive, string name, Func<byte[], byte[]> transform)
    {
        using var source = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                var bytes = entry.FullName == name ? transform(buffer.ToArray()) : buffer.ToArray();
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                using var destination = copy.Open();
                destination.Write(bytes);
            }
        }
        return output.ToArray();
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
