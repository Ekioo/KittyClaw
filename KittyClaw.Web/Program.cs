using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using KittyClaw.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Default to HTTP-only on :5230 when no URL config is provided. KittyClaw is a local-only
// app with no HTTPS cert, so the framework default (HTTP + HTTPS dual binding on :5000/:5001)
// is wrong here. 5230 is the historical KittyClaw port — kept for backward compatibility
// with existing skills, bookmarks, and external integrations that point at it.
//
// Only kick in when nothing else (ASPNETCORE_URLS, launchSettings.applicationUrl, --urls,
// urls config key) has set the URL — otherwise UseUrls() called after CreateBuilder would
// overwrite that config and break the qa launch profile, QaRunner test instances, etc.
//
// Also propagate to ASPNETCORE_URLS so downstream consumers that read the env var directly
// (e.g. AgentRunner.ResolveApiUrl, which builds the API URL passed to skills) see the same
// port Kestrel is actually binding.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    const string fallbackUrl = "http://localhost:5230";
    builder.WebHost.UseUrls(fallbackUrl);
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", fallbackUrl);
}

// KITTYCLAW_DATA_DIR overrides the default %APPDATA%/KittyClaw location.
// Used by isolated test instances (KittyClaw.QaRunner) and anyone running
// multiple parallel KittyClaw processes that must not share registry/projects.
var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
var dataDir = builder.Configuration["KITTYCLAW_DATA_DIR"]
    ?? Path.Combine(appData, "KittyClaw");
var legacyDataDir = Path.Combine(appData, "TodoApp");
if (!Directory.Exists(dataDir) && Directory.Exists(legacyDataDir))
{
    Directory.Move(legacyDataDir, dataDir);
}
var appSettings = new KittyClaw.Core.Services.AppSettingsService(dataDir);
builder.Services.AddSingleton(appSettings);
builder.Services.AddSingleton(new KittyClaw.Core.Services.LocalizationService(appSettings));
builder.Services.AddSingleton(new ProjectService(dataDir));
builder.Services.AddSingleton<TicketService>();
builder.Services.AddSingleton<TicketTransferService>();
builder.Services.AddSingleton<LabelService>();
builder.Services.AddSingleton<ColumnService>();
builder.Services.AddSingleton<PipelineService>();
builder.Services.AddSingleton<ProjectSkillService>();
builder.Services.AddSingleton<ColumnProcessorService>();
builder.Services.AddSingleton<ColumnScheduledTaskService>();
builder.Services.AddSingleton<ColumnExecutionService>();
builder.Services.AddSingleton<MemberService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<ApprovalRegistryService>();
builder.Services.AddSingleton<ApprovalWorkflowService>();
builder.Services.AddSingleton<BoundaryObservationService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<AgentsTemplateService>();
builder.Services.AddSingleton<AgentCliReadinessService>();
builder.Services.AddScoped<KittyClaw.Web.Services.BoardFilterState>();
builder.Services.AddScoped<KittyClaw.Web.Services.BoardSortState>();
builder.Services.AddSingleton<KittyClaw.Web.Services.BoardUpdateNotifier>();
builder.Services.AddSingleton<KittyClaw.Web.Services.WorkflowMigrationPlanner>();
builder.Services.AddScoped<KittyClaw.Web.Services.EscapeKeyStack>();

// Automation engine
builder.Services.AddSingleton<AutomationStore>();
builder.Services.AddSingleton<AutomationQueueStore>();
builder.Services.AddSingleton<TriggerStateStore>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton(new RunLogStore(dataDir));
builder.Services.AddSingleton<AgentRunRegistry>(sp => new AgentRunRegistry(sp.GetRequiredService<RunLogStore>()));
// Cap concurrent claude subprocesses across all projects (chats bypass). Override with the
// KITTYCLAW_MAX_CONCURRENT_AGENTS env var if 3 is too tight or too loose for the host.
var maxConcurrent = int.TryParse(Environment.GetEnvironmentVariable("KITTYCLAW_MAX_CONCURRENT_AGENTS"), out var mc) && mc > 0 ? mc : 3;
builder.Services.AddSingleton(new RunConcurrencyGate(maxConcurrent));
builder.Services.AddSingleton<TicketWorktreeService>();
builder.Services.AddSingleton<WorktreeMergeQueueService>();
builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<RtkIntegrationService>();
builder.Services.AddSingleton<AgentMemoryHandler>(sp => new AgentMemoryHandler(
    sp.GetRequiredService<TicketService>(), sp.GetRequiredService<MemberService>(),
    sp.GetRequiredService<ProjectService>(), sp.GetRequiredService<AgentRunner>(),
    sp.GetRequiredService<SessionRegistry>(), sp.GetRequiredService<ILogger<AgentMemoryHandler>>()));
builder.Services.AddHostedService<ChatMemoryConsolidationService>();
builder.Services.AddSingleton<IColumnAgentDispatcher, ColumnAgentDispatcher>();
builder.Services.AddSingleton<ColumnActionExecutor>();
builder.Services.AddSingleton<CostTracker>();
builder.Services.AddSingleton<CostReportService>();
builder.Services.AddHostedService<CostReportRefreshService>();
// Durable cost records: cost-log.jsonl (daily budget) + per-ticket token/USD totals.
builder.Services.AddHostedService<RunCostRecorder>();
// Evidence: capture and attach verifiable evidence bundles to run and ticket records.
builder.Services.AddSingleton(new KittyClaw.Core.Evidence.EvidenceStore(dataDir));
builder.Services.AddHostedService<KittyClaw.Core.Evidence.RunEvidenceAttacher>();
builder.Services.AddSingleton<AutomationEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomationEngine>());
builder.Services.AddSingleton<ColumnProcessingEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ColumnProcessingEngine>());
builder.Services.AddSingleton<ColumnScheduledTaskEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ColumnScheduledTaskEngine>());
builder.Services.AddSingleton<GitRepositoryWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GitRepositoryWatcher>());
// Dead man's switch: force-release concurrency locks held by hung runs (ticket #98, feature #3).
builder.Services.AddHostedService<KittyClaw.Core.Services.ConcurrencyLockReaper>();
builder.Services.AddSingleton<KittyClaw.Core.Services.DashboardTileGate>();
builder.Services.AddSingleton<KittyClaw.Core.Services.DashboardScriptRunner>();
builder.Services.AddSingleton<KittyClaw.Core.Services.DashboardRefreshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KittyClaw.Core.Services.DashboardRefreshService>());
// Auto-promote Scheduled tickets to their target column once FireAt fires (feature #99).
builder.Services.AddHostedService<KittyClaw.Core.Services.ScheduledPromotionService>();
builder.Services.AddSingleton<KittyClaw.Web.Services.AgentRunsState>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<KittyClaw.Web.Services.UpdateCheckService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KittyClaw.Web.Services.UpdateCheckService>());
// Anonymous daily usage heartbeat (see README "Telemetry" and doc/telemetry.md).
// Never in Development: excludes dotnet watch sessions and QaRunner test instances.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<KittyClaw.Web.Services.TelemetryService>();
}

// Folder picker: only on Windows hosts (local or MAUI-Windows). Cloud deployments
// register nothing, so the UI hides the Parcourir button.
if (OperatingSystem.IsWindows())
    builder.Services.AddSingleton<KittyClaw.Core.Platform.IFolderPicker, KittyClaw.Core.Platform.WindowsFolderPicker>();

// Embedded MCP server (Streamable HTTP) at /mcp — 7 tools proxying the board services, so
// any MCP client can drive the board: `claude mcp add --transport http kittyclaw
// http://localhost:5230/mcp`. Same trust boundary as the REST API (unauthenticated,
// localhost, self-hosted single machine). It is opt-in: set KITTYCLAW_MCP_ENABLED=1
// to register the tools and route. See doc/mcp.md.
var mcpEnabledFlag = builder.Configuration["KITTYCLAW_MCP_ENABLED"];
var mcpEnabled = mcpEnabledFlag == "1"
    || string.Equals(mcpEnabledFlag, "true", StringComparison.OrdinalIgnoreCase);
if (mcpEnabled)
{
    builder.Services.AddMcpServer(options => options.ServerInfo = new()
    {
        Name = "kittyclaw",
        Title = "KittyClaw",
        Version = KittyClaw.Web.Services.VersionFormatter.Format(
            typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion),
        WebsiteUrl = "https://kittyclaw.dev",
    })
        .WithHttpTransport()
        .WithTools<KittyClaw.Web.Api.McpTools>(KittyClaw.Web.Api.McpTools.SerializerOptions);
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // Reject request bodies carrying fields the endpoint does not support (400) instead of
    // silently dropping them. Most API callers are LLM agents that GUESS field names from
    // REST conventions; a wrong guess must be told, not swallowed — a silently-ignored
    // "status" once kept a prod ticket looping for 30 minutes (kittyclaw-front#113).
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddOpenApi();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Serve uploaded images
var uploadsDir = Path.Combine(dataDir, "uploads");
Directory.CreateDirectory(uploadsDir);
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsDir),
    RequestPath = "/uploads",
    // Uploads are user/agent-supplied: forbid content-type sniffing and any active content
    // (e.g. a pre-existing SVG) from executing in the app's origin.
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
    }
});

app.UseAntiforgery();

// Surface strict-JSON binding failures (unknown field, malformed body) as a 400 with the
// offending property named in the payload — the default is a bare 400, useless to an
// agent that needs to know WHICH field to fix.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
        when (ex.InnerException is JsonException jex && !context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = jex.Message });
    }
});

app.MapOpenApi();
app.MapTodoApi();

if (mcpEnabled)
{
    // MCP speaks JSON-RPC, not REST — keep it out of the OpenAPI/api-docs surface.
    app.MapMcp("/mcp").ExcludeFromDescription();
}

if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/dev/update-check/simulate", (string version, KittyClaw.Web.Services.UpdateCheckService svc) =>
    {
        svc.SimulateUpdate(version);
        return Results.Ok(new { simulated = version });
    }).ExcludeFromDescription();
    app.MapPost("/api/dev/update-check/reset", (KittyClaw.Web.Services.UpdateCheckService svc) =>
    {
        svc.ResetSimulation();
        return Results.Ok(new { reset = true });
    }).ExcludeFromDescription();
}

app.MapGet("/api/docs", async (HttpContext ctx) =>
{
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    using var client = new HttpClient();
    var json = await client.GetStringAsync($"{baseUrl}/openapi/v1.json");
    using var doc = JsonDocument.Parse(json);
    var markdown = OpenApiMarkdownGenerator.Generate(doc);
    return Results.Text(markdown, "text/markdown; charset=utf-8");
}).ExcludeFromDescription();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
