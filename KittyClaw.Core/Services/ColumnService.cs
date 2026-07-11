using Microsoft.EntityFrameworkCore;
using KittyClaw.Core.Data;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

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
        ("Scheduled",    "#eab308"),
        ("Review",       "#a78bfa"),
        ("Done",         "#3ecf8e"),
    ];

    internal static async Task EnsureBoardColumnsTableAsync(TodoDbContext db)
    {
        // Only the DDL is memoized: the seed/back-fill below is data-dependent (deleting the
        // Scheduled column must back-fill it on the next read) and must run on every call.
        await MigrationGate.RunOnceAsync(db, "board-columns-table", static d =>
            d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS BoardColumns (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Color TEXT NOT NULL DEFAULT '#5a6a80',
                    SortOrder INTEGER NOT NULL DEFAULT 0
                )
            """));

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
        else
        {
            // Existing boards (feature #99): add the "Scheduled" column once if missing.
            await EnsureScheduledColumnAsync(db);
        }
    }

    // Idempotently inserts the "Scheduled" column (feature #99) on boards that predate it.
    // Placed just after "Blocked" when present; columns stay user-reorderable afterwards.
    private static async Task EnsureScheduledColumnAsync(TodoDbContext db)
    {
        if (await db.BoardColumns.AnyAsync(c => c.Name == "Scheduled")) return;

        var columns = await db.BoardColumns.OrderBy(c => c.SortOrder).ToListAsync();
        var blockedIndex = columns.FindIndex(c => c.Name == "Blocked");
        var insertAt = blockedIndex >= 0 ? blockedIndex + 1 : columns.Count;
        columns.Insert(insertAt, new BoardColumn { Name = "Scheduled", Color = "#eab308" });
        for (int i = 0; i < columns.Count; i++)
            columns[i].SortOrder = i;
        db.BoardColumns.Add(columns[insertAt]);
        await db.SaveChangesAsync();
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

    public async Task<BoardColumn?> UpdateColumnAsync(string projectSlug, int columnId, string? name = null, string? color = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureBoardColumnsTableAsync(db);
        var column = await db.BoardColumns.FindAsync(columnId);
        if (column is null) return null;
        // Ticket re-pointing and the column rename must land atomically: ExecuteSqlRawAsync
        // auto-commits on its own, and a crash before SaveChangesAsync would strand every
        // ticket in a column name that no longer exists.
        await using var tx = await db.Database.BeginTransactionAsync();
        if (name is not null && name != column.Name)
        {
            // Rename: update tickets whose Status points at the old name.
            var oldName = column.Name;
            column.Name = name;
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE Tickets SET Status = {0} WHERE Status = {1}", name, oldName);
        }
        if (color is not null) column.Color = color;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
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

        // Move tickets from this column to the target, atomically with the column removal.
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE Tickets SET Status = {0} WHERE Status = {1}", moveTicketsTo, column.Name);

        db.BoardColumns.Remove(column);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }

    public async Task ReorderColumnAsync(string projectSlug, int columnId, int targetIndex)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureBoardColumnsTableAsync(db);

        var column = await db.BoardColumns.FindAsync(columnId);
        if (column is null) return;

        var columns = await db.BoardColumns
            .Where(c => c.Id != columnId)
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        if (targetIndex < 0) targetIndex = 0;
        if (targetIndex > columns.Count) targetIndex = columns.Count;

        columns.Insert(targetIndex, column);
        for (int i = 0; i < columns.Count; i++)
            columns[i].SortOrder = i;

        await db.SaveChangesAsync();
    }
}
