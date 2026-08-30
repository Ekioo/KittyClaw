using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

/// <summary>
/// Manages project-owned column processor definitions. The versioned processor.json files are
/// authoritative; SQLite is only the runtime projection used by claims and execution history.
/// </summary>
public sealed class ColumnProcessorService(
    ProjectService projects,
    ProjectSkillService skills,
    ILogger<ColumnProcessorService>? logger = null,
    Lazy<DurableWriteRouter>? durableWrites = null)
{
    private const int DefinitionVersion = 2;
    private const int OldestSupportedDefinitionVersion = 1;
    private const string SourceMarkerName = ".source-of-truth-v1";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions DefinitionJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task EnsureTableAsync(TodoDbContext db)
    {
        await MigrationGate.RunOnceAsync(db, "column-processors-v1", static d =>
            d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS ColumnProcessors (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ColumnId INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    Mission TEXT NOT NULL,
                    Prompt TEXT NOT NULL DEFAULT '',
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
                    BeforeActionsJson TEXT NOT NULL DEFAULT '[]',
                    AfterActionsJson TEXT NOT NULL DEFAULT '[]',
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
        await MigrationGate.RunOnceAsync(db, "column-processors-prompt-v1", static d =>
            MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnProcessors ADD COLUMN Prompt TEXT NOT NULL DEFAULT ''"));
        await MigrationGate.RunOnceAsync(db, "column-processors-actions-v1", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnProcessors ADD COLUMN BeforeActionsJson TEXT NOT NULL DEFAULT '[]'");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnProcessors ADD COLUMN AfterActionsJson TEXT NOT NULL DEFAULT '[]'");
        });
    }

    public async Task<ColumnProcessor?> GetAsync(string projectSlug, int columnId)
    {
        var processors = await SynchronizeAsync(projectSlug);
        return processors.FirstOrDefault(p => p.ColumnId == columnId);
    }

    public async Task<List<ColumnProcessor>> ListAsync(string projectSlug) =>
        await SynchronizeAsync(projectSlug);

    public async Task<List<ColumnProcessor>> ListEnabledAsync(string projectSlug)
    {
        var enabled = (await ListAsync(projectSlug)).Where(p => p.Enabled).ToList();
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
        List<ColumnRoute>? routes = null, string? prompt = null,
        List<ColumnProcessorAction>? beforeActions = null,
        List<ColumnProcessorAction>? afterActions = null)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await using var db = projects.GetProjectDb(projectSlug);
            await ColumnService.EnsureBoardColumnsTableAsync(db);
            await EnsureTableAsync(db);
            await SynchronizeLockedAsync(projectSlug, db);

            var definition = new ProcessorDefinition
            {
                Version = DefinitionVersion,
                Column = ColumnReference(columnId),
                Name = name,
                Mission = mission,
                Prompt = prompt ?? "",
                Model = model,
                Enabled = enabled,
                MaxTurns = maxTurns,
                SelectionOrder = selectionOrder,
                MaxAttempts = maxAttempts,
                RetryBackoffSeconds = retryBackoffSeconds,
                AvailableSkills = availableSkills ?? [],
                RecommendedSkills = recommendedSkills ?? [],
                RequiredSkills = requiredSkills ?? [],
                BeforeActions = ToActionDefinitions(beforeActions),
                AfterActions = ToActionDefinitions(afterActions),
                Routing = new ProcessorRoutingDefinition
                {
                    Default = ColumnReference(defaultTargetColumnId),
                    TechnicalFailure = ColumnReference(technicalFailureColumnId),
                    Routes = (routes ?? []).Select(route => new ProcessorRouteDefinition
                    {
                        Outcome = route.Outcome,
                        Target = ColumnReference(route.TargetColumnId)!,
                    }).ToList(),
                },
            };
            var normalized = await ValidateAndNormalizeAsync(projectSlug, db, definition, columnId);

            // The file is the source of truth. If the projection write fails, the next read
            // deterministically repairs SQLite from this successfully written definition.
            await WriteDefinitionAsync(projectSlug, normalized);
            var processor = await UpsertProjectionAsync(db, normalized, columnId);
            await db.SaveChangesAsync();
            await EnsureMemoryAsync(projectSlug, processor);
            return processor;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string projectSlug, int columnId)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await using var db = projects.GetProjectDb(projectSlug);
            await ColumnService.EnsureBoardColumnsTableAsync(db);
            await EnsureTableAsync(db);
            await SynchronizeLockedAsync(projectSlug, db);
            var processor = await db.ColumnProcessors.FirstOrDefaultAsync(p => p.ColumnId == columnId);
            var path = await GetDefinitionPathAsync(projectSlug, columnId);
            if (File.Exists(path)) File.Delete(path);
            if (processor is null) return false;
            db.ColumnProcessors.Remove(processor);
            await db.SaveChangesAsync();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Removes an outgoing definition and every incoming file route before its column disappears.
    /// ColumnService then applies the equivalent runtime projection repair atomically with tickets.
    /// </summary>
    public async Task PrepareColumnDeletionAsync(string projectSlug, int columnId)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await using var db = projects.GetProjectDb(projectSlug);
            await ColumnService.EnsureBoardColumnsTableAsync(db);
            await EnsureTableAsync(db);
            await SynchronizeLockedAsync(projectSlug, db);

            var root = await GetProcessorsRootAsync(projectSlug);
            foreach (var path in Directory.EnumerateFiles(root, "processor.json", SearchOption.AllDirectories))
            {
                var definition = await ReadDefinitionAsync(path);
                var ownerId = ParseColumnReference(definition.Column, path, "column");
                if (ownerId == columnId)
                {
                    File.Delete(path);
                    continue;
                }

                var removed = ColumnReference(columnId);
                var changed = false;
                if (definition.Routing.Default == removed)
                {
                    definition.Routing.Default = null;
                    changed = true;
                }
                if (definition.Routing.TechnicalFailure == removed)
                {
                    definition.Routing.TechnicalFailure = null;
                    changed = true;
                }
                var remaining = definition.Routing.Routes.Where(route => route.Target != removed).ToList();
                if (remaining.Count != definition.Routing.Routes.Count)
                {
                    definition.Routing.Routes = remaining;
                    changed = true;
                }
                foreach (var action in definition.BeforeActions.Concat(definition.AfterActions))
                {
                    if (action.OnFailure != removed) continue;
                    action.OnFailure = null;
                    changed = true;
                }
                if (changed) await WriteDefinitionAsync(projectSlug, definition);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string?> GetMemoryIndexPathAsync(string projectSlug, int columnId)
    {
        var processor = await GetAsync(projectSlug, columnId);
        if (processor is null) return null;
        return await EnsureMemoryAsync(projectSlug, processor);
    }

    public async Task<string> GetDefinitionPathAsync(string projectSlug, int columnId)
    {
        var root = await GetProcessorsRootAsync(projectSlug);
        return Path.Combine(root, ColumnReference(columnId), "processor.json");
    }

    private async Task<List<ColumnProcessor>> SynchronizeAsync(string projectSlug)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await using var db = projects.GetProjectDb(projectSlug);
            await ColumnService.EnsureBoardColumnsTableAsync(db);
            await EnsureTableAsync(db);
            return await SynchronizeLockedAsync(projectSlug, db);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<ColumnProcessor>> SynchronizeLockedAsync(string projectSlug, TodoDbContext db)
    {
        var root = await GetProcessorsRootAsync(projectSlug);
        var marker = Path.Combine(root, SourceMarkerName);
        var existing = await db.ColumnProcessors.OrderBy(p => p.Id).ToListAsync();

        // One-time, lossless migration: project existing SQLite definitions to files before the
        // absence of a file acquires its new meaning (processor intentionally removed).
        if (!File.Exists(marker))
        {
            DurableWriteRoute? route = null;
            var project = await projects.GetProjectAsync(projectSlug)
                ?? throw new InvalidOperationException($"Le projet '{projectSlug}' n'existe pas.");
            if (durableWrites is not null && project.WorktreesEnabled &&
                !string.IsNullOrWhiteSpace(project.IntegrationBranch))
            {
                route = await durableWrites.Value.ResolveAsync(projectSlug, null, [".agents/processors"]);
                root = Path.Combine(route.RootPath, ".agents", "processors");
                Directory.CreateDirectory(root);
                marker = Path.Combine(root, SourceMarkerName);
            }

            foreach (var processor in existing)
            {
                var path = Path.Combine(root, ColumnReference(processor.ColumnId), "processor.json");
                if (!File.Exists(path)) await WriteDefinitionAtRootAsync(root, ToDefinition(processor));
            }
            if (!File.Exists(marker))
                await File.WriteAllTextAsync(marker, "processor.json is authoritative; SQLite stores only a runtime projection.\n");

            if (route is not null)
            {
                var validation = await durableWrites!.Value.CommitAndQueueAsync(projectSlug, route,
                    "chore: migrate processor definitions");
                if (validation.Status != DurableWriteValidationStatus.Ready)
                    throw new InvalidOperationException(validation.Error ??
                        $"Processor migration durable write requires review ({validation.Status}).");
            }
        }

        var files = Directory.EnumerateFiles(root, "processor.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var definedColumns = new HashSet<int>();
        foreach (var path in files)
        {
            var definition = await ReadDefinitionAsync(path);
            var columnId = ParseColumnReference(definition.Column, path, "column");
            var expectedDirectory = Path.Combine(root, ColumnReference(columnId));
            if (!Path.GetFullPath(Path.GetDirectoryName(path)!).Equals(Path.GetFullPath(expectedDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"La définition '{path}' doit se trouver dans '{expectedDirectory}'.");
            if (!definedColumns.Add(columnId))
                throw new InvalidOperationException($"Plusieurs processor.json ciblent {ColumnReference(columnId)}.");
            try
            {
                var normalized = await ValidateAndNormalizeAsync(projectSlug, db, definition, columnId);
                await UpsertProjectionAsync(db, normalized, columnId);
            }
            catch (InvalidOperationException error)
            {
                // A versioned definition must remain available for repair, but one invalid file
                // must not make the whole project board or processing loop unavailable.
                logger?.LogWarning(error,
                    "Processor definition {ProcessorPath} is invalid for project {ProjectSlug}; disabling its runtime projection",
                    path, projectSlug);
                var projection = existing.FirstOrDefault(processor => processor.ColumnId == columnId);
                if (projection is not null)
                {
                    projection.Enabled = false;
                    projection.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        foreach (var processor in existing.Where(p => !definedColumns.Contains(p.ColumnId)))
            db.ColumnProcessors.Remove(processor);
        await db.SaveChangesAsync();
        return await db.ColumnProcessors.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
    }

    private async Task<ProcessorDefinition> ValidateAndNormalizeAsync(
        string projectSlug, TodoDbContext db, ProcessorDefinition definition, int columnId)
    {
        if (definition.Version < OldestSupportedDefinitionVersion || definition.Version > DefinitionVersion)
            throw new InvalidOperationException($"Version processor.json non prise en charge pour {ColumnReference(columnId)} : {definition.Version}.");
        if (string.IsNullOrWhiteSpace(definition.Name)) throw new InvalidOperationException("Le nom du processeur est requis.");
        if (string.IsNullOrWhiteSpace(definition.Mission)) throw new InvalidOperationException("La mission du processeur est requise.");
        if (definition.MaxTurns < 1) throw new InvalidOperationException("MaxTurns doit être supérieur à zéro.");
        if (definition.MaxAttempts < 1) throw new InvalidOperationException("MaxAttempts doit être supérieur à zéro.");
        if (definition.RetryBackoffSeconds < 1) throw new InvalidOperationException("RetryBackoffSeconds doit être supérieur à zéro.");
        if (!await db.BoardColumns.AnyAsync(c => c.Id == columnId))
            throw new InvalidOperationException($"La colonne #{columnId} n'existe pas pour son processor.json.");

        definition.Name = definition.Name.Trim();
        definition.Mission = definition.Mission.Trim();
        definition.Prompt = definition.Prompt?.Trim() ?? "";
        definition.Model = string.IsNullOrWhiteSpace(definition.Model) ? null : definition.Model.Trim();
        definition.AvailableSkills = NormalizeStrings(definition.AvailableSkills);
        definition.RecommendedSkills = NormalizeStrings(definition.RecommendedSkills);
        definition.RequiredSkills = NormalizeStrings(definition.RequiredSkills);
        definition.BeforeActions ??= [];
        definition.AfterActions ??= [];
        definition.Routing ??= new ProcessorRoutingDefinition();
        definition.Routing.Routes ??= [];

        var knownSkills = (await skills.ListAsync(projectSlug)).Select(skill => skill.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referencedSkills = definition.AvailableSkills.Concat(definition.RecommendedSkills)
            .Concat(definition.RequiredSkills).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var unknownSkills = referencedSkills.Where(slug => !knownSkills.Contains(slug)).ToList();
        if (unknownSkills.Count > 0)
            throw new InvalidOperationException($"Skills projet inconnus : {string.Join(", ", unknownSkills)}.");

        var targets = new List<int>();
        if (definition.Routing.Default is not null)
            targets.Add(ParseColumnReference(definition.Routing.Default, ColumnReference(columnId), "routing.default"));
        if (definition.Routing.TechnicalFailure is not null)
            targets.Add(ParseColumnReference(definition.Routing.TechnicalFailure, ColumnReference(columnId), "routing.technicalFailure"));
        foreach (var route in definition.Routing.Routes)
        {
            route.Outcome = route.Outcome?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(route.Outcome))
                throw new InvalidOperationException($"Une route de {ColumnReference(columnId)} n'a pas d'outcome.");
            targets.Add(ParseColumnReference(route.Target, ColumnReference(columnId), $"route '{route.Outcome}'"));
        }
        var allActions = definition.BeforeActions.Concat(definition.AfterActions).ToList();
        var duplicateActionIds = allActions.Where(action => !string.IsNullOrWhiteSpace(action.Id))
            .GroupBy(action => action.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicateActionIds.Count > 0)
            throw new InvalidOperationException($"Identifiants d’actions dupliqués : {string.Join(", ", duplicateActionIds)}.");
        foreach (var action in allActions)
        {
            action.Id = action.Id?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(action.Id))
                throw new InvalidOperationException("Chaque action de processeur doit avoir un identifiant stable.");
            ValidateProcessorAction(action.Action, action.Id);
            if (action.OnFailure is not null)
                targets.Add(ParseColumnReference(action.OnFailure, ColumnReference(columnId), $"action '{action.Id}'.onFailure"));
        }
        var duplicateOutcomes = definition.Routing.Routes.GroupBy(route => route.Outcome, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicateOutcomes.Count > 0)
            throw new InvalidOperationException($"Outcomes dupliqués : {string.Join(", ", duplicateOutcomes)}.");
        if (targets.Contains(columnId))
            throw new InvalidOperationException("Une route ne peut pas renvoyer vers sa propre colonne. Utilisez la politique de nouvelle tentative pour rejouer un traitement.");
        var distinctTargets = targets.Distinct().ToList();
        var knownTargets = await db.BoardColumns.Where(column => distinctTargets.Contains(column.Id)).Select(column => column.Id).ToListAsync();
        var missingTargets = distinctTargets.Except(knownTargets).ToList();
        if (missingTargets.Count > 0)
            throw new InvalidOperationException($"Colonnes de routage inconnues : {string.Join(", ", missingTargets)}.");
        return definition;
    }

    private static async Task<ColumnProcessor> UpsertProjectionAsync(TodoDbContext db, ProcessorDefinition definition, int columnId)
    {
        var processor = await db.ColumnProcessors.FirstOrDefaultAsync(p => p.ColumnId == columnId);
        if (processor is null)
        {
            processor = new ColumnProcessor { ColumnId = columnId, Name = definition.Name };
            db.ColumnProcessors.Add(processor);
        }
        processor.Name = definition.Name;
        processor.Mission = definition.Mission;
        processor.Prompt = definition.Prompt;
        processor.Model = definition.Model;
        processor.Enabled = definition.Enabled;
        processor.MaxTurns = definition.MaxTurns;
        processor.SelectionOrder = definition.SelectionOrder;
        processor.MaxAttempts = definition.MaxAttempts;
        processor.RetryBackoffSeconds = definition.RetryBackoffSeconds;
        processor.DefaultTargetColumnId = ParseOptionalColumnReference(definition.Routing.Default);
        processor.TechnicalFailureColumnId = ParseOptionalColumnReference(definition.Routing.TechnicalFailure);
        processor.AvailableSkills = definition.AvailableSkills;
        processor.RecommendedSkills = definition.RecommendedSkills;
        processor.RequiredSkills = definition.RequiredSkills;
        processor.Routes = definition.Routing.Routes.Select(route =>
            new ColumnRoute(route.Outcome, ParseColumnReference(route.Target, definition.Column, $"route '{route.Outcome}'"))).ToList();
        processor.BeforeActions = FromActionDefinitions(definition.BeforeActions, definition.Column);
        processor.AfterActions = FromActionDefinitions(definition.AfterActions, definition.Column);
        processor.UpdatedAt = DateTime.UtcNow;
        return processor;
    }

    private async Task WriteDefinitionAsync(string projectSlug, ProcessorDefinition definition)
    {
        var root = await GetProcessorsRootAsync(projectSlug);
        await WriteDefinitionAtRootAsync(root, definition);
    }

    private static async Task WriteDefinitionAtRootAsync(string root, ProcessorDefinition definition)
    {
        var columnId = ParseColumnReference(definition.Column, "processor.json", "column");
        var path = Path.Combine(root, ColumnReference(columnId), "processor.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(definition, DefinitionJson) + Environment.NewLine;
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, json);
        File.Move(temporary, path, true);
    }

    private static async Task<ProcessorDefinition> ReadDefinitionAsync(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ProcessorDefinition>(await File.ReadAllTextAsync(path), DefinitionJson)
                ?? throw new InvalidOperationException($"La définition '{path}' est vide.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Le fichier processeur '{path}' est invalide : {ex.Message}", ex);
        }
    }

    private async Task<string> GetProcessorsRootAsync(string projectSlug)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Le projet '{projectSlug}' n'existe pas.");
        var root = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "processors");
        Directory.CreateDirectory(root);
        return root;
    }

    private async Task<string> EnsureMemoryAsync(string projectSlug, ColumnProcessor processor)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Le projet '{projectSlug}' n'existe pas.");
        var workspace = projects.ResolveWorkspacePath(project);
        var relativeMemoryDir = Path.Combine(".agents", "processors", ColumnReference(processor.ColumnId), "memory");
        var primaryIndex = Path.Combine(workspace, relativeMemoryDir, "MEMORY.md");
        DurableWriteRoute? route = null;
        var root = workspace;
        if (durableWrites is not null && project.WorktreesEnabled &&
            !string.IsNullOrWhiteSpace(project.IntegrationBranch) &&
            await MemoryRequiresWriteAsync(workspace, processor.ColumnId, primaryIndex))
        {
            route = await durableWrites.Value.ResolveAsync(projectSlug, null, [relativeMemoryDir]);
            root = route.RootPath;
        }

        var memoryDir = Path.Combine(root, relativeMemoryDir);
        var index = Path.Combine(memoryDir, "MEMORY.md");
        try
        {
            Directory.CreateDirectory(memoryDir);
            if (!File.Exists(index))
                await File.WriteAllTextAsync(index, $"# {processor.Name} memory\n\nPersistent lessons for column #{processor.ColumnId}.\n");
            await MigrateLegacyMemoryAsync(workspace, processor.ColumnId, index);
        }
        catch
        {
            if (route is not null)
                await durableWrites!.Value.PreserveExecutionAsync(projectSlug, route,
                    $"Processor memory migration failed for column {processor.ColumnId}.");
            throw;
        }

        if (route is not null)
        {
            var validation = await durableWrites!.Value.CommitAndQueueAsync(projectSlug, route,
                $"chore: migrate column {processor.ColumnId} memory");
            if (validation.Status != DurableWriteValidationStatus.Ready)
                throw new InvalidOperationException(validation.Error ??
                    $"Processor memory migration durable write requires review ({validation.Status}).");
        }

        return index;
    }

    private static async Task<bool> MemoryRequiresWriteAsync(string workspace, int columnId, string canonicalPath)
    {
        if (!File.Exists(canonicalPath)) return true;
        var legacyPath = Path.Combine(workspace, ".agents", $"column-{columnId}", "memory.md");
        if (!File.Exists(legacyPath)) return false;
        var canonical = await File.ReadAllTextAsync(canonicalPath);
        return (await File.ReadAllLinesAsync(legacyPath))
            .Select(line => line.TrimEnd())
            .Any(line => line.StartsWith("- ", StringComparison.Ordinal) &&
                !canonical.Contains(line, StringComparison.Ordinal));
    }

    private static async Task MigrateLegacyMemoryAsync(string workspace, int columnId, string canonicalPath)
    {
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

    private static ProcessorDefinition ToDefinition(ColumnProcessor processor) => new()
    {
        Version = DefinitionVersion,
        Column = ColumnReference(processor.ColumnId),
        Name = processor.Name,
        Mission = processor.Mission,
        Prompt = processor.Prompt,
        Model = processor.Model,
        Enabled = processor.Enabled,
        MaxTurns = processor.MaxTurns,
        SelectionOrder = processor.SelectionOrder,
        MaxAttempts = processor.MaxAttempts,
        RetryBackoffSeconds = processor.RetryBackoffSeconds,
        AvailableSkills = processor.AvailableSkills,
        RecommendedSkills = processor.RecommendedSkills,
        RequiredSkills = processor.RequiredSkills,
        BeforeActions = ToActionDefinitions(processor.BeforeActions),
        AfterActions = ToActionDefinitions(processor.AfterActions),
        Routing = new ProcessorRoutingDefinition
        {
            Default = ColumnReference(processor.DefaultTargetColumnId),
            TechnicalFailure = ColumnReference(processor.TechnicalFailureColumnId),
            Routes = processor.Routes.Select(route => new ProcessorRouteDefinition
            {
                Outcome = route.Outcome,
                Target = ColumnReference(route.TargetColumnId)!,
            }).ToList(),
        },
    };

    private static List<string> NormalizeStrings(IEnumerable<string>? values) =>
        (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();

    private static List<ProcessorActionDefinition> ToActionDefinitions(IEnumerable<ColumnProcessorAction>? actions) =>
        (actions ?? []).Select(action => new ProcessorActionDefinition
        {
            Id = action.Id,
            Action = action.Action,
            OnFailure = ColumnReference(action.FailureTargetColumnId),
        }).ToList();

    private static List<ColumnProcessorAction> FromActionDefinitions(
        IEnumerable<ProcessorActionDefinition> actions, string source) =>
        actions.Select(action => new ColumnProcessorAction(
            action.Id,
            action.Action,
            action.OnFailure is null ? null : ParseColumnReference(action.OnFailure, source, $"action '{action.Id}'.onFailure"))).ToList();

    private static void ValidateProcessorAction(ActionSpec? action, string id)
    {
        if (action is not SetLabelsActionSpec and not AddCommentActionSpec and not CreateTicketActionSpec
            and not ExecutePowerShellActionSpec and not HttpRequestActionSpec)
            throw new InvalidOperationException(
                $"Type d’action non pris en charge dans un processeur ({id}) : {action?.GetType().Name ?? "null"}. " +
                "L’agent et le routage restent uniques au niveau de la colonne.");
        switch (action)
        {
            case ExecutePowerShellActionSpec script when string.IsNullOrWhiteSpace(script.Script) && string.IsNullOrWhiteSpace(script.ScriptFile):
                throw new InvalidOperationException($"L’action PowerShell '{id}' doit définir un script ou un fichier.");
            case HttpRequestActionSpec request when string.IsNullOrWhiteSpace(request.Url):
                throw new InvalidOperationException($"L’action HTTP '{id}' doit définir une URL.");
            case AddCommentActionSpec comment when string.IsNullOrWhiteSpace(comment.Content):
                throw new InvalidOperationException($"L’action commentaire '{id}' doit définir un contenu.");
            case CreateTicketActionSpec create when string.IsNullOrWhiteSpace(create.Title):
                throw new InvalidOperationException($"L’action de création de ticket '{id}' doit définir un titre.");
        }
    }

    private static string ColumnReference(int columnId) => $"column-{columnId}";
    private static string? ColumnReference(int? columnId) => columnId is null ? null : ColumnReference(columnId.Value);
    private static int? ParseOptionalColumnReference(string? value) => value is null ? null : ParseColumnReference(value, "processor.json", "routing");

    private static int ParseColumnReference(string? value, string source, string field)
    {
        if (value is null || !value.StartsWith("column-", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(value[7..], out var id) || id < 1)
            throw new InvalidOperationException($"Référence de colonne invalide dans {source} ({field}) : '{value}'. Utilisez 'column-<id>'.");
        return id;
    }

    private sealed class ProcessorDefinition
    {
        public int Version { get; set; } = DefinitionVersion;
        public string Column { get; set; } = "";
        public string Name { get; set; } = "";
        public string Mission { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string? Model { get; set; }
        public bool Enabled { get; set; } = true;
        public int MaxTurns { get; set; } = 100;
        public TicketSelectionOrder SelectionOrder { get; set; } = TicketSelectionOrder.Position;
        public int MaxAttempts { get; set; } = 3;
        public int RetryBackoffSeconds { get; set; } = 60;
        public List<string> AvailableSkills { get; set; } = [];
        public List<string> RecommendedSkills { get; set; } = [];
        public List<string> RequiredSkills { get; set; } = [];
        public List<ProcessorActionDefinition> BeforeActions { get; set; } = [];
        public List<ProcessorActionDefinition> AfterActions { get; set; } = [];
        public ProcessorRoutingDefinition Routing { get; set; } = new();
    }

    private sealed class ProcessorRoutingDefinition
    {
        public string? Default { get; set; }
        public string? TechnicalFailure { get; set; }
        public List<ProcessorRouteDefinition> Routes { get; set; } = [];
    }

    private sealed class ProcessorRouteDefinition
    {
        public string Outcome { get; set; } = "";
        public string Target { get; set; } = "";
    }

    private sealed class ProcessorActionDefinition
    {
        public string Id { get; set; } = "";
        public ActionSpec Action { get; set; } = new SetLabelsActionSpec();
        public string? OnFailure { get; set; }
    }
}
