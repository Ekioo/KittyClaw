using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class AgentsTemplatePathSecurityTests
{
    public static IEnumerable<object[]> AssetTargets => new[]
    {
        ".agents/agent-budget.json", ".agents/memory-consolidation.md", ".agents/preamble.md",
        "AGENTS.md", "CLAUDE.md",
    }.SelectMany(relative => new[] { new object[] { relative, false }, new object[] { relative, true } });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linked_shared_directory_cannot_receive_template_writes(bool overwrite)
    {
        using var workspace = new TempDir();
        using var outside = new TempDir();
        const string marker = "Synthetic external policy.";
        var sentinel = Path.Combine(outside.Path, "policy.txt");
        await File.WriteAllTextAsync(sentinel, marker);
        var link = Path.Combine(workspace.Path, ".agents");
        LinkDirectory(link, outside.Path);
        try
        {
            var templates = new AgentsTemplateService();
            Assert.Empty(templates.DetectConflicts(workspace.Path));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                templates.InitializeAsync(workspace.Path, overwrite, initializeGit: true));
            Assert.Contains("linked", error.Message);
            Assert.Equal(marker, await File.ReadAllTextAsync(sentinel));
            Assert.Single(Directory.GetFiles(outside.Path));
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, ".git")));
            Assert.False(File.Exists(Path.Combine(workspace.Path, "AGENTS.md")));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [MemberData(nameof(AssetTargets))]
    public async Task Linked_asset_cannot_create_or_overwrite_an_external_file(string relative, bool targetExists)
    {
        using var workspace = new TempDir();
        using var outside = new TempDir();
        const string marker = "Synthetic external policy must survive.";
        var sentinel = Path.Combine(outside.Path, "policy.txt");
        if (targetExists) await File.WriteAllTextAsync(sentinel, marker);
        var link = Path.Combine(workspace.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, sentinel);
        try
        {
            var templates = new AgentsTemplateService();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                templates.InitializeAsync(workspace.Path, true, initializeGit: false));
            Assert.Equal(targetExists, File.Exists(sentinel));
            if (targetExists) Assert.Equal(marker, await File.ReadAllTextAsync(sentinel));
        }
        finally { File.Delete(link); }
    }

    [Theory]
    [InlineData(".agents/agent-budget.json")]
    [InlineData(".agents/memory-consolidation.md")]
    [InlineData(".agents/preamble.md")]
    [InlineData("AGENTS.md")]
    [InlineData("CLAUDE.md")]
    public async Task Skip_preserves_existing_linked_assets_without_opening_them_for_writing(string relative)
    {
        using var workspace = new TempDir();
        using var outside = new TempDir();
        const string marker = "Owner-managed synthetic shared policy.";
        var sentinel = Path.Combine(outside.Path, "policy.txt");
        await File.WriteAllTextAsync(sentinel, marker);
        var link = Path.Combine(workspace.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, sentinel);
        try
        {
            var templates = new AgentsTemplateService();
            Assert.Contains(relative, templates.DetectConflicts(workspace.Path));
            var result = await templates.InitializeAsync(workspace.Path, false, initializeGit: false);
            Assert.Contains(relative, result.Skipped);
            Assert.DoesNotContain(relative, result.Written);
            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.Equal(marker, await File.ReadAllTextAsync(sentinel));
        }
        finally { File.Delete(link); }
    }

    [Fact]
    public async Task Skip_preserves_a_linked_shared_directory_when_all_its_assets_already_exist()
    {
        using var workspace = new TempDir();
        using var outside = new TempDir();
        var templates = new AgentsTemplateService();
        foreach (var relative in templates.RelativePaths())
            await File.WriteAllTextAsync(Path.Combine(outside.Path, relative), "Owner-managed shared fixture.");
        var link = Path.Combine(workspace.Path, ".agents");
        LinkDirectory(link, outside.Path);
        try
        {
            var result = await templates.InitializeAsync(workspace.Path, false, initializeGit: false);
            Assert.All(templates.RelativePaths(), relative => Assert.Contains(".agents/" + relative, result.Skipped));
            Assert.Equal(templates.RelativePaths().Count, Directory.GetFiles(outside.Path).Length);
            foreach (var relative in templates.RelativePaths())
                Assert.Equal("Owner-managed shared fixture.", await File.ReadAllTextAsync(Path.Combine(outside.Path, relative)));
            Assert.Contains("AGENTS.md", result.Written);
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("CLAUDE.md")]
    public async Task Refusal_happens_before_any_other_template_is_created_or_overwritten(string linkedRootFile)
    {
        using var workspace = new TempDir();
        using var outside = new TempDir();
        var agents = Path.Combine(workspace.Path, ".agents");
        Directory.CreateDirectory(agents);
        var budget = Path.Combine(agents, "agent-budget.json");
        var preamble = Path.Combine(agents, "preamble.md");
        await File.WriteAllTextAsync(budget, "{\"dailyBudgetUsd\":12}");
        await File.WriteAllTextAsync(preamble, "Existing workspace instructions.");
        var sentinel = Path.Combine(outside.Path, "policy.txt");
        await File.WriteAllTextAsync(sentinel, "Existing external fixture.");
        var link = Path.Combine(workspace.Path, linkedRootFile);
        File.CreateSymbolicLink(link, sentinel);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new AgentsTemplateService().InitializeAsync(workspace.Path, true, initializeGit: false));
            Assert.Equal("{\"dailyBudgetUsd\":12}", await File.ReadAllTextAsync(budget));
            Assert.Equal("Existing workspace instructions.", await File.ReadAllTextAsync(preamble));
            Assert.False(File.Exists(Path.Combine(agents, "memory-consolidation.md")));
            Assert.Equal("Existing external fixture.", await File.ReadAllTextAsync(sentinel));
        }
        finally { File.Delete(link); }
    }

    [Fact]
    public void Preflight_keeps_the_explicitly_selected_workspace_as_the_trusted_root()
    {
        using var parent = new TempDir();
        using var selected = new TempDir();
        var workspace = Path.Combine(parent.Path, "selected");
        LinkDirectory(workspace, selected.Path);
        try
        {
            new AgentsTemplateService().ValidateWriteDestinations(workspace, false);
            Assert.Empty(Directory.EnumerateFileSystemEntries(selected.Path));
        }
        finally { Directory.Delete(workspace); }
    }

    private static void LinkDirectory(string link, string target)
    {
        if (OperatingSystem.IsWindows()) DurableWriteRouter.CreateDirectoryJunction(link, target);
        else Directory.CreateSymbolicLink(link, target);
    }
}
