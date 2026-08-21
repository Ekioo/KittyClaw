using System.Text.Json;
using System.Text.RegularExpressions;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

/// <summary>Stores reusable project capabilities independently from agent identities.</summary>
public sealed partial class ProjectSkillService(ProjectService projects)
{
    private sealed record SkillMetadata(string Name, string? Description = null);
    private sealed record SkillDocument(string? Description, string Body);

    public async Task<List<ProjectSkill>> ListAsync(string projectSlug)
    {
        var root = await ResolveRootAsync(projectSlug);
        if (!Directory.Exists(root)) return [];
        var result = new List<ProjectSkill>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var instructions = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(instructions)) continue;
            var slug = Path.GetFileName(directory);
            var metadata = await ReadMetadataAsync(directory) ?? new SkillMetadata(slug);
            var document = await NormalizeAsync(directory, slug, metadata);
            result.Add(new ProjectSkill(slug, metadata.Name, document.Description!, instructions));
        }
        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<string?> ReadInstructionsAsync(string projectSlug, string skillSlug)
    {
        var path = await ResolveSkillPathAsync(projectSlug, skillSlug, mustExist: true);
        if (path is null) return null;
        var metadata = await ReadMetadataAsync(path) ?? new SkillMetadata(skillSlug);
        return (await NormalizeAsync(path, skillSlug, metadata)).Body;
    }

    public async Task<ProjectSkill> CreateAsync(
        string projectSlug, string name, string instructions, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Le nom du skill est requis.");
        var root = await ResolveRootAsync(projectSlug);
        Directory.CreateDirectory(root);
        var baseSlug = SlugPattern().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (baseSlug.Length == 0) baseSlug = "skill";
        var slug = baseSlug;
        for (var suffix = 2; Directory.Exists(Path.Combine(root, slug)); suffix++) slug = $"{baseSlug}-{suffix}";
        var directory = Path.Combine(root, slug);
        Directory.CreateDirectory(directory);
        var effectiveDescription = NormalizeDescription(description, name, instructions);
        var metadata = new SkillMetadata(name.Trim(), effectiveDescription);
        await WriteAtomicAsync(Path.Combine(directory, "skill.json"), JsonSerializer.Serialize(metadata));
        await WriteAtomicAsync(Path.Combine(directory, "SKILL.md"), BuildDocument(slug, effectiveDescription, ParseDocument(instructions ?? "").Body));
        return new ProjectSkill(slug, name.Trim(), effectiveDescription, Path.Combine(directory, "SKILL.md"));
    }

    public async Task<ProjectSkill?> UpdateAsync(
        string projectSlug, string skillSlug, string? name, string? instructions,
        string? description = null)
    {
        var directory = await ResolveSkillPathAsync(projectSlug, skillSlug, mustExist: true);
        if (directory is null) return null;
        var metadata = await ReadMetadataAsync(directory) ?? new SkillMetadata(skillSlug);
        var current = await NormalizeAsync(directory, skillSlug, metadata);
        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Le nom du skill est requis.");
            metadata = metadata with { Name = name.Trim() };
        }
        var incoming = instructions is null ? null : ParseDocument(instructions);
        var body = incoming?.Body ?? current.Body;
        var effectiveDescription = NormalizeDescription(
            description ?? incoming?.Description ?? metadata.Description ?? current.Description,
            metadata.Name, body);
        metadata = metadata with { Description = effectiveDescription };
        await WriteAtomicAsync(Path.Combine(directory, "skill.json"), JsonSerializer.Serialize(metadata));
        await WriteAtomicAsync(Path.Combine(directory, "SKILL.md"), BuildDocument(skillSlug, effectiveDescription, body));
        return new ProjectSkill(skillSlug, metadata.Name, effectiveDescription, Path.Combine(directory, "SKILL.md"));
    }

    public async Task<bool> DeleteAsync(string projectSlug, string skillSlug)
    {
        var directory = await ResolveSkillPathAsync(projectSlug, skillSlug, mustExist: true);
        if (directory is null) return false;
        Directory.Delete(directory, recursive: true);
        return true;
    }

    private async Task<string> ResolveRootAsync(string projectSlug)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Le projet '{projectSlug}' n'existe pas.");
        return Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "skills");
    }

    private async Task<string?> ResolveSkillPathAsync(string projectSlug, string slug, bool mustExist)
    {
        if (!ValidSlugPattern().IsMatch(slug)) throw new InvalidOperationException("Identifiant de skill invalide.");
        var path = Path.Combine(await ResolveRootAsync(projectSlug), slug);
        return !mustExist || Directory.Exists(path) ? path : null;
    }

    private static async Task<SkillMetadata?> ReadMetadataAsync(string directory)
    {
        var path = Path.Combine(directory, "skill.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SkillMetadata>(await File.ReadAllTextAsync(path)); }
        catch (JsonException) { return null; }
    }

    private static async Task<SkillDocument> NormalizeAsync(
        string directory, string slug, SkillMetadata metadata)
    {
        var instructionsPath = Path.Combine(directory, "SKILL.md");
        var original = await File.ReadAllTextAsync(instructionsPath);
        var parsed = ParseDocument(original);
        var description = NormalizeDescription(metadata.Description ?? parsed.Description, metadata.Name, parsed.Body);
        return new SkillDocument(description, parsed.Body);
    }

    private static SkillDocument ParseDocument(string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return new SkillDocument(null, normalized.Trim());
        var closing = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (closing < 0) return new SkillDocument(null, normalized.Trim());
        var header = normalized[4..closing];
        var match = DescriptionPattern().Match(header);
        string? description = null;
        if (match.Success)
        {
            var value = match.Groups[1].Value.Trim();
            if (value.StartsWith('"'))
            {
                try { description = JsonSerializer.Deserialize<string>(value); }
                catch (JsonException) { description = value.Trim('"'); }
            }
            else description = value;
        }
        return new SkillDocument(description, normalized[(closing + 5)..].Trim());
    }

    private static string BuildDocument(string slug, string description, string body) =>
        $"---\nname: {slug}\ndescription: {JsonSerializer.Serialize(description)}\n---\n\n{body.Trim()}\n";

    private static string NormalizeDescription(string? description, string displayName, string body)
    {
        var value = description?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().TrimStart('#').Trim())
                .FirstOrDefault(line => line.Length > 0);
        }
        if (string.IsNullOrWhiteSpace(value))
            value = $"Use this skill to perform {displayName.Trim()} tasks for this project.";
        if (value.Length <= 240) return value;
        var cut = value.LastIndexOf(' ', 239);
        return value[..(cut > 80 ? cut : 240)].TrimEnd() + "…";
    }

    private static async Task WriteAtomicAsync(string path, string content)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugPattern();
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ValidSlugPattern();
    [GeneratedRegex("^description:\\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex DescriptionPattern();
}
