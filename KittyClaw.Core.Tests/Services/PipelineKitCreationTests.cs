using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Services;

public sealed class PipelineKitCreationTests : IDisposable
{
    private readonly TempDir _temp = new();
    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("schedule")]
    public async Task Creation_target_uses_stable_key_after_column_and_pipeline_renames(string placement)
    {
        var f = await FixtureAsync(placement);
        var bytes = Rewrite(f.Kit, pipeline =>
        {
            var target = pipeline["columns"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "Target")!;
            Assert.Equal(target["key"]!.GetValue<string>(), Action(pipeline, placement)["createTarget"]!.GetValue<string>());
            target["name"] = "Renamed target";
        });
        var installed = (await f.Import.InstallAsync(f.Slug, bytes,
            new PipelineImportConfirmation { PipelineName = "Renamed pipeline" }))!;
        Assert.True(installed.Enabled);
        var source = installed.Columns.Single(c => c.Name == "Start");
        var targetColumn = installed.Columns.Single(c => c.Name == "Renamed target");
        var processor = (await f.Processors.GetAsync(f.Slug, source.ColumnId))!;
        var action = placement == "schedule"
            ? (await new ColumnScheduledTaskService(f.Projects).ListAsync(f.Slug, source.ColumnId)).Single().Actions.Single()
            : (placement == "before" ? processor.BeforeActions : processor.AfterActions).Single();
        Assert.Equal("Renamed target", Assert.IsType<CreateTicketActionSpec>(action.Action).Status);
        var tickets = new TicketService(f.Projects, new MemberService(f.Projects));
        var incoming = await tickets.CreateTicketAsync(f.Slug, "Incoming", pipelineId: installed.PipelineId, columnId: source.ColumnId);
        var result = await new ColumnActionExecutor(f.Projects, tickets, NullLogger<ColumnActionExecutor>.Instance)
            .ExecuteAsync(f.Slug, processor, new ColumnExecution { Id = "test", TicketId = incoming.Id },
                incoming, action, null, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
        await using var db = f.Projects.GetProjectDb(f.Slug);
        var created = Assert.Single(db.Tickets.Where(t => t.Title == "Created by kit"));
        Assert.Equal(installed.PipelineId, created.PipelineId);
        Assert.Equal(targetColumn.ColumnId, created.ColumnId);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("schedule")]
    public async Task Source_parent_reference_blocks_export_and_untrusted_import(string placement)
    {
        var f = await FixtureAsync(placement);
        var bytes = Rewrite(f.Kit, pipeline => Action(pipeline, placement)["action"]!["parentId"] = 42);
        var preview = (await f.Import.AnalyzeAsync(f.Slug, bytes))!;
        Assert.False(preview.Installable);
        Assert.Contains(preview.Blockages, i => i.Category == "external-reference");
        await Assert.ThrowsAsync<PipelineImportRejectedException>(() => f.Import.InstallAsync(f.Slug, bytes,
            new PipelineImportConfirmation { PipelineName = "Rejected" }));
        var spec = (CreateTicketActionSpec)f.Action.Action;
        spec.ParentId = 42;
        await f.Save();
        var blocked = await Assert.ThrowsAsync<PipelineExportBlockedException>(() => f.Export.ExportAsync(f.Slug, f.PipelineId));
        Assert.Contains(blocked.Findings, i => i.Category == "external-reference" && i.Excerpt.Contains("parentId"));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("schedule")]
    public async Task Creation_destination_outside_the_exported_pipeline_blocks_export(string placement)
    {
        var f = await FixtureAsync(placement);
        var main = (await new PipelineService(f.Projects).ListAsync(f.Slug)).Single(p => p.IsDefault);
        await new ColumnService(f.Projects).CreateColumnAsync(f.Slug, "External target", pipelineId: main.Id);
        ((CreateTicketActionSpec)f.Action.Action).Status = "External target";
        await f.Save();
        var blocked = await Assert.ThrowsAsync<PipelineExportBlockedException>(() => f.Export.ExportAsync(f.Slug, f.PipelineId));
        Assert.Contains(blocked.Findings, i => i.Category == "external-reference" && i.Excerpt.Contains("status"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Legacy_destinations_are_validated_and_unknown_keys_never_fall_back_to_names(bool unknownKey, bool validName)
    {
        var f = await FixtureAsync("after");
        var bytes = Rewrite(f.Kit, pipeline =>
        {
            var action = Action(pipeline, "after");
            if (unknownKey) action["createTarget"] = "missing-key";
            else action.AsObject().Remove("createTarget");
            action["action"]!["status"] = validName || unknownKey ? "Target" : "Missing column";
        });
        var preview = (await f.Import.AnalyzeAsync(f.Slug, bytes))!;
        Assert.Equal(validName, preview.Installable);
        if (validName)
            Assert.True((await f.Import.InstallAsync(f.Slug, bytes, new PipelineImportConfirmation { PipelineName = "Legacy" }))!.Enabled);
        else Assert.Contains(preview.Blockages, i => i.Category == "invalid-processor");
    }

    private sealed record Fixture(ProjectService Projects, string Slug, int PipelineId, ColumnProcessorService Processors,
        PipelineExportService Export, PipelineImportService Import, ColumnProcessorAction Action, Func<Task> Save, byte[] Kit);

    private async Task<Fixture> FixtureAsync(string placement)
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Creation kit");
        var skills = new ProjectSkillService(projects);
        var processors = new ColumnProcessorService(projects, skills);
        var columns = new ColumnService(projects, processors);
        var pipelines = new PipelineService(projects);
        var pipeline = await pipelines.CreateAsync(project.Slug, "Creation flow");
        var source = await columns.CreateColumnAsync(project.Slug, "Start", pipelineId: pipeline.Id);
        await columns.CreateColumnAsync(project.Slug, "Target", pipelineId: pipeline.Id);
        var main = (await pipelines.ListAsync(project.Slug)).Single(p => p.IsDefault);
        await columns.CreateColumnAsync(project.Slug, "Renamed target", pipelineId: main.Id);
        var action = new ColumnProcessorAction("create", new CreateTicketActionSpec { Title = "Created by kit", Status = "Target" });
        async Task Save()
        {
            await processors.SaveAsync(project.Slug, source.Id, "Creator", "Create tickets", null, true, 20, [], [], [],
                beforeActions: placement == "before" ? [action] : [], afterActions: placement == "after" ? [action] : []);
            if (placement == "schedule") await new ColumnScheduledTaskService(projects).SaveColumnAsync(project.Slug, source.Id,
                [new ColumnScheduledTask { Id = "schedule", ColumnId = source.Id, Name = "Daily", Cron = "0 8 * * *", TimeZoneId = "UTC", Actions = [action] }]);
        }
        await Save();
        var exporter = new PipelineExportService(projects, pipelines, columns, processors, skills);
        var importer = new PipelineImportService(projects, pipelines, columns, processors,
            new ProjectSecretVault(_temp.Path, new TestSecretProtector()));
        return new(projects, project.Slug, pipeline.Id, processors, exporter, importer, action, Save,
            (await exporter.ExportAsync(project.Slug, pipeline.Id))!.Content);
    }

    private static JsonNode Action(JsonNode pipeline, string placement)
    {
        var source = pipeline["columns"]![0]!;
        return placement == "schedule" ? source["scheduledTasks"]![0]!["actions"]![0]!
            : source["processor"]![placement == "before" ? "beforeActions" : "afterActions"]![0]!;
    }

    private static byte[] Rewrite(byte[] kit, Action<JsonNode> change)
    {
        using var input = new ZipArchive(new MemoryStream(kit));
        var files = input.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); using var b = new MemoryStream(); s.CopyTo(b); return b.ToArray(); });
        var pipeline = JsonNode.Parse(files["pipeline.json"])!;
        change(pipeline);
        files["pipeline.json"] = JsonSerializer.SerializeToUtf8Bytes(pipeline);
        var manifest = JsonNode.Parse(files["manifest.json"])!;
        foreach (var file in manifest["files"]!.AsArray())
            file!["sha256"] = Convert.ToHexString(SHA256.HashData(files[file["path"]!.GetValue<string>()])).ToLowerInvariant();
        files["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, content) in files) { using var stream = zip.CreateEntry(name).Open(); stream.Write(content); }
        return output.ToArray();
    }
}
