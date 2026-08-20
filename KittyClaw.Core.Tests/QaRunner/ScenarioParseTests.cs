using System.Text.Json;
using KittyClaw.QaRunner;

namespace KittyClaw.Core.Tests.QaRunner;

/// <summary>
/// Lightweight smoke tests for the QaRunner DTO layer. Full Playwright-driven runs are
/// expensive (download Chromium ~150 MB on first hit) and require a real KittyClaw.Web
/// child process; those are exercised via the manual smoke test described in
/// KittyClaw.QaRunner/README.md, not in CI.
/// </summary>
public class ScenarioParseTests
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "KittyClaw.slnx")))
                return current.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the KittyClaw repository root.");
    }

    [Fact]
    public void Scenario_Deserialises_FromSampleJson()
    {
        var json = """
        {
          "setup": [
            { "type": "createGitRepository", "name": "repositoryPath", "value": "integration" },
            { "type": "createProject", "name": "qa-test", "workspacePath": "D:/foo" },
            { "type": "commitGitFile", "workspacePath": "{repositoryPath}", "target": "integration", "name": "fixture.txt", "text": "ready" },
            { "type": "togglePause", "project": "qa-test" }
          ],
          "actions": [
            { "type": "navigate", "url": "/" },
            { "type": "selectOption", "selector": ".model", "value": "codex:gpt-5.6-sol" },
            { "type": "screenshot", "name": "home", "description": "Home" },
            { "type": "assertValue", "selector": ".model", "expected": "codex:gpt-5.6-sol" },
            { "type": "assertCss", "selector": ".x", "property": "color", "expected": "rgb(245,158,11)" },
            { "type": "setViewport", "width": 390, "height": 844 }
          ],
          "verdict": { "passOn": "all-asserts-pass" }
        }
        """;

        var s = JsonSerializer.Deserialize<Scenario>(json, Opts);

        Assert.NotNull(s);
        Assert.Equal(4, s!.Setup.Count);
        Assert.Equal("createGitRepository", s.Setup[0].Type);
        Assert.Equal("repositoryPath", s.Setup[0].Name);
        Assert.Equal("createProject", s.Setup[1].Type);
        Assert.Equal("D:/foo", s.Setup[1].WorkspacePath);
        Assert.Equal("commitGitFile", s.Setup[2].Type);
        Assert.Equal(6, s.Actions.Count);
        Assert.Equal("/", s.Actions[0].Url);
        Assert.Equal("codex:gpt-5.6-sol", s.Actions[1].Value);
        Assert.Equal("home", s.Actions[2].Name);
        Assert.Equal("codex:gpt-5.6-sol", s.Actions[3].Expected);
        Assert.Equal("rgb(245,158,11)", s.Actions[4].Expected);
        Assert.Equal(390, s.Actions[5].Width);
        Assert.Equal(844, s.Actions[5].Height);
        Assert.Equal("all-asserts-pass", s.Verdict.PassOn);
    }

    [Fact]
    public void ScenarioRunner_DefinesSetViewportExactlyOnce()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "KittyClaw.QaRunner",
            "ScenarioRunner.cs"));

        Assert.Equal(1, source.Split("case \"setViewport\":", StringSplitOptions.None).Length - 1);
        Assert.Contains("action.Width", source, StringComparison.Ordinal);
        Assert.Contains("action.Height", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenarioResult_RoundTrip_PreservesAssertionsAndScreenshots()
    {
        var r = new ScenarioResult
        {
            Verdict = "FAIL",
            Notes = "color mismatch",
            Assertions =
            {
                new AssertionEntry { Selector = ".x", Property = "color", Expected = "rgb(0,0,0)", Actual = "rgb(255,0,0)", Passed = false },
            },
            Screenshots =
            {
                new ScreenshotEntry { Name = "home", Description = "home page", LocalPath = @"C:\tmp\home.png", UploadedUrl = "/uploads/abc.png" },
            },
        };

        var json = JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = false });
        var back = JsonSerializer.Deserialize<ScenarioResult>(json, Opts)!;

        Assert.Equal("FAIL", back.Verdict);
        Assert.Single(back.Assertions);
        Assert.False(back.Assertions[0].Passed);
        Assert.Single(back.Screenshots);
        Assert.Equal("/uploads/abc.png", back.Screenshots[0].UploadedUrl);
    }

    [Fact]
    public void ResolveJson_EscapesWindowsPathsAndPreservesNonStringValues()
    {
        using var document = JsonDocument.Parse("""{"repositoryPath":"{path}","ticketId":42,"enabled":true}""");

        var json = ScenarioRunner.ResolveJson(document.RootElement,
            new Dictionary<string, string> { ["path"] = @"C:\Users\admin\repo" });
        using var resolved = JsonDocument.Parse(json);

        Assert.Equal(@"C:\Users\admin\repo", resolved.RootElement.GetProperty("repositoryPath").GetString());
        Assert.Equal(42, resolved.RootElement.GetProperty("ticketId").GetInt32());
        Assert.True(resolved.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void ResolveText_ExpandsScenarioDirectoryInPortableFixtures()
    {
        var resolved = ScenarioRunner.ResolveText(
            @"{scenarioDirectory}\fixtures\rtk-fake.cmd",
            new Dictionary<string, string> { ["scenarioDirectory"] = @"D:\qa\ticket-290" });

        Assert.Equal(@"D:\qa\ticket-290\fixtures\rtk-fake.cmd", resolved);
    }
}
