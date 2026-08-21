using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NCrontab;

namespace KittyClaw.Core.Services;

/// <summary>
/// Project-owned cron tasks attached to stable columns. Versioned files are authoritative;
/// SQLite stores scheduling state and durable action checkpoints.
/// </summary>
public sealed class ColumnScheduledTaskService(
    ProjectService projects,
    ILogger<ColumnScheduledTaskService>? logger = null)
{
    private const int DefinitionVersion = 1;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ReportedInvalidFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ColumnScheduledTaskService> _logger = logger ?? NullLogger<ColumnScheduledTaskService>.Instance;
    private static readonly JsonSerializerOptions FileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task EnsureTablesAsync(TodoDbContext db)
    {
        await MigrationGate.RunOnceAsync(db, "column-scheduled-tasks-v1", static d =>
            d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS ColumnScheduledTasks (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ColumnId INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    Enabled INTEGER NOT NULL DEFAULT 1,
                    Cron TEXT NOT NULL,
                    TimeZoneId TEXT NOT NULL,
                    TicketScope INTEGER NOT NULL DEFAULT 0,
                    ActionsJson TEXT NOT NULL DEFAULT '[]',
                    NextRunAt TEXT NOT NULL,
                    LastRunAt TEXT NULL,
                    LastStatus TEXT NULL,
                    LastError TEXT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ColumnScheduledTasks_Due
                    ON ColumnScheduledTasks(Enabled, NextRunAt);
                CREATE TABLE IF NOT EXISTS ColumnScheduledTaskRuns (
                    Id TEXT NOT NULL PRIMARY KEY,
                    TaskId TEXT NOT NULL,
                    Status INTEGER NOT NULL DEFAULT 0,
                    StartedAt TEXT NOT NULL,
                    EndedAt TEXT NULL,
                    CompletedActionIdsJson TEXT NOT NULL DEFAULT '[]',
                    CurrentActionId TEXT NULL,
                    Error TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ColumnScheduledTaskRuns_TaskStatus
                    ON ColumnScheduledTaskRuns(TaskId, Status);
                """));
    }

    public async Task<List<ColumnScheduledTask>> ListAsync(string projectSlug, int? columnId = null)
    {
        await SynchronizeAsync(projectSlug);
        await using var db = projects.GetProjectDb(projectSlug);
        var query = db.ColumnScheduledTasks.AsNoTracking().AsQueryable();
        if (columnId is not null) query = query.Where(task => task.ColumnId == columnId);
        return await query.OrderBy(task => task.ColumnId).ThenBy(task => task.Name).ToListAsync();
    }

    public async Task<List<ColumnScheduledTask>> SaveColumnAsync(
        string projectSlug, int columnId, IReadOnlyList<ColumnScheduledTask> tasks)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await using var db = projects.GetProjectDb(projectSlug);
            await ColumnService.EnsureBoardColumnsTableAsync(db);
            await EnsureTablesAsync(db);
            if (!await db.BoardColumns.AnyAsync(column => column.Id == columnId))
                throw new InvalidOperationException($"La colonne #{columnId} n’existe pas.");
            var definitions = tasks.Select(task => ToDefinition(task, columnId)).ToList();
            ValidateDefinitions(definitions, columnId, await db.BoardColumns.Select(column => column.Id).ToListAsync());
            await WriteColumnFileAsync(projectSlug, columnId, definitions);
            await SynchronizeLockedAsync(projectSlug, db);
            return await db.ColumnScheduledTasks.AsNoTracking()
                .Where(task => task.ColumnId == columnId).OrderBy(task => task.Name).ToListAsync();
        }
        finally { gate.Release(); }
    }

    public async Task PrepareColumnDeletionAsync(string projectSlug, int columnId)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var path = await GetDefinitionPathAsync(projectSlug, columnId);
            if (File.Exists(path)) File.Delete(path);
            await using var db = projects.GetProjectDb(projectSlug);
            await ColumnService.EnsureBoardColumnsTableAsync(db);
            await EnsureTablesAsync(db);
            var project = await projects.GetProjectAsync(projectSlug)
                ?? throw new InvalidOperationException($"Le projet '{projectSlug}' n’existe pas.");
            var root = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "schedules");
            if (Directory.Exists(root))
            {
                var deletedReference = $"column-{columnId}";
                foreach (var otherPath in Directory.EnumerateFiles(root, "tasks.json", SearchOption.AllDirectories))
                {
                    if (string.Equals(otherPath, path, StringComparison.OrdinalIgnoreCase)) continue;
                    var file = JsonSerializer.Deserialize<ColumnTasksDefinition>(
                        await File.ReadAllTextAsync(otherPath), FileJson);
                    if (file is null) continue;
                    var changed = false;
                    foreach (var action in file.Tasks.SelectMany(task => task.Actions))
                    {
                        if (!string.Equals(action.OnFailure, deletedReference, StringComparison.OrdinalIgnoreCase)) continue;
                        action.OnFailure = null;
                        changed = true;
                    }
                    if (changed) await WriteDefinitionFileAsync(otherPath, file);
                }
            }
            await SynchronizeLockedAsync(projectSlug, db);
        }
        finally { gate.Release(); }
    }

    public async Task<List<(ColumnScheduledTask Task, ColumnScheduledTaskRun Run)>> ClaimDueAsync(
        string projectSlug, DateTime nowUtc)
    {
        await SynchronizeAsync(projectSlug);
        return await ClaimDueAfterSynchronizationAsync(projectSlug, nowUtc);
    }

    public async Task<List<(ColumnScheduledTask Task, ColumnScheduledTaskRun Run)>> ClaimDueForBackgroundAsync(
        string projectSlug, DateTime nowUtc)
    {
        await SynchronizeAsync(projectSlug, tolerateInvalidFiles: true);
        return await ClaimDueAfterSynchronizationAsync(projectSlug, nowUtc);
    }

    private async Task<List<(ColumnScheduledTask Task, ColumnScheduledTaskRun Run)>> ClaimDueAfterSynchronizationAsync(
        string projectSlug, DateTime nowUtc)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTablesAsync(db);
        var due = await db.ColumnScheduledTasks
            .Where(task => task.Enabled && task.NextRunAt <= nowUtc)
            .OrderBy(task => task.NextRunAt).Take(20).ToListAsync();
        var claimed = new List<(ColumnScheduledTask, ColumnScheduledTaskRun)>();
        foreach (var task in due)
        {
            var active = await db.ColumnScheduledTaskRuns.AnyAsync(run =>
                run.TaskId == task.Id && run.Status == ColumnScheduledTaskRunStatus.Running);
            if (active) continue;
            var run = new ColumnScheduledTaskRun
            {
                Id = Guid.NewGuid().ToString("N"),
                TaskId = task.Id,
                StartedAt = nowUtc,
            };
            task.LastRunAt = nowUtc;
            task.LastStatus = "running";
            task.LastError = null;
            task.NextRunAt = ComputeNext(task.Cron, task.TimeZoneId, nowUtc);
            db.ColumnScheduledTaskRuns.Add(run);
            claimed.Add((task, run));
        }
        await db.SaveChangesAsync();
        return claimed;
    }

    public async Task<List<(ColumnScheduledTask Task, ColumnScheduledTaskRun Run)>> RecoverAsync(string projectSlug)
    {
        await SynchronizeAsync(projectSlug);
        return await RecoverAfterSynchronizationAsync(projectSlug);
    }

    public async Task<List<(ColumnScheduledTask Task, ColumnScheduledTaskRun Run)>> RecoverForBackgroundAsync(string projectSlug)
    {
        await SynchronizeAsync(projectSlug, tolerateInvalidFiles: true);
        return await RecoverAfterSynchronizationAsync(projectSlug);
    }

    private async Task<List<(ColumnScheduledTask Task, ColumnScheduledTaskRun Run)>> RecoverAfterSynchronizationAsync(
        string projectSlug)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTablesAsync(db);
        var running = await db.ColumnScheduledTaskRuns
            .Where(run => run.Status == ColumnScheduledTaskRunStatus.Running).ToListAsync();
        var tasks = await db.ColumnScheduledTasks.ToDictionaryAsync(task => task.Id);
        var resumable = new List<(ColumnScheduledTask, ColumnScheduledTaskRun)>();
        foreach (var run in running)
        {
            if (!tasks.TryGetValue(run.TaskId, out var task))
            {
                run.Status = ColumnScheduledTaskRunStatus.Cancelled;
                run.EndedAt = DateTime.UtcNow;
                run.Error = "La tâche planifiée n’existe plus.";
                continue;
            }
            if (string.IsNullOrWhiteSpace(run.CurrentActionId))
            {
                resumable.Add((task, run));
                continue;
            }
            run.Status = ColumnScheduledTaskRunStatus.Failed;
            run.EndedAt = DateTime.UtcNow;
            run.Error = $"L’action '{run.CurrentActionId}' a été interrompue ; son résultat externe est incertain et n’a pas été rejoué.";
            task.LastStatus = "failed";
            task.LastError = run.Error;
        }
        await db.SaveChangesAsync();
        return resumable;
    }

    public async Task BeginActionAsync(string projectSlug, ColumnScheduledTaskRun run, string actionId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTablesAsync(db);
        var row = await db.ColumnScheduledTaskRuns.FindAsync(run.Id)
            ?? throw new InvalidOperationException($"Le run planifié '{run.Id}' n’existe plus.");
        row.CurrentActionId = actionId;
        await db.SaveChangesAsync();
        run.CurrentActionId = actionId;
    }

    public async Task CompleteActionAsync(string projectSlug, ColumnScheduledTaskRun run, string actionId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTablesAsync(db);
        var row = await db.ColumnScheduledTaskRuns.FindAsync(run.Id)
            ?? throw new InvalidOperationException($"Le run planifié '{run.Id}' n’existe plus.");
        var completed = row.CompletedActionIds;
        if (!completed.Contains(actionId, StringComparer.OrdinalIgnoreCase)) completed.Add(actionId);
        row.CompletedActionIds = completed;
        row.CurrentActionId = null;
        await db.SaveChangesAsync();
        run.CompletedActionIds = completed;
        run.CurrentActionId = null;
    }

    public async Task FinishRunAsync(
        string projectSlug, ColumnScheduledTask task, ColumnScheduledTaskRun run, string? error)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTablesAsync(db);
        var runRow = await db.ColumnScheduledTaskRuns.FindAsync(run.Id);
        var taskRow = await db.ColumnScheduledTasks.FindAsync(task.Id);
        if (runRow is null) return;
        runRow.Status = error is null ? ColumnScheduledTaskRunStatus.Completed : ColumnScheduledTaskRunStatus.Failed;
        runRow.EndedAt = DateTime.UtcNow;
        runRow.CurrentActionId = null;
        runRow.Error = error;
        if (taskRow is not null)
        {
            taskRow.LastStatus = error is null ? "completed" : "failed";
            taskRow.LastError = error;
        }
        await db.SaveChangesAsync();
    }

    public async Task<string> GetDefinitionPathAsync(string projectSlug, int columnId)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Le projet '{projectSlug}' n’existe pas.");
        return Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "schedules", $"column-{columnId}", "tasks.json");
    }

    internal static DateTime ComputeNext(string cron, string timeZoneId, DateTime nowUtc)
    {
        var schedule = CrontabSchedule.Parse(cron);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        var nextLocal = DateTime.SpecifyKind(schedule.GetNextOccurrence(localNow), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(nextLocal, zone);
    }

    private async Task SynchronizeAsync(string projectSlug, bool tolerateInvalidFiles = false)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await using var db = projects.GetProjectDb(projectSlug);
            await ColumnService.EnsureBoardColumnsTableAsync(db);
            await EnsureTablesAsync(db);
            await SynchronizeLockedAsync(projectSlug, db, tolerateInvalidFiles);
        }
        finally { gate.Release(); }
    }

    private async Task SynchronizeLockedAsync(
        string projectSlug, TodoDbContext db, bool tolerateInvalidFiles = false)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Le projet '{projectSlug}' n’existe pas.");
        var root = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "schedules");
        Directory.CreateDirectory(root);
        var knownColumns = await db.BoardColumns.Select(column => column.Id).ToListAsync();
        var definedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var protectedColumns = new HashSet<int>();
        foreach (var path in Directory.EnumerateFiles(root, "tasks.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string? content = null;
            try
            {
                content = await File.ReadAllTextAsync(path);
                var file = JsonSerializer.Deserialize<ColumnTasksDefinition>(content, FileJson)
                    ?? throw new InvalidOperationException($"Le fichier de tâches '{path}' est vide.");
                if (file.Version != DefinitionVersion)
                    throw new InvalidOperationException($"Version de tâches planifiées non prise en charge dans '{path}'.");
                var columnId = ParseColumnReference(file.Column, path);
                ValidateDefinitions(file.Tasks, columnId, knownColumns);
                var fileIds = file.Tasks.Select(definition => definition.Id).ToList();
                var duplicate = fileIds.FirstOrDefault(id => definedIds.Contains(id));
                if (duplicate is not null)
                    throw new InvalidOperationException($"Identifiant de tâche planifiée dupliqué : {duplicate}.");
                foreach (var id in fileIds) definedIds.Add(id);
                foreach (var definition in file.Tasks)
                {
                    var row = await db.ColumnScheduledTasks.FindAsync(definition.Id);
                    var scheduleChanged = row is null
                        || !string.Equals(row.Cron, definition.Cron, StringComparison.Ordinal)
                        || !string.Equals(row.TimeZoneId, definition.TimeZoneId, StringComparison.OrdinalIgnoreCase);
                    row ??= new ColumnScheduledTask
                    {
                        Id = definition.Id,
                        ColumnId = columnId,
                        Name = definition.Name,
                        NextRunAt = ComputeNext(definition.Cron, definition.TimeZoneId, DateTime.UtcNow),
                    };
                    if (db.Entry(row).State == EntityState.Detached) db.ColumnScheduledTasks.Add(row);
                    row.ColumnId = columnId;
                    row.Name = definition.Name.Trim();
                    row.Enabled = definition.Enabled;
                    row.Cron = definition.Cron.Trim();
                    row.TimeZoneId = definition.TimeZoneId;
                    row.TicketScope = definition.TicketScope;
                    row.Actions = definition.Actions.Select(action => new ColumnProcessorAction(
                        action.Id, action.Action, ParseOptionalColumnReference(action.OnFailure))).ToList();
                    if (scheduleChanged) row.NextRunAt = ComputeNext(row.Cron, row.TimeZoneId, DateTime.UtcNow);
                    row.UpdatedAt = DateTime.UtcNow;
                }
                ReportedInvalidFiles.TryRemove(InvalidFileKey(projectSlug, path), out _);
            }
            catch (Exception ex) when (tolerateInvalidFiles && ex is not OperationCanceledException)
            {
                if (TryParseColumnDirectory(path, out var protectedColumn)) protectedColumns.Add(protectedColumn);
                ReportInvalidFileOnce(projectSlug, path, content, ex);
            }
        }
        var removed = await db.ColumnScheduledTasks
            .Where(task => !definedIds.Contains(task.Id) && !protectedColumns.Contains(task.ColumnId)).ToListAsync();
        db.ColumnScheduledTasks.RemoveRange(removed);
        await db.SaveChangesAsync();
    }

    private void ReportInvalidFileOnce(string projectSlug, string path, string? content, Exception error)
    {
        var fingerprintSource = content is null ? $"{error.GetType().FullName}:{error.Message}" : content;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)));
        var key = InvalidFileKey(projectSlug, path);
        if (!ReportedInvalidFiles.TryGetValue(key, out var previous) || previous != fingerprint)
        {
            ReportedInvalidFiles[key] = fingerprint;
            _logger.LogWarning(
                "Invalid scheduled task definition ignored for project {Project} at {Path}: {Cause}",
                projectSlug, path, error.Message);
        }
    }

    private static string InvalidFileKey(string projectSlug, string path) => $"{projectSlug}|{Path.GetFullPath(path)}";

    private static bool TryParseColumnDirectory(string path, out int columnId)
    {
        columnId = 0;
        var directory = Path.GetFileName(Path.GetDirectoryName(path));
        return directory is not null
            && directory.StartsWith("column-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(directory[7..], out columnId)
            && columnId > 0;
    }

    private async Task WriteColumnFileAsync(
        string projectSlug, int columnId, IReadOnlyList<ScheduledTaskDefinition> definitions)
    {
        var path = await GetDefinitionPathAsync(projectSlug, columnId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new ColumnTasksDefinition
        {
            Version = DefinitionVersion,
            Column = $"column-{columnId}",
            Tasks = definitions.ToList(),
        };
        await WriteDefinitionFileAsync(path, file);
    }

    private static async Task WriteDefinitionFileAsync(string path, ColumnTasksDefinition file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(file, FileJson) + Environment.NewLine);
        File.Move(temporary, path, true);
    }

    private static ScheduledTaskDefinition ToDefinition(ColumnScheduledTask task, int columnId) => new()
    {
        Id = string.IsNullOrWhiteSpace(task.Id) ? Guid.NewGuid().ToString("N") : task.Id.Trim(),
        Name = task.Name,
        Enabled = task.Enabled,
        Cron = task.Cron,
        TimeZoneId = string.IsNullOrWhiteSpace(task.TimeZoneId) ? TimeZoneInfo.Local.Id : task.TimeZoneId,
        TicketScope = task.TicketScope,
        Actions = task.Actions.Select(action => new ScheduledActionDefinition
        {
            Id = action.Id,
            Action = action.Action,
            OnFailure = action.FailureTargetColumnId is null ? null : $"column-{action.FailureTargetColumnId}",
        }).ToList(),
    };

    private static void ValidateDefinitions(
        IReadOnlyList<ScheduledTaskDefinition> definitions, int columnId, IReadOnlyCollection<int> knownColumns)
    {
        if (!knownColumns.Contains(columnId)) throw new InvalidOperationException($"La colonne #{columnId} n’existe pas.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in definitions)
        {
            task.Id = task.Id?.Trim() ?? "";
            task.Name = task.Name?.Trim() ?? "";
            task.Cron = task.Cron?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(task.Id) || !ids.Add(task.Id))
                throw new InvalidOperationException("Chaque tâche planifiée doit avoir un identifiant stable et unique.");
            if (string.IsNullOrWhiteSpace(task.Name)) throw new InvalidOperationException("Le nom de la tâche planifiée est requis.");
            _ = ComputeNext(task.Cron, task.TimeZoneId, DateTime.UtcNow);
            if (task.Actions.Count == 0) throw new InvalidOperationException($"La tâche '{task.Name}' ne contient aucune action.");
            var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in task.Actions)
            {
                action.Id = action.Id?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(action.Id) || !actionIds.Add(action.Id))
                    throw new InvalidOperationException($"Les actions de la tâche '{task.Name}' doivent avoir des identifiants uniques.");
                if (action.Action is not SetLabelsActionSpec and not AddCommentActionSpec and not CreateTicketActionSpec
                    and not ExecutePowerShellActionSpec and not HttpRequestActionSpec)
                    throw new InvalidOperationException($"Type d’action interdit dans une tâche planifiée : {action.Action.GetType().Name}.");
                if (task.TicketScope == ScheduledTaskTicketScope.None
                    && action.Action is SetLabelsActionSpec or AddCommentActionSpec)
                    throw new InvalidOperationException($"L’action {action.Action.UiTypeKey} requiert la cible « premier ticket de la colonne ».");
                if (action.OnFailure is null) continue;
                var target = ParseColumnReference(action.OnFailure, task.Name);
                if (target == columnId) throw new InvalidOperationException("Une action planifiée ne peut pas router un ticket vers sa propre colonne.");
                if (!knownColumns.Contains(target)) throw new InvalidOperationException($"Colonne d’échec inconnue : #{target}.");
            }
        }
    }

    private static int ParseColumnReference(string value, string source)
    {
        if (!value.StartsWith("column-", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(value[7..], out var id) || id < 1)
            throw new InvalidOperationException($"Référence de colonne invalide dans '{source}' : {value}.");
        return id;
    }

    private static int? ParseOptionalColumnReference(string? value) =>
        value is null ? null : ParseColumnReference(value, "onFailure");

    private sealed class ColumnTasksDefinition
    {
        public int Version { get; set; } = DefinitionVersion;
        public string Column { get; set; } = "";
        public List<ScheduledTaskDefinition> Tasks { get; set; } = [];
    }

    private sealed class ScheduledTaskDefinition
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Cron { get; set; } = "0 9 * * 1";
        public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
        public ScheduledTaskTicketScope TicketScope { get; set; }
        public List<ScheduledActionDefinition> Actions { get; set; } = [];
    }

    private sealed class ScheduledActionDefinition
    {
        public string Id { get; set; } = "";
        public ActionSpec Action { get; set; } = new ExecutePowerShellActionSpec();
        public string? OnFailure { get; set; }
    }
}
