using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class AgentPromptPathSecurityTests
{
    private static AgentRunContext Context(string workspace, string? scope = null) => new()
    {
        ProjectSlug = "prompt-path-fixture", WorkspacePath = workspace,
        AgentName = "programmer", SkillFile = "(inline)", SessionScope = scope,
        BoundaryEnforcement = BoundaryEnforcementMode.Enforce,
    };

    [Theory]
    [InlineData(".agents", "programmer/memory.md")]
    [InlineData(".agents/programmer", "memory.md")]
    [InlineData(".agents/programmer/memory", "MEMORY.md")]
    public async Task Linked_context_directory_cannot_supply_external_prompt_content(string relative, string targetFile)
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        var link = Path.Combine(root.Path, relative);
        var sentinel = Path.Combine(outside.Path, targetFile);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        await File.WriteAllTextAsync(sentinel, "EXTERNAL-SYNTHETIC-CONTEXT-20261122");
        if (OperatingSystem.IsWindows()) DurableWriteRouter.CreateDirectoryJunction(link, outside.Path);
        else Directory.CreateSymbolicLink(link, outside.Path);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => AgentRunner.BuildPromptAsync(
                Context(root.Path), "Ordinary instructions.", false, CancellationToken.None));
            Assert.Equal("EXTERNAL-SYNTHETIC-CONTEXT-20261122", await File.ReadAllTextAsync(sentinel));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("preamble.md")]
    [InlineData("programmer/memory.md")]
    [InlineData("programmer/memory/MEMORY.md")]
    [InlineData("programmer/memory/topic.md")]
    public async Task Linked_context_file_is_rejected_before_prompt_dispatch(string relative)
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        var index = Path.Combine(root.Path, ".agents", "programmer", "memory", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        if (!relative.EndsWith("MEMORY.md", StringComparison.Ordinal))
            await File.WriteAllTextAsync(index, "Ordinary memory index.");
        var link = Path.Combine(root.Path, ".agents", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var sentinel = Path.Combine(outside.Path, "fixture.txt");
        await File.WriteAllTextAsync(sentinel, "Synthetic external context.");
        File.CreateSymbolicLink(link, sentinel);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => AgentRunner.BuildPromptAsync(
                Context(root.Path, "consolidate"), "Ordinary instructions.", false, CancellationToken.None));
        }
        finally { File.Delete(link); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_context_keeps_preamble_index_legacy_and_consolidation_topics(bool consolidate)
    {
        using var root = new TempDir();
        var agent = Path.Combine(root.Path, ".agents", "programmer");
        var memory = Path.Combine(agent, "memory");
        Directory.CreateDirectory(memory);
        await File.WriteAllTextAsync(Path.Combine(root.Path, ".agents", "preamble.md"), "Shared instructions for {agent}.");
        await File.WriteAllTextAsync(Path.Combine(memory, "MEMORY.md"), "Ordinary memory index.");
        await File.WriteAllTextAsync(Path.Combine(memory, "topic.md"), "Ordinary topic details.");
        await File.WriteAllTextAsync(Path.Combine(agent, "memory.md"), "Ordinary legacy memory.");

        var prompt = await AgentRunner.BuildPromptAsync(Context(root.Path, consolidate ? "consolidate" : null),
            "Ordinary skill instructions.", false, CancellationToken.None);

        Assert.Contains("Shared instructions for programmer.", prompt);
        Assert.Contains("Ordinary memory index.", prompt);
        Assert.Contains("Ordinary legacy memory.", prompt);
        Assert.Contains("Ordinary skill instructions.", prompt);
        Assert.Equal(consolidate, prompt.Contains("Ordinary topic details.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linked_skill_fails_the_registered_run_without_launching_a_provider(bool linkDirectory)
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        var directory = Path.Combine(root.Path, ".agents", "programmer");
        var link = linkDirectory ? directory : Path.Combine(directory, "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var sentinel = Path.Combine(outside.Path, "SKILL.md");
        await File.WriteAllTextAsync(sentinel, "Synthetic external instructions.");
        if (!linkDirectory) File.CreateSymbolicLink(link, sentinel);
        else if (OperatingSystem.IsWindows()) DurableWriteRouter.CreateDirectoryJunction(link, outside.Path);
        else Directory.CreateSymbolicLink(link, outside.Path);
        try
        {
            var registry = new AgentRunRegistry();
            var runner = new AgentRunner(new SessionRegistry(root.Path), registry, new RunConcurrencyGate(1),
                NullLogger<AgentRunner>.Instance);
            var run = await runner.RunAsync(new AgentRunContext
            {
                ProjectSlug = "prompt-path-fixture", WorkspacePath = root.Path,
                AgentName = "programmer", SkillFile = "programmer/SKILL.md", PersistSession = false,
            }, CancellationToken.None);
            Assert.Equal(AgentRunStatus.Failed, run.Status);
            Assert.Same(run, registry.Get(run.RunId));
            Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error" && e.Text.Contains("linked"));
            Assert.DoesNotContain(run.SnapshotBuffer(), e => e.Kind is "launch" or "assistant");
        }
        finally
        {
            if (linkDirectory) Directory.Delete(link);
            else File.Delete(link);
        }
    }

    [Fact]
    public async Task Consolidation_instructions_cannot_include_an_external_link_target()
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        var projects = new ProjectService(root.Path);
        var project = await projects.CreateProjectAsync("Consolidation context fixture");
        var workspace = projects.ResolveWorkspacePath(project);
        var link = Path.Combine(workspace, ".agents", "memory-consolidation.md");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var sentinel = Path.Combine(outside.Path, "fixture.txt");
        await File.WriteAllTextAsync(sentinel, "Synthetic external consolidation instructions.");
        File.CreateSymbolicLink(link, sentinel);
        try
        {
            var runner = new AgentRunner(new SessionRegistry(root.Path), new AgentRunRegistry(),
                new RunConcurrencyGate(1), NullLogger<AgentRunner>.Instance);
            var handler = new AgentMemoryHandler(new MemberService(projects), projects, runner, NullLogger.Instance);
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ConsolidateAdHocConversationAsync(
                project.Slug, workspace, "programmer", "Synthetic conversation.", CancellationToken.None));
        }
        finally { File.Delete(link); }
    }
}
