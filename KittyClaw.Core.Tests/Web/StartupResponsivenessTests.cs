using System.Diagnostics;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Web;

public sealed class StartupResponsivenessTests
{
    [Fact]
    public async Task HealthAndRootRespondWhileDeferredStartupWorkIsStillRunning()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-startup-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var projects = new ProjectService(dataDir);
            var project = await projects.CreateProjectAsync("Slow dashboard migration");
            await projects.UpdateProjectAsync(project.Slug, workspacePath: null, worktreesEnabled: false);
            Directory.CreateDirectory(projects.ResolveWorkspacePath(project));

            var migration = new SlowDashboardService(projects);
            var startupGate = new ManualStartupWorkGate();
            await using var factory = new StartupFactory(dataDir, migration, startupGate);
            var stopwatch = Stopwatch.StartNew();
            using var client = factory.CreateClient();
            Assert.Same(migration, factory.Services.GetRequiredService<DashboardService>());
            Assert.NotEmpty(await factory.Services.GetRequiredService<ProjectService>().ListProjectsAsync());

            var health = await client.GetAsync("/api/engine/health");
            var root = await client.GetAsync("/");
            var responseElapsed = stopwatch.Elapsed;
            Assert.False(migration.Started.Task.IsCompleted,
                "Dashboard maintenance must remain behind the host-start gate.");
            startupGate.Release();
            await migration.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(health.IsSuccessStatusCode);
            Assert.True(root.IsSuccessStatusCode);
            Assert.False(migration.Completed.Task.IsCompleted,
                "HTTP responses must not wait for the real dashboard startup migration to finish.");
            Assert.True(responseElapsed < TimeSpan.FromSeconds(4),
                $"HTTP startup took {responseElapsed} while dashboard migration was deferred.");
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(dataDir);
        }
    }

    private sealed class StartupFactory(
        string dataDir,
        DashboardService dashboard,
        StartupWorkGate startupGate)
        : WebApplicationFactory<CreateProjectRequest>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("KITTYCLAW_DATA_DIR", dataDir);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DashboardService>();
                services.AddSingleton(dashboard);
                services.RemoveAll<StartupWorkGate>();
                services.AddSingleton(startupGate);
                services.RemoveAll<IHostedService>();
                services.AddHostedService(provider =>
                    provider.GetRequiredService<DashboardRefreshService>());
            });
        }
    }

    private sealed class ManualStartupWorkGate() : StartupWorkGate(new TestApplicationLifetime())
    {
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task WaitAsync(CancellationToken cancellationToken) =>
            _released.Task.WaitAsync(cancellationToken);

        public void Release() => _released.TrySetResult();
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class SlowDashboardService(ProjectService projects)
        : DashboardService(projects, NullLogger<DashboardService>.Instance)
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task MigrateAsync(string projectSlug, string workspace, Action<string>? log = null)
        {
            Started.TrySetResult();
            Thread.Sleep(TimeSpan.FromSeconds(5));
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(100);
            }
        }
    }
}
