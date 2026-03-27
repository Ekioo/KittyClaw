using Microsoft.EntityFrameworkCore;
using Todo.Core.Data;
using Todo.Core.Models;

namespace Todo.Core.Services;

public class ColumnService
{
    private readonly ProjectService _projectService;

    public ColumnService(ProjectService projectService)
    {
        _projectService = projectService;
    }

    private static readonly (string Name, string Color)[] DefaultColumns =
    [
        ("Backlog",      "#5a6a80"),
        ("Todo",         "#4a9eff"),
        ("InProgress",   "#f59e42"),
        ("Blocked",      "#f06b6b"),
        ("OwnerReview",  "#a78bfa"),
        ("Done",         "#3ecf8e"),
    ];

    internal static async Task EnsureBoardColumnsTableAsync(TodoDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS BoardColumns (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Color TEXT NOT NULL DEFAULT '#5a6a80',
                SortOrder INTEGER NOT NULL DEFAULT 0
            )
        """);

        // Seed defaults if table is empty
        var count = await db.BoardColumns.CountAsync();
        if (count == 0)
        {
            for (int i = 0; i < DefaultColumns.Length; i++)
            {
                db.BoardColumns.Add(new BoardColumn
                {
                    Name = DefaultColumns[i].Name,
                    Color = DefaultColumns[i].Color,
                    SortOrder = i
                });
            }
            await db.SaveChangesAsync();
        }
    }

    public async Task<List<BoardColumn>> ListColumnsAsync(string projectSlug)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureBoardColumnsTableAsync(db);
        return await db.BoardColumns.OrderBy(c => c.SortOrder).ToListAsync();
    }

    public async Task<BoardColumn> CreateColumnAsync(string projectSlug, string name, string color = "#5a6a80")
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureBoardColumnsTableAsync(db);
        var maxSort = await db.BoardColumns.Select(c => (int?)c.SortOrder).MaxAsync() ?? -1;
        var column = new BoardColumn
        {
            Name = name,
            Color = color,
            SortOrder = maxSort + 1
        };
        db.BoardColumns.Add(column);
        await db.SaveChangesAsync();
        return column;
    }

    public async Task<bool> DeleteColumnAsync(string projectSlug, int columnId, string moveTicketsTo)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureBoardColumnsTableAsync(db);
        var column = await db.BoardColumns.FindAsync(columnId);
        if (column is null) return false;

        // Ensure the target column exists
        var targetExists = await db.BoardColumns.AnyAsync(c => c.Name == moveTicketsTo && c.Id != columnId);
        if (!targetExists) return false;

        // Move tickets from this column to the target
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE Tickets SET Status = {0} WHERE Status = {1}", moveTicketsTo, column.Name);

        db.BoardColumns.Remove(column);
        await db.SaveChangesAsync();
        return true;
    }
}
