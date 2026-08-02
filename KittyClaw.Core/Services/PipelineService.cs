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

    public PipelineService(ProjectService projects) => _projects = projects;

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

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugPattern();
}
