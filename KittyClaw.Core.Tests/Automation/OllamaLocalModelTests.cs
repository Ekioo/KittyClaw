using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public class OllamaLocalModelTests
{
    // Case 1: SaveLocalModelConfigAsync persists LocalModelBaseUrl and LocalModelName
    [Fact]
    public async Task SaveLocalModelConfig_PersistsAndReloads()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("ollama-save-test");

        await projects.SaveLocalModelConfigAsync(project.Slug, "http://localhost:11434", "qwen3-coder:30b");

        var loaded = await projects.GetProjectAsync(project.Slug);
        Assert.Equal("http://localhost:11434", loaded!.LocalModelBaseUrl);
        Assert.Equal("qwen3-coder:30b", loaded.LocalModelName);
    }

    [Fact]
    public async Task UpdateProject_FallbackModel_PersistsGrokAndSurvivesReload()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("fallback-grok-test");

        var updated = await projects.UpdateProjectAsync(
            project.Slug, workspacePath: null, fallbackModel: "grok-4.5", updateFallback: true);
        Assert.Equal("grok-4.5", updated!.FallbackModel);

        var loaded = await projects.GetProjectAsync(project.Slug);
        Assert.Equal("grok-4.5", loaded!.FallbackModel);

        // Workspace-only update must not clear the fallback. (Absolute path on every OS —
        // workspace paths are validated since §2.6.)
        var workspace = Path.Combine(tmp.Path, "work");
        await projects.UpdateProjectAsync(project.Slug, workspacePath: workspace, updateFallback: false);
        loaded = await projects.GetProjectAsync(project.Slug);
        Assert.Equal("grok-4.5", loaded!.FallbackModel);
        Assert.Equal(workspace, loaded.WorkspacePath);

        // Explicit clear.
        await projects.UpdateProjectAsync(project.Slug, loaded.WorkspacePath, fallbackModel: null, updateFallback: true);
        loaded = await projects.GetProjectAsync(project.Slug);
        Assert.Null(loaded!.FallbackModel);
    }

}
