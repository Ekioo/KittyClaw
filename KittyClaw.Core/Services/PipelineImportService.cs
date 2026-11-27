using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

/// <summary>
/// Analyzes and installs untrusted ".kittyclaw-pipeline" kits. Analysis validates the ZIP
/// container, manifest version, structure, inventory and SHA-256 hashes without writing
/// anything, and blocks Zip Slip, absolute or traversing paths, symlinks, executables,
/// nested archives, duplicate paths and size/ratio/depth/count overruns. Installation is a
/// logical atomic transaction with compensating rollback: a failure leaves the project
/// strictly unchanged. Kit content is never executed during analysis or installation.
/// </summary>
public sealed partial class PipelineImportService(
    ProjectService projects,
    PipelineService pipelines,
    ColumnService columns,
    ColumnProcessorService processors,
    ProjectSecretVault vault, JevConfigurationService? jevConfiguration = null)
{
    // Documented kit limits, aligned with the export side (PipelineExportService).
    private const long MaxArchiveBytes = 8 * 1024 * 1024;
    private const int MaxEntries = 500;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const long MaxTotalBytes = 16 * 1024 * 1024;
    private const int MaxPathDepth = 8;
    private const int MaxPathLength = 240;
    private const int MaxColumns = 50;
    private const int MaxCompressionRatio = 100;
    private const long RatioEnforcementThreshold = 1024 * 1024;

    public const string EmbeddedScriptsApproval = "embeddedScripts";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallGates = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions KitJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // Parity with the export policy: what may never leave a project may never enter one either.
    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".msi", ".com", ".scr",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".jar", PipelineKitFormat.FileExtension,
    };

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".psm1", ".psd1", ".sh", ".bat", ".cmd", ".py", ".js", ".ts",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".yaml", ".yml", ".ps1", ".psm1", ".psd1", ".sh", ".py", ".js", ".ts",
        ".csv", ".xml", ".html", ".htm", ".css", ".toml", ".ini", ".cfg", ".sql", ".cs", ".razor", ".bat", ".cmd",
    };

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Test seam: throws mid-installation to prove the compensating rollback.</summary>
    internal Func<string, Task>? InstallFaultInjector { get; set; }

    /// <summary>Imports root-level workspace kits during project creation, retaining all activation gates.</summary>
    public async Task<List<string>> ImportWorkspaceAsync(string projectSlug)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException("Project not found.");
        var workspace = projects.ResolveWorkspacePath(project);
        var notices = new List<string>();
        string[] files;
        try
        {
            files = Directory.GetFiles(workspace, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).Equals(PipelineKitFormat.FileExtension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [ex.Message];
        }

        foreach (var file in files)
        {
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaxArchiveBytes)
                    throw new InvalidOperationException("Pipeline archive exceeds the 8 MiB limit.");
                var archive = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(archive);
                var preview = await AnalyzeAsync(projectSlug, archive);
                var confirmation = new PipelineImportConfirmation();
                if (preview is not null && preview.Installable)
                {
                    var names = (await pipelines.ListAsync(projectSlug)).Select(p => p.Name.Trim())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var name = preview.PipelineName;
                    for (var suffix = 2; names.Contains(name); suffix++)
                        name = $"{preview.PipelineName} ({suffix})";
                    confirmation.PipelineName = name;
                }
                var result = await InstallAsync(projectSlug, archive, confirmation);
                if (result is not null && !result.Enabled)
                    notices.Add($"{Path.GetFileName(file)}: {string.Join(", ", result.DisabledReasons)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or PipelineImportRejectedException or PipelineImportConflictException)
            {
                notices.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return notices;
    }

    /// <summary>Write-free analysis. Null when the project does not exist. Never throws for kit defects:
    /// they are reported as <see cref="PipelineImportPreview.Blockages"/>.</summary>
    public async Task<PipelineImportPreview?> AnalyzeAsync(string projectSlug, byte[] archive)
    {
        var project = await projects.GetProjectAsync(projectSlug);
        if (project is null) return null;

        var (kit, issues) = Parse(archive);
        var preview = new PipelineImportPreview { Blockages = issues };
        if (kit is null) return preview;

        preview.JevRequired = kit.Pipeline.Columns.Any(c => c.Processor?.RequiresJev == true);
        preview.JevCredentialAvailable = await HasJevCredentialAsync(projectSlug);
        preview.FormatVersion = kit.Manifest.FormatVersion;
        preview.PipelineName = kit.Pipeline.Name.Trim();
        preview.PipelineSlug = kit.Pipeline.Slug;
        preview.Files = kit.Files.Select(file => new PipelineImportFile(
            file.Key, file.Value.LongLength,
            Convert.ToHexString(SHA256.HashData(file.Value)).ToLowerInvariant(), Verified: true)).ToList();
        preview.Columns = kit.Pipeline.Columns.Select(column => new PipelineImportColumn(
            column.Key, column.Name, column.Role,
            column.Processor?.Name, column.Processor?.Model, column.Processor?.Enabled ?? false,
            (column.Processor is null
                ? Enumerable.Empty<PortableProcessorAction>()
                : column.Processor.BeforeActions.Concat(column.Processor.AfterActions))
                .Concat(column.ScheduledTasks.SelectMany(task => task.Actions))
                .Select(action => action.Action.UiTypeKey).Distinct(StringComparer.Ordinal).ToList())).ToList();
        preview.Parameters = kit.Parameters.Select(p => new PipelineImportPlaceholder(p.Name, p.Occurrences)).ToList();
        preview.Models = kit.Models.Select(model => new PipelineImportRequirement(
            model, ClaudeModelCatalog.Models.Contains(model, StringComparer.OrdinalIgnoreCase))).ToList();
        preview.EmbeddedScripts = kit.ScriptFiles;
        preview.RequiredApprovals = ComputeRequiredApprovals(kit);

        var workspace = projects.ResolveWorkspacePath(project);
        var skillsRoot = Path.Combine(workspace, ".agents", "skills");
        if (kit.ReferencedSkills.Count != 0 && ProjectSkillService.HasLinkedRoot(skillsRoot))
        {
            preview.Blockages.Add(LinkedSkillsRootIssue());
            return preview;
        }
        preview.Agents = kit.Agents.Select(agent => new PipelineImportRequirement(
            agent, File.Exists(Path.Combine(workspace, ".agents", agent, "SKILL.md")))).ToList();

        var vaultNames = await ListVaultSecretNamesAsync(projectSlug);
        preview.Secrets = kit.Secrets.Select(secret => new PipelineImportSecret(
            secret.Name, secret.Occurrences, vaultNames.Contains(secret.Name))).ToList();

        var existingPipelines = await pipelines.ListAsync(projectSlug);
        preview.PipelineNameConflict = existingPipelines.Any(p =>
            string.Equals(p.Name.Trim(), preview.PipelineName, StringComparison.OrdinalIgnoreCase));

        foreach (var slug in kit.ReferencedSkills)
        {
            if (kit.SkillFolders.TryGetValue(slug, out var folder))
            {
                var fingerprint = FingerprintKitSkill(folder);
                var resolution = ResolveSkillCollision(skillsRoot, slug, fingerprint);
                var name = ReadKitSkillName(folder) ?? slug;
                preview.Skills.Add(new PipelineImportSkill(
                    slug, name, Embedded: true, fingerprint, folder.Count, resolution, Available: true));
                if (resolution == PipelineImportSkillResolution.ReuseIdentical)
                    preview.Creation.SkillsToReuse.Add(slug);
                else
                    preview.Creation.SkillsToCreate.Add(slug);
            }
            else
            {
                // Declared-only prerequisite: it must already exist because a processor cannot
                // reference an unknown project skill.
                var available = ProjectSkillService.IsAvailableSkill(Path.Combine(skillsRoot, slug));
                preview.Skills.Add(new PipelineImportSkill(
                    slug, slug, Embedded: false, Fingerprint: null, FileCount: 0, Resolution: null, available));
                if (!available)
                    preview.Blockages.Add(new PipelineImportIssue(
                        "missing-skill", $"{PipelineKitFormat.SkillsFolderName}/{slug}",
                        $"La skill projet '{slug}' est requise mais absente du projet et non embarquée dans le kit."));
            }
        }

        preview.Creation.ColumnsToCreate = kit.Pipeline.Columns.Count;
        preview.Creation.ProcessorsToCreate = kit.Pipeline.Columns.Count(column => column.Processor is not null);
        preview.Installable = preview.Blockages.Count == 0;
        return preview;
    }

    /// <summary>
    /// Installs the kit as a new pipeline and its skills in one logical atomic transaction.
    /// Null when the project does not exist.
    /// </summary>
    /// <exception cref="PipelineImportRejectedException">The archive is invalid, hostile or tampered with. Nothing was written.</exception>
    /// <exception cref="PipelineImportConflictException">Unresolved name/slug collisions. Nothing was written.</exception>
    public async Task<PipelineImportResult?> InstallAsync(
        string projectSlug, byte[] archive, PipelineImportConfirmation confirmation)
    {
        var gate = InstallGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await InstallLockedAsync(projectSlug, archive, confirmation);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PipelineImportResult?> InstallLockedAsync(
        string projectSlug, byte[] archive, PipelineImportConfirmation confirmation)
    {
        var project = await projects.GetProjectAsync(projectSlug);
        if (project is null) return null;

        var (kit, issues) = Parse(archive);
        if (kit is null || issues.Count > 0)
            throw new PipelineImportRejectedException(issues);

        var workspace = projects.ResolveWorkspacePath(project);
        var skillsRoot = Path.Combine(workspace, ".agents", "skills");
        if (kit.ReferencedSkills.Count != 0 && ProjectSkillService.HasLinkedRoot(skillsRoot))
            throw new PipelineImportRejectedException([LinkedSkillsRootIssue()]);

        // --- Collision resolution: rename or cancel, never overwrite. ---
        var conflicts = new List<PipelineImportIssue>();
        var finalName = string.IsNullOrWhiteSpace(confirmation.PipelineName)
            ? kit.Pipeline.Name.Trim()
            : confirmation.PipelineName.Trim();
        var existingPipelines = await pipelines.ListAsync(projectSlug);
        if (existingPipelines.Any(p => string.Equals(p.Name.Trim(), finalName, StringComparison.OrdinalIgnoreCase)))
            conflicts.Add(new PipelineImportIssue(
                "pipeline-name-conflict", PipelineKitFormat.PipelineEntryName,
                $"Un pipeline nommé '{finalName}' existe déjà : fournissez un autre nom ou annulez l’import."));

        var slugMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var skillsToCreate = new List<string>();
        var skillsToReuse = new List<string>();
        foreach (var (slug, folder) in kit.SkillFolders)
        {
            var fingerprint = FingerprintKitSkill(folder);
            var resolution = ResolveSkillCollision(skillsRoot, slug, fingerprint);
            if (resolution == PipelineImportSkillResolution.ReuseIdentical)
            {
                slugMap[slug] = slug;
                skillsToReuse.Add(slug);
                continue;
            }
            var rename = confirmation.SkillRenames.TryGetValue(slug, out var requested) ? requested.Trim() : null;
            var finalSlug = string.IsNullOrWhiteSpace(rename) ? slug : rename;
            if (resolution == PipelineImportSkillResolution.RenameRequired && finalSlug == slug)
            {
                conflicts.Add(new PipelineImportIssue(
                    "skill-conflict", $"{PipelineKitFormat.SkillsFolderName}/{slug}",
                    $"La skill '{slug}' existe déjà avec un contenu différent : fournissez un renommage ou annulez. Aucun écrasement possible."));
                continue;
            }
            if (!SkillSlugPattern().IsMatch(finalSlug))
            {
                conflicts.Add(new PipelineImportIssue(
                    "invalid-skill-rename", $"{PipelineKitFormat.SkillsFolderName}/{slug}",
                    $"Le renommage '{finalSlug}' n’est pas un identifiant de skill valide."));
                continue;
            }
            var destination = Path.Combine(skillsRoot, finalSlug);
            if (ProjectSkillService.IsReparsePoint(destination) || Directory.Exists(destination) || slugMap.ContainsValue(finalSlug))
            {
                conflicts.Add(new PipelineImportIssue(
                    "skill-rename-collision", $"{PipelineKitFormat.SkillsFolderName}/{slug}",
                    $"Le renommage '{finalSlug}' entre en collision avec une skill existante ou un autre renommage."));
                continue;
            }
            slugMap[slug] = finalSlug;
            skillsToCreate.Add(finalSlug);
        }
        if (conflicts.Count > 0)
            throw new PipelineImportConflictException(conflicts);

        // --- Gates: missing prerequisite, parameter, secret or approval keeps the install disabled. ---
        var disabledReasons = new List<string>();
        foreach (var approval in ComputeRequiredApprovals(kit))
            if (!confirmation.Approvals.Contains(approval, StringComparer.OrdinalIgnoreCase))
                disabledReasons.Add($"approval:{approval}");
        var parameterValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in kit.Parameters)
        {
            if (confirmation.Parameters.TryGetValue(parameter.Name, out var value) && !string.IsNullOrEmpty(value))
                parameterValues[parameter.Name] = value;
            else
                disabledReasons.Add($"parameter:{parameter.Name}");
        }
        var vaultNames = await ListVaultSecretNamesAsync(projectSlug);
        var secretBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var secret in kit.Secrets)
        {
            var binding = confirmation.SecretBindings.TryGetValue(secret.Name, out var bound) ? bound.Trim() : null;
            if (!string.IsNullOrWhiteSpace(binding))
            {
                if (!PlaceholderNamePattern().IsMatch(binding))
                    throw new InvalidOperationException($"L’association de secret '{binding}' n’est pas un nom de secret valide.");
                if (!vaultNames.Contains(binding))
                    throw new InvalidOperationException($"Le secret '{binding}' associé à '{secret.Name}' n’existe pas dans le vault du projet.");
                secretBindings[secret.Name] = binding;
            }
            else if (vaultNames.Contains(secret.Name))
            {
                secretBindings[secret.Name] = secret.Name;
            }
            else
            {
                disabledReasons.Add($"secret:{secret.Name}");
            }
        }
        foreach (var model in kit.Models)
            if (!ClaudeModelCatalog.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
                disabledReasons.Add($"model:{model}");
        foreach (var agent in kit.Agents)
            if (!File.Exists(Path.Combine(workspace, ".agents", agent, "SKILL.md")))
                disabledReasons.Add($"agent:{agent}");
        var unavailableDeclaredSkills = kit.ReferencedSkills
            .Where(skill => !kit.SkillFolders.ContainsKey(skill)
                && !ProjectSkillService.IsAvailableSkill(Path.Combine(skillsRoot, skill)))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var skill in unavailableDeclaredSkills)
            disabledReasons.Add($"skill:{skill}");
        foreach (var program in kit.Pipeline.Columns.SelectMany(column =>
            (column.Processor is null ? Enumerable.Empty<PortableProcessorAction>()
                : column.Processor.BeforeActions.Concat(column.Processor.AfterActions))
            .Concat(column.ScheduledTasks.SelectMany(task => task.Actions)))
            .Select(action => action.Action).OfType<ExecuteProgramActionSpec>())
        {
            if (!ProgramActionSupport.IsAvailable(program, workspace))
                disabledReasons.Add($"runtime:{program.UiTypeKey}:{program.Program}");
        }
        if (kit.Pipeline.Columns.Any(c => c.Processor?.RequiresJev == true) && !await HasJevCredentialAsync(projectSlug))
            disabledReasons.Add("secret:" + JevQualificationService.SecretName);
        var enabled = disabledReasons.Count == 0;

        // --- Placeholder substitution and logical-key remapping, all in memory. ---
        var pipelineJson = Substitute(
            JsonSerializer.Serialize(kit.Pipeline, KitJson), parameterValues, secretBindings, jsonEncode: true);
        var portable = JsonSerializer.Deserialize<PortablePipeline>(pipelineJson, KitJson)!;

        List<string> MapSkillSlugs(IEnumerable<string> slugs) =>
            slugs.Where(slug => !unavailableDeclaredSkills.Contains(slug))
                .Select(slug => slugMap.GetValueOrDefault(slug, slug)).ToList();

        // --- Logical atomic transaction: journal every created artifact, compensate on failure. ---
        var processorRoot = Path.Combine(workspace, ".agents", "processors");
        var processorMarker = Path.Combine(processorRoot, ".source-of-truth-v1");
        var journal = new InstallJournal
        {
            ProcessorRootExisted = Directory.Exists(processorRoot),
            ProcessorMarkerExisted = File.Exists(processorMarker),
            ProcessorRoot = processorRoot,
            ProcessorMarker = processorMarker,
        };
        try
        {
            var pipeline = await pipelines.CreateAsync(projectSlug, finalName);
            journal.PipelineId = pipeline.Id;
            await FaultAsync("pipeline-created");

            var columnIds = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var column in portable.Columns)
            {
                var created = await columns.CreateColumnAsync(
                    projectSlug, column.Name.Trim(),
                    string.IsNullOrWhiteSpace(column.Color) ? "#5a6a80" : column.Color,
                    pipeline.Id, column.Role, userGuidance: column.UserGuidance ?? "",
                    maxTickets: column.MaxTickets, capacityBlocking: column.CapacityBlocking, requireAcceptanceCriteria: column.RequireAcceptanceCriteria, requireValidatedAcceptanceCriteria: column.RequireValidatedAcceptanceCriteria);
                columnIds[column.Key] = created.Id;
            }
            await FaultAsync("columns-created");

            foreach (var (kitSlug, finalSlug) in slugMap)
            {
                if (skillsToReuse.Contains(kitSlug)) continue;
                var targetDirectory = Path.GetFullPath(Path.Combine(skillsRoot, finalSlug));
                journal.SkillDirectories.Add(targetDirectory);
                foreach (var (relative, content) in kit.SkillFolders[kitSlug])
                {
                    var destination = Path.GetFullPath(Path.Combine(targetDirectory, relative));
                    // Defense in depth: Parse() already rejected traversing paths.
                    if (!destination.StartsWith(targetDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Chemin de skill hors dossier : {relative}");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var bytes = IsTextContent(Path.GetExtension(relative), content)
                        ? Encoding.UTF8.GetBytes(Substitute(
                            Encoding.UTF8.GetString(content), parameterValues, secretBindings, jsonEncode: false))
                        : content;
                    await File.WriteAllBytesAsync(destination, bytes);
                }
            }
            await FaultAsync("skills-installed");

            ActionSpec MapAction(PortableProcessorAction action)
            {
                if (action.Action is not CreateTicketActionSpec) return action.Action;
                var copy = JsonSerializer.Deserialize<CreateTicketActionSpec>(JsonSerializer.Serialize((CreateTicketActionSpec)action.Action, KitJson), KitJson)!;
                var destination = action.CreateTarget is { } key
                    ? portable.Columns.Single(c => c.Key == key)
                    : portable.Columns.Single(c => string.Equals(c.Name.Trim(), copy.Status.Trim(), StringComparison.OrdinalIgnoreCase));
                copy.Status = destination.Name.Trim();
                return copy;
            }

            foreach (var column in portable.Columns)
            {
                if (column.Processor is not { } processor) continue;
                var columnId = columnIds[column.Key];
                journal.ProcessorColumnIds.Add(columnId);

                int? MapColumn(string? key) => key is null ? null : columnIds[key];
                List<ColumnProcessorAction> MapActions(IEnumerable<PortableProcessorAction> actions) =>
                    actions.Select(action => new ColumnProcessorAction(
                        action.Id, MapAction(action), MapColumn(action.OnFailure))).ToList();

                await processors.SaveAsync(
                    projectSlug, columnId, processor.Name, processor.Mission, processor.Model,
                    enabled: enabled && processor.Enabled, processor.MaxTurns,
                    MapSkillSlugs(processor.AvailableSkills),
                    MapSkillSlugs(processor.RecommendedSkills),
                    MapSkillSlugs(processor.RequiredSkills),
                    processor.SelectionOrder, processor.MaxAttempts, processor.RetryBackoffSeconds,
                    MapColumn(processor.Routing.Default), MapColumn(processor.Routing.TechnicalFailure),
                    processor.Routing.Routes.Select(route => new ColumnRoute(route.Outcome, columnIds[route.Target])).ToList(),
                    processor.Prompt,
                    MapActions(processor.BeforeActions), MapActions(processor.AfterActions),
                    evaluateAcceptanceCriteria: processor.EvaluateAcceptanceCriteria,
                    jevFallbackToAgent: processor.JevFallbackToAgent,
                    jevObservationOnly: processor.JevObservationOnly,
                    jevAutomaticRouting: processor.JevAutomaticRouting,
                    jevPostRunObservation: processor.JevPostRunObservation,
                    jevAcceptanceValidation: processor.JevAcceptanceValidation,
                    jevExecutionSelection: processor.JevExecutionSelection,
                    jevConfidenceThreshold: processor.JevConfidenceThreshold,
                    jevAcceptanceCriteria: processor.JevAcceptanceCriteria,
                    jevExecutionProfiles: processor.JevExecutionProfiles.Select(p => p with { Skills = MapSkillSlugs(p.Skills) }).ToList());
            }
            await FaultAsync("processors-installed");

            var schedules = new ColumnScheduledTaskService(projects);
            foreach (var column in portable.Columns.Where(column => column.ScheduledTasks.Count > 0))
            {
                var columnId = columnIds[column.Key];
                journal.ScheduledColumnIds.Add(columnId);
                await schedules.SaveColumnAsync(projectSlug, columnId, column.ScheduledTasks.Select(task =>
                    new ColumnScheduledTask
                    {
                        Id = Guid.NewGuid().ToString("N"), ColumnId = columnId, Name = task.Name,
                        Enabled = enabled && task.Enabled, Cron = task.Cron, TimeZoneId = task.TimeZoneId,
                        TicketScope = task.TicketScope,
                        Actions = task.Actions.Select(action => new ColumnProcessorAction(
                            action.Id, MapAction(action),
                            action.OnFailure is null ? null : columnIds[action.OnFailure])).ToList(),
                    }).ToList());
            }
            await FaultAsync("schedules-installed");

            return new PipelineImportResult
            {
                PipelineId = pipeline.Id,
                PipelineName = pipeline.Name,
                PipelineSlug = pipeline.Slug,
                Enabled = enabled,
                DisabledReasons = disabledReasons,
                Columns = portable.Columns.Select(column => new PipelineImportInstalledColumn(
                    column.Key, columnIds[column.Key], column.Name.Trim())).ToList(),
                SkillsCreated = skillsToCreate,
                SkillsReused = skillsToReuse,
            };
        }
        catch
        {
            await RollbackAsync(projectSlug, journal);
            throw;
        }
    }

    private Task FaultAsync(string step) =>
        InstallFaultInjector is null ? Task.CompletedTask : InstallFaultInjector(step);

    /// <summary>Compensating rollback: removes every journaled artifact, newest first.</summary>
    private async Task RollbackAsync(string projectSlug, InstallJournal journal)
    {
        var schedules = new ColumnScheduledTaskService(projects);
        foreach (var columnId in journal.ScheduledColumnIds)
            await schedules.PrepareColumnDeletionAsync(projectSlug, columnId);
        // Definition files first: a file whose column row disappears would poison every later
        // processor synchronization, so it must never survive the rollback.
        foreach (var columnId in journal.ProcessorColumnIds)
        {
            try
            {
                var path = await processors.GetDefinitionPathAsync(projectSlug, columnId);
                var directory = Path.GetDirectoryName(path)!;
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var directory in journal.SkillDirectories)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (!journal.ProcessorMarkerExisted && File.Exists(journal.ProcessorMarker))
            File.Delete(journal.ProcessorMarker);
        if (!journal.ProcessorRootExisted && Directory.Exists(journal.ProcessorRoot)
            && !Directory.EnumerateFileSystemEntries(journal.ProcessorRoot).Any())
            Directory.Delete(journal.ProcessorRoot);
        if (journal.PipelineId is int pipelineId)
        {
            await using var db = projects.GetProjectDb(projectSlug);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM ColumnProcessors WHERE ColumnId IN (SELECT Id FROM BoardColumns WHERE PipelineId = {0})", pipelineId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM BoardColumns WHERE PipelineId = {0}", pipelineId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Pipelines WHERE Id = {0}", pipelineId);
        }
    }

    private sealed class InstallJournal
    {
        public int? PipelineId { get; set; }
        public List<string> SkillDirectories { get; } = [];
        public List<int> ProcessorColumnIds { get; } = [];
        public List<int> ScheduledColumnIds { get; } = [];
        public required string ProcessorRoot { get; init; }
        public required string ProcessorMarker { get; init; }
        public bool ProcessorRootExisted { get; init; }
        public bool ProcessorMarkerExisted { get; init; }
    }

    private async Task<bool> HasJevCredentialAsync(string projectSlug) => jevConfiguration is not null
        ? (await jevConfiguration.GetAsync(projectSlug)).Available
        : (await ListVaultSecretNamesAsync(projectSlug)).Contains(JevQualificationService.SecretName);

    private async Task<HashSet<string>> ListVaultSecretNamesAsync(string projectSlug)
    {
        try
        {
            return (await vault.ListAsync(projectSlug)).Select(secret => secret.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Locked or unavailable store: no secret can be considered bound.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static List<string> ComputeRequiredApprovals(ParsedKit kit)
    {
        var approvals = new SortedSet<string>(kit.RiskyActionTypes, StringComparer.Ordinal);
        if (kit.ScriptFiles.Count > 0) approvals.Add(EmbeddedScriptsApproval);
        return [.. approvals];
    }

    private static PipelineImportIssue LinkedSkillsRootIssue() => new(
        "linked-skill-root", PipelineKitFormat.SkillsFolderName,
        "Project skill directories cannot be symbolic links or reparse points.");

    private static PipelineImportSkillResolution ResolveSkillCollision(
        string skillsRoot, string slug, string kitFingerprint)
    {
        var folder = Path.Combine(skillsRoot, slug);
        if (ProjectSkillService.IsReparsePoint(folder)) return PipelineImportSkillResolution.RenameRequired;
        if (!Directory.Exists(folder)) return PipelineImportSkillResolution.Create;
        return string.Equals(FingerprintDiskSkill(folder), kitFingerprint, StringComparison.Ordinal)
            ? PipelineImportSkillResolution.ReuseIdentical
            : PipelineImportSkillResolution.RenameRequired;
    }

    private static string? ReadKitSkillName(List<(string Relative, byte[] Content)> folder)
    {
        var metadata = folder.FirstOrDefault(file => file.Relative == "skill.json");
        if (metadata.Content is null) return null;
        try
        {
            return JsonDocument.Parse(metadata.Content).RootElement.TryGetProperty("Name", out var name)
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Parsing and validation (no filesystem or database access whatsoever).
    // ---------------------------------------------------------------------

    private sealed record ParsedKit(
        PipelineKitManifest Manifest,
        PortablePipeline Pipeline,
        IReadOnlyDictionary<string, byte[]> Files,
        IReadOnlyDictionary<string, List<(string Relative, byte[] Content)>> SkillFolders,
        SortedSet<string> ReferencedSkills,
        SortedSet<string> Agents,
        SortedSet<string> Models,
        SortedSet<string> RiskyActionTypes,
        List<string> ScriptFiles,
        List<PipelineKitPlaceholder> Parameters,
        List<PipelineKitPlaceholder> Secrets);

    private static (ParsedKit? Kit, List<PipelineImportIssue> Issues) Parse(byte[] archive)
    {
        var issues = new List<PipelineImportIssue>();
        void Reject(string category, string path, string message) =>
            issues.Add(new PipelineImportIssue(category, path, message));

        if (archive.Length == 0)
        {
            Reject("invalid-zip", "", "L’archive est vide.");
            return (null, issues);
        }
        if (archive.LongLength > MaxArchiveBytes)
        {
            Reject("kit-too-large", "", $"L’archive dépasse la limite de {MaxArchiveBytes / (1024 * 1024)} Mo.");
            return (null, issues);
        }

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            Reject("invalid-zip", "", $"Le contenu n’est pas une archive ZIP lisible : {ex.Message}");
            return (null, issues);
        }

        using (zip)
        {
            if (zip.Entries.Count > MaxEntries)
            {
                Reject("too-many-entries", "", $"L’archive contient plus de {MaxEntries} entrées.");
                return (null, issues);
            }

            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declaredTotal = 0;
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName;
                // Unix symlink flag in the upper (external attributes) bytes.
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    Reject("symlink", path, "Les liens symboliques et points de reparse sont interdits dans un kit.");
                    continue;
                }
                var isDirectory = path.EndsWith('/') && entry.Length == 0;
                var pathError = ValidateEntryPath(isDirectory ? path.TrimEnd('/') : path);
                if (pathError is not null)
                {
                    Reject("unsafe-path", path, pathError);
                    continue;
                }
                if (isDirectory) continue;
                if (!seen.Add(path))
                {
                    Reject("duplicate-path", path, "Chemin dupliqué dans l’archive (comparaison insensible à la casse).");
                    continue;
                }
                if (entry.Length > MaxFileBytes)
                {
                    Reject("file-too-large", path, $"Chaque fichier est limité à {MaxFileBytes / (1024 * 1024)} Mo.");
                    continue;
                }
                declaredTotal += entry.Length;
                if (declaredTotal > MaxTotalBytes)
                {
                    Reject("kit-too-large", path, $"Le contenu non compressé dépasse la limite de {MaxTotalBytes / (1024 * 1024)} Mo.");
                    return (null, issues);
                }
                var content = ReadCapped(entry, out var truncated);
                if (truncated || content.LongLength != entry.Length)
                {
                    Reject("zip-bomb", path, "La taille réelle décompressée ne correspond pas à l’en-tête déclaré.");
                    continue;
                }
                files[path] = content;
            }

            var totalUncompressed = files.Values.Sum(content => content.LongLength);
            if (totalUncompressed > RatioEnforcementThreshold
                && totalUncompressed / Math.Max(1L, archive.LongLength) > MaxCompressionRatio)
                Reject("zip-bomb", "", $"Ratio de compression supérieur à {MaxCompressionRatio}:1.");

            if (!files.ContainsKey(PipelineKitFormat.ManifestEntryName))
                Reject("missing-entry", PipelineKitFormat.ManifestEntryName, "manifest.json est absent de l’archive.");
            if (!files.ContainsKey(PipelineKitFormat.PipelineEntryName))
                Reject("missing-entry", PipelineKitFormat.PipelineEntryName, "pipeline.json est absent de l’archive.");
            if (issues.Count > 0) return (null, issues);

            // Structure: only manifest.json, pipeline.json and skills/<slug>/** are allowed.
            var skillFolders = new Dictionary<string, List<(string Relative, byte[] Content)>>(StringComparer.Ordinal);
            foreach (var (path, content) in files)
            {
                if (path == PipelineKitFormat.ManifestEntryName) continue;
                if (path != PipelineKitFormat.PipelineEntryName)
                {
                    var match = SkillEntryPattern().Match(path);
                    if (!match.Success)
                    {
                        Reject("unexpected-entry", path, "Entrée en dehors de manifest.json, pipeline.json et skills/<slug>/.");
                        continue;
                    }
                    var slug = match.Groups["slug"].Value;
                    if (!skillFolders.TryGetValue(slug, out var folder))
                        skillFolders[slug] = folder = [];
                    folder.Add((match.Groups["relative"].Value, content));

                    var extension = Path.GetExtension(path);
                    if (ForbiddenExtensions.Contains(extension))
                        Reject("forbidden-file-type", path, "Les exécutables binaires et archives imbriquées sont interdits dans un kit.");
                }
                if (LooksExecutableOrArchive(content))
                    Reject("binary-or-archive", path, "Signature d’exécutable binaire ou d’archive imbriquée détectée.");
            }

            // Manifest: strict format and version. Unknown versions are refused, never approximated.
            PipelineKitManifest? manifest = null;
            try
            {
                manifest = JsonSerializer.Deserialize<PipelineKitManifest>(files[PipelineKitFormat.ManifestEntryName], KitJson);
            }
            catch (JsonException ex)
            {
                Reject("invalid-manifest", PipelineKitFormat.ManifestEntryName, $"manifest.json est illisible : {ex.Message}");
            }
            if (manifest is null)
            {
                if (issues.Count == 0)
                    Reject("invalid-manifest", PipelineKitFormat.ManifestEntryName, "manifest.json est vide.");
                return (null, issues);
            }
            if (manifest.Format != PipelineKitFormat.Format)
                Reject("unsupported-format", PipelineKitFormat.ManifestEntryName,
                    $"Format inconnu : '{manifest.Format}' (attendu : '{PipelineKitFormat.Format}').");
            if (manifest.FormatVersion != PipelineKitFormat.Version
                || manifest.Compatibility.MinFormatVersion > PipelineKitFormat.Version
                || manifest.Compatibility.MinFormatVersion < 1)
                Reject("unsupported-version", PipelineKitFormat.ManifestEntryName,
                    $"Version de format non prise en charge : {manifest.FormatVersion} (minFormatVersion {manifest.Compatibility.MinFormatVersion}). Version supportée : {PipelineKitFormat.Version}.");
            if (issues.Count > 0) return (null, issues);

            // Inventory: the manifest must list exactly every non-manifest entry, hashes must verify.
            var declared = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
            {
                if (!declared.TryAdd(file.Path, file.Sha256))
                    Reject("inventory-mismatch", file.Path, "Chemin déclaré deux fois dans le manifeste.");
            }
            foreach (var path in declared.Keys.Where(path => !files.ContainsKey(path)))
                Reject("inventory-mismatch", path, "Fichier déclaré dans le manifeste mais absent de l’archive.");
            foreach (var path in files.Keys.Where(path =>
                         path != PipelineKitFormat.ManifestEntryName && !declared.ContainsKey(path)))
                Reject("inventory-mismatch", path, "Fichier présent dans l’archive mais non déclaré dans le manifeste.");
            foreach (var (path, sha) in declared)
            {
                if (!files.TryGetValue(path, out var content)) continue;
                var computed = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (!string.Equals(computed, sha, StringComparison.OrdinalIgnoreCase))
                    Reject("hash-mismatch", path, "L’empreinte SHA-256 ne correspond pas au contenu : archive altérée.");
            }
            if (issues.Count > 0) return (null, issues);

            // Pipeline definition.
            PortablePipeline? pipeline = null;
            try
            {
                pipeline = JsonSerializer.Deserialize<PortablePipeline>(files[PipelineKitFormat.PipelineEntryName], KitJson);
            }
            catch (JsonException ex)
            {
                Reject("invalid-pipeline", PipelineKitFormat.PipelineEntryName, $"pipeline.json est illisible : {ex.Message}");
            }
            if (pipeline is null)
            {
                if (issues.Count == 0)
                    Reject("invalid-pipeline", PipelineKitFormat.PipelineEntryName, "pipeline.json est vide.");
                return (null, issues);
            }

            var referencedSkills = new SortedSet<string>(StringComparer.Ordinal);
            var agents = new SortedSet<string>(StringComparer.Ordinal);
            var models = new SortedSet<string>(StringComparer.Ordinal);
            var riskyActionTypes = new SortedSet<string>(StringComparer.Ordinal);
            ValidatePipeline(pipeline, issues, referencedSkills, agents, models, riskyActionTypes);

            // Every embedded skill must be referenced by the pipeline and carry a SKILL.md.
            foreach (var (slug, folder) in skillFolders)
            {
                if (!referencedSkills.Contains(slug))
                    Reject("unreferenced-skill", $"{PipelineKitFormat.SkillsFolderName}/{slug}",
                        "Skill embarquée non référencée par le pipeline : contenu superflu refusé.");
                if (!folder.Any(file => file.Relative == "SKILL.md"))
                    Reject("invalid-skill", $"{PipelineKitFormat.SkillsFolderName}/{slug}",
                        "Une skill embarquée doit contenir un SKILL.md à sa racine.");
            }

            var scriptFiles = files.Keys
                .Where(path => path.StartsWith(PipelineKitFormat.SkillsFolderName + "/", StringComparison.Ordinal)
                    && ScriptExtensions.Contains(Path.GetExtension(path)))
                .Order(StringComparer.Ordinal).ToList();

            // Placeholders are recomputed from the actual content: the manifest is not trusted.
            var texts = new List<string> { Encoding.UTF8.GetString(files[PipelineKitFormat.PipelineEntryName]) };
            texts.AddRange(files
                .Where(file => file.Key != PipelineKitFormat.ManifestEntryName
                    && file.Key != PipelineKitFormat.PipelineEntryName
                    && IsTextContent(Path.GetExtension(file.Key), file.Value))
                .Select(file => Encoding.UTF8.GetString(file.Value)));
            var parameters = CollectPlaceholders("input", texts);
            var secrets = CollectPlaceholders("secret", texts);

            if (issues.Count > 0) return (null, issues);
            var contentFiles = files.Where(file => file.Key != PipelineKitFormat.ManifestEntryName)
                .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
            return (new ParsedKit(manifest, pipeline, contentFiles, skillFolders,
                referencedSkills, agents, models, riskyActionTypes, scriptFiles, parameters, secrets), issues);
        }
    }

    private static void ValidatePipeline(
        PortablePipeline pipeline, List<PipelineImportIssue> issues,
        SortedSet<string> referencedSkills, SortedSet<string> agents,
        SortedSet<string> models, SortedSet<string> riskyActionTypes)
    {
        void Reject(string category, string message) =>
            issues.Add(new PipelineImportIssue(category, PipelineKitFormat.PipelineEntryName, message));

        if (pipeline.FormatVersion != PipelineKitFormat.Version)
            Reject("unsupported-version", $"pipeline.json déclare la version {pipeline.FormatVersion} ; version supportée : {PipelineKitFormat.Version}.");
        if (string.IsNullOrWhiteSpace(pipeline.Name) || pipeline.Name.Trim().Length > 100)
            Reject("invalid-pipeline", "Le nom du pipeline est requis (100 caractères max).");
        if (pipeline.Columns.Count is 0 or > MaxColumns)
        {
            Reject("invalid-pipeline", $"Le pipeline doit contenir entre 1 et {MaxColumns} colonnes.");
            return;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in pipeline.Columns)
        {
            if (!SkillSlugPattern().IsMatch(column.Key) || !keys.Add(column.Key))
                Reject("invalid-pipeline", $"Clé de colonne invalide ou dupliquée : '{column.Key}'.");
            if (string.IsNullOrWhiteSpace(column.Name) || column.Name.Trim().Length > 100 || !names.Add(column.Name.Trim()))
                Reject("invalid-pipeline", $"Nom de colonne vide, trop long ou dupliqué : '{column.Name}'.");
            if (!string.IsNullOrWhiteSpace(column.Color) && !ColorPattern().IsMatch(column.Color))
                Reject("invalid-pipeline", $"Couleur invalide pour la colonne '{column.Key}' : '{column.Color}'.");
            if (column.MaxTickets < 0)
                Reject("invalid-pipeline", $"Negative ticket capacity for column '{column.Key}'.");
            if (!Enum.IsDefined(column.Role))
                Reject("invalid-pipeline", $"Rôle inconnu pour la colonne '{column.Key}'.");
        }

        foreach (var column in pipeline.Columns)
        {
            if (column.Processor is { } processor) ValidateProcessor(column, processor);
            foreach (var task in column.ScheduledTasks)
            {
                if (string.IsNullOrWhiteSpace(task.Name) || task.Actions.Count == 0 || !Enum.IsDefined(task.TicketScope))
                    Reject("invalid-schedule", $"Invalid scheduled task in column '{column.Key}'.");
                try { _ = ColumnScheduledTaskService.ComputeNext(task.Cron, task.TimeZoneId, DateTime.UtcNow); }
                catch (Exception ex) when (ex is ArgumentException or NCrontab.CrontabException or TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    Reject("invalid-schedule", $"Invalid schedule in column '{column.Key}': {ex.Message}");
                }
                if (task.TicketScope == ScheduledTaskTicketScope.None && task.Actions.Any(action =>
                    action.Action is SetLabelsActionSpec or AddCommentActionSpec))
                    Reject("invalid-schedule", "Scheduled ticket actions require the first-ticket scope.");
                ValidateProcessor(column, new PortableProcessor
                {
                    Name = task.Name, Mission = "Scheduled task", BeforeActions = task.Actions,
                });
            }
        }

        void ValidateProcessor(PortableColumn column, PortableProcessor processor)
        {
            var context = $"colonne '{column.Key}'";
            if (string.IsNullOrWhiteSpace(processor.Name) || string.IsNullOrWhiteSpace(processor.Mission))
                Reject("invalid-processor", $"Nom et mission du processeur sont requis ({context}).");
            if (processor.MaxTurns < 1 || processor.MaxAttempts < 1 || processor.RetryBackoffSeconds < 1)
                Reject("invalid-processor", $"MaxTurns, MaxAttempts et RetryBackoffSeconds doivent être supérieurs à zéro ({context}).");
            if (!double.IsFinite(processor.JevConfidenceThreshold) || processor.JevConfidenceThreshold < 0 || processor.JevConfidenceThreshold > 1)
                Reject("invalid-processor", "JEV confidence threshold must be between 0 and 1.");
            if (processor.JevAcceptanceCriteria is null || processor.JevExecutionProfiles is null
                || processor.JevAcceptanceCriteria.Count > 20 || processor.JevAcceptanceCriteria.Any(c => c is null || c.Length > 2000)
                || processor.JevExecutionProfiles.Count > 10
                || (processor.JevExecutionSelection && processor.JevExecutionProfiles.Count == 0)
                || processor.JevExecutionProfiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name)
                    || p.Name.Length > 100 || string.IsNullOrWhiteSpace(p.Model) || p.Model.Length > 200
                    || p.Description?.Length > 2000 || p.Skills is null)
                || processor.JevExecutionProfiles.Select(p => p.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != processor.JevExecutionProfiles.Count)
            {
                Reject("invalid-processor", "Invalid JEV review criteria or execution profiles.");
                return;
            }
            if ((processor.JevAcceptanceValidation && (processor.JevObservationOnly || processor.JevPostRunObservation
                    || processor.JevAutomaticRouting || processor.JevExecutionSelection))
                || (processor.JevExecutionSelection && processor.JevObservationOnly))
                Reject("invalid-processor", "Incompatible JEV modes.");
            foreach (var profile in processor.JevExecutionProfiles) models.Add(profile.Model);
            if (!Enum.IsDefined(processor.SelectionOrder))
                Reject("invalid-processor", $"Ordre de sélection inconnu ({context}).");
            if (!string.IsNullOrWhiteSpace(processor.Model)) models.Add(processor.Model.Trim());
            foreach (var slug in processor.AvailableSkills.Concat(processor.RecommendedSkills).Concat(processor.RequiredSkills).Concat(processor.JevExecutionProfiles.SelectMany(p => p.Skills)))
            {
                if (SkillSlugPattern().IsMatch(slug)) referencedSkills.Add(slug);
                else Reject("invalid-processor", $"Référence de skill invalide '{slug}' ({context}).");
            }

            void CheckTarget(string? key, string field)
            {
                if (key is null) return;
                if (!keys.Contains(key))
                    Reject("invalid-processor", $"Cible de routage inconnue '{key}' pour {field} ({context}).");
                else if (key == column.Key)
                    Reject("invalid-processor", $"Une route ne peut pas renvoyer vers sa propre colonne ({field}, {context}).");
            }
            CheckTarget(processor.Routing.Default, "routing.default");
            CheckTarget(processor.Routing.TechnicalFailure, "routing.technicalFailure");
            var outcomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var route in processor.Routing.Routes)
            {
                if (string.IsNullOrWhiteSpace(route.Outcome) || !outcomes.Add(route.Outcome.Trim()))
                    Reject("invalid-processor", $"Outcome de route vide ou dupliqué ({context}).");
                CheckTarget(route.Target, $"route '{route.Outcome}'");
            }

            var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in processor.BeforeActions.Concat(processor.AfterActions))
            {
                if (string.IsNullOrWhiteSpace(action.Id) || !actionIds.Add(action.Id.Trim()))
                    Reject("invalid-processor", $"Identifiant d’action vide ou dupliqué ({context}).");
                if (action.CreateTarget is not null && action.Action is not CreateTicketActionSpec)
                    Reject("invalid-processor", "createTarget is only valid on createTicket actions.");
                CheckTarget(action.OnFailure, $"action '{action.Id}'.onFailure");
                switch (action.Action)
                {
                    case SetLabelsActionSpec:
                        break;
                    case AddCommentActionSpec comment:
                        if (string.IsNullOrWhiteSpace(comment.Content))
                            Reject("invalid-processor", $"L’action commentaire '{action.Id}' doit définir un contenu ({context}).");
                        break;
                    case CreateTicketActionSpec create:
                        if (create.ParentId is not null)
                            Reject("external-reference", "createTicket parentId refers to a source-project ticket and cannot be imported.");
                        if (action.CreateTarget is { } creationKey)
                        {
                            if (!keys.Contains(creationKey))
                                Reject("invalid-processor", $"Unknown createTicket destination key '{creationKey}'.");
                        }
                        else if (!pipeline.Columns.Any(c => string.Equals(c.Name.Trim(), create.Status?.Trim(), StringComparison.OrdinalIgnoreCase)))
                            Reject("invalid-processor", "createTicket status does not match a column in the imported pipeline.");
                        if (string.IsNullOrWhiteSpace(create.Title))
                            Reject("invalid-processor", $"L’action de création de ticket '{action.Id}' doit définir un titre ({context}).");
                        if (!string.IsNullOrWhiteSpace(create.AssignedTo)) agents.Add(create.AssignedTo.Trim());
                        break;
                    case ExecutePowerShellActionSpec script:
                        riskyActionTypes.Add("executePowerShell");
                        if (!string.IsNullOrWhiteSpace(script.ScriptFile))
                            Reject("external-reference", $"L’action '{action.Id}' référence un fichier de script externe : seuls les scripts en ligne sont importables ({context}).");
                        if (string.IsNullOrWhiteSpace(script.Script) && string.IsNullOrWhiteSpace(script.ScriptFile))
                            Reject("invalid-processor", $"L’action PowerShell '{action.Id}' doit définir un script ({context}).");
                        break;
                    case ExecuteProgramActionSpec program:
                        riskyActionTypes.Add(program.UiTypeKey);
                        try { ProgramActionSupport.Validate(program); }
                        catch (InvalidOperationException ex) { Reject("invalid-program", ex.Message); }
                        if (program is ExecutePythonActionSpec python && !string.IsNullOrWhiteSpace(python.ScriptFile))
                            Reject("external-reference", "Portable Python actions must embed their source or reference a module.");
                        break;
                    case HttpRequestActionSpec request:
                        riskyActionTypes.Add("httpRequest");
                        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                            Reject("invalid-processor", $"L’action HTTP '{action.Id}' doit définir une URL http(s) absolue ({context}).");
                        break;
                    default:
                        Reject("invalid-processor",
                            $"Type d’action non pris en charge dans un processeur importé : {action.Action?.GetType().Name ?? "null"} ({context}).");
                        break;
                }
            }
        }
    }

    private static string? ValidateEntryPath(string path)
    {
        if (path.Length == 0) return "Chemin d’entrée vide.";
        if (path.Length > MaxPathLength) return $"Chemin d’entrée trop long ({MaxPathLength} caractères max).";
        if (path.Contains('\\')) return "Séparateur '\\' interdit dans un chemin d’archive.";
        if (path.StartsWith('/')) return "Chemin absolu interdit.";
        if (path.Length >= 2 && path[1] == ':') return "Chemin absolu avec lettre de lecteur interdit.";
        var segments = path.Split('/');
        if (segments.Length > MaxPathDepth) return $"Profondeur de chemin supérieure à {MaxPathDepth}.";
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..") return "Segment de chemin vide ou traversant ('..').";
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return "Segment terminé par un point ou une espace.";
            foreach (var character in segment)
                if (character < 0x20 || "<>:\"|?*".Contains(character))
                    return $"Caractère interdit dans le chemin : '{(character < 0x20 ? "\\x" + ((int)character).ToString("X2") : character.ToString())}'.";
            if (WindowsReservedNames.Contains(Path.GetFileNameWithoutExtension(segment)))
                return $"Nom de fichier réservé Windows : '{segment}'.";
        }
        return null;
    }

    /// <summary>Reads at most <see cref="MaxFileBytes"/> + 1 bytes so a lying header cannot expand.</summary>
    private static byte[] ReadCapped(ZipArchiveEntry entry, out bool truncated)
    {
        using var source = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > MaxFileBytes)
            {
                truncated = true;
                return buffer.ToArray();
            }
            buffer.Write(chunk, 0, read);
        }
        truncated = false;
        return buffer.ToArray();
    }

    private static bool LooksExecutableOrArchive(byte[] content)
    {
        if (content.Length < 4) return false;
        return (content[0] == 0x4D && content[1] == 0x5A)                                            // MZ (PE)
            || (content[0] == 0x7F && content[1] == 0x45 && content[2] == 0x4C && content[3] == 0x46) // ELF
            || (content[0] == 0x50 && content[1] == 0x4B && content[2] is 0x03 or 0x05 or 0x07)       // ZIP family
            || (content[0] == 0x1F && content[1] == 0x8B)                                            // gzip
            || (content[0] == 0x37 && content[1] == 0x7A && content[2] == 0xBC && content[3] == 0xAF) // 7z
            || (content[0] == 0x52 && content[1] == 0x61 && content[2] == 0x72 && content[3] == 0x21) // RAR
            || IsMachO(content);
    }

    private static bool IsMachO(byte[] content)
    {
        var magic = BitConverter.ToUInt32(content, 0);
        return magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xBEBAFECA or 0xCAFEBABE;
    }

    private static bool IsTextContent(string extension, byte[] content) =>
        TextExtensions.Contains(extension) || Array.IndexOf(content, (byte)0, 0, Math.Min(content.Length, 8000)) < 0;

    private static List<PipelineKitPlaceholder> CollectPlaceholders(string kind, IEnumerable<string> texts)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var text in texts)
            foreach (Match match in PlaceholderPattern().Matches(text))
            {
                if (!string.Equals(match.Groups["kind"].Value, kind, StringComparison.Ordinal)) continue;
                var name = match.Groups["name"].Value;
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        return counts.Select(pair => new PipelineKitPlaceholder { Name = pair.Key, Occurrences = pair.Value }).ToList();
    }

    /// <summary>
    /// Replaces {{input.NAME}} with supplied values and rewrites {{secret.NAME}} to its vault
    /// binding. Secret values are never inlined. In JSON mode values are JSON-escaped so a
    /// substitution can never alter the document structure.
    /// </summary>
    private static string Substitute(
        string text, IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<string, string> secretBindings, bool jsonEncode)
    {
        return PlaceholderPattern().Replace(text, match =>
        {
            var name = match.Groups["name"].Value;
            if (match.Groups["kind"].Value == "input" && parameters.TryGetValue(name, out var value))
                return jsonEncode ? JsonSerializer.Serialize(value)[1..^1] : value;
            if (match.Groups["kind"].Value == "secret"
                && secretBindings.TryGetValue(name, out var bound) && bound != name)
                return "{{secret." + bound + "}}";
            return match.Value;
        });
    }

    private static string FingerprintKitSkill(List<(string Relative, byte[] Content)> files) =>
        Fingerprint(files.Select(file => (file.Relative, file.Content)));

    private static string? FingerprintDiskSkill(string folder)
    {
        var root = Path.GetFullPath(folder);
        var pending = new Stack<string>();
        var files = new List<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (ProjectSkillService.IsReparsePoint(path)) return null;
                if (Directory.Exists(path)) pending.Push(path);
                else files.Add(path);
            }
        }
        return Fingerprint(files.Select(path =>
            (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllBytes(path))));
    }

    /// <summary>Content fingerprint over relative paths and per-file hashes; text newlines are
    /// normalized so a Git checkout cannot break the identical-skill detection.</summary>
    private static string Fingerprint(IEnumerable<(string Path, byte[] Content)> files)
    {
        var builder = new StringBuilder();
        foreach (var (path, content) in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var bytes = IsTextContent(System.IO.Path.GetExtension(path), content)
                ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content).Replace("\r\n", "\n"))
                : content;
            builder.Append(path).Append('\n')
                .Append(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    [GeneratedRegex(@"^skills/(?<slug>[a-z0-9][a-z0-9-]*)/(?<relative>.+)$")]
    private static partial Regex SkillEntryPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SkillSlugPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    [GeneratedRegex(@"\{\{\s*(?<kind>input|secret)\.(?<name>[A-Za-z0-9_][A-Za-z0-9_.-]*)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]*$")]
    private static partial Regex PlaceholderNamePattern();
}
