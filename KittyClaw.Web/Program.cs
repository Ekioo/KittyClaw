using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using KittyClaw.Web.Components;
using Velopack;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HostFiltering;
using KittyClaw.Web.Services;

// Installer hooks must exit before the web host or any data services are created.
// Applying updates is explicit, so a second launch cannot update a running server.
VelopackApp.Build()
#if WINDOWS
    .OnFirstRun(_ => KittyClaw.Web.Services.WindowsDesktopLifetime.ConfirmInstallation())
#endif
    .SetAutoApplyOnStartup(false).Run();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
#if WINDOWS
    ContentRootPath = AppContext.BaseDirectory,
#endif
});

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
#if WINDOWS
using var desktop = builder.Configuration.GetValue("KITTYCLAW_DESKTOP", true)
    ? new KittyClaw.Web.Services.WindowsDesktopLifetime(dataDir) : null;
if (desktop is { IsFirstInstance: false }) return;
#endif
var legacyDataDir = Path.Combine(appData, "TodoApp");
if (!Directory.Exists(dataDir) && Directory.Exists(legacyDataDir))
{
    Directory.Move(legacyDataDir, dataDir);
}
var appSettings = new KittyClaw.Core.Services.AppSettingsService(dataDir);
var webProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "web-keys")));
if (OperatingSystem.IsWindows()) webProtection.ProtectKeysWithDpapi();
var networkProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dataDir, "network-keys")), options =>
{
    if (OperatingSystem.IsWindows()) options.ProtectKeysWithDpapi();
});
var networkAccess = new NetworkAccessService(dataDir, networkProtection,
    builder.Configuration.GetValue("NetworkAccess:HttpsPort", 5233));
builder.Services.AddSingleton(networkAccess);
if (networkAccess.Enabled)
{
    var certificate = networkAccess.Certificate();
    builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
    var urls = builder.Configuration["urls"] ?? "http://localhost:5230";
    builder.WebHost.UseUrls($"{urls};https://*:{networkAccess.Port}");
    // Keep DNS-rebinding protection: allow concrete machine names and interface addresses only.
    builder.Services.PostConfigure<HostFilteringOptions>(options =>
    {
        options.AllowedHosts = new[] { "localhost", "127.0.0.1", "[::1]", Environment.MachineName }
            .Concat(networkAccess.Addresses.Select(address => address.ToString())).ToArray();
    });
    networkAccess.Listening = true;
}
// A tunnel origin has its own loopback port and never inherits local trust.
var remoteAccessStore = new RemoteAccessStore(dataDir, networkProtection);
builder.Services.AddSingleton(remoteAccessStore);
var remoteTunnel = remoteAccessStore.OriginOptions(RemoteTunnelOptions.Read(builder.Configuration));
builder.Services.AddSingleton(remoteTunnel);
if (remoteTunnel.Enabled)
{
    var urls = builder.Configuration["urls"] ?? "http://localhost:5230";
    if (remoteTunnel.Port == networkAccess.Port || urls.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Any(url => Uri.TryCreate(url.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri)
            && uri.Port == remoteTunnel.Port))
        throw new InvalidOperationException("RemoteTunnel:Port must be distinct from every existing listener.");
    builder.WebHost.UseUrls($"{urls};http://localhost:{remoteTunnel.Port}");
    builder.Services.PostConfigure<HostFilteringOptions>(options =>
        options.AllowedHosts = (options.AllowedHosts ?? Array.Empty<string>())
            .Append(remoteTunnel.Hostname).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
}
builder.Services.AddSingleton(sp => new RemoteAccessBroker(
    new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    { MaxResponseContentBufferSize = 64 * 1024, Timeout = TimeSpan.FromSeconds(95) },
    new Uri(builder.Configuration["RemoteAccess:BrokerUrl"] ?? "https://remote-api.kittyclaw.dev")));
builder.Services.AddSingleton<ICloudflaredRuntime, CloudflaredRuntime>();
builder.Services.AddSingleton<RemoteAccessConnector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteAccessConnector>());
builder.Services.AddSingleton(appSettings);
builder.Services.AddSingleton(new KittyClaw.Core.Services.LocalizationService(appSettings));
var projectSecretVault = new ProjectSecretVault(dataDir);
builder.Services.AddSingleton(projectSecretVault);
builder.Services.AddSingleton(new ProjectService(dataDir, projectSecretVault));
builder.Services.AddSingleton<GitRepositoryInitializationService>();
builder.Services.AddSingleton(new RepositoryIntakeService(dataDir));
builder.Services.AddSingleton(sp => new FirstRunProviderService(dataDir,
    sp.GetRequiredService<AgentCliReadinessService>()));
builder.Services.AddSingleton(sp => new DashboardGuidanceService(dataDir));
builder.Services.AddSingleton(new FirstProjectActivationMetricsService(dataDir));
builder.Services.AddSingleton(sp => new TicketService(sp.GetRequiredService<ProjectService>(),
    sp.GetRequiredService<MemberService>(), sp.GetRequiredService<ColumnProcessorService>(),
    sp.GetRequiredService<ILogger<TicketService>>(),
    new Lazy<JevWorkReviewService>(sp.GetRequiredService<JevWorkReviewService>)));
builder.Services.AddSingleton(sp => new FirstTicketDraftService(dataDir, sp.GetRequiredService<TicketService>()));
builder.Services.AddSingleton(sp => new MinimalWorkflowService(dataDir,
    sp.GetRequiredService<PipelineService>(), sp.GetRequiredService<ColumnService>(),
    sp.GetRequiredService<ColumnProcessorService>(), sp.GetRequiredService<TicketService>()));
builder.Services.AddSingleton<TicketTransferService>();
builder.Services.AddSingleton<LabelService>();
builder.Services.AddSingleton<ColumnService>();
builder.Services.AddSingleton<PipelineService>();
builder.Services.AddSingleton<ProjectSkillService>();
builder.Services.AddSingleton(sp => new ColumnProcessorService(
    sp.GetRequiredService<ProjectService>(),
    sp.GetRequiredService<ProjectSkillService>(),
    sp.GetService<ILogger<ColumnProcessorService>>(),
    new Lazy<DurableWriteRouter>(sp.GetRequiredService<DurableWriteRouter>)));
builder.Services.AddSingleton<PipelineExportService>();
builder.Services.AddSingleton<PipelineImportService>();
builder.Services.AddSingleton<ColumnScheduledTaskService>();
builder.Services.AddSingleton<ColumnExecutionService>();
builder.Services.AddSingleton<ColumnMemoryCapitalizationService>();
builder.Services.AddSingleton<MemberService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<ApprovalRegistryService>();
builder.Services.AddSingleton<ApprovalWorkflowService>();
builder.Services.AddSingleton<RuntimeBoundaryEnforcementService>();
builder.Services.AddSingleton<RuntimeBoundaryGateService>();
builder.Services.AddSingleton<BoundaryObservationService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<AgentsTemplateService>();
builder.Services.AddSingleton<AgentCliReadinessService>();
builder.Services.AddScoped<KittyClaw.Web.Services.BoardFilterState>();
builder.Services.AddScoped<KittyClaw.Web.Services.BoardSortState>();
builder.Services.AddSingleton<KittyClaw.Web.Services.BoardUpdateNotifier>();
builder.Services.AddSingleton<KittyClaw.Web.Services.BoardFocusService>();
builder.Services.AddSingleton<KittyClaw.Web.Services.WorkflowMigrationPlanner>();
builder.Services.AddSingleton<KittyClaw.Web.Services.TicketImprovementService>();
builder.Services.AddScoped<KittyClaw.Web.Services.EscapeKeyStack>();

// Agent execution and column workflow services
builder.Services.AddSingleton(new SessionRegistry(dataDir));
builder.Services.AddSingleton(new RunLogStore(dataDir));
builder.Services.AddSingleton<AgentRunRegistry>(sp =>
    new AgentRunRegistry(sp.GetRequiredService<RunLogStore>(), loadPersistedRuns: false));
builder.Services.AddSingleton<RunHistoryLoadingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunHistoryLoadingService>());
builder.Services.AddHostedService<TicketCommentSteeringService>();
builder.Services.AddHostedService<KittyClaw.Web.Services.InterruptedChatRecoveryService>();
// Cap concurrent claude subprocesses across all projects (chats bypass). Override with the
// KITTYCLAW_MAX_CONCURRENT_AGENTS env var if 3 is too tight or too loose for the host.
var maxConcurrent = int.TryParse(Environment.GetEnvironmentVariable("KITTYCLAW_MAX_CONCURRENT_AGENTS"), out var mc) && mc > 0 ? mc : 3;
builder.Services.AddSingleton(new RunConcurrencyGate(maxConcurrent));
builder.Services.AddSingleton<TicketWorktreeService>();
builder.Services.AddSingleton<DurableWriteRouter>();
// Shared by WorktreeMergeQueueService (writer) and AgentRunner (reader): lets the end-of-run
// boundary check classify local-checkout synchronization as a coordinated change, not agent work.
builder.Services.AddSingleton<PrimaryCheckoutActivityRegistry>();
builder.Services.AddSingleton<WorktreeMergeQueueService>();
builder.Services.AddSingleton<WorktreeFinalizationCoordinator>();
builder.Services.AddHostedService<WorktreeMergeQueueProcessor>();
builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<RunWorkingFilesService>();
builder.Services.AddSingleton<TemporaryStorageService>();
builder.Services.AddSingleton<RtkIntegrationService>();
builder.Services.AddSingleton<AgentMemoryHandler>(sp => new AgentMemoryHandler(
    sp.GetRequiredService<MemberService>(),
    sp.GetRequiredService<ProjectService>(), sp.GetRequiredService<AgentRunner>(),
    sp.GetRequiredService<ILogger<AgentMemoryHandler>>(),
    sp.GetRequiredService<DurableWriteRouter>()));
builder.Services.AddHostedService<ChatMemoryConsolidationService>();
builder.Services.AddSingleton<IColumnAgentDispatcher, ColumnAgentDispatcher>();
builder.Services.AddSingleton<ColumnActionExecutor>();
builder.Services.AddSingleton<CostTracker>();
builder.Services.AddSingleton<AgentBudgetService>();
builder.Services.AddSingleton<CostReportService>();
builder.Services.AddSingleton<IRtkSavingsReader, RtkSavingsReader>();
builder.Services.AddHostedService<CostReportRefreshService>();
// Durable cost records: cost-log.jsonl (daily budget) + per-ticket token/USD totals.
builder.Services.AddHostedService<RunCostRecorder>();
builder.Services.AddHostedService<AgentRunLifecycle>();
// Evidence: capture and attach verifiable evidence bundles to run and ticket records.
builder.Services.AddSingleton(new KittyClaw.Core.Evidence.EvidenceStore(dataDir));
builder.Services.AddHostedService<KittyClaw.Core.Evidence.RunEvidenceAttacher>();
builder.Services.AddSingleton(sp => new JevConfigurationService(sp.GetRequiredService<ProjectSecretVault>(), dataDir));
builder.Services.AddSingleton<JevCostRecorder>();
builder.Services.AddSingleton(sp => new JevQualificationService(
    sp.GetRequiredService<ProjectService>(), sp.GetRequiredService<ProjectSecretVault>(),
    new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { MaxResponseContentBufferSize = 256 * 1024 },
    sp.GetRequiredService<JevConfigurationService>(), sp.GetRequiredService<JevCostRecorder>()));
builder.Services.AddSingleton(sp => new JevWorkReviewService(sp.GetRequiredService<ProjectService>(),
    sp.GetRequiredService<JevConfigurationService>(), new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { MaxResponseContentBufferSize = 256 * 1024 }, sp.GetRequiredService<JevCostRecorder>()));
builder.Services.AddSingleton<JevWorkDecisionService>();
builder.Services.AddHostedService<JevWorkReviewEngine>();
builder.Services.AddSingleton<JevDecisionStore>();
builder.Services.AddSingleton<JevProposalDecisionService>();
builder.Services.AddHostedService<JevObservationEngine>();
builder.Services.AddSingleton<ColumnProcessingEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ColumnProcessingEngine>());
builder.Services.AddSingleton<ColumnScheduledTaskEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ColumnScheduledTaskEngine>());
// Dead man's switch: force-release concurrency locks held by hung runs (ticket #98, feature #3).
builder.Services.AddHostedService<KittyClaw.Core.Services.ConcurrencyLockReaper>();
builder.Services.AddSingleton<KittyClaw.Core.Services.DashboardTileGate>();
builder.Services.AddSingleton<KittyClaw.Core.Services.DashboardScriptRunner>();
builder.Services.AddSingleton<KittyClaw.Core.Services.StartupWorkGate>();
builder.Services.AddSingleton<KittyClaw.Core.Services.DashboardRefreshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KittyClaw.Core.Services.DashboardRefreshService>());
// Auto-promote Scheduled tickets to their target column once FireAt fires (feature #99).
builder.Services.AddHostedService<KittyClaw.Core.Services.ScheduledPromotionService>();
builder.Services.AddSingleton<ProjectUpdateReviewService>();
builder.Services.AddHostedService<ProjectUpdateReviewEngine>();
builder.Services.AddSingleton<KittyClaw.Web.Services.AgentRunsState>();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("KittyClawLocal", (sp, http) =>
{
    var server = sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
    var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
    http.BaseAddress = LocalApiAddress.Resolve(addresses is { Count: > 0 } ? addresses
        : (builder.Configuration["urls"] ?? "http://localhost:5230").Split(';'), remoteTunnel.Port);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddSingleton<KittyClaw.Web.Services.IInstalledUpdateClient, KittyClaw.Web.Services.VelopackUpdateClient>();
builder.Services.AddSingleton<KittyClaw.Web.Services.InstalledUpdateService>();
builder.Services.AddSingleton<KittyClaw.Web.Services.UpdateCheckService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KittyClaw.Web.Services.UpdateCheckService>());
// Anonymous daily usage heartbeat (see README "Telemetry" and docs/telemetry.md).
// Never in Development: excludes dotnet watch sessions and QaRunner test instances.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<KittyClaw.Web.Services.TelemetryService>();
}

// Folder picker: only on Windows hosts (local or MAUI-Windows). Cloud deployments
// register nothing, so the UI hides the Parcourir button.
if (OperatingSystem.IsWindows())
    builder.Services.AddSingleton<KittyClaw.Core.Platform.IFolderPicker, KittyClaw.Core.Platform.WindowsFolderPicker>();

// Embedded MCP server (Streamable HTTP) at /mcp, sharing application handlers with REST.
// The transport is registered once, while access is gated dynamically below by the global
// persisted setting. This lets the UI enable or disable MCP immediately without a restart.
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
    .WithTools<KittyClaw.Web.Api.McpTools>(KittyClaw.Web.Api.McpTools.SerializerOptions)
    .WithTools<KittyClaw.Web.Api.McpManagementTools>(KittyClaw.Web.Api.McpTools.SerializerOptions);

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
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        // Pasted chat images travel JS -> .NET as base64 data URLs (client cap: 5 MB per
        // image, ~6.7 MB once base64-encoded). The SignalR default (32 KB) closes the
        // circuit on any real screenshot, leaving image preparation stuck with no preview.
        options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
    });

var app = builder.Build();

// Apply browser protections even to access-denied and setup responses.
app.UseMiddleware<BrowserSecurityHeadersMiddleware>();

// Gate all remote traffic, including uploads, APIs, MCP and Blazor connections.
app.UseMiddleware<RemoteTunnelMiddleware>();
app.UseMiddleware<RemoteAccessSetupMiddleware>();
app.UseMiddleware<NetworkAccessMiddleware>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Keep the MCP endpoint absent from the caller's perspective while disabled. The setting is
// read for every request so changing it from the global UI takes effect without restarting.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp") && !appSettings.McpEnabled)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

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

// MCP speaks JSON-RPC, not REST — keep it out of the OpenAPI/api-docs surface.
app.MapMcp("/mcp").ExcludeFromDescription();

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

app.MapGet("/api/docs", async (IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    // Never derive a server-side destination from the caller's Host or scheme.
    using var client = clients.CreateClient("KittyClawLocal");
    var json = await client.GetStringAsync("/openapi/v1.json", cancellationToken);
    using var doc = JsonDocument.Parse(json);
    var markdown = OpenApiMarkdownGenerator.Generate(doc);
    return Results.Text(markdown, "text/markdown; charset=utf-8");
}).ExcludeFromDescription();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

#if WINDOWS
desktop?.Attach(app);
try { app.Run(); }
catch (Exception ex) when (desktop is not null)
{
    desktop.ReportStartupFailure(ex);
}
#else
app.Run();
#endif
