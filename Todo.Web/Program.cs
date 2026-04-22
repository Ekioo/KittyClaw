using System.Text.Json;
using System.Text.Json.Serialization;
using Todo.Core.Automation;
using Todo.Core.Services;
using Todo.Web.Api;
using Todo.Web.Components;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TodoApp"
);
var appSettings = new Todo.Core.Services.AppSettingsService(dataDir);
builder.Services.AddSingleton(appSettings);
builder.Services.AddSingleton(new Todo.Core.Services.LocalizationService(appSettings));
builder.Services.AddSingleton(new ProjectService(dataDir));
builder.Services.AddSingleton<TicketService>();
builder.Services.AddSingleton<LabelService>();
builder.Services.AddSingleton<ColumnService>();
builder.Services.AddSingleton<MemberService>();
builder.Services.AddSingleton<AgentsTemplateService>();
builder.Services.AddSingleton<Todo.Web.Services.BoardFilterState>();
builder.Services.AddSingleton<Todo.Web.Services.BoardUpdateNotifier>();

// Automation engine
builder.Services.AddSingleton<AutomationStore>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton(new RunLogStore(dataDir));
builder.Services.AddSingleton<AgentRunRegistry>(sp => new AgentRunRegistry(sp.GetRequiredService<RunLogStore>()));
builder.Services.AddSingleton<ClaudeRunner>();
builder.Services.AddSingleton<CostTracker>();
builder.Services.AddSingleton<AutomationEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomationEngine>());
builder.Services.AddSingleton<GitRepositoryWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GitRepositoryWatcher>());
builder.Services.AddSingleton<Todo.Web.Services.AgentRunsState>();
builder.Services.AddHttpClient();

// Folder picker: only on Windows hosts (local or MAUI-Windows). Cloud deployments
// register nothing, so the UI hides the Parcourir button.
if (OperatingSystem.IsWindows())
    builder.Services.AddSingleton<Todo.Core.Platform.IFolderPicker, Todo.Core.Platform.WindowsFolderPicker>();

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
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

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
