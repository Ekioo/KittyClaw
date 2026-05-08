namespace KittyClaw.Core.Tests.Helpers;

internal static class TestSkillBuilder
{
    /// <summary>
    /// Drops a minimal SKILL.md for an agent into the workspace's .agents/<agent>/ folder.
    /// The skill body embeds a <!--scenario:NAME--> marker that the mock claude reads to
    /// pick which NDJSON to replay.
    /// </summary>
    public static string Create(string workspacePath, string agentName, string scenario, string? extraSkillBody = null)
    {
        var dir = Path.Combine(workspacePath, ".agents", agentName);
        Directory.CreateDirectory(dir);
        var skillPath = Path.Combine(dir, "SKILL.md");
        var body = $"# {agentName}\n\n<!--scenario:{scenario}-->\n\n{extraSkillBody ?? ""}";
        File.WriteAllText(skillPath, body);
        return skillPath;
    }
}
