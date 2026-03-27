using Microsoft.EntityFrameworkCore;
using Todo.Core.Data;
using Todo.Core.Models;

namespace Todo.Core.Services;

public class LabelService
{
    private readonly ProjectService _projectService;

    public LabelService(ProjectService projectService)
    {
        _projectService = projectService;
    }

    private static async Task EnsureLabelTablesAsync(TodoDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Labels (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Color TEXT NOT NULL DEFAULT '#6366f1'
            )
        """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS TicketLabels (
                TicketsId INTEGER NOT NULL,
                LabelsId INTEGER NOT NULL,
                PRIMARY KEY (TicketsId, LabelsId)
            )
        """);
    }

    public async Task<List<Label>> ListLabelsAsync(string projectSlug)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureLabelTablesAsync(db);
        return await db.Labels.OrderBy(l => l.Name).ToListAsync();
    }

    public async Task<Label> CreateLabelAsync(string projectSlug, string name, string color)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureLabelTablesAsync(db);
        var label = new Label { Name = name, Color = color };
        db.Labels.Add(label);
        await db.SaveChangesAsync();
        return label;
    }

    public async Task<bool> DeleteLabelAsync(string projectSlug, int labelId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureLabelTablesAsync(db);
        var label = await db.Labels.FindAsync(labelId);
        if (label is null) return false;
        db.Labels.Remove(label);
        await db.SaveChangesAsync();
        return true;
    }
}
