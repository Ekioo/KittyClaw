using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace KittyClaw.Core.Tests.Api;

public sealed partial class McpEndpointTests
{
    private static readonly string[] ManagementTools =
    [
        "add_ticket_dependency",
        "analyze_ticket_improvement",
        "apply_ticket_improvement",
        "answer_ticket_question",
        "ask_ticket_question",
        "cancel_column_execution",
        "configure_local_model",
        "create_column",
        "create_label",
        "create_member",
        "create_pipeline",
        "create_project",
        "create_project_skill",
        "decide_approval",
        "delete_column",
        "delete_column_processor",
        "delete_label",
        "delete_member",
        "delete_project_skill",
        "delete_ticket",
        "delete_ticket_comment",
        "enqueue_worktree_merge",
        "focus_ticket",
        "get_column_processor",
        "get_engine_health",
        "get_project",
        "get_project_skill",
        "get_run",
        "get_run_evidence",
        "get_runtime_capabilities",
        "get_ticket_brief",
        "get_ticket_evidence",
        "get_worktree_merge",
        "list_approval_decisions",
        "list_approval_receipts",
        "list_approval_requests",
        "list_column_executions",
        "list_columns",
        "list_concurrency_groups",
        "list_labels",
        "list_pipelines",
        "list_project_skills",
        "list_runs",
        "list_ticket_questions",
        "list_worktree_merges",
        "patch_ticket_labels",
        "process_next_worktree_merge",
        "remove_ticket_dependency",
        "remove_ticket_parent",
        "reorder_column",
        "reorder_ticket",
        "resume_worktree_merge",
        "retry_column_execution",
        "retry_run",
        "retry_worktree_synchronization",
        "save_column_processor",
        "schedule_ticket",
        "set_ticket_parent",
        "steer_run",
        "stop_run",
        "toggle_project_pause",
        "transfer_ticket",
        "update_column",
        "update_label",
        "update_member",
        "update_pipeline",
        "update_project",
        "update_project_skill",
        "update_ticket",
        "update_ticket_comment",
    ];

    private async Task<JsonElement> CallAsync(string name, Dictionary<string, object?>? arguments = null)
    {
        var client = _mcp ?? await ConnectAsync();
        var result = await client.CallToolAsync(name, arguments);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.False(result.IsError == true, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<string> NewProjectAsync()
    {
        var result = await CallAsync("create_project", new()
        {
            ["request"] = new { name = "mcp-management-" + Guid.NewGuid().ToString("N")[..8] },
        });
        return result.GetProperty("slug").GetString()!;
    }

    [Fact]
    public async Task ManagementSchemas_ExcludeServicesAndCancellation_AndDescribeEffects()
    {
        var client = await ConnectAsync();
        var tools = await client.ListToolsAsync();
        foreach (var tool in tools)
        {
            var properties = tool.JsonSchema.GetProperty("properties");
            foreach (var property in properties.EnumerateObject())
                Assert.DoesNotContain(property.Name, new[] { "notifier", "services", "cancellationToken", "ts", "registry", "engine" });
        }
        var delete = tools.Single(t => t.Name == "delete_ticket");
        Assert.True(delete.ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools.Single(t => t.Name == "get_run").ProtocolTool.Annotations?.ReadOnlyHint);
        var update = tools.Single(t => t.Name == "update_ticket");
        Assert.True(update.JsonSchema.GetProperty("properties").TryGetProperty("request", out _));
    }

    [Fact]
    public async Task Management_RestDocumentation_ContainsSharedOperations()
    {
        var spec = await _http.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = spec.GetProperty("paths");
        Assert.DoesNotContain(paths.EnumerateObject(), path => path.Name.Contains("/automations", StringComparison.Ordinal) || path.Name.Contains("automation-queue", StringComparison.Ordinal));
        var parameters = paths.GetProperty("/api/projects/{slug}/runs").GetProperty("get").GetProperty("parameters");
        Assert.Contains(parameters.EnumerateArray(), p => p.GetProperty("name").GetString() == "includeCompleted");
        using var document = JsonDocument.Parse(spec.GetRawText());
        var docs = OpenApiMarkdownGenerator.Generate(document);
        Assert.Contains("includeCompleted", docs);
    }

    [Fact]
    public async Task Management_CreateConfigureAndDelete_RoundTripsThroughRest()
    {
        var project = await NewProjectAsync();
        var member = await CallAsync("create_member", new() { ["project"] = project, ["request"] = new { name = "Reviewer" } });
        var pipeline = await CallAsync("create_pipeline", new() { ["project"] = project, ["request"] = new { name = "Research" } });
        var pipelineId = pipeline.GetProperty("id").GetInt32();
        var column = await CallAsync("create_column", new()
        {
            ["project"] = project, ["request"] = new { name = "Investigate", pipelineId },
        });
        var columnId = column.GetProperty("id").GetInt32();
        await CallAsync("update_column", new()
        {
            ["project"] = project, ["columnId"] = columnId, ["request"] = new { userGuidance = "Collect evidence" },
        });
        var created = await CallAsync("create_ticket", new()
        {
            ["project"] = project, ["title"] = "Investigate behavior", ["pipelineId"] = pipelineId, ["columnId"] = columnId,
        });
        var id = created.GetProperty("id").GetInt32();
        await CallAsync("update_ticket", new()
        {
            ["project"] = project, ["id"] = id,
            ["request"] = new { author = "owner", title = "Updated through MCP", priority = "Critical", assignedTo = member.GetProperty("slug").GetString() },
        });
        var ticket = await _http.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/tickets/{id}");
        Assert.Equal("Updated through MCP", ticket.GetProperty("title").GetString());
        Assert.Equal("Critical", ticket.GetProperty("priority").GetString());
        Assert.Equal(columnId, ticket.GetProperty("columnId").GetInt32());
        var list = await CallAsync("list_tickets", new() { ["project"] = project, ["search"] = "Updated through MCP", ["priority"] = "Critical" });
        Assert.Single(list.EnumerateArray());
        await CallAsync("delete_ticket", new() { ["project"] = project, ["id"] = id });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _http.GetAsync($"/api/projects/{project}/tickets/{id}")).StatusCode);
    }

    [Fact]
    public async Task Management_ConflictAndUnknownProjects_AreToolErrors_WithoutSideEffects()
    {
        var project = await NewProjectAsync();
        var created = await CallAsync("create_ticket", new() { ["project"] = project, ["title"] = "Concurrent update" });
        var id = created.GetProperty("id").GetInt32();
        var conflict = await _mcp!.CallToolAsync("update_ticket", new Dictionary<string, object?>
        {
            ["project"] = project, ["id"] = id,
            ["request"] = new { author = "owner", title = "Must not change", expectedStatus = "Done" },
        });
        Assert.True(conflict.IsError);
        Assert.Contains("409", string.Join("", conflict.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        var ticket = await CallAsync("get_ticket", new() { ["project"] = project, ["id"] = id });
        Assert.Equal("Concurrent update", ticket.GetProperty("title").GetString());
        foreach (var tool in new[] { "list_pipelines", "list_runs", "list_approval_requests", "list_worktree_merges" })
        {
            var error = await _mcp.CallToolAsync(tool, new Dictionary<string, object?> { ["project"] = "unknown-mcp-management" });
            Assert.True(error.IsError);
            Assert.Contains("list_projects", string.Join("", error.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        }
        Assert.Null(await _factory.Services.GetRequiredService<ProjectService>().GetProjectAsync("unknown-mcp-management"));
        await CallAsync("list_projects");
    }

    [Fact]
    public async Task Management_HierarchyDependenciesLabelsAndSchedule_Persist()
    {
        var project = await NewProjectAsync();
        var parent = await CallAsync("create_ticket", new() { ["project"] = project, ["title"] = "Parent" });
        var parentId = parent.GetProperty("id").GetInt32();
        var child = await CallAsync("create_ticket", new() { ["project"] = project, ["title"] = "Child", ["parentId"] = parentId });
        var id = child.GetProperty("id").GetInt32();
        Assert.Equal(parentId, child.GetProperty("parentId").GetInt32());
        await CallAsync("remove_ticket_parent", new() { ["project"] = project, ["id"] = id });
        await CallAsync("set_ticket_parent", new() { ["project"] = project, ["id"] = id, ["request"] = new { parentId } });
        await CallAsync("patch_ticket_labels", new() { ["project"] = project, ["id"] = id, ["request"] = new { author = "owner", add = new[] { "mcp-tested" } } });
        var blocker = await CallAsync("create_ticket", new() { ["project"] = project, ["title"] = "Blocker" });
        var dependency = await CallAsync("add_ticket_dependency", new() { ["project"] = project, ["id"] = id, ["request"] = new { blockedById = blocker.GetProperty("id").GetInt32() } });
        await CallAsync("remove_ticket_dependency", new() { ["project"] = project, ["id"] = id, ["depId"] = dependency.GetProperty("id").GetInt32() });
        await CallAsync("schedule_ticket", new() { ["project"] = project, ["id"] = id, ["request"] = new { author = "owner", fireAt = DateTime.UtcNow.AddDays(5), targetStatus = "Todo" } });
        var ticket = await CallAsync("get_ticket", new() { ["project"] = project, ["id"] = id });
        Assert.Equal("Scheduled", ticket.GetProperty("status").GetString());
        Assert.Contains(ticket.GetProperty("labels").EnumerateArray(), l => l.GetProperty("name").GetString() == "mcp-tested");
    }

    [Fact]
    public async Task Management_ReadOnlySupervision_WorksOnAnIsolatedProject()
    {
        var project = await NewProjectAsync();
        foreach (var name in new[] { "list_runs", "list_concurrency_groups", "list_column_executions", "list_approval_requests", "list_approval_decisions", "list_approval_receipts", "list_worktree_merges", "list_pipelines", "list_project_skills" })
        {
            var result = await CallAsync(name, new() { ["project"] = project });
            Assert.Equal(JsonValueKind.Array, result.ValueKind);
        }
        await CallAsync("get_runtime_capabilities");
        await CallAsync("get_engine_health");
        var missing = await _mcp!.CallToolAsync("get_run", new Dictionary<string, object?> { ["project"] = project, ["runId"] = "not-found" });
        Assert.True(missing.IsError);
    }

    [Fact]
    public async Task Management_LegacyAutomations_AreNotExposed()
    {
        var client = await ConnectAsync();
        var tools = await client.ListToolsAsync();
        Assert.DoesNotContain(tools, tool => tool.Name.Contains("automation", StringComparison.Ordinal));
        var health = await CallAsync("get_engine_health");
        Assert.Equal("column-processing", health.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task Management_ProcessorConfiguration_ValidatesAndNotifiesBoard()
    {
        var project = await NewProjectAsync();
        var columns = await CallAsync("list_columns", new() { ["project"] = project });
        var columnId = columns.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Todo").GetProperty("id").GetInt32();
        var notifications = new List<string>();
        var notifier = _factory.Services.GetRequiredService<KittyClaw.Web.Services.BoardUpdateNotifier>();
        notifier.OnProjectUpdated += notifications.Add;
        try
        {
            await CallAsync("save_column_processor", new()
            {
                ["project"] = project, ["columnId"] = columnId,
                ["request"] = new { name = "Test processor", mission = "Inspect evidence", enabled = false, maxAttempts = 2 },
            });
            var processor = await CallAsync("get_column_processor", new() { ["project"] = project, ["columnId"] = columnId });
            Assert.False(processor.GetProperty("enabled").GetBoolean());
            Assert.Equal(2, processor.GetProperty("maxAttempts").GetInt32());
            Assert.Contains(project, notifications);
            await CallAsync("delete_column_processor", new() { ["project"] = project, ["columnId"] = columnId });
            var missing = await _mcp!.CallToolAsync("get_column_processor", new Dictionary<string, object?> { ["project"] = project, ["columnId"] = columnId });
            Assert.True(missing.IsError);
        }
        finally { notifier.OnProjectUpdated -= notifications.Add; }
    }

    [Fact]
    public async Task Management_RunLogsAndSteering_AreScopedToTheProject()
    {
        var project = await NewProjectAsync();
        var otherProject = await NewProjectAsync();
        var registry = _factory.Services.GetRequiredService<AgentRunRegistry>();
        var run = registry.Register(new AgentRun
        {
            RunId = Guid.NewGuid().ToString("N"), ProjectSlug = project, TicketId = null,
            AgentName = "mcp-test", SkillFile = "test/SKILL.md", ConcurrencyGroup = "test",
            StartedAt = DateTime.UtcNow,
        });
        run.Push(new StreamEvent(DateTime.UtcNow, "text", "Isolated log evidence"));
        try
        {
            var read = await CallAsync("get_run", new() { ["project"] = project, ["runId"] = run.RunId });
            Assert.Contains("Isolated log evidence", read.GetProperty("events").GetRawText());
            foreach (var name in new[] { "get_run", "steer_run", "stop_run", "retry_run", "get_run_evidence" })
            {
                var args = new Dictionary<string, object?> { ["project"] = otherProject, ["runId"] = run.RunId };
                if (name == "steer_run") args["request"] = new { text = "Must not arrive" };
                var error = await _mcp!.CallToolAsync(name, args);
                Assert.True(error.IsError);
            }
            Assert.False(run.Cancellation.IsCancellationRequested);
            Assert.False(run.SteeringQueue.Reader.TryRead(out _));
            await CallAsync("steer_run", new() { ["project"] = project, ["runId"] = run.RunId, ["request"] = new { text = "Inspect the evidence" } });
            Assert.True(run.SteeringQueue.Reader.TryRead(out var text));
            Assert.Equal("Inspect the evidence", text);
            registry.Complete(run.RunId, AgentRunStatus.Completed, 0);
            var active = await CallAsync("list_runs", new() { ["project"] = project });
            Assert.Empty(active.EnumerateArray());
            var history = await CallAsync("list_runs", new() { ["project"] = project, ["includeCompleted"] = true });
            Assert.Contains(history.EnumerateArray(), r => r.GetProperty("runId").GetString() == run.RunId);
        }
        finally { registry.Complete(run.RunId, AgentRunStatus.Stopped, null); }
    }
}
