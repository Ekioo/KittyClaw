using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Services;

public sealed class DashboardScriptRunnerTests
{
    private readonly DashboardScriptRunner _runner = new(NullLogger<DashboardScriptRunner>.Instance);

    [Theory]
    [InlineData("script.ps1", true)]
    [InlineData("script.sh", true)]
    [InlineData("script.js", true)]
    [InlineData("script.py", true)]
    [InlineData("script.PS1", true)]
    [InlineData("script.txt", false)]
    [InlineData("script.bat", false)]
    [InlineData("script", false)]
    public void IsSupported_ReturnsCorrectResult(string fileName, bool expected)
    {
        Assert.Equal(expected, DashboardScriptRunner.IsSupported(fileName));
    }

    [Fact]
    public async Task RunAsync_UnsupportedExtension_ReturnsConfigError()
    {
        var result = await _runner.RunAsync("script.bat", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ConfigError);
        Assert.Contains("Unsupported", result.ConfigError);
    }

    [Fact]
    public async Task RunAsync_MissingInterpreter_ReturnsConfigError()
    {
        var result = await _runner.RunAsync(
            Path.Combine(Path.GetTempPath(), "nonexistent_script_196.py"),
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RunAsync_PowerShellEchoScript_CapturesStdout()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kc-test-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(tmp, "Write-Output 'hello from script'");
            var result = await _runner.RunAsync(tmp, Directory.GetCurrentDirectory(), CancellationToken.None);

            if (result.ConfigError is not null) return; // pwsh not available

            Assert.True(result.IsSuccess, $"Script failed: exit={result.ExitCode} stderr={result.Stderr}");
            Assert.Contains("hello from script", result.Stdout);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ReturnsFailure()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kc-test-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(tmp, "Write-Error 'boom'; exit 1");
            var result = await _runner.RunAsync(tmp, Directory.GetCurrentDirectory(), CancellationToken.None);

            if (result.ConfigError is not null) return; // pwsh not available

            Assert.False(result.IsSuccess);
            Assert.NotEqual(0, result.ExitCode);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}

public sealed class TileSidecarTests
{
    [Fact]
    public void TryParse_BasicFields_Parsed()
    {
        var yaml = "template: markdown\nrefresh: 3600\nprompt: \"summarise\"\nmodel: \"\"\ntitle: \"My Tile\"";
        var sidecar = TileSidecarSerializer.TryParse(yaml);

        Assert.NotNull(sidecar);
        Assert.Equal("markdown", sidecar.Template);
        Assert.Equal(3600, sidecar.Refresh);
        Assert.Equal("summarise", sidecar.Prompt);
        Assert.Equal("My Tile", sidecar.Title);
    }

    [Fact]
    public void TryParse_OldScriptField_Ignored()
    {
        // Old sidecars with script: field must parse without error (IgnoreUnmatchedProperties)
        var yaml = "template: markdown\nrefresh: 0\nprompt: \"\"\nmodel: \"\"\ntitle: \"\"\nscript: my-tile.ps1";
        var sidecar = TileSidecarSerializer.TryParse(yaml);

        Assert.NotNull(sidecar);
        Assert.Equal("markdown", sidecar.Template);
    }

    [Fact]
    public void Serialize_RoundTrip()
    {
        var original = new TileSidecar("donut", 86400, "Count tickets by priority", "claude-opus-4-7", "Priority distribution");
        var yaml = TileSidecarSerializer.Serialize(original);
        var parsed = TileSidecarSerializer.TryParse(yaml);

        Assert.NotNull(parsed);
        Assert.Equal(original.Template, parsed.Template);
        Assert.Equal(original.Refresh, parsed.Refresh);
        Assert.Equal(original.Prompt, parsed.Prompt);
        Assert.Equal(original.Model, parsed.Model);
        Assert.Equal(original.Title, parsed.Title);
    }
}

public sealed class DashboardServicePathTests
{
    // Uses a temporary directory so no ProjectService needed for path-only tests.

    [Fact]
    public void GetTileDirPath_ReturnsExpectedPath()
    {
        var workspace = @"C:\workspace";
        var svc = CreateService();
        var dir = svc.GetTileDirPath(workspace, "my-tile");
        Assert.Equal(Path.Combine(workspace, ".dashboard", "my-tile"), dir);
    }

    [Fact]
    public void GetSidecarPath_ReturnsExpectedPath()
    {
        var workspace = @"C:\workspace";
        var svc = CreateService();
        var path = svc.GetSidecarPath(workspace, "my-tile");
        Assert.Equal(Path.Combine(workspace, ".dashboard", "my-tile", "tile.yaml"), path);
    }

    [Fact]
    public void GetOutputPath_MarkdownTemplate_ReturnsMdExtension()
    {
        var workspace = @"C:\workspace";
        var svc = CreateService();
        var path = svc.GetOutputPath(workspace, "my-tile", "markdown");
        Assert.Equal(Path.Combine(workspace, ".dashboard", "my-tile", "output.md"), path);
    }

    [Fact]
    public void FindScript_NoFolder_ReturnsNull()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var svc = CreateService();
        var (path, error) = svc.FindScript(tmp, "nonexistent");
        Assert.Null(path);
        Assert.Null(error);
    }

    [Fact]
    public void FindScript_SingleScript_ReturnsPath()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var tileDir = Path.Combine(tmp, ".dashboard", "my-tile");
            Directory.CreateDirectory(tileDir);
            var scriptPath = Path.Combine(tileDir, "script.ps1");
            File.WriteAllText(scriptPath, "# test");

            var svc = CreateService();
            var (found, error) = svc.FindScript(tmp, "my-tile");
            Assert.NotNull(found);
            Assert.Null(error);
            Assert.Equal(scriptPath, found);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void FindScript_MultipleScripts_ReturnsConfigError()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var tileDir = Path.Combine(tmp, ".dashboard", "my-tile");
            Directory.CreateDirectory(tileDir);
            File.WriteAllText(Path.Combine(tileDir, "script.ps1"), "# a");
            File.WriteAllText(Path.Combine(tileDir, "script.py"), "# b");

            var svc = CreateService();
            var (path, error) = svc.FindScript(tmp, "my-tile");
            Assert.Null(path);
            Assert.NotNull(error);
            Assert.Contains("Multiple", error);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void GetAvailableSlugs_ListsSubfolders()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var dashDir = Path.Combine(tmp, ".dashboard");
            Directory.CreateDirectory(Path.Combine(dashDir, "alpha"));
            Directory.CreateDirectory(Path.Combine(dashDir, "beta"));

            var svc = CreateService();
            var slugs = svc.GetAvailableSlugs(tmp);
            Assert.Equal(["alpha", "beta"], slugs);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public async Task MigrateAsync_FlatLayout_CreatesExpectedFolders()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var dashDir = Path.Combine(tmp, ".dashboard");
            Directory.CreateDirectory(dashDir);

            // Write flat sidecar
            await File.WriteAllTextAsync(Path.Combine(dashDir, "my-tile.json.yaml"),
                "template: donut\nrefresh: 3600\nprompt: \"\"\nmodel: \"\"\ntitle: My Tile\nscript: my-tile.ps1");
            // Write result file
            await File.WriteAllTextAsync(Path.Combine(dashDir, "my-tile.json"), "[{\"label\":\"a\",\"value\":1}]");
            // Write script
            await File.WriteAllTextAsync(Path.Combine(dashDir, "my-tile.ps1"), "# script");

            var svc = CreateService();
            var log = new List<string>();
            // projectSlug doesn't matter here since we don't test DB migration in this test
            await svc.MigrateAsync("test-project", tmp, msg => log.Add(msg));

            // New folder should exist
            var tileDir = Path.Combine(dashDir, "my-tile");
            Assert.True(Directory.Exists(tileDir), "tile folder not created");
            Assert.True(File.Exists(Path.Combine(tileDir, "tile.yaml")), "tile.yaml missing");
            Assert.True(File.Exists(Path.Combine(tileDir, "output.json")), "output.json missing");
            Assert.True(File.Exists(Path.Combine(tileDir, "script.ps1")), "script.ps1 missing");

            // Old flat files should be gone
            Assert.False(File.Exists(Path.Combine(dashDir, "my-tile.json.yaml")));
            Assert.False(File.Exists(Path.Combine(dashDir, "my-tile.json")));
            Assert.False(File.Exists(Path.Combine(dashDir, "my-tile.ps1")));

            // tile.yaml should NOT contain the script field
            var yaml = await File.ReadAllTextAsync(Path.Combine(tileDir, "tile.yaml"));
            Assert.DoesNotContain("script:", yaml.ToLowerInvariant());
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public async Task MigrateAsync_FolderAlreadyExists_Skipped()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var dashDir = Path.Combine(tmp, ".dashboard");
            Directory.CreateDirectory(dashDir);
            // Write flat sidecar
            await File.WriteAllTextAsync(Path.Combine(dashDir, "my-tile.json.yaml"),
                "template: donut\nrefresh: 0\nprompt: \"\"\nmodel: \"\"\ntitle: \"\"");
            // Folder already exists
            var tileDir = Path.Combine(dashDir, "my-tile");
            Directory.CreateDirectory(tileDir);
            await File.WriteAllTextAsync(Path.Combine(tileDir, "tile.yaml"),
                "template: markdown\nrefresh: 0\nprompt: \"\"\nmodel: \"\"\ntitle: existing");

            var svc = CreateService();
            await svc.MigrateAsync("test-project", tmp);

            // Should not overwrite existing folder
            var yaml = await File.ReadAllTextAsync(Path.Combine(tileDir, "tile.yaml"));
            Assert.Contains("existing", yaml);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrateAsync_RefusesScriptOutsideDashboard(bool absolute)
    {
        using var temp = new TempDir();
        var workspace = Path.Combine(temp.Path, "workspace");
        var dashboard = Directory.CreateDirectory(Path.Combine(workspace, ".dashboard")).FullName;
        var outside = Path.Combine(temp.Path, "fixture.sh");
        var script = absolute ? outside : Path.GetRelativePath(dashboard, outside);
        var sidecar = Path.Combine(dashboard, "tile.json.yaml");
        var output = Path.Combine(dashboard, "tile.json");
        await File.WriteAllTextAsync(outside, "# synthetic fixture");
        await File.WriteAllTextAsync(output, "{}");
        await File.WriteAllTextAsync(sidecar, $"template: markdown\nscript: '{script}'");

        await CreateService().MigrateAsync("test-project", workspace);

        Assert.Equal("# synthetic fixture", await File.ReadAllTextAsync(outside));
        Assert.True(File.Exists(sidecar));
        Assert.True(File.Exists(output));
        Assert.False(Directory.Exists(Path.Combine(dashboard, "tile")));
    }

    [Fact]
    public async Task MigrateAsync_PreservesNestedInDashboardScriptSupport()
    {
        using var temp = new TempDir();
        var dashboard = Directory.CreateDirectory(Path.Combine(temp.Path, ".dashboard")).FullName;
        var scripts = Directory.CreateDirectory(Path.Combine(dashboard, "scripts")).FullName;
        var script = Path.Combine(scripts, "fixture.sh");
        await File.WriteAllTextAsync(script, "# synthetic fixture");
        await File.WriteAllTextAsync(Path.Combine(dashboard, "tile.json.yaml"),
            "template: markdown\nscript: scripts/fixture.sh");

        await CreateService().MigrateAsync("test-project", temp.Path);

        Assert.Equal("# synthetic fixture",
            await File.ReadAllTextAsync(Path.Combine(dashboard, "tile", "script.sh")));
        Assert.False(File.Exists(script));
    }

    [Fact]
    public async Task MigrateAsync_RefusesScriptUnderLinkedDirectory()
    {
        using var temp = new TempDir();
        var workspace = Path.Combine(temp.Path, "workspace");
        var dashboard = Directory.CreateDirectory(Path.Combine(workspace, ".dashboard")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside")).FullName;
        var link = Path.Combine(dashboard, "scripts");
        if (OperatingSystem.IsWindows())
            DurableWriteRouter.CreateDirectoryJunction(link, outside);
        else
            Directory.CreateSymbolicLink(link, outside);

        try
        {
            var script = Path.Combine(outside, "fixture.sh");
            var sidecar = Path.Combine(dashboard, "tile.json.yaml");
            await File.WriteAllTextAsync(script, "# synthetic fixture");
            await File.WriteAllTextAsync(sidecar, "template: markdown\nscript: scripts/fixture.sh");

            await CreateService().MigrateAsync("test-project", workspace);

            Assert.Equal("# synthetic fixture", await File.ReadAllTextAsync(script));
            Assert.True(File.Exists(sidecar));
            Assert.False(Directory.Exists(Path.Combine(dashboard, "tile")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Tile_file_operations_reject_linked_files()
    {
        using var workspace = new TempDir();
        using var outside = new TempDir();
        var dashboard = CreateService();
        const string tile = "fixture";
        var directory = Directory.CreateDirectory(dashboard.GetTileDirPath(workspace.Path, tile)).FullName;
        var sidecar = new TileSidecar(TileTemplate.Markdown, 0);
        var script = DashboardService.ScriptBaseName + DashboardService.DefaultScriptExtension();
        foreach (var filename in new[] { DashboardService.SidecarFileName, "output.md", script })
        {
            var target = Path.Combine(outside.Path, filename);
            await File.WriteAllTextAsync(target, FixtureContent(filename));
            var link = Path.Combine(directory, filename);
            if (OperatingSystem.IsWindows())
            {
                // Git for Windows can run on hosts without symlink privileges. Exercise
                // file links on those hosts only when the filesystem permits their creation.
                try { File.CreateSymbolicLink(link, target); }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 1314)
                {
                    return;
                }
            }
            else
                File.CreateSymbolicLink(link, target);
        }

        Assert.Null(await dashboard.ReadSidecarAsync(workspace.Path, tile));
        Assert.Null(await dashboard.ReadOutputAsync(workspace.Path, tile));
        var (scriptPath, configError) = dashboard.FindScript(workspace.Path, tile);
        Assert.Null(scriptPath);
        Assert.NotNull(configError);

        await Assert.ThrowsAsync<DashboardTilePathException>(
            () => dashboard.WriteSidecarAsync(workspace.Path, tile, sidecar));
        await Assert.ThrowsAsync<DashboardTilePathException>(
            () => dashboard.WriteOutputAsync(workspace.Path, tile, "updated fixture", TileTemplate.Markdown));
        await Assert.ThrowsAsync<DashboardTilePathException>(
            () => dashboard.WriteScriptAsync(workspace.Path, tile, "updated fixture"));
        foreach (var target in Directory.GetFiles(outside.Path))
            Assert.Equal(FixtureContent(Path.GetFileName(target)), await File.ReadAllTextAsync(target));

        static string FixtureContent(string filename) => filename == DashboardService.SidecarFileName
            ? "template: markdown\nrefresh: 0\ntitle: Synthetic fixture"
            : "synthetic fixture";
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("one/two")]
    public void Tile_directory_operations_require_one_name(string tileSlug)
    {
        using var fixture = new TempDir();
        var dashboard = CreateService();

        Assert.False(dashboard.IsTileDirectoryAllowed(fixture.Path, tileSlug));
        Assert.Throws<DashboardTilePathException>(() => dashboard.DeleteTileFolder(fixture.Path, tileSlug));
    }

    private static DashboardService CreateService()
    {
        // Create a minimal ProjectService stub — we only call path-based DashboardService methods
        // that don't touch the DB, so passing null is safe.
        return new DashboardService(null!);
    }
}