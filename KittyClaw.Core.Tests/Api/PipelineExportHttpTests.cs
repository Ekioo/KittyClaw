using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    public async Task End_to_end_kit_is_sanitized_portable_remapped_and_never_executes_content()
    {
        var (sourceSlug, pipelineId, sourceColumnId, targetColumnId) =
            await CreatePipelineAsync("Portable source");
        var secretValue = "vault-only-" + Guid.NewGuid().ToString("N");
        var secret = await _client.PutAsJsonAsync($"/api/projects/{sourceSlug}/secrets/DEPLOY_TOKEN",
            new { value = secretValue });
        secret.EnsureSuccessStatusCode();
        var skill = await _client.PostAsJsonAsync($"/api/projects/{sourceSlug}/project-skills", new
        {
            name = "Portable verifier",
            description = "Complete embedded project skill",
            instructions = "Verify {{input.REGION}} using {{secret.TOKEN}} without side effects."
        });
        skill.EnsureSuccessStatusCode();
        var skillSlug = (await skill.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var processorSave = await _client.PutAsJsonAsync($"/api/projects/{sourceSlug}/columns/{sourceColumnId}/processor", new
        {
            name = "Portable worker",
            mission = "Deploy {{input.REGION}} with {{secret.TOKEN}}.",
            prompt = "Keep the workflow portable.",
            enabled = true,
            availableSkills = new[] { skillSlug },
            recommendedSkills = new[] { skillSlug },
            requiredSkills = new[] { skillSlug },
            routes = new[] { new { outcome = "completed", targetColumnId } },
            beforeActions = new object[]
            {
                new { id = "no-shell", action = new { type = "executePowerShell", script = "throw 'pipeline kit content must not execute'" } },
                new { id = "no-network", action = new { type = "httpRequest", method = "POST", url = "https://127.0.0.1:1/must-not-run" } },
            },
            afterActions = Array.Empty<object>(),
        });
        processorSave.EnsureSuccessStatusCode();

        var kit = await _client.GetByteArrayAsync($"/api/projects/{sourceSlug}/pipelines/{pipelineId}/export");
        using (var zip = new ZipArchive(new MemoryStream(kit), ZipArchiveMode.Read))
        {
            var names = zip.Entries.Select(entry => entry.FullName).Order().ToArray();
            Assert.Equal(new[]
            {
                "manifest.json", "pipeline.json", $"skills/{skillSlug}/skill.json",
                $"skills/{skillSlug}/SKILL.md",
            }, names);
            var allText = string.Join("\n", zip.Entries.Select(ReadEntry));
            Assert.DoesNotContain(secretValue, allText, StringComparison.Ordinal);
            Assert.DoesNotContain("ticket", allText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("agentCost", allText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workspacePath", allText, StringComparison.OrdinalIgnoreCase);
            var portable = JsonDocument.Parse(ReadEntry(zip.GetEntry("pipeline.json")!)).RootElement;
            Assert.All(portable.GetProperty("columns").EnumerateArray(), column =>
            {
                Assert.False(column.TryGetProperty("id", out _));
                Assert.False(column.TryGetProperty("pipelineId", out _));
            });
        }
        var targetCreate = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Portable target"));
        targetCreate.EnsureSuccessStatusCode();
        var targetSlug = (await targetCreate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var targetSecret = await _client.PutAsJsonAsync($"/api/projects/{targetSlug}/secrets/DEPLOY_TOKEN",
            new { value = "target-only-" + Guid.NewGuid().ToString("N") });
        targetSecret.EnsureSuccessStatusCode();
        var targetDefaultPipeline = (await _client.GetFromJsonAsync<JsonElement[]>(
            $"/api/projects/{targetSlug}/pipelines"))![0].GetProperty("id").GetInt32();
        for (var i = 0; i < 2; i++)
        {
            var decoy = await _client.PostAsJsonAsync($"/api/projects/{targetSlug}/columns",
                new { name = $"Target-only {i}", pipelineId = targetDefaultPipeline });
            decoy.EnsureSuccessStatusCode();
        }
        using var analyzeBody = new ByteArrayContent(kit);
        analyzeBody.Headers.ContentType = new("application/zip");
        var analyze = await _client.PostAsync($"/api/projects/{targetSlug}/pipeline-kits/analyze", analyzeBody);
        analyze.EnsureSuccessStatusCode();

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(kit), "kit", "portable.kittyclaw-pipeline");
        form.Add(new StringContent(JsonSerializer.Serialize(new
        {
            parameters = new { REGION = "eu-west" },
            secretBindings = new { TOKEN = "DEPLOY_TOKEN" },
            approvals = new[] { "executePowerShell", "httpRequest" },
        }), Encoding.UTF8, "application/json"), "confirmation");
        var confirm = await _client.PostAsync($"/api/projects/{targetSlug}/pipeline-kits/confirm", form);
        Assert.True(confirm.StatusCode == HttpStatusCode.Created,
            $"Expected 201 Created, got {(int)confirm.StatusCode}: {await confirm.Content.ReadAsStringAsync()}");

        var installed = await confirm.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(installed.GetProperty("enabled").GetBoolean());
        var installedColumns = installed.GetProperty("columns").EnumerateArray().ToArray();
        var importedSourceId = installedColumns.Single(column => column.GetProperty("key").GetString() == "entree")
            .GetProperty("columnId").GetInt32();
        var importedTargetId = installedColumns.Single(column => column.GetProperty("key").GetString() == "sortie")
            .GetProperty("columnId").GetInt32();
        Assert.NotEqual(sourceColumnId, importedSourceId);
        Assert.NotEqual(targetColumnId, importedTargetId);
        var importedProcessor = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{targetSlug}/columns/{importedSourceId}/processor");
        Assert.Equal(importedTargetId,
            importedProcessor.GetProperty("routes")[0].GetProperty("targetColumnId").GetInt32());
        Assert.Equal(skillSlug, importedProcessor.GetProperty("requiredSkills")[0].GetString());
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

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
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

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ProjectSecretVault>();
                services.AddSingleton(new ProjectSecretVault(_dataDir, new TestSecretProtector()));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", null);
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }
}
