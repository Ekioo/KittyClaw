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

    [Fact]
    public void Scenario_Deserialises_FromSampleJson()
    {
        var json = """
        {
          "setup": [
            { "type": "createProject", "name": "qa-test", "workspacePath": "D:/foo" },
            { "type": "togglePause", "project": "qa-test" }
          ],
          "actions": [
            { "type": "navigate", "url": "/" },
            { "type": "screenshot", "name": "home", "description": "Home" },
            { "type": "assertCss", "selector": ".x", "property": "color", "expected": "rgb(245,158,11)" }
          ],
          "verdict": { "passOn": "all-asserts-pass" }
        }
        """;

        var s = JsonSerializer.Deserialize<Scenario>(json, Opts);

        Assert.NotNull(s);
        Assert.Equal(2, s!.Setup.Count);
        Assert.Equal("createProject", s.Setup[0].Type);
        Assert.Equal("D:/foo", s.Setup[0].WorkspacePath);
        Assert.Equal(3, s.Actions.Count);
        Assert.Equal("/", s.Actions[0].Url);
        Assert.Equal("home", s.Actions[1].Name);
        Assert.Equal("rgb(245,158,11)", s.Actions[2].Expected);
        Assert.Equal("all-asserts-pass", s.Verdict.PassOn);
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
}
