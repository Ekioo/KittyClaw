using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Todo.Core.Automation.Triggers;
using Todo.Core.Services;

namespace Todo.Core.Automation;

/// <summary>
/// Watches .git/HEAD and .git/refs (recursive) for each loaded project.
/// When git writes new refs (commit, merge, rebase…) a debounced GitCommitSignal
/// is pushed to AutomationEngine so GitCommitTrigger reacts in &lt;1 s instead of
/// waiting for the 60 s poll.
///
/// One FileSystemWatcher is created per project workspace. Watchers are created
/// lazily when a project workspace contains a .git directory and are disposed when
/// the service stops or a project is unloaded.
/// </summary>
public sealed class GitRepositoryWatcher : BackgroundService
{
    private const int DebounceMs = 500;

    private readonly ProjectService _projects;
    private readonly AutomationStore _store;
    private readonly AutomationEngine _engine;
    private readonly ILogger<GitRepositoryWatcher> _logger;

    // slug → active watcher entry
    private readonly ConcurrentDictionary<string, WatcherEntry> _watchers = new();

    public GitRepositoryWatcher(
        ProjectService projects,
        AutomationStore store,
        AutomationEngine engine,
        ILogger<GitRepositoryWatcher> logger)
    {
        _projects = projects;
        _store = store;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GitRepositoryWatcher started");

        // Refresh the watcher set every 30 s to pick up newly added projects.
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncWatchersAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "GitRepositoryWatcher sync failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        // Dispose all watchers on shutdown.
        foreach (var entry in _watchers.Values) entry.Dispose();
        _watchers.Clear();
    }

    private async Task SyncWatchersAsync(CancellationToken ct)
    {
        var projects = await _projects.ListProjectsAsync();
        var activeSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            if (ct.IsCancellationRequested) return;
            activeSlugs.Add(project.Slug);

            if (_watchers.ContainsKey(project.Slug)) continue;

            string workspace;
            try
            {
                var (_, ws, _) = await _store.LoadAsync(project.Slug);
                workspace = ws;
            }
            catch { continue; }

            var gitDir = Path.Combine(workspace, ".git");
            if (!Directory.Exists(gitDir)) continue;

            var entry = new WatcherEntry(workspace, project.Slug, _engine, _logger);
            if (_watchers.TryAdd(project.Slug, entry))
                _logger.LogInformation("GitRepositoryWatcher: watching {Slug} at {Path}", project.Slug, gitDir);
        }

        // Remove watchers for projects that no longer exist.
        foreach (var slug in _watchers.Keys.ToList())
        {
            if (!activeSlugs.Contains(slug) && _watchers.TryRemove(slug, out var old))
            {
                old.Dispose();
                _logger.LogInformation("GitRepositoryWatcher: stopped watching {Slug}", slug);
            }
        }
    }

    private sealed class WatcherEntry : IDisposable
    {
        private readonly string _slug;
        private readonly AutomationEngine _engine;
        private readonly ILogger _logger;
        private readonly FileSystemWatcher _headWatcher;
        private readonly FileSystemWatcher _refsWatcher;
        private Timer? _debounce;
        private readonly object _lock = new();

        public WatcherEntry(string workspace, string slug, AutomationEngine engine, ILogger logger)
        {
            _slug = slug;
            _engine = engine;
            _logger = logger;

            var gitDir = Path.Combine(workspace, ".git");

            // Watch HEAD (branch switch / new commit on current branch)
            _headWatcher = new FileSystemWatcher(gitDir, "HEAD")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _headWatcher.Changed += OnGitChanged;

            // Watch refs/heads recursively (branch tip updates)
            var refsDir = Path.Combine(gitDir, "refs");
            if (!Directory.Exists(refsDir)) Directory.CreateDirectory(refsDir);
            _refsWatcher = new FileSystemWatcher(refsDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _refsWatcher.Changed += OnGitChanged;
            _refsWatcher.Created += OnGitChanged;
        }

        private void OnGitChanged(object sender, FileSystemEventArgs e)
        {
            // Debounce: reset the timer on every event; fire 500 ms after the last one.
            lock (_lock)
            {
                _debounce?.Dispose();
                _debounce = new Timer(FireSignal, null, DebounceMs, Timeout.Infinite);
            }
        }

        private void FireSignal(object? _)
        {
            _logger.LogDebug("GitRepositoryWatcher: git change detected for {Slug}", _slug);
            _ = _engine.NotifySignalAsync(_slug, new GitCommitSignal(_slug));
        }

        public void Dispose()
        {
            _headWatcher.EnableRaisingEvents = false;
            _refsWatcher.EnableRaisingEvents = false;
            _headWatcher.Dispose();
            _refsWatcher.Dispose();
            lock (_lock) { _debounce?.Dispose(); _debounce = null; }
        }
    }
}
