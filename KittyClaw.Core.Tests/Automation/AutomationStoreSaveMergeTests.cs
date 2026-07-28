using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Non-regression tests for ticket #115: SaveAsync must never silently erase an automation that
/// was added to automations.json on disk (by an agent, another session, …) between the caller's
/// load and its save, and the serializer must round-trip unknown fields (e.g. custom pins).
/// </summary>
public class AutomationStoreSaveMergeTests
{
    private static async Task<(AutomationStore Store, string Slug, string ConfigPath)> BuildAsync(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("store-merge-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));
        var store = new AutomationStore(projects);
        return (store, project.Slug, Path.Combine(workspace, ".agents", "automations.json"));
    }

    private static KittyClaw.Core.Automation.Automation Auto(string id) => new()
    {
        Id = id,
        Name = id,
        Trigger = new IntervalTriggerSpec { Cron = "0 7 * * *" },
    };

    private static void WriteDisk(string configPath, params KittyClaw.Core.Automation.Automation[] autos)
    {
        var cfg = new AutomationConfig { Automations = autos.ToList() };
        File.WriteAllText(configPath, JsonSerializer.Serialize(cfg, AutomationStore.JsonOptions));
    }

    [Fact]
    public async Task Stale_save_preserves_automation_added_on_disk_concurrently()
    {
        using var tmp = new TempDir();
        var (store, slug, configPath) = await BuildAsync(tmp);

        await store.SaveAsync(slug, new AutomationConfig { Automations = { Auto("a") } });
        var (config, _, _, stamp) = await store.LoadWithStampAsync(slug);

        // Concurrent edit: an agent adds "b" directly on disk after the caller loaded.
        WriteDisk(configPath, Auto("a"), Auto("b"));

        var result = await store.SaveAsync(slug, config, stamp);

        Assert.True(result.Diverged);
        Assert.Equal(new[] { "b" }, result.PreservedIds);
        var (reloaded, _, _) = await store.LoadAsync(slug);
        Assert.Equal(new[] { "a", "b" }, reloaded.Automations.Select(a => a.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Save_with_current_stamp_honors_deletion()
    {
        using var tmp = new TempDir();
        var (store, slug, _) = await BuildAsync(tmp);

        await store.SaveAsync(slug, new AutomationConfig { Automations = { Auto("a"), Auto("b") } });
        var (config, _, _, stamp) = await store.LoadWithStampAsync(slug);
        config.Automations.RemoveAll(a => a.Id == "b");

        var result = await store.SaveAsync(slug, config, stamp);

        Assert.False(result.Diverged);
        Assert.Empty(result.PreservedIds);
        var (reloaded, _, _) = await store.LoadAsync(slug);
        Assert.Equal(new[] { "a" }, reloaded.Automations.Select(a => a.Id));
    }

    [Fact]
    public async Task Save_without_base_stamp_never_drops_disk_automations()
    {
        using var tmp = new TempDir();
        var (store, slug, configPath) = await BuildAsync(tmp);
        WriteDisk(configPath, Auto("a"), Auto("b"));

        // Legacy API client PUTs a config that only knows about "a".
        var result = await store.SaveAsync(slug, new AutomationConfig { Automations = { Auto("a") } });

        Assert.Equal(new[] { "b" }, result.PreservedIds);
        var (reloaded, _, _) = await store.LoadAsync(slug);
        Assert.Equal(new[] { "a", "b" }, reloaded.Automations.Select(a => a.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Caller_edits_win_over_disk_for_same_automation_id()
    {
        using var tmp = new TempDir();
        var (store, slug, configPath) = await BuildAsync(tmp);

        await store.SaveAsync(slug, new AutomationConfig { Automations = { Auto("a") } });
        var (config, _, _, stamp) = await store.LoadWithStampAsync(slug);

        var diskEdit = Auto("a");
        diskEdit.Name = "disk-edit";
        WriteDisk(configPath, diskEdit);

        config.Automations[0].Name = "caller-edit";
        await store.SaveAsync(slug, config, stamp);

        var (reloaded, _, _) = await store.LoadAsync(slug);
        Assert.Equal("caller-edit", Assert.Single(reloaded.Automations).Name);
    }

    [Fact]
    public async Task Unknown_fields_survive_load_save_round_trip()
    {
        using var tmp = new TempDir();
        var (store, slug, configPath) = await BuildAsync(tmp);

        File.WriteAllText(configPath, """
        {
          "automations": [
            {
              "id": "shorts-cadence-daily",
              "model": "haiku",
              "trigger": { "type": "interval", "cron": "0 7 * * *", "customTriggerField": 42 },
              "conditions": [],
              "actions": [ { "type": "runAgent", "agent": "publisher", "model": "haiku", "customActionField": "keep-me" } ]
            }
          ],
          "customTopLevel": true
        }
        """);

        var (config, _, _, stamp) = await store.LoadWithStampAsync(slug);
        await store.SaveAsync(slug, config, stamp);

        var saved = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(saved);
        var auto = doc.RootElement.GetProperty("automations")[0];
        Assert.Equal("haiku", auto.GetProperty("model").GetString());
        Assert.Equal(42, auto.GetProperty("trigger").GetProperty("customTriggerField").GetInt32());
        Assert.Equal("keep-me", auto.GetProperty("actions")[0].GetProperty("customActionField").GetString());
        Assert.Equal("haiku", auto.GetProperty("actions")[0].GetProperty("model").GetString());
        Assert.True(doc.RootElement.GetProperty("customTopLevel").GetBoolean());

        // The saved file must stay loadable (no duplicate keys, discriminators intact).
        var (reloaded, _, _) = await store.LoadAsync(slug);
        var a = Assert.Single(reloaded.Automations);
        Assert.IsType<IntervalTriggerSpec>(a.Trigger);
        Assert.Equal("haiku", Assert.IsType<RunAgentActionSpec>(Assert.Single(a.Actions)).Model);
    }

    [Fact]
    public void UiTypeKey_is_not_serialized()
    {
        var cfg = new AutomationConfig { Automations = { Auto("a") } };
        var json = JsonSerializer.Serialize(cfg, AutomationStore.JsonOptions);
        Assert.DoesNotContain("uiTypeKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_saves_do_not_corrupt_the_file()
    {
        using var tmp = new TempDir();
        var (store, slug, _) = await BuildAsync(tmp);
        await store.SaveAsync(slug, new AutomationConfig { Automations = { Auto("base") } });

        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            var (config, _, _, _) = await store.LoadWithStampAsync(slug);
            config.Automations.Add(Auto($"auto-{i}"));
            // No base stamp: every concurrently added automation must be preserved.
            await store.SaveAsync(slug, config);
        });
        await Task.WhenAll(tasks);

        var (reloaded, _, _) = await store.LoadAsync(slug);
        var ids = reloaded.Automations.Select(a => a.Id).ToHashSet();
        Assert.Contains("base", ids);
        for (var i = 0; i < 8; i++) Assert.Contains($"auto-{i}", ids);
    }
}
