using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class PipelineImportServiceTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly ProjectService _projects;
    private readonly PipelineService _pipelines;
    private readonly ColumnProcessorService _processors;
    private readonly PipelineImportService _import;

    public PipelineImportServiceTests()
    {
        _projects = new ProjectService(_temp.Path);
        var skills = new ProjectSkillService(_projects);
        _processors = new ColumnProcessorService(_projects, skills);
        var columns = new ColumnService(_projects, _processors);
        _pipelines = new PipelineService(_projects);
        _import = new PipelineImportService(_projects, _pipelines, columns, _processors,
            new ProjectSecretVault(_temp.Path, new TestSecretProtector()));
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(false, "triage")]
    [InlineData(true, "triage")]
    [InlineData(true, "review")]
    [InlineData(true, "execution")]
    [InlineData(true, "post-run")]
    public async Task Jev_roundtrip_preserves_policy_and_stable_routes_when_main_exists(bool credentials, string mode)
    {
        var source = await _projects.CreateProjectAsync("Jev source");
        var columns = new ColumnService(_projects, _processors);
        var main = (await _pipelines.ListAsync(source.Slug)).First();
        var inbox = await columns.CreateColumnAsync(source.Slug, "Same label", pipelineId: main.Id);
        var target = await columns.CreateColumnAsync(source.Slug, "Mail destination", pipelineId: main.Id);
        var skills = new ProjectSkillService(_projects);
        var profileSkill = await skills.CreateAsync(source.Slug, "Mail profile", "Classify mail safely.");
        await _processors.SaveAsync(source.Slug, inbox.Id, "Mail triage", "Sort mail", null, true, 20, [], [], mode == "execution" ? [profileSkill.Slug] : [],
            jevObservationOnly: mode == "triage", jevAutomaticRouting: mode != "review", jevConfidenceThreshold: 0.9,
            jevAcceptanceValidation: mode == "review", jevExecutionSelection: mode == "execution",
            jevPostRunObservation: mode == "post-run",
            jevExecutionProfiles: mode == "execution" ? [new("Mail", ClaudeModelCatalog.DefaultModel, [profileSkill.Slug], "Sort mail")] : [],
            jevFallbackToAgent: false, defaultTargetColumnId: target.Id,
            routes: [new ColumnRoute("mail", target.Id)], jevAcceptanceCriteria: ["Classify incoming mail"]);
        var vault = new ProjectSecretVault(_temp.Path, new TestSecretProtector());
        const string secret = "private-key-never-export-this-value";
        await vault.SetAsync(source.Slug, JevQualificationService.SecretName, secret);
        var exporter = new PipelineExportService(_projects, _pipelines, columns, _processors, skills);
        var kit = (await exporter.ExportAsync(source.Slug, main.Id))!;
        using (var zip = new ZipArchive(new MemoryStream(kit.Content)))
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                Assert.DoesNotContain(secret, await reader.ReadToEndAsync());
            }
        var destination = await _projects.CreateProjectAsync("Jev destination");
        if (credentials) await vault.SetAsync(destination.Slug, JevQualificationService.SecretName, secret);
        var preview = (await _import.AnalyzeAsync(destination.Slug, kit.Content))!;
        Assert.True(preview.Installable);
        Assert.True(preview.JevRequired);
        Assert.Equal(credentials, preview.JevCredentialAvailable);
        var result = (await _import.InstallAsync(destination.Slug, kit.Content,
            new PipelineImportConfirmation { PipelineName = "Renamed mail pipeline" }))!;
        Assert.Equal(credentials, result.Enabled);
        var imported = (await _processors.ListAsync(destination.Slug)).Single();
        Assert.Equal(mode == "triage", imported.JevObservationOnly);
        Assert.Equal(mode != "review", imported.JevAutomaticRouting);
        Assert.Equal(mode == "review", imported.JevAcceptanceValidation);
        Assert.Equal(mode == "execution", imported.JevExecutionSelection);
        Assert.Equal(mode == "post-run", imported.JevPostRunObservation);
        if (mode == "execution")
        {
            var profile = Assert.Single(imported.JevExecutionProfiles);
            Assert.Equal("Mail", profile.Name);
            Assert.Equal(ClaudeModelCatalog.DefaultModel, profile.Model);
            Assert.Equal("Sort mail", profile.Description);
            Assert.Equal(new[] { profileSkill.Slug }, profile.Skills);
            Assert.Empty(imported.AvailableSkills);
        }
        Assert.False(imported.JevFallbackToAgent);
        Assert.Equal(0.9, imported.JevConfidenceThreshold);
        Assert.Equal(new[] { "Classify incoming mail" }, imported.JevAcceptanceCriteria);
        Assert.Equal(result.Columns.Single(c => c.ColumnId != imported.ColumnId && c.Name == "Mail destination").ColumnId,
            imported.Routes.Single().TargetColumnId);
        Assert.Equal(imported.DefaultTargetColumnId, imported.Routes.Single().TargetColumnId);
        Assert.Equal("Renamed mail pipeline", result.PipelineName);
        if (!credentials) Assert.Contains("secret:TYPESAFE_API_KEY", result.DisabledReasons);
    }

    [Fact]
    public async Task Workspace_import_processes_root_kits_and_continues_after_invalid_archives()
    {
        var project = await _projects.CreateProjectAsync("Workspace kits");
        var workspace = _projects.ResolveWorkspacePath(project);
        await File.WriteAllBytesAsync(Path.Combine(workspace, "00-invalid.kittyclaw-pipeline"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(workspace, "01-valid.kittyclaw-pipeline"), BuildKit());
        await File.WriteAllBytesAsync(Path.Combine(workspace, "02-valid.KITTYCLAW-PIPELINE"), BuildKit());
        var nested = Directory.CreateDirectory(Path.Combine(workspace, "nested")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(nested, "ignored.kittyclaw-pipeline"), BuildKit());
        var before = (await _pipelines.ListAsync(project.Slug)).Count;

        var notices = await _import.ImportWorkspaceAsync(project.Slug);

        Assert.Single(notices);
        Assert.Contains("00-invalid.kittyclaw-pipeline", notices[0]);
        var pipelines = await _pipelines.ListAsync(project.Slug);
        Assert.Equal(before + 2, pipelines.Count);
        Assert.Contains(pipelines, p => p.Name.EndsWith(" (2)"));
        Assert.True(File.Exists(Path.Combine(workspace, "01-valid.kittyclaw-pipeline")));
    }

    [Fact]
    public async Task Workspace_import_keeps_unapproved_actions_disabled()
    {
        var project = await _projects.CreateProjectAsync("Unapproved workspace kit");
        var workspace = _projects.ResolveWorkspacePath(project);
        await File.WriteAllBytesAsync(Path.Combine(workspace, "risky.kittyclaw-pipeline"), BuildKit(riskyActions: true));

        var notices = await _import.ImportWorkspaceAsync(project.Slug);

        Assert.Single(notices);
        Assert.Contains("approval:executePowerShell", notices[0]);
        Assert.Contains("approval:httpRequest", notices[0]);
        var processors = await _processors.ListAsync(project.Slug);
        Assert.NotEmpty(processors);
        Assert.All(processors, processor => Assert.False(processor.Enabled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workspace_import_applies_confirmed_python_permission(bool approved)
    {
        var project = await _projects.CreateProjectAsync("Python workspace kit");
        var workspace = _projects.ResolveWorkspacePath(project);
        await File.WriteAllBytesAsync(Path.Combine(workspace, "python.kittyclaw-pipeline"), BuildKit(pythonScheduledTask: true));
        var required = await _import.GetWorkspaceRequiredApprovalsAsync(workspace);
        Assert.Equal(new[] { "executePython" }, required);
        Assert.Empty(await _processors.ListAsync(project.Slug));
        var notices = await _import.ImportWorkspaceAsync(project.Slug, approved ? required : []);
        Assert.Equal(approved, Assert.Single(await _processors.ListAsync(project.Slug)).Enabled);
        if (approved) Assert.Empty(notices);
        else Assert.Contains(notices, notice => notice.Contains("approval:executePython"));
    }

    [Theory]
    [InlineData("../escape.txt", "unsafe-path")]
    [InlineData("/absolute.txt", "unsafe-path")]
    [InlineData("skills/demo/payload.zip", "forbidden-file-type")]
    [InlineData("skills/demo/tool.exe", "forbidden-file-type")]
    public async Task Hostile_entries_are_rejected_during_write_free_analysis(string path, string category)
    {
        var project = await _projects.CreateProjectAsync("Hostile " + Guid.NewGuid().ToString("N"));
        var workspace = _projects.ResolveWorkspacePath(project);
        var before = Directory.GetFiles(workspace, "*", SearchOption.AllDirectories);

        var preview = await _import.AnalyzeAsync(project.Slug, BuildKit((path, [0x4D, 0x5A, 0x00, 0x01])));

        Assert.NotNull(preview);
        Assert.Contains(preview.Blockages, issue => issue.Category == category);
        Assert.Equal(before, Directory.GetFiles(workspace, "*", SearchOption.AllDirectories));
        Assert.Single(await _pipelines.ListAsync(project.Slug)); // only the project's default pipeline
    }

    [Fact]
    public async Task Late_failure_rolls_back_pipeline_columns_processors_and_skill_folder()
    {
        var project = await _projects.CreateProjectAsync("Rollback " + Guid.NewGuid().ToString("N"));
        var workspace = _projects.ResolveWorkspacePath(project);
        var beforeCount = (await _pipelines.ListAsync(project.Slug)).Count;
        _import.InstallFaultInjector = step => step == "skills-installed"
            ? Task.FromException(new IOException("synthetic late failure"))
            : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => _import.InstallAsync(
            project.Slug, BuildKit(), new PipelineImportConfirmation()));

        Assert.Equal(beforeCount, (await _pipelines.ListAsync(project.Slug)).Count);
        Assert.False(Directory.Exists(Path.Combine(workspace, ".agents", "skills", "demo")));
    }

    [Fact]
    public async Task Unknown_format_version_is_refused_without_approximation()
    {
        var project = await _projects.CreateProjectAsync("Version " + Guid.NewGuid().ToString("N"));

        var preview = await _import.AnalyzeAsync(project.Slug, BuildKit(formatVersion: 99));

        Assert.Contains(preview!.Blockages, issue => issue.Category == "unsupported-version");
    }

    [Theory]
    [InlineData(false, "duplicate-path")]
    [InlineData(true, "symlink")]
    public async Task Duplicate_and_symlink_entries_are_rejected(bool symlink, string category)
    {
        var project = await _projects.CreateProjectAsync("Entry " + Guid.NewGuid().ToString("N"));
        var hostile = AddArchiveEntry(BuildKit(), symlink ? "skills/demo/link" : "pipeline.json", [1], symlink);

        var preview = await _import.AnalyzeAsync(project.Slug, hostile);

        Assert.Contains(preview!.Blockages, issue => issue.Category == category);
    }

    [Theory]
    [InlineData("compressed-size", "kit-too-large")]
    [InlineData("file-size", "file-too-large")]
    [InlineData("total-size", "kit-too-large")]
    [InlineData("compression-ratio", "zip-bomb")]
    [InlineData("path-depth", "unsafe-path")]
    [InlineData("entry-count", "too-many-entries")]
    public async Task Every_documented_archive_limit_is_enforced(string limit, string category)
    {
        var project = await _projects.CreateProjectAsync("Limit " + Guid.NewGuid().ToString("N"));
        var archive = BuildLimitArchive(limit);

        var preview = await _import.AnalyzeAsync(project.Slug, archive);

        Assert.Contains(preview!.Blockages, issue => issue.Category == category);
    }

    [Fact]
    public async Task Pipeline_name_collision_requires_an_explicit_rename()
    {
        var project = await _projects.CreateProjectAsync("Collision " + Guid.NewGuid().ToString("N"));
        await _pipelines.CreateAsync(project.Slug, "Imported flow");

        var preview = await _import.AnalyzeAsync(project.Slug, BuildKit());
        var conflict = await Assert.ThrowsAsync<PipelineImportConflictException>(() =>
            _import.InstallAsync(project.Slug, BuildKit(), new()));
        var result = await _import.InstallAsync(project.Slug, BuildKit(), new() { PipelineName = "Imported flow 2" });

        Assert.True(preview!.PipelineNameConflict);
        Assert.Contains(conflict.Issues, issue => issue.Category == "pipeline-name-conflict");
        Assert.Equal("Imported flow 2", result!.PipelineName);
    }

    [Fact]
    public async Task Kit_with_skills_is_refused_when_the_skill_root_is_linked()
    {
        var project = await _projects.CreateProjectAsync("Linked kit root");
        var agents = Directory.CreateDirectory(Path.Combine(_projects.ResolveWorkspacePath(project), ".agents")).FullName;
        var link = Path.Combine(agents, "skills");
        using var outside = new TempDir();
        if (OperatingSystem.IsWindows())
            DurableWriteRouter.CreateDirectoryJunction(link, outside.Path);
        else
            Directory.CreateSymbolicLink(link, outside.Path);
        try
        {
            var kit = BuildKit();
            var preview = await _import.AnalyzeAsync(project.Slug, kit);
            Assert.False(preview!.Installable);
            Assert.Contains(preview.Blockages, issue => issue.Category == "linked-skill-root");

            var rejected = await Assert.ThrowsAsync<PipelineImportRejectedException>(
                () => _import.InstallAsync(project.Slug, kit, new()));
            Assert.Contains(rejected.Issues, issue => issue.Category == "linked-skill-root");
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
            Assert.Single(await _pipelines.ListAsync(project.Slug));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Linked_subdirectory_prevents_identical_skill_reuse()
    {
        var project = await _projects.CreateProjectAsync("Linked skill reuse fixture");
        var skill = Directory.CreateDirectory(
            Path.Combine(_projects.ResolveWorkspacePath(project), ".agents", "skills", "demo")).FullName;
        await File.WriteAllTextAsync(Path.Combine(skill, "SKILL.md"),
            "---\nname: demo\ndescription: \"demo\"\n---\n\nDemo skill.\n");
        var link = Path.Combine(skill, "attachments");
        using var outside = new TempDir();
        if (OperatingSystem.IsWindows())
            DurableWriteRouter.CreateDirectoryJunction(link, outside.Path);
        else
            Directory.CreateSymbolicLink(link, outside.Path);
        try
        {
            var preview = await _import.AnalyzeAsync(project.Slug, BuildKit());
            Assert.Equal(PipelineImportSkillResolution.RenameRequired, Assert.Single(preview!.Skills).Resolution);
            await Assert.ThrowsAsync<PipelineImportConflictException>(
                () => _import.InstallAsync(project.Slug, BuildKit(), new()));
            Assert.Single(await _pipelines.ListAsync(project.Slug));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Identical_skill_is_reused_without_rewriting_it()
    {
        var project = await _projects.CreateProjectAsync("Reuse " + Guid.NewGuid().ToString("N"));
        var skillPath = Path.Combine(_projects.ResolveWorkspacePath(project), ".agents", "skills", "demo", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        await File.WriteAllTextAsync(skillPath, "---\nname: demo\ndescription: \"demo\"\n---\n\nDemo skill.\n");
        var before = await File.ReadAllBytesAsync(skillPath);

        var result = await _import.InstallAsync(project.Slug, BuildKit(), new());

        Assert.Equal(["demo"], result!.SkillsReused);
        Assert.Equal(before, await File.ReadAllBytesAsync(skillPath));
    }

    [Fact]
    public async Task Divergent_skill_rename_remaps_every_processor_skill_collection()
    {
        var project = await _projects.CreateProjectAsync("Rename " + Guid.NewGuid().ToString("N"));
        var existing = Path.Combine(_projects.ResolveWorkspacePath(project), ".agents", "skills", "demo");
        Directory.CreateDirectory(existing);
        await File.WriteAllTextAsync(Path.Combine(existing, "SKILL.md"), "---\nname: demo\ndescription: \"different\"\n---\n\nDifferent skill.\n");
        var existingBefore = await File.ReadAllBytesAsync(Path.Combine(existing, "SKILL.md"));

        var result = await _import.InstallAsync(project.Slug, BuildKit(allSkillCollections: true), new()
        {
            SkillRenames = new() { ["demo"] = "demo-imported" },
        });
        var processor = await _processors.GetAsync(project.Slug, result!.Columns.Single().ColumnId);

        Assert.Equal(["demo-imported"], processor!.AvailableSkills);
        Assert.Equal(["demo-imported"], processor.RecommendedSkills);
        Assert.Equal(["demo-imported"], processor.RequiredSkills);
        Assert.Equal(existingBefore, await File.ReadAllBytesAsync(Path.Combine(existing, "SKILL.md")));
    }

    [Fact]
    public async Task Missing_prerequisites_and_each_separate_approval_disable_all_processors()
    {
        var project = await _projects.CreateProjectAsync("Disabled " + Guid.NewGuid().ToString("N"));
        var kit = BuildKit(includeEmbeddedSkill: false, allPrerequisites: true, riskyActions: true, scriptFile: true);

        var preview = await _import.AnalyzeAsync(project.Slug, kit);
        var result = await _import.InstallAsync(project.Slug, kit, new());
        var processor = await _processors.GetAsync(project.Slug, result!.Columns.Single().ColumnId);

        Assert.Equal(["embeddedScripts", "executePowerShell", "httpRequest"], preview!.RequiredApprovals);
        Assert.False(result.Enabled);
        Assert.False(processor!.Enabled);
        Assert.Contains("parameter:REGION", result.DisabledReasons);
        Assert.Contains("secret:TOKEN", result.DisabledReasons);
        Assert.Contains(result.DisabledReasons, reason => reason.StartsWith("model:"));
        Assert.Contains("agent:missing-agent", result.DisabledReasons);
        Assert.Contains("skill:demo", result.DisabledReasons);
        Assert.Contains("approval:embeddedScripts", result.DisabledReasons);
        Assert.Contains("approval:executePowerShell", result.DisabledReasons);
        Assert.Contains("approval:httpRequest", result.DisabledReasons);
    }

    [Fact]
    public async Task Failure_after_processor_persistence_restores_database_and_filesystem_exactly()
    {
        var project = await _projects.CreateProjectAsync("Late rollback " + Guid.NewGuid().ToString("N"));
        var workspace = _projects.ResolveWorkspacePath(project);
        var before = Snapshot(workspace);
        _import.InstallFaultInjector = step => step == "processors-installed"
            ? Task.FromException(new IOException("synthetic post-processor failure"))
            : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => _import.InstallAsync(project.Slug, BuildKit(), new()));

        Assert.Equal(before, Snapshot(workspace));
    }

    internal static byte[] BuildKit(
        (string Path, byte[] Content)? extra = null,
        int formatVersion = 1,
        bool includeEmbeddedSkill = true,
        bool allSkillCollections = false,
        bool allPrerequisites = false,
        bool riskyActions = false,
        bool scriptFile = false,
        bool pythonScheduledTask = false)
    {
        var actions = riskyActions
            ? new object[]
            {
                new { id = "script", action = new Dictionary<string, object?> { ["type"] = "executePowerShell", ["script"] = "Write-Output safe" }, onFailure = (string?)null },
                new { id = "request", action = new Dictionary<string, object?> { ["type"] = "httpRequest", ["method"] = "POST", ["url"] = "https://example.invalid/hook" }, onFailure = (string?)null },
                new { id = "agent", action = new Dictionary<string, object?> { ["type"] = "createTicket", ["title"] = "Follow-up", ["assignedTo"] = "missing-agent" }, onFailure = (string?)null },
            }
            : [];
        var pipeline = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 1,
            name = "Imported flow",
            slug = "imported-flow",
            columns = new[] { new
            {
                key = "todo", name = "Todo", color = "#5a6a80", role = "Normal", userGuidance = "",
                scheduledTasks = pythonScheduledTask ? new object[] { new {
                    name = "Python ingestion", enabled = false, cron = "* * * * *", timeZoneId = "UTC", ticketScope = "None",
                    actions = new[] { new { id = "python", action = new { type = "executePython", script = "print('review only')" } } },
                } } : [],
                processor = new
                {
                    name = "Worker", mission = allPrerequisites ? "Work {{input.REGION}} {{secret.TOKEN}}." : "Work.", prompt = "",
                    model = allPrerequisites ? "missing:model" : null, enabled = true,
                    maxTurns = 10, selectionOrder = "Position", maxAttempts = 2, retryBackoffSeconds = 5,
                    availableSkills = allSkillCollections ? new[] { "demo" } : Array.Empty<string>(),
                    recommendedSkills = allSkillCollections ? new[] { "demo" } : Array.Empty<string>(),
                    requiredSkills = scriptFile ? new[] { "demo", "scripted" } : new[] { "demo" },
                    beforeActions = actions, afterActions = Array.Empty<object>(),
                    routing = new { @default = (string?)null, technicalFailure = (string?)null, routes = Array.Empty<object>() },
                },
            } },
        });
        var skill = Encoding.UTF8.GetBytes("---\nname: demo\ndescription: \"demo\"\n---\n\nDemo skill.\n");
        var entries = new List<(string Path, byte[] Content)> { ("pipeline.json", pipeline) };
        if (includeEmbeddedSkill) entries.Add(("skills/demo/SKILL.md", skill));
        if (scriptFile)
        {
            entries.Add(("skills/scripted/SKILL.md", Encoding.UTF8.GetBytes("---\nname: scripted\ndescription: scripted\n---\nScripted skill.\n")));
            entries.Add(("skills/scripted/tool.ps1", Encoding.UTF8.GetBytes("Write-Output safe")));
        }
        if (extra is { } value) entries.Add(value);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "kittyclaw-pipeline",
            formatVersion,
            metadata = new { name = "Imported flow", slug = "imported-flow" },
            provenance = new { generator = "test", generatorVersion = "1", projectSlug = "source", exportedAt = "2026-01-01T00:00:00Z" },
            compatibility = new { minFormatVersion = 1 },
            parameters = allPrerequisites ? new[] { new { name = "REGION", occurrences = 1 } } : Array.Empty<object>(),
            secrets = allPrerequisites ? new[] { new { name = "TOKEN", occurrences = 1 } } : Array.Empty<object>(),
            requirements = new
            {
                models = allPrerequisites ? new[] { "missing:model" } : Array.Empty<string>(),
                agents = allPrerequisites ? new[] { "missing-agent" } : Array.Empty<string>(),
                skills = scriptFile
                    ? new[] { new { slug = "demo", name = "Demo", embedded = includeEmbeddedSkill }, new { slug = "scripted", name = "Scripted", embedded = true } }
                    : new[] { new { slug = "demo", name = "Demo", embedded = includeEmbeddedSkill } },
                riskyActionTypes = riskyActions ? new[] { "executePowerShell", "httpRequest" } : Array.Empty<string>(),
            },
            files = entries.Select(entry => new
            {
                path = entry.Path,
                sha256 = Convert.ToHexString(SHA256.HashData(entry.Content)).ToLowerInvariant(),
            }),
        });
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "manifest.json", manifest);
            foreach (var entry in entries) Write(zip, entry.Path, entry.Content);
        }
        return output.ToArray();
    }

    private static byte[] BuildLimitArchive(string limit)
    {
        var random = new byte[8 * 1024 * 1024 + 1];
        RandomNumberGenerator.Fill(random);
        if (limit == "compressed-size") return random;

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            switch (limit)
            {
                case "file-size":
                    Write(zip, "oversized.txt", new byte[2 * 1024 * 1024 + 1]);
                    break;
                case "total-size":
                    for (var i = 0; i < 9; i++)
                        Write(zip, $"part-{i}.txt", new byte[2 * 1024 * 1024]);
                    break;
                case "compression-ratio":
                    var entry = zip.CreateEntry("compressible.txt", CompressionLevel.SmallestSize);
                    using (var stream = entry.Open()) stream.Write(new byte[1024 * 1024 + 1]);
                    break;
                case "path-depth":
                    Write(zip, "a/b/c/d/e/f/g/h/i.txt", [1]);
                    break;
                case "entry-count":
                    for (var i = 0; i < 501; i++) Write(zip, $"f{i}.txt", [1]);
                    break;
            }
        }
        return output.ToArray();
    }

    private static Dictionary<string, string> Snapshot(string root) => Directory
        .GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private static void Write(ZipArchive zip, string path, byte[] content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static byte[] AddArchiveEntry(byte[] archive, string path, byte[] content, bool symlink)
    {
        using var source = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var original in source.Entries)
            {
                using var input = original.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                Write(target, original.FullName, buffer.ToArray());
            }
            var added = target.CreateEntry(path, CompressionLevel.NoCompression);
            if (symlink) added.ExternalAttributes = 0xA000 << 16;
            using var stream = added.Open();
            stream.Write(content);
        }
        return output.ToArray();
    }
}
