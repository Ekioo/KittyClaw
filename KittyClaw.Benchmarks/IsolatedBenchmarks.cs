using System.Net;
using System.Text.Json;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed record AsyncWorkload(string Name, Func<Task> Operation);

/// <summary>Owns the host, environment overrides, synthetic data and their cleanup.</summary>
internal sealed class IsolatedBenchmarks : IDisposable
{
    internal string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "kittyclaw-perf-" + Guid.NewGuid().ToString("N"));
    internal List<AsyncWorkload> Workloads { get; } = [];
    internal List<string> ProjectSlugs { get; } = [];
    private readonly Dictionary<string, string?> previousEnvironment = [];
    private BenchmarkFactory? factory;
    private HttpClient? client;

    internal async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(Path.Combine(DataDirectory, "settings.json"), """{"OnboardingSeen":true,"Language":"en"}""");
        SetEnvironment("KITTYCLAW_DATA_DIR", DataDirectory);
        SetEnvironment("ASPNETCORE_URLS", "http://127.0.0.1:0");
        SetEnvironment("KITTYCLAW_DESKTOP", "false");
        factory = new BenchmarkFactory();
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (factory.Services.GetServices<IHostedService>().Any(s =>
                s.GetType().Assembly == typeof(ProjectService).Assembly || s.GetType().Assembly == typeof(CreateProjectRequest).Assembly))
            throw new InvalidOperationException("Application background workers must be disabled in the benchmark host.");
        var projects = factory.Services.GetRequiredService<ProjectService>();
        if (Path.GetFullPath(projects.DataDir) != Path.GetFullPath(DataDirectory))
            throw new InvalidOperationException("Benchmark host did not select its isolated data directory.");
        var tickets = factory.Services.GetRequiredService<TicketService>();
        var columns = factory.Services.GetRequiredService<ColumnService>();

        // Read the host's live contract; never consult a production instance or a snapshot.
        using var specResponse = await client.GetAsync("/openapi/v1.json");
        specResponse.EnsureSuccessStatusCode();
        using var spec = JsonDocument.Parse(await specResponse.Content.ReadAsByteArrayAsync());
        foreach (var path in new[] { "/api/projects", "/api/projects/{slug}", "/api/projects/{slug}/tickets",
                     "/api/projects/{slug}/tickets/{id}", "/api/projects/{slug}/columns" })
            if (!spec.RootElement.GetProperty("paths").TryGetProperty(path, out _))
                throw new InvalidOperationException($"Benchmark route absent from live OpenAPI: {path}");

        foreach (var size in new[] { 100, 1000 })
        {
            var project = await projects.CreateProjectAsync($"Synthetic benchmark {size}");
            ProjectSlugs.Add(project.Slug);
            var column = (await columns.ListColumnsAsync(project.Slug)).First();
            var timestamp = DateTime.UnixEpoch;
            await using (var db = projects.GetProjectDb(project.Slug))
            {
                var label = new Label { Name = "synthetic", Color = "#6366f1" };
                for (var i = 0; i < size; i++)
                    db.Tickets.Add(new Ticket
                    {
                        Title = $"Synthetic ticket {i:D4}", Description = $"Synthetic description {i:D4}",
                        PipelineId = column.PipelineId, ColumnId = column.Id, Status = column.Name,
                        SortOrder = i, CreatedAt = timestamp, UpdatedAt = timestamp,
                        Labels = [label],
                        Comments = [new() { Content = "Synthetic comment", CreatedAt = timestamp }],
                        Activities = [new() { Text = "Synthetic activity", Author = "owner", CreatedAt = timestamp }],
                    });
                await db.SaveChangesAsync();
            }
            // Migrations, host startup and correctness checks are outside measured regions.
            var board = await tickets.ListTicketsAsync(project.Slug);
            if (board.Count != size) throw new InvalidDataException("Incorrect fixture size.");
            var id = board[0].Id;
            var detail = await tickets.GetTicketAsync(project.Slug, id);
            if (detail?.Comments.Count != 1 || detail.Labels.Count != 1)
                throw new InvalidDataException("Incomplete relational fixture.");
            Workloads.Add(new($"sqlite.board/{size}", async () =>
            {
                if ((await tickets.ListTicketsAsync(project.Slug)).Count != size)
                    throw new InvalidDataException("Board size changed during benchmark.");
            }));
            Workloads.Add(new($"sqlite.detail/{size}", async () =>
            {
                if (await tickets.GetTicketAsync(project.Slug, id) is null)
                    throw new InvalidDataException("Fixture ticket disappeared.");
            }));
            // Committed writes toggle a fixed-size field: no unbounded ticket/activity growth.
            Workloads.Add(new($"sqlite.update/{size}", async () =>
            {
                await using var db = projects.GetProjectDb(project.Slug);
                var row = await db.Tickets.SingleAsync(t => t.Id == id);
                row.Description = row.Description == "Synthetic write A" ? "Synthetic write B" : "Synthetic write A";
                if (await db.SaveChangesAsync() != 1) throw new InvalidDataException("Update was not persisted.");
            }));
            AddGet($"api.project/{size}", $"/api/projects/{project.Slug}");
            AddGet($"api.board/{size}", $"/api/projects/{project.Slug}/tickets");
            AddGet($"api.search/{size}", $"/api/projects/{project.Slug}/tickets?search=000&status={Uri.EscapeDataString(column.Name)}");
            AddGet($"api.detail/{size}", $"/api/projects/{project.Slug}/tickets/{id}");
            AddGet($"api.columns/{size}", $"/api/projects/{project.Slug}/columns");
        }
        Workloads.Add(new("sqlite.projects/2", async () =>
        {
            if ((await projects.ListProjectsAsync()).Count != 2) throw new InvalidDataException("Registry size changed.");
        }));
        AddGet("api.projects/2", "/api/projects");
        // Exercise every route and validate status + complete JSON before starting measurements.
        foreach (var workload in Workloads) await workload.Operation();
    }

    private void AddGet(string name, string path) => Workloads.Add(new(name, async () =>
    {
        using var response = await client!.GetAsync(path);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException($"{path}: expected 200, got {response.StatusCode}.");
        // Include response serialization, body consumption and client JSON parsing.
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        if (body.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            throw new InvalidDataException($"{path}: invalid JSON response shape.");
    }));

    private void SetEnvironment(string name, string value)
    {
        previousEnvironment.Add(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        try
        {
            client?.Dispose();
            factory?.Dispose();
            if (Directory.Exists(DataDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(DataDirectory, "*.db", SearchOption.AllDirectories))
                {
                    using var connection = new SqliteConnection($"Data Source={path}");
                    SqliteConnection.ClearPool(connection);
                }
                Directory.Delete(DataDirectory, recursive: true);
            }
        }
        finally
        {
            foreach (var (name, value) in previousEnvironment)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class BenchmarkFactory : WebApplicationFactory<CreateProjectRequest>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                // Disable ALL background workers: automation, provider dispatch, telemetry,
                // updates, git watchers and recovery. HTTP runs entirely in TestServer.
                services.RemoveAll<IHostedService>();
            });
        }
    }
}
