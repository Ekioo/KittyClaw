using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

/// <summary>
/// Builds portable ".kittyclaw-pipeline" kits: a deterministic ZIP with a versioned manifest,
/// a pipeline serialized on logical column keys (no database ids), and the full folder of every
/// referenced project skill. Export is refused while the scanner still reports probable secrets,
/// credentials, absolute paths or out-of-folder skill references — no override in v1.
/// Tickets, comments, runs, executions, memories, costs and vault values are never exported.
/// </summary>
public sealed partial class PipelineExportService(
    ProjectService projects,
    PipelineService pipelines,
    ColumnService columns,
    ColumnProcessorService processors,
    ProjectSkillService skills,
    TimeProvider? clock = null)
{
    private const int MaxSkillFiles = 400;
    private const long MaxSkillFileBytes = 2 * 1024 * 1024;
    private const long MaxTotalBytes = 16 * 1024 * 1024;

    private static readonly DateTimeOffset FixedEntryTimestamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".msi", ".com", ".scr",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".jar", PipelineKitFormat.FileExtension,
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".yaml", ".yml", ".ps1", ".psm1", ".psd1", ".sh", ".py", ".js", ".ts",
        ".csv", ".xml", ".html", ".htm", ".css", ".toml", ".ini", ".cfg", ".sql", ".cs", ".razor", ".bat", ".cmd",
    };

    private static readonly JsonSerializerOptions KitJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Returns the kit bytes, null when the project or pipeline does not exist.</summary>
    /// <exception cref="PipelineExportBlockedException">Sanitization findings remain.</exception>
    public async Task<PipelineExportResult?> ExportAsync(string projectSlug, int pipelineId)
    {
        var project = await projects.GetProjectAsync(projectSlug);
        if (project is null) return null;
        var pipeline = await pipelines.GetAsync(projectSlug, pipelineId);
        if (pipeline is null) return null;

        var pipelineColumns = (await columns.ListColumnsAsync(projectSlug, pipelineId))
            .OrderBy(column => column.SortOrder).ThenBy(column => column.Id).ToList();
        var allProcessors = await processors.ListAsync(projectSlug);
        var processorsByColumn = allProcessors.Where(p => pipelineColumns.Any(c => c.Id == p.ColumnId))
            .ToDictionary(p => p.ColumnId);

        var findings = new List<PipelineExportFinding>();
        var keys = BuildColumnKeys(pipelineColumns);
        var referencedSkills = new SortedSet<string>(StringComparer.Ordinal);
        var referencedAgents = new SortedSet<string>(StringComparer.Ordinal);
        var models = new SortedSet<string>(StringComparer.Ordinal);
        var riskyActionTypes = new SortedSet<string>(StringComparer.Ordinal);

        var portable = new PortablePipeline { Name = pipeline.Name, Slug = pipeline.Slug };
        foreach (var column in pipelineColumns)
        {
            var portableColumn = new PortableColumn
            {
                Key = keys[column.Id],
                Name = column.Name,
                Color = column.Color,
                Role = column.Role,
                UserGuidance = column.UserGuidance,
            };
            if (processorsByColumn.TryGetValue(column.Id, out var processor))
                portableColumn.Processor = BuildPortableProcessor(
                    processor, keys, findings, referencedSkills, referencedAgents, models, riskyActionTypes);
            portable.Columns.Add(portableColumn);
        }

        var pipelineJson = JsonSerializer.Serialize(portable, KitJson) + "\n";
        PipelineKitScanner.ScanText(PipelineKitFormat.PipelineEntryName, pipelineJson, isSkillFile: false, findings);

        var workspace = projects.ResolveWorkspacePath(project);
        var projectSkills = (await skills.ListAsync(projectSlug)).ToDictionary(s => s.Slug, StringComparer.OrdinalIgnoreCase);
        var embeddedFiles = new List<(string ArchivePath, byte[] Content)>();
        var skillRequirements = new List<PipelineKitSkillRequirement>();
        var totalBytes = (long)Encoding.UTF8.GetByteCount(pipelineJson);
        foreach (var slug in referencedSkills)
        {
            if (projectSkills.TryGetValue(slug, out var skill))
            {
                skillRequirements.Add(new PipelineKitSkillRequirement { Slug = skill.Slug, Name = skill.Name, Embedded = true });
                CollectSkillFolder(workspace, skill.Slug, findings, embeddedFiles, ref totalBytes);
            }
            else
            {
                // Referenced without a local project folder: declared prerequisite only.
                skillRequirements.Add(new PipelineKitSkillRequirement { Slug = slug, Name = slug, Embedded = false });
            }
        }
        // Agent identities are workspace-global skills (.agents/<agent>/SKILL.md): declared, never copied.
        foreach (var agent in referencedAgents)
        {
            if (skillRequirements.Any(requirement => string.Equals(requirement.Slug, agent, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (File.Exists(Path.Combine(workspace, ".agents", agent, "SKILL.md")))
                skillRequirements.Add(new PipelineKitSkillRequirement { Slug = agent, Name = agent, Embedded = false });
        }

        if (findings.Count > 0)
            throw new PipelineExportBlockedException(findings.Distinct().ToList());

        var pipelineBytes = Encoding.UTF8.GetBytes(pipelineJson);
        var files = new List<(string ArchivePath, byte[] Content)> { (PipelineKitFormat.PipelineEntryName, pipelineBytes) };
        files.AddRange(embeddedFiles);
        files.Sort((a, b) => string.CompareOrdinal(a.ArchivePath, b.ArchivePath));

        var scannableTexts = new List<string> { pipelineJson };
        scannableTexts.AddRange(embeddedFiles
            .Where(file => IsTextContent(Path.GetExtension(file.ArchivePath), file.Content))
            .Select(file => Encoding.UTF8.GetString(file.Content)));

        var manifest = new PipelineKitManifest
        {
            Metadata = new PipelineKitMetadata { Name = pipeline.Name, Slug = pipeline.Slug },
            Provenance = new PipelineKitProvenance
            {
                GeneratorVersion = GeneratorVersion(),
                ProjectSlug = projectSlug,
                ExportedAt = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            },
            Parameters = CollectPlaceholders("input", scannableTexts),
            Secrets = CollectPlaceholders("secret", scannableTexts),
            Requirements = new PipelineKitRequirements
            {
                Models = [.. models],
                Agents = [.. referencedAgents],
                Skills = skillRequirements.OrderBy(requirement => requirement.Slug, StringComparer.Ordinal).ToList(),
                RiskyActionTypes = [.. riskyActionTypes],
            },
            Files = files.Select(file => new PipelineKitFileEntry
            {
                Path = file.ArchivePath,
                Sha256 = Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant(),
            }).ToList(),
        };
        var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, KitJson) + "\n");

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, PipelineKitFormat.ManifestEntryName, manifestBytes);
            foreach (var (archivePath, content) in files)
                AddEntry(zip, archivePath, content);
        }
        return new PipelineExportResult(pipeline.Slug + PipelineKitFormat.FileExtension, buffer.ToArray());
    }

    private static void AddEntry(ZipArchive zip, string path, byte[] content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedEntryTimestamp;
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static PortableProcessor BuildPortableProcessor(
        ColumnProcessor processor, Dictionary<int, string> keys, List<PipelineExportFinding> findings,
        SortedSet<string> referencedSkills, SortedSet<string> referencedAgents,
        SortedSet<string> models, SortedSet<string> riskyActionTypes)
    {
        string? MapColumn(int? columnId, string context)
        {
            if (columnId is null) return null;
            if (keys.TryGetValue(columnId.Value, out var key)) return key;
            findings.Add(new PipelineExportFinding(
                "unportable-reference", PipelineKitFormat.PipelineEntryName, 0,
                $"{context} (processeur '{processor.Name}')",
                "La cible est en dehors du pipeline exporté ; re-routez vers une colonne du pipeline ou retirez cette destination."));
            return null;
        }

        List<PortableProcessorAction> MapActions(IEnumerable<ColumnProcessorAction> actions)
        {
            var mapped = new List<PortableProcessorAction>();
            foreach (var action in actions)
            {
                switch (action.Action)
                {
                    case ExecutePowerShellActionSpec script:
                        riskyActionTypes.Add("executePowerShell");
                        if (!string.IsNullOrWhiteSpace(script.ScriptFile))
                            findings.Add(new PipelineExportFinding(
                                "external-reference", PipelineKitFormat.PipelineEntryName, 0,
                                $"action '{action.Id}' scriptFile: {script.ScriptFile}",
                                "Un fichier de script externe n’est pas embarqué ; intégrez le script en ligne ou dans une skill projet."));
                        break;
                    case HttpRequestActionSpec:
                        riskyActionTypes.Add("httpRequest");
                        break;
                    case CreateTicketActionSpec create when !string.IsNullOrWhiteSpace(create.AssignedTo):
                        referencedAgents.Add(create.AssignedTo.Trim());
                        break;
                }
                mapped.Add(new PortableProcessorAction
                {
                    Id = action.Id,
                    Action = action.Action,
                    OnFailure = MapColumn(action.FailureTargetColumnId, $"action '{action.Id}'.onFailure"),
                });
            }
            return mapped;
        }

        foreach (var slug in processor.AvailableSkills.Concat(processor.RecommendedSkills).Concat(processor.RequiredSkills))
            referencedSkills.Add(slug);
        if (processor.Model is not null) models.Add(processor.Model);

        return new PortableProcessor
        {
            Name = processor.Name,
            Mission = processor.Mission,
            Prompt = processor.Prompt,
            Model = processor.Model,
            Enabled = processor.Enabled,
            MaxTurns = processor.MaxTurns,
            SelectionOrder = processor.SelectionOrder,
            MaxAttempts = processor.MaxAttempts,
            RetryBackoffSeconds = processor.RetryBackoffSeconds,
            AvailableSkills = [.. processor.AvailableSkills],
            RecommendedSkills = [.. processor.RecommendedSkills],
            RequiredSkills = [.. processor.RequiredSkills],
            BeforeActions = MapActions(processor.BeforeActions),
            AfterActions = MapActions(processor.AfterActions),
            Routing = new PortableRouting
            {
                Default = MapColumn(processor.DefaultTargetColumnId, "routing.default"),
                TechnicalFailure = MapColumn(processor.TechnicalFailureColumnId, "routing.technicalFailure"),
                Routes = processor.Routes.Select(route => new PortableRoute
                {
                    Outcome = route.Outcome,
                    Target = MapColumn(route.TargetColumnId, $"route '{route.Outcome}'") ?? "",
                }).ToList(),
            },
        };
    }

    private void CollectSkillFolder(
        string workspace, string slug, List<PipelineExportFinding> findings,
        List<(string ArchivePath, byte[] Content)> files, ref long totalBytes)
    {
        var folder = Path.GetFullPath(Path.Combine(workspace, ".agents", "skills", slug));
        var paths = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal).ToList();
        if (files.Count + paths.Count > MaxSkillFiles)
        {
            findings.Add(new PipelineExportFinding(
                "kit-too-large", $"{PipelineKitFormat.SkillsFolderName}/{slug}", 0,
                $"{paths.Count} fichiers (limite totale {MaxSkillFiles})",
                "Réduisez le contenu des skills embarquées."));
            return;
        }
        foreach (var path in paths)
        {
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            var archivePath = $"{PipelineKitFormat.SkillsFolderName}/{slug}/{relative}";
            if (relative.StartsWith("..", StringComparison.Ordinal) || HasReparsePoint(folder, path))
            {
                findings.Add(new PipelineExportFinding(
                    "out-of-folder-reference", archivePath, 0, relative,
                    "Les liens symboliques et chemins sortant du dossier de la skill ne sont jamais embarqués."));
                continue;
            }
            var extension = Path.GetExtension(path);
            if (ForbiddenExtensions.Contains(extension))
            {
                findings.Add(new PipelineExportFinding(
                    "forbidden-file-type", archivePath, 0, relative,
                    "Les exécutables binaires et archives imbriquées sont interdits dans un kit."));
                continue;
            }
            if (new FileInfo(path).Length > MaxSkillFileBytes)
            {
                findings.Add(new PipelineExportFinding(
                    "file-too-large", archivePath, 0, relative,
                    $"Chaque fichier de skill est limité à {MaxSkillFileBytes / (1024 * 1024)} Mo."));
                continue;
            }
            var content = File.ReadAllBytes(path);
            totalBytes += content.LongLength;
            if (totalBytes > MaxTotalBytes)
            {
                findings.Add(new PipelineExportFinding(
                    "kit-too-large", archivePath, 0, relative,
                    $"Le contenu non compressé d’un kit est limité à {MaxTotalBytes / (1024 * 1024)} Mo."));
                return;
            }
            if (IsTextContent(extension, content))
                PipelineKitScanner.ScanText(archivePath, Encoding.UTF8.GetString(content), isSkillFile: true, findings);
            files.Add((archivePath, content));
        }
    }

    private static bool HasReparsePoint(string root, string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return true;
        for (var directory = Path.GetDirectoryName(path);
             directory is not null && directory.Length >= root.Length;
             directory = Path.GetDirectoryName(directory))
        {
            if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
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

    private static Dictionary<int, string> BuildColumnKeys(List<BoardColumn> orderedColumns)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var keys = new Dictionary<int, string>();
        foreach (var column in orderedColumns)
        {
            var baseKey = Slugify(column.Name);
            var key = baseKey;
            for (var suffix = 2; !used.Add(key); suffix++) key = $"{baseKey}-{suffix}";
            keys[column.Id] = key;
        }
        return keys;
    }

    private static string Slugify(string name)
    {
        var decomposed = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var ascii = new string(decomposed.Where(c =>
            CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        var slug = NonAlphanumericPattern().Replace(ascii, "-").Trim('-');
        return slug.Length == 0 ? "column" : slug;
    }

    private static string GeneratorVersion()
    {
        var assembly = typeof(PipelineExportService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational ?? assembly.GetName().Version?.ToString() ?? "0";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    [GeneratedRegex(@"\{\{\s*(?<kind>input|secret)\.(?<name>[A-Za-z0-9_][A-Za-z0-9_.-]*)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumericPattern();
}
