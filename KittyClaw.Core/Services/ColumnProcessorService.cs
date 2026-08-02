using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

public sealed class ColumnProcessorService(
    ProjectService projects,
    ProjectSkillService skills)
{
    private static Task EnsureTableAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "column-processors-v1", static d =>
            d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS ColumnProcessors (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ColumnId INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    Mission TEXT NOT NULL,
                    Model TEXT NULL,
                    Enabled INTEGER NOT NULL DEFAULT 1,
                    MaxTurns INTEGER NOT NULL DEFAULT 100,
                    AvailableSkillsJson TEXT NOT NULL DEFAULT '[]',
                    RecommendedSkillsJson TEXT NOT NULL DEFAULT '[]',
                    RequiredSkillsJson TEXT NOT NULL DEFAULT '[]',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_ColumnProcessors_ColumnId
                    ON ColumnProcessors(ColumnId);
                """));

    public async Task<ColumnProcessor?> GetAsync(string projectSlug, int columnId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await EnsureTableAsync(db);
        return await db.ColumnProcessors.AsNoTracking().FirstOrDefaultAsync(p => p.ColumnId == columnId);
    }

    public async Task<ColumnProcessor> SaveAsync(
        string projectSlug, int columnId, string name, string mission, string? model,
        bool enabled, int maxTurns, List<string>? availableSkills,
        List<string>? recommendedSkills, List<string>? requiredSkills)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Le nom du processeur est requis.");
        if (string.IsNullOrWhiteSpace(mission)) throw new InvalidOperationException("La mission du processeur est requise.");
        if (maxTurns < 1) throw new InvalidOperationException("MaxTurns doit être supérieur à zéro.");

        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await EnsureTableAsync(db);
        if (!await db.BoardColumns.AnyAsync(c => c.Id == columnId))
            throw new InvalidOperationException($"La colonne #{columnId} n'existe pas.");

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
        processor.AvailableSkills = availableSkills ?? [];
        processor.RecommendedSkills = recommendedSkills ?? [];
        processor.RequiredSkills = requiredSkills ?? [];
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
        return index;
    }
}
