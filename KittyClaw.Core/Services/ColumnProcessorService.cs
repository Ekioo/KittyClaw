using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

public sealed class ColumnProcessorService(
    ProjectService projects,
    ProjectSkillService skills)
{
    private static async Task EnsureTableAsync(TodoDbContext db)
    {
        await MigrationGate.RunOnceAsync(db, "column-processors-v1", static d =>
            d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS ColumnProcessors (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ColumnId INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    Mission TEXT NOT NULL,
                    Model TEXT NULL,
                    Enabled INTEGER NOT NULL DEFAULT 1,
                    MaxTurns INTEGER NOT NULL DEFAULT 100,
                    SelectionOrder INTEGER NOT NULL DEFAULT 0,
                    MaxAttempts INTEGER NOT NULL DEFAULT 3,
                    RetryBackoffSeconds INTEGER NOT NULL DEFAULT 60,
                    DefaultTargetColumnId INTEGER NULL,
                    TechnicalFailureColumnId INTEGER NULL,
                    AvailableSkillsJson TEXT NOT NULL DEFAULT '[]',
                    RecommendedSkillsJson TEXT NOT NULL DEFAULT '[]',
                    RequiredSkillsJson TEXT NOT NULL DEFAULT '[]',
                    RoutesJson TEXT NOT NULL DEFAULT '[]',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_ColumnProcessors_ColumnId
                    ON ColumnProcessors(ColumnId);
                """));
        await MigrationGate.RunOnceAsync(db, "column-processors-routing-v1", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnProcessors ADD COLUMN SelectionOrder INTEGER NOT NULL DEFAULT 0");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnProcessors ADD COLUMN MaxAttempts INTEGER NOT NULL DEFAULT 3");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnProcessors ADD COLUMN RetryBackoffSeconds INTEGER NOT NULL DEFAULT 60");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnProcessors ADD COLUMN DefaultTargetColumnId INTEGER NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnProcessors ADD COLUMN TechnicalFailureColumnId INTEGER NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnProcessors ADD COLUMN RoutesJson TEXT NOT NULL DEFAULT '[]'");
        });
    }

    public async Task<ColumnProcessor?> GetAsync(string projectSlug, int columnId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await EnsureTableAsync(db);
        return await db.ColumnProcessors.AsNoTracking().FirstOrDefaultAsync(p => p.ColumnId == columnId);
    }

    public async Task<List<ColumnProcessor>> ListEnabledAsync(string projectSlug)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await EnsureTableAsync(db);
        var enabled = await db.ColumnProcessors.AsNoTracking().Where(p => p.Enabled).OrderBy(p => p.Id).ToListAsync();
        // Ensure every enabled processor has canonical memory even when its column has
        // no eligible ticket. This also migrates lessons written by early builds to the
        // inferred .agents/column-{id}/memory.md path during engine startup.
        foreach (var processor in enabled)
            await EnsureMemoryAsync(projectSlug, processor);
        return enabled;
    }

    public async Task<ColumnProcessor> SaveAsync(
        string projectSlug, int columnId, string name, string mission, string? model,
        bool enabled, int maxTurns, List<string>? availableSkills,
        List<string>? recommendedSkills, List<string>? requiredSkills,
        TicketSelectionOrder selectionOrder = TicketSelectionOrder.Position,
        int maxAttempts = 3, int retryBackoffSeconds = 60,
        int? defaultTargetColumnId = null, int? technicalFailureColumnId = null,
        List<ColumnRoute>? routes = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Le nom du processeur est requis.");
        if (string.IsNullOrWhiteSpace(mission)) throw new InvalidOperationException("La mission du processeur est requise.");
        if (maxTurns < 1) throw new InvalidOperationException("MaxTurns doit être supérieur à zéro.");
        if (maxAttempts < 1) throw new InvalidOperationException("MaxAttempts doit être supérieur à zéro.");
        if (retryBackoffSeconds < 1) throw new InvalidOperationException("RetryBackoffSeconds doit être supérieur à zéro.");

        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await EnsureTableAsync(db);
        if (!await db.BoardColumns.AnyAsync(c => c.Id == columnId))
            throw new InvalidOperationException($"La colonne #{columnId} n'existe pas.");
        var targetIds = (routes ?? []).Select(r => r.TargetColumnId)
            .Concat(defaultTargetColumnId is null ? [] : [defaultTargetColumnId.Value])
            .Concat(technicalFailureColumnId is null ? [] : [technicalFailureColumnId.Value])
            .Distinct().ToList();
        if (targetIds.Contains(columnId))
            throw new InvalidOperationException("Une route ne peut pas renvoyer vers sa propre colonne. Utilisez la politique de nouvelle tentative pour rejouer un traitement.");
        var knownTargets = await db.BoardColumns.Where(c => targetIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
        var missingTargets = targetIds.Except(knownTargets).ToList();
        if (missingTargets.Count > 0)
            throw new InvalidOperationException($"Colonnes de routage inconnues : {string.Join(", ", missingTargets)}.");

        var knownSkills = (await skills.ListAsync(projectSlug)).Select(s => s.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = (availableSkills ?? []).Concat(recommendedSkills ?? []).Concat(requiredSkills ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var unknown = referenced.Where(s => !knownSkills.Contains(s)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"Skills projet inconnus : {string.Join(", ", unknown)}.");

        var processor = await db.ColumnProcessors.FirstOrDefaultAsync(p => p.ColumnId == columnId);
        if (processor is null)
        {
            processor = new ColumnProcessor { ColumnId = columnId, Name = name.Trim() };
            db.ColumnProcessors.Add(processor);
        }
        processor.Name = name.Trim();
        processor.Mission = mission.Trim();
        processor.Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        processor.Enabled = enabled;
        processor.MaxTurns = maxTurns;
        processor.SelectionOrder = selectionOrder;
        processor.MaxAttempts = maxAttempts;
        processor.RetryBackoffSeconds = retryBackoffSeconds;
        processor.DefaultTargetColumnId = defaultTargetColumnId;
        processor.TechnicalFailureColumnId = technicalFailureColumnId;
        processor.AvailableSkills = availableSkills ?? [];
        processor.RecommendedSkills = recommendedSkills ?? [];
        processor.RequiredSkills = requiredSkills ?? [];
        processor.Routes = routes ?? [];
        processor.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await EnsureMemoryAsync(projectSlug, processor);
        return processor;
    }

    public async Task<bool> DeleteAsync(string projectSlug, int columnId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var processor = await db.ColumnProcessors.FirstOrDefaultAsync(p => p.ColumnId == columnId);
        if (processor is null) return false;
        db.ColumnProcessors.Remove(processor);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<string?> GetMemoryIndexPathAsync(string projectSlug, int columnId)
    {
        var processor = await GetAsync(projectSlug, columnId);
        if (processor is null) return null;
        return await EnsureMemoryAsync(projectSlug, processor);
    }

    private async Task<string> EnsureMemoryAsync(string projectSlug, ColumnProcessor processor)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Le projet '{projectSlug}' n'existe pas.");
        var memoryDir = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "processors", $"column-{processor.ColumnId}", "memory");
        Directory.CreateDirectory(memoryDir);
        var index = Path.Combine(memoryDir, "MEMORY.md");
        if (!File.Exists(index))
            File.WriteAllText(index, $"# {processor.Name} memory\n\nPersistent lessons for column #{processor.ColumnId}.\n");
        await MigrateLegacyMemoryAsync(projects.ResolveWorkspacePath(project), processor.ColumnId, index);
        return index;
    }

    private static async Task MigrateLegacyMemoryAsync(string workspace, int columnId, string canonicalPath)
    {
        // Early column-processor builds did not expose the canonical memory path in the
        // runtime prompt. Some agents consequently inferred .agents/column-{id}/memory.md.
        // Preserve useful lessons from those files without deleting or repeatedly copying them.
        var legacyPath = Path.Combine(workspace, ".agents", $"column-{columnId}", "memory.md");
        if (!File.Exists(legacyPath)) return;
        var canonical = await File.ReadAllTextAsync(canonicalPath);
        var lessons = (await File.ReadAllLinesAsync(legacyPath))
            .Select(line => line.TrimEnd())
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Where(line => !canonical.Contains(line, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (lessons.Count == 0) return;
        await File.AppendAllTextAsync(canonicalPath, "\n" + string.Join("\n", lessons) + "\n");
    }
}
