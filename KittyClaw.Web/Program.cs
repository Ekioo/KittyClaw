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
// (e.g. ClaudeRunner.ResolveApiUrl, which builds the API URL passed to skills) see the same
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
var dataDir = Environment.GetEnvironmentVariable("KITTYCLAW_DATA_DIR")
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
builder.Services.AddSingleton<LabelService>();
builder.Services.AddSingleton<ColumnService>();
builder.Services.AddSingleton<MemberService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<AgentsTemplateService>();
builder.Services.AddSingleton<KittyClaw.Web.Services.BoardFilterState>();
builder.Services.AddSingleton<KittyClaw.Web.Services.BoardUpdateNotifier>();

// Automation engine
builder.Services.AddSingleton<AutomationStore>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton(new RunLogStore(dataDir));
builder.Services.AddSingleton<AgentRunRegistry>(sp => new AgentRunRegistry(sp.GetRequiredService<RunLogStore>()));
// Cap concurrent claude subprocesses across all projects (chats bypass). Override with the
// KITTYCLAW_MAX_CONCURRENT_AGENTS env var if 3 is too tight or too loose for the host.
var maxConcurrent = int.TryParse(Environment.GetEnvironmentVariable("KITTYCLAW_MAX_CONCURRENT_AGENTS"), out var mc) && mc > 0 ? mc : 3;
builder.Services.AddSingleton(new RunConcurrencyGate(maxConcurrent));
builder.Services.AddSingleton<ClaudeRunner>();
builder.Services.AddSingleton<CostTracker>();
builder.Services.AddSingleton<AutomationEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomationEngine>());
builder.Services.AddSingleton<GitRepositoryWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GitRepositoryWatcher>());
builder.Services.AddSingleton<KittyClaw.Core.Services.DashboardTileGate>();
builder.Services.AddSingleton<KittyClaw.Core.Services.DashboardScriptRunner>();
builder.Services.AddSingleton<KittyClaw.Core.Services.DashboardRefreshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KittyClaw.Core.Services.DashboardRefreshService>());
builder.Services.AddSingleton<KittyClaw.Web.Services.AgentRunsState>();
builder.Services.AddHttpClient();

// Folder picker: only on Windows hosts (local or MAUI-Windows). Cloud deployments
// register nothing, so the UI hides the Parcourir button.
if (OperatingSystem.IsWindows())
    builder.Services.AddSingleton<KittyClaw.Core.Platform.IFolderPicker, KittyClaw.Core.Platform.WindowsFolderPicker>();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
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
    RequestPath = "/uploads"
});

app.UseAntiforgery();

app.MapOpenApi();
app.MapTodoApi();

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
