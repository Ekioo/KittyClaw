using System.Text.Json;
using System.Text.Json.Serialization;
using Todo.Core.Services;
using Todo.Web.Api;
using Todo.Web.Components;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "TodoApp"
);
builder.Services.AddSingleton(new ProjectService(dataDir));
builder.Services.AddSingleton<TicketService>();
builder.Services.AddSingleton<LabelService>();
builder.Services.AddSingleton<ColumnService>();
builder.Services.AddSingleton<MemberService>();
builder.Services.AddScoped<Todo.Web.Services.BoardFilterState>();
builder.Services.AddSingleton<Todo.Web.Services.BoardUpdateNotifier>();

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
