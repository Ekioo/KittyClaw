using System.Text.RegularExpressions;
using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

/// <summary>Manages project-local pipelines and the backward-compatible workflow schema.</summary>
public sealed partial class PipelineService
{
    public const string DefaultPipelineSlug = "main";
    public const string DefaultPipelineName = "Main";

    private readonly ProjectService _projects;

    private readonly ColumnProcessorService? _processors;
    private readonly ColumnScheduledTaskService? _scheduledTasks;

    public PipelineService(ProjectService projects, ColumnProcessorService? processors = null,
        ColumnScheduledTaskService? scheduledTasks = null)
    {
        _projects = projects;
        _processors = processors;
        _scheduledTasks = scheduledTasks;
    }

    internal static async Task<Pipeline> EnsureWorkflowSchemaAsync(TodoDbContext db)
    {
        await MigrationGate.RunOnceAsync(db, "workflow-pipelines-v1", static async d =>
        {
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS Pipelines (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Slug TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    IsDefault INTEGER NOT NULL DEFAULT 0
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Pipelines_Slug ON Pipelines(Slug);
                """);

            await MigrationGate.AddColumnIfMissingAsync(d,
                "ALTER TABLE BoardColumns ADD COLUMN PipelineId INTEGER NOT NULL DEFAULT 0");
            await MigrationGate.AddColumnIfMissingAsync(d,
                "ALTER TABLE BoardColumns ADD COLUMN Role INTEGER NOT NULL DEFAULT 0");
            await MigrationGate.AddColumnIfMissingAsync(d,
                "ALTER TABLE Tickets ADD COLUMN PipelineId INTEGER NOT NULL DEFAULT 0");
            await MigrationGate.AddColumnIfMissingAsync(d,
                "ALTER TABLE Tickets ADD COLUMN ColumnId INTEGER NULL");
            await MigrationGate.AddColumnIfMissingAsync(d,
                "ALTER TABLE Tickets ADD COLUMN BlocksParent INTEGER NOT NULL DEFAULT 1");

            await d.Database.ExecuteSqlRawAsync("""
                INSERT INTO Pipelines (Name, Slug, SortOrder, IsDefault)
                SELECT 'Main', 'main', 0, 1
                WHERE NOT EXISTS (SELECT 1 FROM Pipelines);
                UPDATE Pipelines SET IsDefault = CASE
                    WHEN Id = (SELECT Id FROM Pipelines ORDER BY IsDefault DESC, SortOrder, Id LIMIT 1) THEN 1
                    ELSE 0 END;
                UPDATE BoardColumns SET PipelineId = (SELECT Id FROM Pipelines WHERE IsDefault = 1 LIMIT 1)
                    WHERE PipelineId = 0;
                UPDATE Tickets SET PipelineId = (SELECT Id FROM Pipelines WHERE IsDefault = 1 LIMIT 1)
                    WHERE PipelineId = 0;
                UPDATE Tickets SET ColumnId = (
                    SELECT c.Id FROM BoardColumns c
                    WHERE c.PipelineId = Tickets.PipelineId AND c.Name = Tickets.Status
                    ORDER BY c.Id LIMIT 1
                ) WHERE ColumnId IS NULL;
                UPDATE BoardColumns SET Role = 1 WHERE Role = 0 AND Name IN ('Blocked', 'Scheduled');
                UPDATE BoardColumns SET Role = 5 WHERE Name = 'Blocked' AND Role IN (0, 1);
                UPDATE BoardColumns SET Role = 2 WHERE Role = 0 AND Name IN ('Done', 'Published', 'Delivered');
                UPDATE BoardColumns SET Role = 3 WHERE Role = 0 AND Name IN ('Rejected', 'Cancelled', 'Abandoned');
                DROP INDEX IF EXISTS IX_BoardColumns_Name;
                DELETE FROM BoardColumns WHERE Id NOT IN (
                    SELECT MIN(Id) FROM BoardColumns GROUP BY PipelineId, Name
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_BoardColumns_PipelineId_Name
                    ON BoardColumns(PipelineId, Name);
                CREATE INDEX IF NOT EXISTS IX_Tickets_PipelineId_ColumnId
                    ON Tickets(PipelineId, ColumnId);
                """);
        });

        await MigrationGate.RunOnceAsync(db, "blocked-column-role-v1", static async d =>
        {
            await d.Database.ExecuteSqlRawAsync(
                "UPDATE BoardColumns SET Role = 5 WHERE Name = 'Blocked' AND Role IN (0, 1)");
        });

        return await db.Pipelines.AsNoTracking().SingleAsync(p => p.IsDefault);
    }

    public async Task<List<Pipeline>> ListAsync(string projectSlug)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        return await db.Pipelines.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
    }

    public async Task<Pipeline?> GetAsync(string projectSlug, int pipelineId)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        return await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId);
    }

    public async Task<Pipeline> CreateAsync(string projectSlug, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Le nom du pipeline est requis.");

        await using var db = _projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var baseSlug = SlugPattern().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (baseSlug.Length == 0) baseSlug = "pipeline";
        var slug = baseSlug;
        for (var suffix = 2; await db.Pipelines.AnyAsync(p => p.Slug == slug); suffix++)
            slug = $"{baseSlug}-{suffix}";

        var pipeline = new Pipeline
        {
            Name = name.Trim(),
            Slug = slug,
            SortOrder = (await db.Pipelines.MaxAsync(p => (int?)p.SortOrder) ?? -1) + 1,
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync();
        return pipeline;
    }

    public async Task<Pipeline?> UpdateAsync(string projectSlug, int pipelineId, string? name)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var pipeline = await db.Pipelines.FindAsync(pipelineId);
        if (pipeline is null) return null;
        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Le nom du pipeline est requis.");
            pipeline.Name = name.Trim();
        }
        await db.SaveChangesAsync();
        return pipeline;
    }

    public async Task<bool> DeleteAsync(string projectSlug, int pipelineId)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await MigrationGate.AddColumnIfMissingAsync(db, "ALTER TABLE Tickets ADD COLUMN ParentId INTEGER NULL");
        await ColumnProcessorService.EnsureTableAsync(db);
        await ColumnExecutionService.EnsureTableAsync(db);
        await TicketService.EnsureActivityTableAsync(db);
        await TicketService.EnsureTicketDependenciesTableAsync(db);
        await ColumnScheduledTaskService.EnsureTablesAsync(db);
        var pipeline = await db.Pipelines.FindAsync(pipelineId);
        if (pipeline is null) return false;
        var replacement = await db.Pipelines.OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            .FirstOrDefaultAsync(p => p.Id != pipelineId)
            ?? throw new InvalidOperationException("A project must retain at least one pipeline.");
        var columnIds = await db.BoardColumns.Where(c => c.PipelineId == pipelineId).Select(c => c.Id).ToListAsync();
        var ticketIds = db.Tickets.Where(t => t.PipelineId == pipelineId).Select(t => t.Id);
        if (await db.ColumnExecutions.AnyAsync(e => ticketIds.Contains(e.TicketId) && e.Status == ColumnExecutionStatus.Running))
            throw new InvalidOperationException("Stop running agents in this pipeline before deleting it.");
        var taskIds = await db.ColumnScheduledTasks.Where(t => columnIds.Contains(t.ColumnId)).Select(t => t.Id).ToListAsync();
        if (await db.ColumnScheduledTaskRuns.AnyAsync(r => taskIds.Contains(r.TaskId) && r.Status == ColumnScheduledTaskRunStatus.Running))
            throw new InvalidOperationException("Stop running scheduled tasks in this pipeline before deleting it.");
        foreach (var columnId in columnIds)
        {
            if (_processors is not null) await _processors.PrepareColumnDeletionAsync(projectSlug, columnId);
            if (_scheduledTasks is not null) await _scheduledTasks.PrepareColumnDeletionAsync(projectSlug, columnId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        // Children in other pipelines survive and become independent tickets.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tickets SET ParentId = NULL WHERE ParentId IN (SELECT Id FROM Tickets WHERE PipelineId = {pipelineId})");
        await db.ColumnExecutions.Where(e => ticketIds.Contains(e.TicketId)).ExecuteDeleteAsync();
        await db.TicketQuestions.Where(q => ticketIds.Contains(q.TicketId)).ExecuteDeleteAsync();
        await db.Comments.Where(c => ticketIds.Contains(c.TicketId)).ExecuteDeleteAsync();
        await db.ActivityEntries.Where(a => ticketIds.Contains(a.TicketId)).ExecuteDeleteAsync();
        await db.TicketDependencies.Where(d => ticketIds.Contains(d.BlockedTicketId) || ticketIds.Contains(d.BlocksTicketId)).ExecuteDeleteAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS TicketLabels (TicketsId INTEGER NOT NULL, LabelsId INTEGER NOT NULL, PRIMARY KEY (TicketsId, LabelsId))");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM TicketLabels WHERE TicketsId IN (SELECT Id FROM Tickets WHERE PipelineId = {pipelineId})");
        await db.Tickets.Where(t => t.PipelineId == pipelineId).ExecuteDeleteAsync();
        await db.ColumnScheduledTaskRuns.Where(r => taskIds.Contains(r.TaskId)).ExecuteDeleteAsync();
        await db.ColumnScheduledTasks.Where(t => columnIds.Contains(t.ColumnId)).ExecuteDeleteAsync();
        foreach (var processor in await db.ColumnProcessors.ToListAsync())
        {
            if (columnIds.Contains(processor.ColumnId)) { db.ColumnProcessors.Remove(processor); continue; }
            if (processor.DefaultTargetColumnId is int target && columnIds.Contains(target)) processor.DefaultTargetColumnId = null;
            if (processor.TechnicalFailureColumnId is int failure && columnIds.Contains(failure)) processor.TechnicalFailureColumnId = null;
            processor.Routes = processor.Routes.Where(r => !columnIds.Contains(r.TargetColumnId)).ToList();
        }
        await db.BoardColumns.Where(c => c.PipelineId == pipelineId).ExecuteDeleteAsync();
        if (pipeline.IsDefault) replacement.IsDefault = true;
        db.Pipelines.Remove(pipeline);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugPattern();
}
