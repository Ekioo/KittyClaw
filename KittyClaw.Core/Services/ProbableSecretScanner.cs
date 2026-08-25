using System.Text.RegularExpressions;

namespace KittyClaw.Core.Services;

internal static partial class ProbableSecretScanner
{
    public static bool ContainsProbableSecret(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
            return false;

        var content = File.ReadAllText(path);
        foreach (Match match in SecretAssignmentRegex().Matches(content))
        {
            if (match.Groups["quote"].Success)
                return true;

            var value = match.Groups["value"].Value;
            var remainder = content.AsSpan(match.Index + match.Length).TrimStart();
            if (IdentifierRegex().IsMatch(value)
                && !remainder.IsEmpty
                && ",;)}]".Contains(remainder[0]))
                continue;

            return true;
        }

        return false;
    }

    // Quoted values are literals. An unquoted code identifier followed by ordinary
    // statement/object punctuation is a variable reference, not embedded secret material.
    [GeneratedRegex("(?i)(?:api[_-]?key|access[_-]?token|client[_-]?secret|password|private[_-]?key)\\s*[:=]\\s*(?:(?<quote>['\\\"])(?<value>[-A-Za-z0-9_\\/+=]{8,})\\k<quote>|(?<value>[-A-Za-z0-9_\\/+=]{8,}))")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex IdentifierRegex();
}
