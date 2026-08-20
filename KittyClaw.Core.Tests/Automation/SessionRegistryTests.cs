using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class SessionRegistryTests
{
    [Fact]
    public void Configured_registry_migrates_legacy_state_without_rewriting_project_checkout()
    {
        using var root = new KittyClaw.Core.Tests.Helpers.TempDir();
        var workspace = Path.Combine(root.Path, "project");
        var data = Path.Combine(root.Path, "data");
        var legacy = Path.Combine(workspace, ".agents", "channel", "dispatch-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        const string original = "{\"_sessions\":{\"worker:sweep\":\"legacy-session\"}}";
        File.WriteAllText(legacy, original);
        var registry = new SessionRegistry(data);

        Assert.Equal("legacy-session", registry.GetSessionId(workspace, "worker", null));
        registry.SetSessionId(workspace, "reviewer", 42, "new-session");

        Assert.Equal(original, File.ReadAllText(legacy));
        var runtime = registry.ChannelFilePath(workspace, "dispatch-state.json");
        Assert.StartsWith(Path.GetFullPath(data), runtime, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new-session", File.ReadAllText(runtime));
    }

    [Fact]
    public void Configured_cost_tracker_writes_outside_project_checkout()
    {
        using var root = new KittyClaw.Core.Tests.Helpers.TempDir();
        var workspace = Path.Combine(root.Path, "project");
        var data = Path.Combine(root.Path, "data");
        Directory.CreateDirectory(workspace);
        var sessions = new SessionRegistry(data);
        var tracker = new CostTracker(sessions);

        tracker.LogRun(workspace, new CostLogEntry(
            DateTime.UtcNow, "worker", 42, "model", 10, 5, 0, 0, 0.01m, 1, 0));

        Assert.False(File.Exists(Path.Combine(workspace, ".agents", "channel", "cost-log.jsonl")));
        Assert.True(File.Exists(tracker.CurrentLogPath(workspace)));
    }

    [Fact]
    public void WriteAllTextWithRetry_RetriesTransientIoFailures()
    {
        var writes = 0;
        var delays = new List<TimeSpan>();

        SessionRegistry.WriteAllTextWithRetry(
            "dispatch-state.json",
            "{}",
            (_, _) =>
            {
                writes++;
                if (writes < 3)
                    throw new IOException("sharing violation");
            },
            delays.Add);

        Assert.Equal(3, writes);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(50)],
            delays);
    }

    [Fact]
    public void WriteAllTextWithRetry_DoesNotRetryNonIoFailures()
    {
        var writes = 0;
        var delays = 0;

        Assert.Throws<UnauthorizedAccessException>(() =>
            SessionRegistry.WriteAllTextWithRetry(
                "dispatch-state.json",
                "{}",
                (_, _) =>
                {
                    writes++;
                    throw new UnauthorizedAccessException();
                },
                _ => delays++));

        Assert.Equal(1, writes);
        Assert.Equal(0, delays);
    }

    [Fact]
    public void WriteAllTextWithRetry_SurfacesPersistentIoFailureAfterBoundedRetries()
    {
        var writes = 0;
        var delays = 0;

        Assert.Throws<IOException>(() =>
            SessionRegistry.WriteAllTextWithRetry(
                "dispatch-state.json",
                "{}",
                (_, _) =>
                {
                    writes++;
                    throw new IOException("still locked");
                },
                _ => delays++));

        Assert.Equal(6, writes);
        Assert.Equal(5, delays);
    }
}
