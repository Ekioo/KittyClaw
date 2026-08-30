using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Core.Models;
using KittyClaw.Core.Automation;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KittyClaw.Core.Tests.Services;

public sealed class ProjectSkillAndProcessorTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly ProjectService _projects;
    private readonly ProjectSkillService _skills;
    private readonly ColumnProcessorService _processors;
    private readonly ColumnService _columns;
    private readonly PipelineService _pipelines;

    public ProjectSkillAndProcessorTests()
    {
        _projects = new ProjectService(_temp.Path);
        _skills = new ProjectSkillService(_projects);
        _processors = new ColumnProcessorService(_projects, _skills);
        _columns = new ColumnService(_projects, _processors);
        _pipelines = new PipelineService(_projects);
    }

    [Fact]
    public async Task Project_skill_keeps_stable_slug_when_display_name_changes()
    {
        var project = await _projects.CreateProjectAsync("Skills");
        var skill = await _skills.CreateAsync(project.Slug, "Run tests", "# Run tests\n");

        var renamed = await _skills.UpdateAsync(project.Slug, skill.Slug, "Verify build", null);

        Assert.NotNull(renamed);
        Assert.Equal("run-tests", renamed.Slug);
        Assert.Equal("Verify build", renamed.Name);
        Assert.Equal("# Run tests", await _skills.ReadInstructionsAsync(project.Slug, skill.Slug));
    }

    [Fact]
    public async Task Project_skill_is_written_with_codex_frontmatter()
    {
        var project = await _projects.CreateProjectAsync("Codex skill");

        var skill = await _skills.CreateAsync(
            project.Slug, "Quality routing", "Check every acceptance criterion.",
            "Validate deliverables and choose a routing outcome.");
        var document = await File.ReadAllTextAsync(skill.InstructionsPath);

        Assert.StartsWith("---\nname: quality-routing\ndescription: \"Validate deliverables and choose a routing outcome.\"\n---\n", document);
        Assert.EndsWith("Check every acceptance criterion.\n", document);
        Assert.Equal("Check every acceptance criterion.", await _skills.ReadInstructionsAsync(project.Slug, skill.Slug));
    }

    [Fact]
    public async Task Listing_skills_reads_legacy_plain_markdown_without_modifying_versioned_files()
    {
        var project = await _projects.CreateProjectAsync("Legacy project skill");
        var skill = await _skills.CreateAsync(project.Slug, "Ticket triage", "Initial body.");
        const string legacyBody = "Reusable ticket triage. Clarify acceptance criteria.";
        await File.WriteAllTextAsync(skill.InstructionsPath, legacyBody);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(skill.InstructionsPath)!, "skill.json"),
            "{\"Name\":\"Ticket triage\"}");
        var metadataPath = Path.Combine(Path.GetDirectoryName(skill.InstructionsPath)!, "skill.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);

        var migrated = Assert.Single(await _skills.ListAsync(project.Slug));
        var document = await File.ReadAllTextAsync(skill.InstructionsPath);

        Assert.Equal(skill.Slug, migrated.Slug);
        Assert.Equal(legacyBody, await _skills.ReadInstructionsAsync(project.Slug, skill.Slug));
        Assert.Equal(legacyBody, document);
        Assert.Equal(originalMetadata, await File.ReadAllTextAsync(metadataPath));
    }

    [Fact]
    public async Task Listing_skills_does_not_truncate_existing_versioned_descriptions()
    {
        var project = await _projects.CreateProjectAsync("Long project skill");
        var skill = await _skills.CreateAsync(project.Slug, "Durable routing", "Keep writes isolated.");
        var directory = Path.GetDirectoryName(skill.InstructionsPath)!;
        var description = string.Join(' ', Enumerable.Repeat("complete durable routing guidance", 12));
        var document = $"---\nname: {skill.Slug}\ndescription: {JsonSerializer.Serialize(description)}\n---\n\nKeep writes isolated.\n";
        var metadata = JsonSerializer.Serialize(new { Name = "Durable routing", Description = description });
        await File.WriteAllTextAsync(skill.InstructionsPath, document);
        await File.WriteAllTextAsync(Path.Combine(directory, "skill.json"), metadata);

        var listed = Assert.Single(await _skills.ListAsync(project.Slug));

        Assert.EndsWith("…", listed.Description);
        Assert.Equal(document, await File.ReadAllTextAsync(skill.InstructionsPath));
        Assert.Equal(metadata, await File.ReadAllTextAsync(Path.Combine(directory, "skill.json")));
    }

    [Fact]
    public async Task Processor_is_bound_to_stable_column_and_owns_memory()
    {
        var project = await _projects.CreateProjectAsync("Processors");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Editorial");
        var column = await _columns.CreateColumnAsync(project.Slug, "Review", pipelineId: pipeline.Id);
        var factCheck = await _skills.CreateAsync(project.Slug, "Fact check", "Verify factual claims.");

        var saved = await _processors.SaveAsync(
            project.Slug, column.Id, "Editorial reviewer", "Validate publication safety.", null,
            enabled: true, maxTurns: 50, availableSkills: [],
            recommendedSkills: [factCheck.Slug], requiredSkills: [factCheck.Slug]);
        await _pipelines.UpdateAsync(project.Slug, pipeline.Id, "Content");
        await _columns.UpdateColumnAsync(project.Slug, column.Id, name: "Validation");

        var reloaded = await _processors.GetAsync(project.Slug, column.Id);
        var memoryPath = await _processors.GetMemoryIndexPathAsync(project.Slug, column.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(saved.Id, reloaded.Id);
        Assert.Equal(column.Id, reloaded.ColumnId);
        Assert.Equal([factCheck.Slug], reloaded.RequiredSkills);
        Assert.NotNull(memoryPath);
        Assert.True(File.Exists(memoryPath));
        Assert.Contains($"column-{column.Id}", memoryPath);
    }

    [Fact]
    public async Task Processor_definition_file_is_authoritative_and_includes_prompt()
    {
        var project = await _projects.CreateProjectAsync("File processor");
        var columns = await _columns.ListColumnsAsync(project.Slug);
        var source = columns[0];
        var target = columns[1];

        await _processors.SaveAsync(project.Slug, source.Id, "File worker", "Short mission.", null,
            true, 42, [], [], [], defaultTargetColumnId: target.Id,
            routes: [new("accepted", target.Id)], prompt: "Follow the detailed checklist.");

        var path = await _processors.GetDefinitionPathAsync(project.Slug, source.Id);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal("Follow the detailed checklist.", json["prompt"]!.GetValue<string>());
        Assert.Equal($"column-{target.Id}", json["routing"]!["default"]!.GetValue<string>());
        Assert.Equal($"column-{target.Id}", json["routing"]!["routes"]![0]!["target"]!.GetValue<string>());

        json["name"] = "Edited on disk";
        json["prompt"] = "Prompt edited outside KittyClaw.";
        await File.WriteAllTextAsync(path, json.ToJsonString(new() { WriteIndented = true }));

        var synchronized = await _processors.GetAsync(project.Slug, source.Id);
        Assert.NotNull(synchronized);
        Assert.Equal("Edited on disk", synchronized.Name);
        Assert.Equal("Prompt edited outside KittyClaw.", synchronized.Prompt);
    }

    [Fact]
    public async Task Processor_actions_round_trip_through_versioned_file_and_runtime_projection()
    {
        var project = await _projects.CreateProjectAsync("Processor actions");
        var columns = await _columns.ListColumnsAsync(project.Slug);
        var source = columns[0];
        var failure = columns[1];
        var before = new ColumnProcessorAction(
            "prepare",
            new ExecutePowerShellActionSpec { Script = "Write-Output ready" },
            failure.Id);
        var after = new ColumnProcessorAction(
            "notify",
            new HttpRequestActionSpec { Url = "https://example.com/hook", Method = "POST" },
            failure.Id);

        await _processors.SaveAsync(project.Slug, source.Id, "Worker", "Do work.", null,
            true, 20, [], [], [], technicalFailureColumnId: failure.Id,
            beforeActions: [before], afterActions: [after]);

        var path = await _processors.GetDefinitionPathAsync(project.Slug, source.Id);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(2, json["version"]!.GetValue<int>());
        Assert.Equal("executePowerShell", json["beforeActions"]![0]!["action"]!["type"]!.GetValue<string>());
        Assert.Equal($"column-{failure.Id}", json["beforeActions"]![0]!["onFailure"]!.GetValue<string>());
        Assert.Equal("httpRequest", json["afterActions"]![0]!["action"]!["type"]!.GetValue<string>());

        var reloaded = await _processors.GetAsync(project.Slug, source.Id);
        Assert.NotNull(reloaded);
        Assert.IsType<ExecutePowerShellActionSpec>(Assert.Single(reloaded.BeforeActions).Action);
        Assert.IsType<HttpRequestActionSpec>(Assert.Single(reloaded.AfterActions).Action);
        Assert.Equal(failure.Id, reloaded.AfterActions[0].FailureTargetColumnId);
    }

    [Fact]
    public async Task Processor_rejects_hidden_agents_and_self_routes_inside_actions()
    {
        var project = await _projects.CreateProjectAsync("Invalid processor actions");
        var source = (await _columns.ListColumnsAsync(project.Slug))[0];

        var hiddenAgent = await Assert.ThrowsAsync<InvalidOperationException>(() => _processors.SaveAsync(
            project.Slug, source.Id, "Worker", "Do work.", null, true, 20, [], [], [],
            beforeActions: [new("another-agent", new RunAgentActionSpec { Agent = "other" })]));
        Assert.Contains("Type d’action non pris en charge", hiddenAgent.Message);

        var selfRoute = await Assert.ThrowsAsync<InvalidOperationException>(() => _processors.SaveAsync(
            project.Slug, source.Id, "Worker", "Do work.", null, true, 20, [], [], [],
            beforeActions: [new("script", new ExecutePowerShellActionSpec { Script = "exit 1" }, source.Id)]));
        Assert.Contains("propre colonne", selfRoute.Message);
    }

    [Fact]
    public async Task Removing_definition_file_removes_runtime_projection_after_initial_migration()
    {
        var project = await _projects.CreateProjectAsync("Removed file processor");
        var column = (await _columns.ListColumnsAsync(project.Slug)).First();
        await _processors.SaveAsync(project.Slug, column.Id, "Worker", "Do work.", null,
            true, 20, [], [], []);
        var path = await _processors.GetDefinitionPathAsync(project.Slug, column.Id);

        File.Delete(path);

        Assert.Null(await _processors.GetAsync(project.Slug, column.Id));
    }

    [Fact]
    public async Task First_read_exports_legacy_sqlite_processor_without_losing_configuration()
    {
        var project = await _projects.CreateProjectAsync("Legacy database processor");
        var column = (await _columns.ListColumnsAsync(project.Slug)).First();
        await using (var db = _projects.GetProjectDb(project.Slug))
        {
            await ColumnProcessorService.EnsureTableAsync(db);
            db.ColumnProcessors.Add(new ColumnProcessor
            {
                ColumnId = column.Id,
                Name = "Legacy worker",
                Mission = "Preserve this mission.",
                Prompt = "Preserve this prompt.",
                Enabled = false,
                MaxTurns = 77,
            });
            await db.SaveChangesAsync();
        }

        var migrated = await _processors.GetAsync(project.Slug, column.Id);
        var path = await _processors.GetDefinitionPathAsync(project.Slug, column.Id);

        Assert.NotNull(migrated);
        Assert.Equal("Preserve this prompt.", migrated.Prompt);
        Assert.False(migrated.Enabled);
        Assert.Equal(77, migrated.MaxTurns);
        Assert.True(File.Exists(path));
        Assert.Contains("Preserve this mission.", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task First_read_routes_legacy_processor_migration_through_maintenance_worktree()
    {
        var repository = ProjectWorktreeSettingsTests.CreateRepository(_temp.Path, "integration");
        var project = await _projects.CreateProjectAsync("Routed legacy processor");
        await _projects.UpdateProjectAsync(project.Slug, repository);
        await _projects.UpdateProjectAsync(project.Slug, null,
            worktreesEnabled: true, integrationBranch: "integration");
        var tickets = new TicketService(_projects, new MemberService(_projects));
        var worktrees = new TicketWorktreeService(_projects, tickets);
        var queue = new WorktreeMergeQueueService(_projects, worktrees);
        var router = new DurableWriteRouter(_projects, worktrees, queue);
        var processors = new ColumnProcessorService(_projects, _skills,
            durableWrites: new Lazy<DurableWriteRouter>(() => router));
        var column = (await _columns.ListColumnsAsync(project.Slug)).First();
        await using (var db = _projects.GetProjectDb(project.Slug))
        {
            await ColumnProcessorService.EnsureTableAsync(db);
            db.ColumnProcessors.Add(new ColumnProcessor
            {
                ColumnId = column.Id,
                Name = "Legacy routed worker",
                Mission = "Preserve routed migration.",
                Prompt = "Keep every field.",
                Enabled = false,
                MaxTurns = 91,
            });
            await db.SaveChangesAsync();
        }
        var initialStatus = Git(repository, "status", "--porcelain=v1", "--untracked-files=all");

        var migrated = await processors.GetAsync(project.Slug, column.Id);

        Assert.NotNull(migrated);
        Assert.Equal("Keep every field.", migrated.Prompt);
        Assert.False(migrated.Enabled);
        Assert.Equal(91, migrated.MaxTurns);
        Assert.Equal(initialStatus, Git(repository, "status", "--porcelain=v1", "--untracked-files=all"));
        Assert.False(File.Exists(Path.Combine(repository, ".agents", "processors", ".source-of-truth-v1")));
        var request = Assert.Single(await queue.ListAsync(project.Slug));
        Assert.Equal(WorktreeMergeJobKind.Maintenance, request.JobKind);
        Assert.True(File.Exists(Path.Combine(request.WorktreePath, ".agents", "processors", ".source-of-truth-v1")));
        Assert.True(File.Exists(Path.Combine(request.WorktreePath, ".agents", "processors", $"column-{column.Id}", "processor.json")));

        await processors.GetAsync(project.Slug, column.Id);
        Assert.Single(await queue.ListAsync(project.Slug));

        var integrated = await queue.ProcessNextAsync(project.Slug, CancellationToken.None);
        Assert.NotNull(integrated);
        Assert.Equal(WorktreeMergeStatus.Completed, integrated.Status);
        Assert.True(File.Exists(Path.Combine(repository, ".agents", "processors", ".source-of-truth-v1")));
        var reloaded = await processors.GetAsync(project.Slug, column.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Legacy routed worker", reloaded.Name);
        Assert.Single(await queue.ListAsync(project.Slug));
    }

    [Fact]
    public async Task Interrupted_processor_migration_reuses_committed_maintenance_checkpoint()
    {
        var repository = ProjectWorktreeSettingsTests.CreateRepository(_temp.Path, "integration");
        var project = await _projects.CreateProjectAsync("Interrupted routed processor");
        await _projects.UpdateProjectAsync(project.Slug, repository);
        await _projects.UpdateProjectAsync(project.Slug, null,
            worktreesEnabled: true, integrationBranch: "integration");
        var tickets = new TicketService(_projects, new MemberService(_projects));
        var worktrees = new TicketWorktreeService(_projects, tickets);
        var queue = new WorktreeMergeQueueService(_projects, worktrees);
        var router = new DurableWriteRouter(_projects, worktrees, queue);
        var processors = new ColumnProcessorService(_projects, _skills,
            durableWrites: new Lazy<DurableWriteRouter>(() => router));
        var column = (await _columns.ListColumnsAsync(project.Slug)).First();
        await using (var db = _projects.GetProjectDb(project.Slug))
        {
            await ColumnProcessorService.EnsureTableAsync(db);
            db.ColumnProcessors.Add(new ColumnProcessor
            {
                ColumnId = column.Id,
                Name = "Checkpoint worker",
                Mission = "Preserve the committed definition.",
                Prompt = "Keep the checkpointed prompt.",
                Enabled = false,
                MaxTurns = 73,
            });
            await db.SaveChangesAsync();
        }

        await processors.GetAsync(project.Slug, column.Id);
        var interrupted = Assert.Single(await queue.ListAsync(project.Slug));
        var definitionPath = Path.Combine(interrupted.WorktreePath, ".agents", "processors",
            $"column-{column.Id}", "processor.json");
        var committedDefinition = await File.ReadAllTextAsync(definitionPath);
        var committedHead = Git(interrupted.WorktreePath, "rev-parse", "HEAD").Trim();
        var baselineHead = Git(repository, "rev-parse", "integration").Trim();
        await using (var db = _projects.GetProjectDb(project.Slug))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.CommitPending},
                    Checkpoint = {(int)WorktreeMergeCheckpoint.Writing}, SourceCommit = {baselineHead}
                WHERE Id = {interrupted.Id}
                """);
            var stale = await db.ColumnProcessors.SingleAsync(p => p.ColumnId == column.Id);
            Assert.NotNull(stale);
            stale.Name = "Stale SQLite name";
            stale.Prompt = "Must not overwrite the committed file.";
            stale.MaxTurns = 1;
            await db.SaveChangesAsync();
        }
        queue.ReleaseMaintenanceWrite(interrupted.Id);

        var restartedQueue = new WorktreeMergeQueueService(_projects, worktrees);
        var restartedRouter = new DurableWriteRouter(_projects, worktrees, restartedQueue);
        var restartedProcessors = new ColumnProcessorService(_projects, _skills,
            durableWrites: new Lazy<DurableWriteRouter>(() => restartedRouter));
        var recovered = await restartedProcessors.GetAsync(project.Slug, column.Id);

        Assert.NotNull(recovered);
        Assert.Equal("Checkpoint worker", recovered.Name);
        Assert.Equal("Keep the checkpointed prompt.", recovered.Prompt);
        Assert.False(recovered.Enabled);
        Assert.Equal(73, recovered.MaxTurns);
        Assert.Equal(committedDefinition, await File.ReadAllTextAsync(definitionPath));
        Assert.Equal(committedHead, Git(interrupted.WorktreePath, "rev-parse", "HEAD").Trim());
        Assert.Single(Directory.EnumerateFiles(interrupted.WorktreePath, ".source-of-truth-v1", SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateFiles(interrupted.WorktreePath, "processor.json", SearchOption.AllDirectories));
        var resumed = Assert.Single(await restartedQueue.ListAsync(project.Slug));
        Assert.Equal(interrupted.Id, resumed.Id);
        Assert.Equal(WorktreeMergeStatus.Pending, resumed.Status);

        var integrated = await restartedQueue.ProcessNextAsync(project.Slug, CancellationToken.None);
        Assert.NotNull(integrated);
        Assert.Equal(WorktreeMergeStatus.Completed, integrated.Status);
        Assert.Equal(committedHead, Git(repository, "rev-parse", "integration").Trim());
        Assert.Single(await restartedQueue.ListAsync(project.Slug));
    }

    [Fact]
    public async Task Processor_rejects_unknown_project_skills()
    {
        var project = await _projects.CreateProjectAsync("Unknown skill");
        var column = (await _columns.ListColumnsAsync(project.Slug)).First();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _processors.SaveAsync(
            project.Slug, column.Id, "Worker", "Do the work.", null, true, 20,
            availableSkills: ["missing"], recommendedSkills: [], requiredSkills: []));

        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public async Task Processor_listing_disables_invalid_projection_without_hiding_the_project_board()
    {
        var project = await _projects.CreateProjectAsync("Stale processor skill");
        var column = (await _columns.ListColumnsAsync(project.Slug)).First();
        var skill = await _skills.CreateAsync(project.Slug, "Workflow routing", "Route tickets safely.");
        await _processors.SaveAsync(project.Slug, column.Id, "Worker", "Do the work.", null, true, 20,
            availableSkills: [skill.Slug], recommendedSkills: [], requiredSkills: []);
        var definitionPath = await _processors.GetDefinitionPathAsync(project.Slug, column.Id);
        var originalDefinition = await File.ReadAllTextAsync(definitionPath);
        Assert.True(await _skills.DeleteAsync(project.Slug, skill.Slug));

        var processor = Assert.Single(await _processors.ListAsync(project.Slug));
        var enabled = await _processors.ListEnabledAsync(project.Slug);

        Assert.False(processor.Enabled);
        Assert.Empty(enabled);
        Assert.Equal(originalDefinition, await File.ReadAllTextAsync(definitionPath));
    }

    [Fact]
    public async Task Processor_listing_includes_disabled_processors_for_configuration_views()
    {
        var project = await _projects.CreateProjectAsync("Processor listing");
        var column = (await _columns.ListColumnsAsync(project.Slug)).First();
        await _processors.SaveAsync(project.Slug, column.Id, "Paused worker", "Do work later.", null,
            enabled: false, maxTurns: 20, [], [], []);

        var all = await _processors.ListAsync(project.Slug);
        var enabled = await _processors.ListEnabledAsync(project.Slug);

        Assert.Equal(column.Id, Assert.Single(all).ColumnId);
        Assert.Empty(enabled);
    }

    [Fact]
    public async Task Deleting_column_removes_its_processor_and_repairs_incoming_routes()
    {
        var project = await _projects.CreateProjectAsync("Safe column deletion");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Editorial");
        var source = await _columns.CreateColumnAsync(project.Slug, "Source", pipelineId: pipeline.Id);
        var removed = await _columns.CreateColumnAsync(project.Slug, "Removed", pipelineId: pipeline.Id);
        var fallback = await _columns.CreateColumnAsync(project.Slug, "Fallback", pipelineId: pipeline.Id);

        await _processors.SaveAsync(project.Slug, source.Id, "Source worker", "Route work.", null,
            true, 20, [], [], [], defaultTargetColumnId: removed.Id,
            technicalFailureColumnId: removed.Id, routes: [new("approved", removed.Id)]);
        await _processors.SaveAsync(project.Slug, removed.Id, "Removed worker", "Do work.", null,
            true, 20, [], [], []);

        Assert.True(await _columns.DeleteColumnAsync(project.Slug, removed.Id, fallback.Name));

        Assert.Null(await _processors.GetAsync(project.Slug, removed.Id));
        var repaired = await _processors.GetAsync(project.Slug, source.Id);
        Assert.NotNull(repaired);
        Assert.Null(repaired.DefaultTargetColumnId);
        Assert.Null(repaired.TechnicalFailureColumnId);
        Assert.Empty(repaired.Routes);
        var removedPath = await _processors.GetDefinitionPathAsync(project.Slug, removed.Id);
        Assert.False(File.Exists(removedPath));
        var sourcePath = await _processors.GetDefinitionPathAsync(project.Slug, source.Id);
        var sourceDefinition = await File.ReadAllTextAsync(sourcePath);
        Assert.DoesNotContain($"column-{removed.Id}", sourceDefinition);
    }

    [Fact]
    public async Task Processor_memory_preserves_lessons_from_legacy_inferred_path()
    {
        var project = await _projects.CreateProjectAsync("Legacy processor memory");
        var column = (await _columns.ListColumnsAsync(project.Slug)).First();
        await _processors.SaveAsync(project.Slug, column.Id, "Worker", "Do work.", null,
            true, 20, [], [], []);
        var workspace = _projects.ResolveWorkspacePath(project);
        var legacyDirectory = Path.Combine(workspace, ".agents", $"column-{column.Id}");
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "memory.md"),
            $"# Legacy memory\n\n- Preserve this lesson.\n");

        await _processors.ListEnabledAsync(project.Slug);
        var canonicalPath = await _processors.GetMemoryIndexPathAsync(project.Slug, column.Id);
        var canonical = await File.ReadAllTextAsync(canonicalPath!);
        await _processors.GetMemoryIndexPathAsync(project.Slug, column.Id);
        var secondRead = await File.ReadAllTextAsync(canonicalPath!);

        Assert.Contains("- Preserve this lesson.", canonical);
        Assert.Equal(1, secondRead.Split("- Preserve this lesson.").Length - 1);
    }

    [Fact]
    public async Task Legacy_processor_memory_migration_uses_maintenance_worktree_without_dirtying_target()
    {
        var repository = ProjectWorktreeSettingsTests.CreateRepository(_temp.Path, "integration");
        var project = await _projects.CreateProjectAsync("Routed legacy processor memory");
        await _projects.UpdateProjectAsync(project.Slug, repository);
        var column = (await _columns.ListColumnsAsync(project.Slug)).First();
        var baselineProcessors = new ColumnProcessorService(_projects, _skills);
        await baselineProcessors.SaveAsync(project.Slug, column.Id, "Memory worker", "Preserve lessons.", null,
            true, 20, [], [], []);
        var legacyDirectory = Path.Combine(repository, ".agents", $"column-{column.Id}");
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "memory.md"),
            "# Legacy memory\n\n- Preserve this routed lesson.\n");
        Git(repository, "add", ".agents");
        Git(repository, "commit", "-m", "test: seed legacy processor memory");
        await _projects.UpdateProjectAsync(project.Slug, null,
            worktreesEnabled: true, integrationBranch: "integration");
        var tickets = new TicketService(_projects, new MemberService(_projects));
        var worktrees = new TicketWorktreeService(_projects, tickets);
        var queue = new WorktreeMergeQueueService(_projects, worktrees);
        var router = new DurableWriteRouter(_projects, worktrees, queue);
        var processors = new ColumnProcessorService(_projects, _skills,
            durableWrites: new Lazy<DurableWriteRouter>(() => router));
        var primaryIndex = Path.Combine(repository, ".agents", "processors", $"column-{column.Id}", "memory", "MEMORY.md");
        var initialStatus = Git(repository, "status", "--porcelain=v1", "--untracked-files=all");

        var routedIndex = await processors.GetMemoryIndexPathAsync(project.Slug, column.Id);

        Assert.Equal(initialStatus, Git(repository, "status", "--porcelain=v1", "--untracked-files=all"));
        Assert.DoesNotContain("- Preserve this routed lesson.", await File.ReadAllTextAsync(primaryIndex));
        var request = Assert.Single(await queue.ListAsync(project.Slug));
        Assert.Equal(WorktreeMergeJobKind.Maintenance, request.JobKind);
        Assert.StartsWith(Path.GetFullPath(request.WorktreePath), Path.GetFullPath(routedIndex!), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("- Preserve this routed lesson.", await File.ReadAllTextAsync(routedIndex!));

        await processors.GetMemoryIndexPathAsync(project.Slug, column.Id);
        Assert.Single(await queue.ListAsync(project.Slug));

        var integrated = await queue.ProcessNextAsync(project.Slug, CancellationToken.None);
        Assert.NotNull(integrated);
        Assert.Equal(WorktreeMergeStatus.Completed, integrated.Status);
        Assert.Contains("- Preserve this routed lesson.", await File.ReadAllTextAsync(primaryIndex));
        Assert.Equal(initialStatus, Git(repository, "status", "--porcelain=v1", "--untracked-files=all"));
    }

    public void Dispose() => _temp.Dispose();

    private static string Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
