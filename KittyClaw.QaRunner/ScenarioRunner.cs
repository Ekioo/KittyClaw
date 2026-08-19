using System.Net.Http.Json;
using System.Diagnostics;
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
            case "createWorkspaceDirectory":
                {
                    var variable = action.Name ?? "workspacePath";
                    var path = Path.Combine(_screenshotDir, "workspaces", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(path);
                    _vars[variable] = path;
                    break;
                }
            case "createGitRepository":
                {
                    var variable = action.Name ?? "repositoryPath";
                    var branch = Resolve(action.Value ?? "integration");
                    var path = Path.Combine(_screenshotDir, "repositories", Guid.NewGuid().ToString("N"));
                    CreateGitRepository(path, branch);
                    _vars[variable] = path;
                    break;
                }
            case "commitGitFile":
                {
                    var repository = Required(Resolve(action.WorkspacePath), "commitGitFile.workspacePath");
                    var branch = Required(Resolve(action.Target), "commitGitFile.target");
                    var file = Required(Resolve(action.Name), "commitGitFile.name");
                    CommitGitFile(repository, branch, file, Resolve(action.Text ?? "fixture\n"));
                    break;
                }
            case "createProject":
                {
                    var name = Resolve(action.Name ?? action.Project ?? "qa-test");
                    var resp = await _http.PostAsJsonAsync($"{_instanceApiUrl}/api/projects", new { name }, ct);
                    await EnsureSuccessAsync(resp, action, $"POST {_instanceApiUrl}/api/projects", ct);
                    if (!string.IsNullOrEmpty(action.WorkspacePath))
                    {
                        var slug = SlugOf(name);
                        var patch = await _http.PatchAsJsonAsync($"{_instanceApiUrl}/api/projects/{slug}",
                            new { workspacePath = Resolve(action.WorkspacePath) }, ct);
                        await EnsureSuccessAsync(patch, action, $"PATCH {_instanceApiUrl}/api/projects/{slug}", ct);
                    }
                    break;
                }
            case "togglePause":
                {
                    var slug = SlugOf(Resolve(action.Project ?? "qa-test"));
                    var resp = await _http.PostAsync($"{_instanceApiUrl}/api/projects/{slug}/pause", null, ct);
                    await EnsureSuccessAsync(resp, action, $"POST {_instanceApiUrl}/api/projects/{slug}/pause", ct);
                    break;
                }
            case "api":
            case "createTicket":
            case "assignTicket":
            case "setStatus":
            case "createDependency":
            case "waitForRun":
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
                    await EnsureSuccessAsync(resp, action, $"POST {_instanceApiUrl}/api/projects/{slug}/tickets", ct);
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
                    await EnsureSuccessAsync(resp, action, $"PATCH {_instanceApiUrl}/api/projects/{slug}/tickets/{id}", ct);
                    break;
                }
            case "setStatus":
                {
                    var project = Resolve(action.Project ?? "qa-test");
                    var slug = SlugOf(project);
                    var id = Resolve(action.Value ?? _vars.GetValueOrDefault("ticketId") ?? throw new InvalidOperationException("setStatus: no ticket id — set 'value' or use after createTicket"));
                    var body = new { status = Resolve(action.Status ?? throw new InvalidOperationException("setStatus: 'status' is required")), author = "qa-runner" };
                    var resp = await _http.PatchAsJsonAsync($"{_instanceApiUrl}/api/projects/{slug}/tickets/{id}/status", body, ct);
                    await EnsureSuccessAsync(resp, action, $"PATCH {_instanceApiUrl}/api/projects/{slug}/tickets/{id}/status", ct);
                    break;
                }
            case "createDependency":
                {
                    var project = Resolve(action.Project ?? "qa-test");
                    var slug = SlugOf(project);
                    var blockedId = int.Parse(Resolve(action.Value ?? _vars.GetValueOrDefault("ticketId") ?? throw new InvalidOperationException("createDependency: 'value' (blocked ticket id) is required")));
                    var blockerId = int.Parse(Resolve(action.Target ?? throw new InvalidOperationException("createDependency: 'target' (blocker ticket id) is required")));
                    var depUrl = $"{_instanceApiUrl}/api/projects/{slug}/tickets/{blockedId}/dependencies";
                    var depJson = $"{{\"blockedById\":{blockerId}}}";
                    var depContent = new StringContent(depJson, System.Text.Encoding.UTF8, "application/json");
                    var resp = await _http.PostAsync(depUrl, depContent, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        throw new InvalidOperationException($"createDependency failed {(int)resp.StatusCode}: {body}");
                    }
                    if (action.Extract?.Count > 0)
                    {
                        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                        ExtractVars(json, action.Extract);
                    }
                    break;
                }
            case "waitForRun":
                {
                    var project = Resolve(action.Project ?? "qa-test");
                    var slug = SlugOf(project);
                    var runId = Resolve(action.Value ?? (_vars.TryGetValue("runId", out var rv) ? rv : null)
                        ?? throw new InvalidOperationException("waitForRun: no runId — set 'value' or use after a step that extracts 'runId'"));
                    var timeoutMs = action.Ms ?? 30000;
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    while (true)
                    {
                        var resp = await _http.GetAsync($"{_instanceApiUrl}/api/projects/{slug}/runs/{runId}", ct);
                        if (resp.IsSuccessStatusCode)
                        {
                            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                            if (json.TryGetProperty("status", out var statusEl)
                                && statusEl.GetString() is { } s && s != "Running")
                                break;
                        }
                        if (DateTime.UtcNow > deadline)
                            throw new TimeoutException($"waitForRun: run {runId} did not complete within {timeoutMs} ms");
                        await Task.Delay(500, ct);
                    }
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
                        var bodyStr = ResolveJson(action.Body.Value, _vars);
                        request.Content = new StringContent(bodyStr, Encoding.UTF8, "application/json");
                    }
                    var resp = await _http.SendAsync(request, ct);
                    await EnsureSuccessAsync(resp, action, $"{method} {url}", ct);
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
            case "rightClick":
                await page.ClickAsync(Required(Resolve(action.Selector), "rightClick.selector"),
                    new() { Button = MouseButton.Right });
                break;
            case "dragAndDrop":
                await page.DragAndDropAsync(
                    Required(Resolve(action.Selector), "dragAndDrop.selector"),
                    Required(Resolve(action.Target), "dragAndDrop.target"));
                break;
            case "assertInteractionDuration":
                {
                    var selector = Required(Resolve(action.Selector), "assertInteractionDuration.selector");
                    var waitForSelector = Required(Resolve(action.WaitForSelector), "assertInteractionDuration.waitForSelector");
                    var maxMs = action.MaxMs ?? throw new InvalidOperationException("assertInteractionDuration: 'maxMs' is required");
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    await page.ClickAsync(selector);
                    await page.Locator(waitForSelector).WaitForAsync(new() { State = WaitForSelectorState.Visible });
                    stopwatch.Stop();
                    result.Assertions.Add(new AssertionEntry
                    {
                        Selector = selector,
                        Property = $"click-to-visible:{waitForSelector}",
                        Expected = $"<={maxMs} ms",
                        Actual = $"{stopwatch.ElapsedMilliseconds} ms",
                        Passed = stopwatch.ElapsedMilliseconds <= maxMs,
                    });
                    break;
                }
            case "assertNavigationDuration":
                {
                    var target = Combine(_instanceApiUrl, Required(Resolve(action.Url), "assertNavigationDuration.url"));
                    var waitForSelector = Required(Resolve(action.WaitForSelector), "assertNavigationDuration.waitForSelector");
                    var maxMs = action.MaxMs ?? throw new InvalidOperationException("assertNavigationDuration: 'maxMs' is required");
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    await page.GotoAsync(target, new() { WaitUntil = WaitUntilState.Load });
                    await page.Locator(waitForSelector).WaitForAsync(new() { State = WaitForSelectorState.Visible });
                    stopwatch.Stop();
                    result.Assertions.Add(new AssertionEntry
                    {
                        Selector = target,
                        Property = $"navigation-to-visible:{waitForSelector}",
                        Expected = $"<={maxMs} ms",
                        Actual = $"{stopwatch.ElapsedMilliseconds} ms",
                        Passed = stopwatch.ElapsedMilliseconds <= maxMs,
                    });
                    break;
                }
            case "fill":
                await page.FillAsync(Required(Resolve(action.Selector), "fill.selector"), Resolve(action.Value ?? ""));
                break;
            case "setLocalStorage":
                await page.EvaluateAsync("([key, value]) => localStorage.setItem(key, value)",
                    new[]
                    {
                        Required(Resolve(action.Name), "setLocalStorage.name"),
                        Resolve(action.Value ?? "")
                    });
                break;
            case "pasteImage":
                {
                    var selector = Required(Resolve(action.Selector), "pasteImage.selector");
                    var base64 = Required(Resolve(action.Value), "pasteImage.value");
                    var mime = Resolve(action.Property ?? "image/png");
                    var text = Resolve(action.Text);
                    await page.EvalOnSelectorAsync(selector, @"(el, payload) => {
                        const binary = atob(payload.base64);
                        const bytes = Uint8Array.from(binary, c => c.charCodeAt(0));
                        const file = new File([bytes], 'clipboard-image.png', { type: payload.mime });
                        const transfer = new DataTransfer();
                        transfer.items.add(file);
                        if (payload.text) transfer.setData('text/plain', payload.text);
                        const allowed = el.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true, cancelable: true }));
                        if (allowed && payload.text) {
                            el.value = el.value + payload.text;
                            el.dispatchEvent(new Event('input', { bubbles: true }));
                        }
                    }", new { base64, mime, text });
                    break;
                }
            case "pasteText":
                {
                    var selector = Required(Resolve(action.Selector), "pasteText.selector");
                    var text = Resolve(action.Value);
                    await page.EvalOnSelectorAsync(selector, @"(el, text) => {
                        const transfer = new DataTransfer();
                        transfer.setData('text/plain', text);
                        const allowed = el.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true, cancelable: true }));
                        if (allowed) {
                            el.value = el.value + text;
                            el.dispatchEvent(new Event('input', { bubbles: true }));
                        }
                    }", text);
                    break;
                }
            case "selectOption":
                await page.SelectOptionAsync(
                    Required(Resolve(action.Selector), "selectOption.selector"),
                    Resolve(action.Value ?? ""));
                break;
            case "wait":
                await page.WaitForTimeoutAsync(action.Ms ?? 500);
                break;
            case "screenshot":
                {
                    var name = Resolve(action.Name ?? $"screenshot-{result.Screenshots.Count + 1}");
                    var path = Path.Combine(_screenshotDir, $"{name}.png");
                    if (string.IsNullOrWhiteSpace(action.Selector))
                    {
                        await page.ScreenshotAsync(new() { Path = path, FullPage = true });
                    }
                    else
                    {
                        await page.Locator(Resolve(action.Selector)).ScreenshotAsync(new() { Path = path });
                    }
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
            case "assertCount":
                {
                    var selector = Required(Resolve(action.Selector), "assertCount.selector");
                    var expected = int.Parse(Required(Resolve(action.Expected), "assertCount.expected"));
                    var actual = await page.Locator(selector).CountAsync();
                    result.Assertions.Add(new AssertionEntry
                    {
                        Selector = selector,
                        Property = "count",
                        Expected = expected.ToString(),
                        Actual = actual.ToString(),
                        Passed = actual == expected,
                    });
                    break;
                }
            case "assertValue":
                {
                    var selector = Required(Resolve(action.Selector), "assertValue.selector");
                    var actual = await page.InputValueAsync(selector);
                    var expected = Resolve(action.Expected);
                    result.Assertions.Add(new AssertionEntry
                    {
                        Selector = selector,
                        Property = "value",
                        Expected = expected,
                        Actual = actual,
                        Passed = string.Equals(actual, expected, StringComparison.Ordinal),
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
            case "waitForRun":
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

    internal static string ResolveJson(JsonElement element, IReadOnlyDictionary<string, string> variables)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteResolvedJson(writer, element, variables);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteResolvedJson(
        Utf8JsonWriter writer,
        JsonElement element,
        IReadOnlyDictionary<string, string> variables)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteResolvedJson(writer, property.Value, variables);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray())
                    WriteResolvedJson(writer, child, variables);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? "";
                foreach (var variable in variables)
                    value = value.Replace("{" + variable.Key + "}", variable.Value);
                writer.WriteStringValue(value);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
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
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var property))
                current = property;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index)
                     && index >= 0 && index < current.GetArrayLength())
                current = current[index];
            else return null;
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

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        ScenarioAction action,
        string request,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"Scenario action '{action.Type}' failed: {request} returned {(int)response.StatusCode} {response.ReasonPhrase}. Response: {body}");
    }

    internal static void CreateGitRepository(string path, string branch)
    {
        Directory.CreateDirectory(path);
        RunGit(path, "init", "-b", branch);
        RunGit(path, "config", "user.email", "qa-runner@kittyclaw.local");
        RunGit(path, "config", "user.name", "KittyClaw QA");
        File.WriteAllText(Path.Combine(path, "README.md"), "# QA repository\n");
        RunGit(path, "add", "README.md");
        RunGit(path, "commit", "-m", "initial fixture");
    }

    internal static void CommitGitFile(string repository, string branch, string relativePath, string content)
    {
        var worktree = ResolveGitWorktree(repository, branch);
        var fullPath = Path.GetFullPath(Path.Combine(worktree, relativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(worktree) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"commitGitFile path '{relativePath}' escapes worktree '{worktree}'.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        RunGit(worktree, "add", "--", relativePath);
        RunGit(worktree, "commit", "-m", $"fixture: {relativePath.Replace('\\', '/')}");
    }

    private static string ResolveGitWorktree(string repository, string branch)
    {
        var output = RunGit(repository, "worktree", "list", "--porcelain");
        string? current = null;
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                current = line[9..];
            else if (current is not null
                     && line.Equals($"branch refs/heads/{branch}", StringComparison.Ordinal))
                return Path.GetFullPath(current);
            else if (line.Length == 0)
                current = null;
        }
        throw new InvalidOperationException($"No Git worktree for branch '{branch}' is registered in '{repository}'.");
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Git could not be started for the QA fixture.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"Git fixture command timed out: git {string.Join(' ', arguments)}");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git fixture command failed ({process.ExitCode}): git {string.Join(' ', arguments)}. {error.Trim()}");
        return output;
    }
}
