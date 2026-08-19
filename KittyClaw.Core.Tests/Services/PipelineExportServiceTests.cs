using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class PipelineExportServiceTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly ProjectService _projects;
    private readonly ProjectSkillService _skills;
    private readonly ColumnProcessorService _processors;
    private readonly ColumnService _columns;
    private readonly PipelineService _pipelines;
    private readonly PipelineExportService _export;

    public PipelineExportServiceTests()
    {
        _projects = new ProjectService(_temp.Path);
        _skills = new ProjectSkillService(_projects);
        _processors = new ColumnProcessorService(_projects, _skills);
        _columns = new ColumnService(_projects, _processors);
        _pipelines = new PipelineService(_projects);
        _export = new PipelineExportService(_projects, _pipelines, _columns, _processors, _skills,
            new FixedTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
    }

    public void Dispose() => _temp.Dispose();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Fixture(
        Project Project, Pipeline Pipeline, BoardColumn Backlog, BoardColumn Review, BoardColumn Done,
        ProjectSkill Skill, string Workspace);

    /// <summary>Editorial pipeline with accented column names, one project skill and one agent identity.</summary>
    private async Task<Fixture> CreateEditorialFixtureAsync(
        string prompt = "Use {{input.PROJECT_NAME}} then call the API with {{secret.API_TOKEN}}.",
        string skillBody = "Verify factual claims with {{input.STYLE_GUIDE}}.",
        string userGuidance = "",
        List<ColumnProcessorAction>? afterActions = null,
        List<ColumnRoute>? extraRoutes = null)
    {
        var project = await _projects.CreateProjectAsync("Kit export " + Guid.NewGuid().ToString("N")[..8]);
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Editorial flow");
        var backlog = await _columns.CreateColumnAsync(project.Slug, "À traiter", pipelineId: pipeline.Id, userGuidance: userGuidance);
        var review = await _columns.CreateColumnAsync(project.Slug, "Relecture", pipelineId: pipeline.Id);
        var done = await _columns.CreateColumnAsync(project.Slug, "Terminé", pipelineId: pipeline.Id, role: ColumnRole.Success);
        var skill = await _skills.CreateAsync(project.Slug, "Fact check", skillBody);
        var workspace = _projects.ResolveWorkspacePath(project);
        TestSkillBuilder.Create(workspace, "programmer", "noop");

        var routes = new List<ColumnRoute> { new("completed", review.Id) };
        routes.AddRange(extraRoutes ?? []);
        var actions = new List<ColumnProcessorAction>
        {
            new("handoff", new CreateTicketActionSpec { Title = "QA the article", AssignedTo = "programmer" }, review.Id),
            new("webhook", new HttpRequestActionSpec
            {
                Url = "https://example.com/hook",
                Headers = new Dictionary<string, string> { ["Authorization"] = "{{secret.WEBHOOK_TOKEN}}" },
            }),
        };
        actions.AddRange(afterActions ?? []);
        await _processors.SaveAsync(
            project.Slug, backlog.Id, "Editorial writer", "Draft the article.", "claude:sonnet",
            enabled: true, maxTurns: 40,
            availableSkills: [], recommendedSkills: [], requiredSkills: [skill.Slug],
            defaultTargetColumnId: review.Id, technicalFailureColumnId: done.Id,
            routes: routes, prompt: prompt,
            beforeActions: [new("notify", new AddCommentActionSpec { Content = "Starting.", Author = "automation" })],
            afterActions: actions);
        return new Fixture(project, pipeline, backlog, review, done, skill, workspace);
    }

    private static (JsonNode Manifest, JsonNode Pipeline, Dictionary<string, byte[]> Entries) OpenKit(byte[] content)
    {
        using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            entries[entry.FullName] = buffer.ToArray();
        }
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(entries["manifest.json"]))!;
        var pipeline = JsonNode.Parse(Encoding.UTF8.GetString(entries["pipeline.json"]))!;
        return (manifest, pipeline, entries);
    }

    [Fact]
    public async Task Nominal_export_is_a_deterministic_zip_with_v1_manifest_and_verifiable_hashes()
    {
        var fixture = await CreateEditorialFixtureAsync();

        var kit = await _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id);
        var again = await _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id);

        Assert.NotNull(kit);
        Assert.Equal(fixture.Pipeline.Slug + ".kittyclaw-pipeline", kit.FileName);
        Assert.True(kit.Content.SequenceEqual(again!.Content), "two exports of the same pipeline must be byte-identical");

        var (manifest, _, entries) = OpenKit(kit.Content);
        Assert.Equal("kittyclaw-pipeline", manifest["format"]!.GetValue<string>());
        Assert.Equal(1, manifest["formatVersion"]!.GetValue<int>());
        Assert.Equal(1, manifest["compatibility"]!["minFormatVersion"]!.GetValue<int>());
        Assert.Equal(fixture.Pipeline.Slug, manifest["metadata"]!["slug"]!.GetValue<string>());
        Assert.Equal("2026-01-02T03:04:05Z", manifest["provenance"]!["exportedAt"]!.GetValue<string>());
        Assert.Equal("KittyClaw", manifest["provenance"]!["generator"]!.GetValue<string>());

        // Every archive file except the manifest is hashed, and each hash verifies.
        var hashed = manifest["files"]!.AsArray().ToDictionary(
            node => node!["path"]!.GetValue<string>(), node => node!["sha256"]!.GetValue<string>());
        Assert.Equal(entries.Keys.Where(name => name != "manifest.json").Order(StringComparer.Ordinal),
            hashed.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, sha) in hashed)
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(entries[path])).ToLowerInvariant());

        // The archive contains exactly the manifest, the pipeline, and the embedded skill folder.
        Assert.Contains("skills/fact-check/SKILL.md", entries.Keys);
        Assert.Contains("skills/fact-check/skill.json", entries.Keys);
        Assert.All(entries.Keys, name => Assert.True(
            name is "manifest.json" or "pipeline.json" || name.StartsWith("skills/fact-check/", StringComparison.Ordinal),
            $"unexpected archive entry: {name}"));

        // Placeholders are inventoried without revealing any value.
        var parameters = manifest["parameters"]!.AsArray().Select(node => node!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("PROJECT_NAME", parameters);
        Assert.Contains("STYLE_GUIDE", parameters);
        var secrets = manifest["secrets"]!.AsArray().Select(node => node!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["API_TOKEN", "WEBHOOK_TOKEN"], secrets);

        var requirements = manifest["requirements"]!;
        Assert.Equal(["claude:sonnet"], requirements["models"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["programmer"], requirements["agents"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("httpRequest", requirements["riskyActionTypes"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Project_skills_are_embedded_while_agent_skills_are_declared_only()
    {
        var fixture = await CreateEditorialFixtureAsync();

        var kit = await _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id);

        var (manifest, _, entries) = OpenKit(kit!.Content);
        var skillRequirements = manifest["requirements"]!["skills"]!.AsArray()
            .ToDictionary(node => node!["slug"]!.GetValue<string>(), node => node!["embedded"]!.GetValue<bool>());
        Assert.True(skillRequirements["fact-check"]);
        Assert.False(skillRequirements["programmer"]);
        Assert.DoesNotContain(entries.Keys, name => name.Contains("programmer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Pipeline_json_uses_logical_column_keys_and_no_database_identifiers()
    {
        var fixture = await CreateEditorialFixtureAsync();

        var kit = await _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id);

        var (_, pipeline, entries) = OpenKit(kit!.Content);
        var columns = pipeline["columns"]!.AsArray();
        Assert.Equal(["a-traiter", "relecture", "termine"],
            columns.Select(column => column!["key"]!.GetValue<string>()));
        var processor = columns[0]!["processor"]!;
        Assert.Equal("relecture", processor["routing"]!["default"]!.GetValue<string>());
        Assert.Equal("termine", processor["routing"]!["technicalFailure"]!.GetValue<string>());
        Assert.Equal("relecture", processor["routing"]!["routes"]![0]!["target"]!.GetValue<string>());
        Assert.Equal("relecture", processor["afterActions"]![0]!["onFailure"]!.GetValue<string>());

        var text = Encoding.UTF8.GetString(entries["pipeline.json"]);
        Assert.DoesNotMatch(new Regex("\"id\"\\s*:\\s*\\d"), text);
        Assert.DoesNotMatch(new Regex("column-\\d"), text);
        Assert.DoesNotContain("columnId", text);
        Assert.DoesNotContain("pipelineId", text);
        Assert.DoesNotContain("createdAt", text);
        Assert.DoesNotContain("updatedAt", text);
    }

    [Fact]
    public async Task Probable_secret_in_prompt_blocks_export_with_masked_occurrence()
    {
        // Concatenated so the synthetic token never appears verbatim in the repository.
        const string secret = "sk-" + "live-abcdefgh12345678xyz";
        var fixture = await CreateEditorialFixtureAsync(prompt: $"Call the API with key {secret}.");

        var blocked = await Assert.ThrowsAsync<PipelineExportBlockedException>(
            () => _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id));

        var finding = Assert.Single(blocked.Findings, f => f.Category == "probable-secret");
        Assert.Equal("pipeline.json", finding.Path);
        Assert.True(finding.Line > 0);
        Assert.DoesNotContain(secret, finding.Excerpt);
        Assert.StartsWith("sk-l", finding.Excerpt);
    }

    [Fact]
    public async Task Literal_authorization_header_blocks_while_placeholder_header_passes()
    {
        var fixture = await CreateEditorialFixtureAsync(afterActions:
        [
            new("push", new HttpRequestActionSpec
            {
                Url = "https://example.com/push",
                Headers = new Dictionary<string, string> { ["X-Api-Key"] = "literal-value-42" },
            }),
        ]);

        var blocked = await Assert.ThrowsAsync<PipelineExportBlockedException>(
            () => _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id));

        Assert.Contains(blocked.Findings, f => f.Category == "authorization-header" && f.Path == "pipeline.json");
        // The nominal fixture already proves {{secret.WEBHOOK_TOKEN}} in an Authorization header is accepted.
        Assert.DoesNotContain(blocked.Findings, f => f.Excerpt.Contains("WEBHOOK_TOKEN"));
    }

    [Fact]
    public async Task Url_credentials_block_export()
    {
        var fixture = await CreateEditorialFixtureAsync(afterActions:
        [
            new("deploy", new HttpRequestActionSpec { Url = "https://deploy:hunterpass@example.com/hook" }),
        ]);

        var blocked = await Assert.ThrowsAsync<PipelineExportBlockedException>(
            () => _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id));

        var finding = Assert.Single(blocked.Findings, f => f.Category == "url-credentials");
        Assert.DoesNotContain("hunterpass", finding.Excerpt);
    }

    [Fact]
    public async Task Absolute_user_path_in_guidance_blocks_export()
    {
        var fixture = await CreateEditorialFixtureAsync(userGuidance: @"Voir C:\Users\bob\notes.md avant de valider.");

        var blocked = await Assert.ThrowsAsync<PipelineExportBlockedException>(
            () => _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id));

        Assert.Contains(blocked.Findings, f => f.Category == "absolute-path" && f.Path == "pipeline.json");
    }

    [Fact]
    public async Task Secret_in_embedded_skill_script_and_text_asset_blocks_export()
    {
        var fixture = await CreateEditorialFixtureAsync();
        var skillFolder = Path.Combine(fixture.Workspace, ".agents", "skills", fixture.Skill.Slug);
        await File.WriteAllTextAsync(Path.Combine(skillFolder, "deploy.ps1"),
            "$env:API_KEY = \"sk-" + "test-abcdef1234567890abcdef\"\nWrite-Output done\n");
        await File.WriteAllTextAsync(Path.Combine(skillFolder, "notes.txt"),
            "Jeton hérité : eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5NXgL0n3I9PlFUP0THsR8U\n");

        var blocked = await Assert.ThrowsAsync<PipelineExportBlockedException>(
            () => _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id));

        Assert.Contains(blocked.Findings, f =>
            f.Category == "probable-secret" && f.Path == $"skills/{fixture.Skill.Slug}/deploy.ps1");
        Assert.Contains(blocked.Findings, f =>
            f.Category == "probable-secret" && f.Path == $"skills/{fixture.Skill.Slug}/notes.txt");
        Assert.All(blocked.Findings, f => Assert.DoesNotContain("1234567890abcdef", f.Excerpt));
    }

    [Fact]
    public async Task Out_of_folder_skill_reference_fails_explicitly()
    {
        var fixture = await CreateEditorialFixtureAsync(
            skillBody: "Run the shared helper first.\n\nSee ../shared/tools.ps1 for the launcher.\n");

        var blocked = await Assert.ThrowsAsync<PipelineExportBlockedException>(
            () => _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id));

        Assert.Contains(blocked.Findings, f =>
            f.Category == "out-of-folder-reference" && f.Path == $"skills/{fixture.Skill.Slug}/SKILL.md");
    }

    [Fact]
    public async Task External_script_file_reference_blocks_export()
    {
        var fixture = await CreateEditorialFixtureAsync(afterActions:
        [
            new("run", new ExecutePowerShellActionSpec { ScriptFile = "tools/run.ps1" }),
        ]);

        var blocked = await Assert.ThrowsAsync<PipelineExportBlockedException>(
            () => _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id));

        Assert.Contains(blocked.Findings, f => f.Category == "external-reference");
    }

    [Fact]
    public async Task Route_to_a_column_outside_the_exported_pipeline_blocks_export()
    {
        var fixture = await CreateEditorialFixtureAsync();
        var other = await _pipelines.CreateAsync(fixture.Project.Slug, "Other flow");
        var elsewhere = await _columns.CreateColumnAsync(fixture.Project.Slug, "Ailleurs", pipelineId: other.Id);
        await _processors.SaveAsync(
            fixture.Project.Slug, fixture.Backlog.Id, "Editorial writer", "Draft the article.", null,
            enabled: true, maxTurns: 40, availableSkills: [], recommendedSkills: [], requiredSkills: [],
            routes: [new("completed", elsewhere.Id)]);

        var blocked = await Assert.ThrowsAsync<PipelineExportBlockedException>(
            () => _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id));

        Assert.Contains(blocked.Findings, f => f.Category == "unportable-reference");
    }

    [Fact]
    public async Task Binary_executable_inside_a_skill_folder_blocks_export()
    {
        var fixture = await CreateEditorialFixtureAsync();
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Workspace, ".agents", "skills", fixture.Skill.Slug, "tool.exe"),
            [0x4D, 0x5A, 0x00, 0x01]);

        var blocked = await Assert.ThrowsAsync<PipelineExportBlockedException>(
            () => _export.ExportAsync(fixture.Project.Slug, fixture.Pipeline.Id));

        Assert.Contains(blocked.Findings, f => f.Category == "forbidden-file-type");
    }

    [Fact]
    public async Task Unknown_pipeline_or_project_returns_null()
    {
        var fixture = await CreateEditorialFixtureAsync();

        Assert.Null(await _export.ExportAsync(fixture.Project.Slug, 999_999));
        Assert.Null(await _export.ExportAsync("no-such-project", fixture.Pipeline.Id));
    }
}
