using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
    private readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal);

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
                    var name = Resolve(action.Name ?? action.Project ?? "qa-test");
                    var resp = await _http.PostAsJsonAsync($"{_instanceApiUrl}/api/projects", new { name }, ct);
                    resp.EnsureSuccessStatusCode();
                    if (!string.IsNullOrEmpty(action.WorkspacePath))
                    {
                        var slug = SlugOf(name);
                        var patch = await _http.PatchAsJsonAsync($"{_instanceApiUrl}/api/projects/{slug}",
                            new { workspacePath = Resolve(action.WorkspacePath) }, ct);
                        patch.EnsureSuccessStatusCode();
                    }
                    break;
                }
            case "togglePause":
                {
                    var slug = SlugOf(Resolve(action.Project ?? "qa-test"));
                    var resp = await _http.PostAsync($"{_instanceApiUrl}/api/projects/{slug}/pause", null, ct);
                    resp.EnsureSuccessStatusCode();
                    break;
                }
            case "api":
            case "createTicket":
            case "assignTicket":
            case "setStatus":
                await ExecuteApiActionAsync(action, ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown setup action: {action.Type}");
        }
    }

    private async Task ExecuteApiActionAsync(ScenarioAction action, CancellationToken ct)
    {
        switch (action.Type)
        {
            case "createTicket":
                {
                    var project = Resolve(action.Project ?? "qa-test");
                    var slug = SlugOf(project);
                    var body = new Dictionary<string, object?> { ["title"] = Resolve(action.Title ?? "Untitled"), ["createdBy"] = Resolve(action.CreatedBy ?? "qa-runner") };
                    if (action.Status is not null) body["status"] = Resolve(action.Status);
                    if (action.Priority is not null) body["priority"] = Resolve(action.Priority);
                    if (action.AssignedTo is not null) body["assignedTo"] = Resolve(action.AssignedTo);
                    var resp = await _http.PostAsJsonAsync($"{_instanceApiUrl}/api/projects/{slug}/tickets", body, ct);
                    resp.EnsureSuccessStatusCode();
                    var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                    _vars["ticketId"] = json.GetProperty("id").GetInt32().ToString();
                    ExtractVars(json, action.Extract);
                    break;
                }
            case "assignTicket":
                {
                    var project = Resolve(action.Project ?? "qa-test");
                    var slug = SlugOf(project);
                    var id = Resolve(action.Value ?? _vars.GetValueOrDefault("ticketId") ?? throw new InvalidOperationException("assignTicket: no ticket id — set 'value' or use after createTicket"));
                    var body = new { assignedTo = Resolve(action.AssignedTo ?? throw new InvalidOperationException("assignTicket: 'assignedTo' is required")), author = "qa-runner" };
                    var resp = await _http.PatchAsJsonAsync($"{_instanceApiUrl}/api/projects/{slug}/tickets/{id}", body, ct);
                    resp.EnsureSuccessStatusCode();
                    break;
                }
            case "setStatus":
                {
                    var project = Resolve(action.Project ?? "qa-test");
                    var slug = SlugOf(project);
                    var id = Resolve(action.Value ?? _vars.GetValueOrDefault("ticketId") ?? throw new InvalidOperationException("setStatus: no ticket id — set 'value' or use after createTicket"));
                    var body = new { status = Resolve(action.Status ?? throw new InvalidOperationException("setStatus: 'status' is required")), author = "qa-runner" };
                    var resp = await _http.PatchAsJsonAsync($"{_instanceApiUrl}/api/projects/{slug}/tickets/{id}/status", body, ct);
                    resp.EnsureSuccessStatusCode();
                    break;
                }
            case "api":
            default:
                {
                    var method = (action.Method ?? "GET").ToUpperInvariant();
                    var path = Resolve(action.Path ?? throw new InvalidOperationException("api: 'path' is required"));
                    var url = Combine(_instanceApiUrl, path);
                    var request = new HttpRequestMessage(new HttpMethod(method), url);
                    if (action.Headers is not null)
                        foreach (var kv in action.Headers)
                            request.Headers.TryAddWithoutValidation(kv.Key, Resolve(kv.Value));
                    if (action.Body.HasValue)
                    {
                        var bodyStr = ResolveJson(action.Body.Value);
                        request.Content = new StringContent(bodyStr, Encoding.UTF8, "application/json");
                    }
                    var resp = await _http.SendAsync(request, ct);
                    resp.EnsureSuccessStatusCode();
                    if (action.Extract?.Count > 0)
                    {
                        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                        ExtractVars(json, action.Extract);
                    }
                    break;
                }
        }
    }

    private async Task ExecuteActionAsync(ScenarioAction action, IPage page, ScenarioResult result, CancellationToken ct)
    {
        switch (action.Type)
        {
            case "navigate":
                {
                    var target = action.Url is null ? _instanceApiUrl : Combine(_instanceApiUrl, Resolve(action.Url));
                    // Use Load (waits for the `load` event) rather than NetworkIdle: Blazor Server's
                    // SignalR keepalive pings prevent the network from ever becoming idle, so
                    // NetworkIdle would always time out.
                    await page.GotoAsync(target, new() { WaitUntil = WaitUntilState.Load });
                    break;
                }
            case "click":
                await page.ClickAsync(Required(Resolve(action.Selector), "click.selector"));
                break;
            case "fill":
                await page.FillAsync(Required(Resolve(action.Selector), "fill.selector"), Resolve(action.Value ?? ""));
                break;
            case "wait":
                await page.WaitForTimeoutAsync(action.Ms ?? 500);
                break;
            case "screenshot":
                {
                    var name = Resolve(action.Name ?? $"screenshot-{result.Screenshots.Count + 1}");
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
                    var selector = Required(Resolve(action.Selector), "assertCss.selector");
                    var prop = Required(action.Property, "assertCss.property");
                    var actual = await page.EvalOnSelectorAsync<string>(selector,
                        $"el => getComputedStyle(el).getPropertyValue('{prop}').trim()");
                    var passed = string.Equals(Normalise(actual), Normalise(Resolve(action.Expected)), StringComparison.OrdinalIgnoreCase);
                    result.Assertions.Add(new AssertionEntry
                    {
                        Selector = selector,
                        Property = prop,
                        Expected = Resolve(action.Expected),
                        Actual = actual,
                        Passed = passed,
                    });
                    break;
                }
            case "assertText":
                {
                    var selector = Required(Resolve(action.Selector), "assertText.selector");
                    var actual = (await page.TextContentAsync(selector))?.Trim();
                    var passed = string.Equals(actual, Resolve(action.Expected), StringComparison.Ordinal);
                    result.Assertions.Add(new AssertionEntry
                    {
                        Selector = selector,
                        Property = "textContent",
                        Expected = Resolve(action.Expected),
                        Actual = actual,
                        Passed = passed,
                    });
                    break;
                }
            case "assertVisible":
                {
                    var selector = Required(Resolve(action.Selector), "assertVisible.selector");
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
            case "assertJson":
                {
                    var path = Resolve(action.Path ?? throw new InvalidOperationException("assertJson: 'path' is required"));
                    var jsonPath = Required(Resolve(action.JsonPath), "assertJson.jsonPath");
                    var expected = Resolve(action.Expected);
                    var url = Combine(_instanceApiUrl, path);
                    var resp = await _http.GetAsync(url, ct);
                    resp.EnsureSuccessStatusCode();
                    var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                    var actual = ExtractPath(json, jsonPath);
                    var passed = string.Equals(actual, expected, StringComparison.Ordinal);
                    result.Assertions.Add(new AssertionEntry
                    {
                        Selector = jsonPath,
                        Property = "json",
                        Expected = expected,
                        Actual = actual ?? $"<path not found in: {json.GetRawText()}>",
                        Passed = passed,
                    });
                    break;
                }
            case "api":
            case "createTicket":
            case "assignTicket":
            case "setStatus":
                await ExecuteApiActionAsync(action, ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown action: {action.Type}");
        }
        await Task.CompletedTask; // suppress warning when no awaits in some branches
    }

    private string Resolve(string? s)
    {
        if (s is null) return "";
        foreach (var kv in _vars)
            s = s.Replace("{" + kv.Key + "}", kv.Value);
        return s;
    }

    private string ResolveJson(JsonElement element)
    {
        // Resolve variables inside a JSON body by round-tripping through string.
        var raw = element.GetRawText();
        foreach (var kv in _vars)
            raw = raw.Replace("{" + kv.Key + "}", kv.Value);
        return raw;
    }

    private void ExtractVars(JsonElement json, Dictionary<string, string>? extract)
    {
        if (extract is null) return;
        foreach (var kv in extract)
        {
            var val = ExtractPath(json, kv.Value);
            if (val is not null) _vars[kv.Key] = val;
        }
    }

    private static string? ExtractPath(JsonElement json, string dotPath)
    {
        var parts = dotPath.Split('.');
        JsonElement current = json;
        foreach (var part in parts)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
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
