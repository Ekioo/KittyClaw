using System.Net.Http.Json;
using Microsoft.Playwright;

namespace KittyClaw.QaRunner;

/// <summary>
/// Drives a Playwright browser against a target KittyClaw instance, executing the
/// <see cref="Scenario"/>'s setup + actions, capturing screenshots, returning a
/// <see cref="ScenarioResult"/>. Pure logic — process management is in
/// <see cref="TestInstance"/>, image upload in <see cref="ScreenshotUploader"/>.
/// </summary>
public sealed class ScenarioRunner
{
    private readonly string _instanceApiUrl;
    private readonly string _screenshotDir;
    private readonly HttpClient _http;

    public ScenarioRunner(string instanceApiUrl, string screenshotDir, HttpClient? http = null)
    {
        _instanceApiUrl = instanceApiUrl.TrimEnd('/');
        _screenshotDir = screenshotDir;
        Directory.CreateDirectory(_screenshotDir);
        _http = http ?? new HttpClient { BaseAddress = new Uri(_instanceApiUrl), Timeout = TimeSpan.FromSeconds(30) };
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(_instanceApiUrl);
    }

    public async Task<ScenarioResult> RunAsync(Scenario scenario, CancellationToken ct = default)
    {
        var result = new ScenarioResult { Verdict = "PASS" };

        // Setup phase: API calls only, no browser.
        foreach (var action in scenario.Setup)
        {
            await ExecuteSetupAsync(action, ct);
        }

        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        await using var ctxBrowser = await browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 900 },
        });
        var page = await ctxBrowser.NewPageAsync();

        foreach (var action in scenario.Actions)
        {
            await ExecuteActionAsync(action, page, result, ct);
        }

        if (scenario.Verdict.PassOn == "all-asserts-pass" && result.Assertions.Any(a => !a.Passed))
        {
            result.Verdict = "FAIL";
            result.Notes = (result.Notes ?? "") + " | Assertion(s) failed.";
        }

        return result;
    }

    private async Task ExecuteSetupAsync(ScenarioAction action, CancellationToken ct)
    {
        switch (action.Type)
        {
            case "createProject":
                {
                    var name = action.Name ?? action.Project ?? "qa-test";
                    var resp = await _http.PostAsJsonAsync($"{_instanceApiUrl}/api/projects", new { name }, ct);
                    resp.EnsureSuccessStatusCode();
                    if (!string.IsNullOrEmpty(action.WorkspacePath))
                    {
                        var slug = SlugOf(name);
                        var patch = await _http.PatchAsJsonAsync($"{_instanceApiUrl}/api/projects/{slug}",
                            new { workspacePath = action.WorkspacePath }, ct);
                        patch.EnsureSuccessStatusCode();
                    }
                    break;
                }
            case "togglePause":
                {
                    var slug = SlugOf(action.Project ?? "qa-test");
                    var resp = await _http.PostAsync($"{_instanceApiUrl}/api/projects/{slug}/pause", null, ct);
                    resp.EnsureSuccessStatusCode();
                    break;
                }
            default:
                throw new InvalidOperationException($"Unknown setup action: {action.Type}");
        }
    }

    private async Task ExecuteActionAsync(ScenarioAction action, IPage page, ScenarioResult result, CancellationToken ct)
    {
        switch (action.Type)
        {
            case "navigate":
                {
                    var target = action.Url is null ? _instanceApiUrl : Combine(_instanceApiUrl, action.Url);
                    // Use Load (waits for the `load` event) rather than NetworkIdle: Blazor Server's
                    // SignalR keepalive pings prevent the network from ever becoming idle, so
                    // NetworkIdle would always time out.
                    await page.GotoAsync(target, new() { WaitUntil = WaitUntilState.Load });
                    break;
                }
            case "click":
                await page.ClickAsync(Required(action.Selector, "click.selector"));
                break;
            case "fill":
                await page.FillAsync(Required(action.Selector, "fill.selector"), action.Value ?? "");
                break;
            case "wait":
                await page.WaitForTimeoutAsync(action.Ms ?? 500);
                break;
            case "screenshot":
                {
                    var name = action.Name ?? $"screenshot-{result.Screenshots.Count + 1}";
                    var path = Path.Combine(_screenshotDir, $"{name}.png");
                    await page.ScreenshotAsync(new() { Path = path, FullPage = true });
                    result.Screenshots.Add(new ScreenshotEntry
                    {
                        Name = name,
                        Description = action.Description,
                        LocalPath = path,
                    });
                    break;
                }
            case "assertCss":
                {
                    var selector = Required(action.Selector, "assertCss.selector");
                    var prop = Required(action.Property, "assertCss.property");
                    var actual = await page.EvalOnSelectorAsync<string>(selector,
                        $"el => getComputedStyle(el).getPropertyValue('{prop}').trim()");
                    var passed = string.Equals(Normalise(actual), Normalise(action.Expected), StringComparison.OrdinalIgnoreCase);
                    result.Assertions.Add(new AssertionEntry
                    {
                        Selector = selector,
                        Property = prop,
                        Expected = action.Expected,
                        Actual = actual,
                        Passed = passed,
                    });
                    break;
                }
            case "assertText":
                {
                    var selector = Required(action.Selector, "assertText.selector");
                    var actual = (await page.TextContentAsync(selector))?.Trim();
                    var passed = string.Equals(actual, action.Expected, StringComparison.Ordinal);
                    result.Assertions.Add(new AssertionEntry
                    {
                        Selector = selector,
                        Property = "textContent",
                        Expected = action.Expected,
                        Actual = actual,
                        Passed = passed,
                    });
                    break;
                }
            case "assertVisible":
                {
                    var selector = Required(action.Selector, "assertVisible.selector");
                    var visible = await page.IsVisibleAsync(selector);
                    result.Assertions.Add(new AssertionEntry
                    {
                        Selector = selector,
                        Property = "visible",
                        Expected = "true",
                        Actual = visible.ToString().ToLowerInvariant(),
                        Passed = visible,
                    });
                    break;
                }
            default:
                throw new InvalidOperationException($"Unknown action: {action.Type}");
        }
        await Task.CompletedTask; // suppress warning when no awaits in some branches
    }

    private static string Required(string? value, string label) =>
        !string.IsNullOrEmpty(value) ? value : throw new InvalidOperationException($"Scenario action missing '{label}'");

    private static string Normalise(string? s) => (s ?? "").Replace(" ", "").ToLowerInvariant();

    private static string Combine(string baseUrl, string path)
    {
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;
        return baseUrl + (path.StartsWith('/') ? path : "/" + path);
    }

    private static string SlugOf(string name)
    {
        // Mirror ProjectService.SlugRegex behaviour on the client side: lowercase + non-alphanum → '-'.
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-');
    }
}
