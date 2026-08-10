using Microsoft.EntityFrameworkCore;
using KittyClaw.Core.Data;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public class ColumnService
{
    private readonly ProjectService _projectService;
    private readonly ColumnProcessorService? _processorService;
    private readonly ColumnScheduledTaskService? _scheduledTaskService;

    public ColumnService(ProjectService projectService, ColumnProcessorService? processorService = null,
        ColumnScheduledTaskService? scheduledTaskService = null)
    {
        _projectService = projectService;
        _processorService = processorService;
        _scheduledTaskService = scheduledTaskService;
    }

    private static readonly (string Name, string Color, ColumnRole Role)[] DefaultColumns =
    [
        ("Backlog",      "#5a6a80", ColumnRole.Normal),
        ("Todo",         "#4a9eff", ColumnRole.Normal),
        ("InProgress",   "#f59e42", ColumnRole.Normal),
        ("Blocked",      "#f06b6b", ColumnRole.Waiting),
        ("Scheduled",    "#eab308", ColumnRole.Waiting),
        ("Review",       "#a78bfa", ColumnRole.Normal),
        ("Done",         "#3ecf8e", ColumnRole.Success),
    ];

    internal static async Task EnsureBoardColumnsTableAsync(TodoDbContext db)
    {
        // Only the DDL is memoized: the seed/back-fill below is data-dependent (deleting the
        // Scheduled column must back-fill it on the next read) and must run on every call.
        await MigrationGate.RunOnceAsync(db, "board-columns-table", static d =>
            d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS BoardColumns (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PipelineId INTEGER NOT NULL DEFAULT 0,
                    Name TEXT NOT NULL,
                    Color TEXT NOT NULL DEFAULT '#5a6a80',
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    Role INTEGER NOT NULL DEFAULT 0,
                    UserGuidance TEXT NOT NULL DEFAULT ''
                )
            """));

        await MigrationGate.RunOnceAsync(db, "board-columns-user-guidance-v1", static async d =>
        {
            var columns = await d.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('BoardColumns')").ToListAsync();
            if (!columns.Contains("UserGuidance", StringComparer.OrdinalIgnoreCase))
                await d.Database.ExecuteSqlRawAsync("ALTER TABLE BoardColumns ADD COLUMN UserGuidance TEXT NOT NULL DEFAULT ''");
        });

        var defaultPipeline = await PipelineService.EnsureWorkflowSchemaAsync(db);

        // Duplicate column names crash the board (ToDictionary by name) and make ticket
        // Status ambiguous. Heal boards that already contain duplicates (the pre-index
        // EnsureScheduledColumnAsync check-then-insert could race), then lock the invariant in.
        await MigrationGate.RunOnceAsync(db, "board-columns-unique-name-per-pipeline-v1", static d =>
            d.Database.ExecuteSqlRawAsync("""
                DELETE FROM BoardColumns WHERE Id NOT IN (
                    SELECT MIN(Id) FROM BoardColumns GROUP BY PipelineId, Name
                );
                DROP INDEX IF EXISTS IX_BoardColumns_Name;
                CREATE UNIQUE INDEX IF NOT EXISTS IX_BoardColumns_PipelineId_Name
                    ON BoardColumns(PipelineId, Name);
            """));

        // Seed defaults if table is empty
        var count = await db.BoardColumns.CountAsync(c => c.PipelineId == defaultPipeline.Id);
        if (count == 0)
        {
            for (int i = 0; i < DefaultColumns.Length; i++)
            {
                db.BoardColumns.Add(new BoardColumn
                {
                    Name = DefaultColumns[i].Name,
                    Color = DefaultColumns[i].Color,
                    SortOrder = i,
                    PipelineId = defaultPipeline.Id,
                    Role = DefaultColumns[i].Role,
                });
            }
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another request (typically the engine's first tick racing the first board
                // load) seeded the same pipeline after our empty check. The unique index is
                // the arbiter; discard our pending copies and use the committed rows.
                foreach (var entry in db.ChangeTracker.Entries<BoardColumn>()
                             .Where(entry => entry.State == EntityState.Added))
                    entry.State = EntityState.Detached;
            }
        }
        else
        {
            // Existing, untouched legacy boards (feature #99): add the "Scheduled" column once
            // if missing. A customized pipeline is authoritative: deleting or renaming its
            // scheduling column must not make every later board read recreate a hard-coded one.
            var names = await db.BoardColumns
                .Where(c => c.PipelineId == defaultPipeline.Id)
                .Select(c => c.Name)
                .ToListAsync();
            var legacyNames = new[] { "Backlog", "Todo", "InProgress", "Blocked", "Review", "Done" };
            if (legacyNames.All(name => names.Contains(name, StringComparer.OrdinalIgnoreCase)))
                await EnsureScheduledColumnAsync(db, defaultPipeline.Id);
        }
    }

    // Idempotently inserts the "Scheduled" column (feature #99) on boards that predate it.
    // Placed just after "Blocked" when present; columns stay user-reorderable afterwards.
    private static async Task EnsureScheduledColumnAsync(TodoDbContext db, int pipelineId)
    {
        if (await db.BoardColumns.AnyAsync(c => c.PipelineId == pipelineId && c.Name == "Scheduled")) return;

        var columns = await db.BoardColumns.Where(c => c.PipelineId == pipelineId).OrderBy(c => c.SortOrder).ToListAsync();
        var blockedIndex = columns.FindIndex(c => c.Name == "Blocked");
        var insertAt = blockedIndex >= 0 ? blockedIndex + 1 : columns.Count;
        columns.Insert(insertAt, new BoardColumn
        {
            PipelineId = pipelineId,
            Name = "Scheduled",
            Color = "#eab308",
            Role = ColumnRole.Waiting,
        });
        for (int i = 0; i < columns.Count; i++)
            columns[i].SortOrder = i;
        db.BoardColumns.Add(columns[insertAt]);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the check-then-insert race against a concurrent caller — the column exists now.
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 };

    public async Task<List<BoardColumn>> ListColumnsAsync(string projectSlug, int? pipelineId = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureBoardColumnsTableAsync(db);
        pipelineId ??= await db.Pipelines.Where(p => p.IsDefault).Select(p => p.Id).SingleAsync();
        return await db.BoardColumns.Where(c => c.PipelineId == pipelineId)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync();
    }

    public async Task<BoardColumn> CreateColumnAsync(
        string projectSlug, string name, string color = "#5a6a80",
        int? pipelineId = null, ColumnRole role = ColumnRole.Normal, int? insertIndex = null,
        string userGuidance = "")
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureBoardColumnsTableAsync(db);
        pipelineId ??= await db.Pipelines.Where(p => p.IsDefault).Select(p => p.Id).SingleAsync();
        if (!await db.Pipelines.AnyAsync(p => p.Id == pipelineId))
            throw new InvalidOperationException($"Le pipeline #{pipelineId} n'existe pas.");
        // Names are unique inside a pipeline; separate pipelines may intentionally use
        // the same vocabulary (for example "Review" or "Published").
        var existing = await db.BoardColumns.FirstOrDefaultAsync(c => c.PipelineId == pipelineId && c.Name == name);
        if (existing is not null) return existing;
        var columns = await db.BoardColumns.Where(c => c.PipelineId == pipelineId)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync();
        var targetIndex = Math.Clamp(insertIndex ?? columns.Count, 0, columns.Count);
        for (var index = targetIndex; index < columns.Count; index++)
            columns[index].SortOrder = index + 1;
        var column = new BoardColumn
        {
            PipelineId = pipelineId.Value,
            Name = name,
            Color = color,
            SortOrder = targetIndex,
            Role = role,
            UserGuidance = userGuidance.Trim(),
        };
        db.BoardColumns.Add(column);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(column).State = EntityState.Detached;
            return await db.BoardColumns.FirstAsync(c => c.PipelineId == pipelineId && c.Name == name);
        }
        return column;
    }

    public async Task<BoardColumn?> UpdateColumnAsync(
        string projectSlug, int columnId, string? name = null, string? color = null,
        ColumnRole? role = null, string? userGuidance = null)
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
            // Refuse renames that would collide with another column: the unique index
            // would reject it anyway, and silently merging two columns loses one of them.
            if (await db.BoardColumns.AnyAsync(c => c.PipelineId == column.PipelineId && c.Name == name && c.Id != columnId))
                return null;
            // Rename: update tickets whose Status points at the old name.
            var oldName = column.Name;
            column.Name = name;
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE Tickets SET Status = {0} WHERE ColumnId = {1} OR (ColumnId IS NULL AND PipelineId = {2} AND Status = {3})",
                name, column.Id, column.PipelineId, oldName);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE Tickets SET ScheduleTarget = {0} WHERE PipelineId = {1} AND ScheduleTarget = {2}",
                name, column.PipelineId, oldName);
        }
        if (color is not null) column.Color = color;
        if (role is not null) column.Role = role.Value;
        if (userGuidance is not null) column.UserGuidance = userGuidance.Trim();
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
        var target = await db.BoardColumns.FirstOrDefaultAsync(c =>
            c.PipelineId == column.PipelineId && c.Name == moveTicketsTo && c.Id != columnId);
        if (target is null) return false;

        // processor.json is authoritative. Repair it before the runtime projection so a
        // later synchronization cannot restore routes to the deleted column.
        if (_processorService is not null)
            await _processorService.PrepareColumnDeletionAsync(projectSlug, columnId);
        if (_scheduledTaskService is not null)
            await _scheduledTaskService.PrepareColumnDeletionAsync(projectSlug, columnId);

        // Move tickets and repair every processor reference atomically with the column removal.
        // Without this cleanup, deleting a routed column leaves invisible destinations that only
        // fail when a future ticket returns the corresponding outcome.
        await ColumnProcessorService.EnsureTableAsync(db);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE Tickets SET Status = {0}, ColumnId = {1}, PipelineId = {2} WHERE ColumnId = {3} OR (ColumnId IS NULL AND PipelineId = {2} AND Status = {4})",
            target.Name, target.Id, target.PipelineId, column.Id, column.Name);

        var processors = await db.ColumnProcessors.ToListAsync();
        foreach (var processor in processors)
        {
            if (processor.ColumnId == columnId)
            {
                db.ColumnProcessors.Remove(processor);
                continue;
            }
            if (processor.DefaultTargetColumnId == columnId)
                processor.DefaultTargetColumnId = null;
            if (processor.TechnicalFailureColumnId == columnId)
                processor.TechnicalFailureColumnId = null;
            processor.Routes = processor.Routes.Where(route => route.TargetColumnId != columnId).ToList();
        }

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
            .Where(c => c.PipelineId == column.PipelineId && c.Id != columnId)
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
