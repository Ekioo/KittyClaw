using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using KittyClaw.Web.Components;

var builder = WebApplication.CreateBuilder(args);

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

string? _cachedApiDocs = null;

app.MapGet("/api/docs", async (HttpContext ctx) =>
{
    if (_cachedApiDocs is null)
    {
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        using var client = new HttpClient();
        var json = await client.GetStringAsync($"{baseUrl}/openapi/v1.json");
        using var doc = JsonDocument.Parse(json);
        _cachedApiDocs = OpenApiMarkdownGenerator.Generate(doc);
    }
    return Results.Text(_cachedApiDocs, "text/markdown; charset=utf-8");
}).ExcludeFromDescription();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
