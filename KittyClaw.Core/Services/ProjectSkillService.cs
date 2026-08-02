using System.Text.Json;
using System.Text.RegularExpressions;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

/// <summary>Stores reusable project capabilities independently from agent identities.</summary>
public sealed partial class ProjectSkillService(ProjectService projects)
{
    private sealed record SkillMetadata(string Name);

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
            var metadata = await ReadMetadataAsync(directory);
            result.Add(new ProjectSkill(slug, metadata?.Name ?? slug, instructions));
        }
        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<string?> ReadInstructionsAsync(string projectSlug, string skillSlug)
    {
        var path = await ResolveSkillPathAsync(projectSlug, skillSlug, mustExist: true);
        return path is null ? null : await File.ReadAllTextAsync(Path.Combine(path, "SKILL.md"));
    }

    public async Task<ProjectSkill> CreateAsync(string projectSlug, string name, string instructions)
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
        await WriteAtomicAsync(Path.Combine(directory, "skill.json"), JsonSerializer.Serialize(new SkillMetadata(name.Trim())));
        await WriteAtomicAsync(Path.Combine(directory, "SKILL.md"), instructions ?? "");
        return new ProjectSkill(slug, name.Trim(), Path.Combine(directory, "SKILL.md"));
    }

    public async Task<ProjectSkill?> UpdateAsync(string projectSlug, string skillSlug, string? name, string? instructions)
    {
        var directory = await ResolveSkillPathAsync(projectSlug, skillSlug, mustExist: true);
        if (directory is null) return null;
        var metadata = await ReadMetadataAsync(directory) ?? new SkillMetadata(skillSlug);
        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Le nom du skill est requis.");
            metadata = new SkillMetadata(name.Trim());
            await WriteAtomicAsync(Path.Combine(directory, "skill.json"), JsonSerializer.Serialize(metadata));
        }
        if (instructions is not null)
            await WriteAtomicAsync(Path.Combine(directory, "SKILL.md"), instructions);
        return new ProjectSkill(skillSlug, metadata.Name, Path.Combine(directory, "SKILL.md"));
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
}
