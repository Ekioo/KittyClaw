using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Api;

public sealed partial class McpEndpointTests
{
    [Fact]
    public async Task Scheduled_task_contracts_are_exposed_in_live_OpenAPI_and_MCP_schemas()
    {
        var spec = await _http.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = spec.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/projects/{slug}/scheduled-tasks").TryGetProperty("get", out _));
        var column = paths.GetProperty("/api/projects/{slug}/columns/{columnId}/scheduled-tasks");
        Assert.True(column.TryGetProperty("get", out _));
        Assert.True(column.TryGetProperty("put", out _));
        Assert.True(column.TryGetProperty("delete", out _));
        var tools = await (await ConnectAsync()).ListToolsAsync();
        var schema = tools.Single(tool => tool.Name == "save_column_scheduled_tasks").JsonSchema.GetRawText();
        Assert.Contains("executePython", schema);
        Assert.Contains("executeProgram", schema);
        Assert.Contains("timeZoneId", schema);
        Assert.Contains("ticketScope", schema);
        Assert.True(tools.Single(tool => tool.Name == "delete_column_scheduled_tasks").ProtocolTool.Annotations?.DestructiveHint);
    }

    [Fact]
    public async Task Scheduled_tasks_round_trip_between_MCP_and_REST_without_executing_actions()
    {
        var project = await NewProjectAsync();
        var column = await CallAsync("create_column", new() { ["project"] = project,
            ["request"] = new { name = "Scheduled scripts" } });
        var columnId = column.GetProperty("id").GetInt32();
        var url = $"/api/projects/{project}/columns/{columnId}/scheduled-tasks";
        var listUrl = $"/api/projects/{project}/scheduled-tasks?columnId={columnId}";
        var request = new
        {
            tasks = new[] { new { name = "Python report", cron = "0 9 * * 1", enabled = false, timeZoneId = "UTC",
                ticketScope = "None", actions = new[] { new { id = "python", action = new
                { type = "executePython", script = "raise Exception('must never execute during configuration')" } } } } },
        };
        var saved = await CallAsync("save_column_scheduled_tasks", new()
        { ["project"] = project, ["columnId"] = columnId, ["request"] = request });
        var id = saved[0].GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(id));
        var viaRest = await _http.GetFromJsonAsync<JsonElement>(listUrl);
        Assert.Equal(viaRest.GetRawText(), (await _http.GetFromJsonAsync<JsonElement>(url)).GetRawText());
        Assert.Equal(id, viaRest[0].GetProperty("id").GetString());
        Assert.Equal("executePython", viaRest[0].GetProperty("actions")[0].GetProperty("action").GetProperty("type").GetString());
        Assert.False(viaRest[0].GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, viaRest[0].GetProperty("lastRunAt").ValueKind);
        Assert.False(viaRest[0].TryGetProperty("actionsJson", out _));
        var nextRun = viaRest[0].GetProperty("nextRunAt").GetString();
        var update = new SaveColumnScheduledTasksRequest([new("Renamed", "0 9 * * 1",
            [new("program", new ExecuteProgramActionSpec { Program = "not-installed-test-program" })],
            Id: id, Enabled: false, TimeZoneId: "UTC")]);
        using var updated = await _http.PutAsJsonAsync(url, update);
        updated.EnsureSuccessStatusCode();
        var viaMcp = await CallAsync("list_column_scheduled_tasks", new() { ["project"] = project, ["columnId"] = columnId });
        Assert.Equal(id, viaMcp[0].GetProperty("id").GetString());
        Assert.Equal("Renamed", viaMcp[0].GetProperty("name").GetString());
        Assert.Equal(nextRun, viaMcp[0].GetProperty("nextRunAt").GetString());
        Assert.Equal("executeProgram", viaMcp[0].GetProperty("actions")[0].GetProperty("action").GetProperty("type").GetString());
        await CallAsync("delete_column_scheduled_tasks", new() { ["project"] = project, ["columnId"] = columnId });
        Assert.Equal(0, (await _http.GetFromJsonAsync<JsonElement>(listUrl)).GetArrayLength());
        using var deleted = await _http.DeleteAsync(url);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Theory]
    [InlineData("bad cron", "UTC", "None")]
    [InlineData("0 9 * * *", "No/SuchZone", "None")]
    [InlineData("0 9 * * *", "UTC", "None", true)]
    public async Task Invalid_schedules_return_400_and_preserve_existing_definitions(
        string cron, string zone, string scope, bool comment = false)
    {
        var project = await NewProjectAsync();
        var column = await CallAsync("create_column", new() { ["project"] = project, ["request"] = new { name = "Validation" } });
        var columnId = column.GetProperty("id").GetInt32();
        var url = $"/api/projects/{project}/columns/{columnId}/scheduled-tasks";
        using var valid = await _http.PutAsJsonAsync(url, new SaveColumnScheduledTasksRequest([
            new("Keep", "0 9 * * *", [new("run", new ExecutePythonActionSpec { Module = "example" })],
                Id: "keep", Enabled: false, TimeZoneId: "UTC")]));
        valid.EnsureSuccessStatusCode();
        using var invalid = await _http.PutAsJsonAsync(url, new { tasks = new[] { new
        { id = "keep", name = "Invalid", cron, timeZoneId = zone, ticketScope = scope, enabled = false,
            actions = new[] { new { id = "action", action = new { type = comment ? "addComment" : "executePython",
                content = "requires ticket", module = "example" } } } } } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var saved = await CallAsync("list_column_scheduled_tasks", new() { ["project"] = project, ["columnId"] = columnId });
        Assert.Equal("Keep", saved[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Schedule_endpoints_report_missing_resources_and_reject_cross_column_task_ids()
    {
        var project = await NewProjectAsync();
        var first = await CallAsync("create_column", new() { ["project"] = project, ["request"] = new { name = "First" } });
        var second = await CallAsync("create_column", new() { ["project"] = project, ["request"] = new { name = "Second" } });
        var request = new SaveColumnScheduledTasksRequest([new("One", "0 9 * * *",
            [new("run", new ExecuteProgramActionSpec { Program = "git" })], Id: "shared-id", Enabled: false, TimeZoneId: "UTC")]);
        using var saved = await _http.PutAsJsonAsync($"/api/projects/{project}/columns/{first.GetProperty("id").GetInt32()}/scheduled-tasks", request);
        saved.EnsureSuccessStatusCode();
        using var conflict = await _http.PutAsJsonAsync($"/api/projects/{project}/columns/{second.GetProperty("id").GetInt32()}/scheduled-tasks", request);
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
        using var missingProject = await _http.GetAsync("/api/projects/no-such-project/scheduled-tasks");
        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);
        using var missingColumn = await _http.PutAsJsonAsync($"/api/projects/{project}/columns/2147483647/scheduled-tasks", request);
        Assert.Equal(HttpStatusCode.NotFound, missingColumn.StatusCode);
    }
}
