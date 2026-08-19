using System.Text.RegularExpressions;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

/// <summary>
/// Scans text destined for a ".kittyclaw-pipeline" kit and reports blocking occurrences:
/// probable secrets, URL credentials, authentication headers, absolute or user paths, and
/// out-of-folder references inside skill files. Placeholders ({{input.NAME}}, {{secret.NAME}})
/// are the sanctioned representation and are never flagged.
/// </summary>
internal static class PipelineKitScanner
{
    private sealed record Rule(string Category, Regex Pattern, string Guidance, bool MaskValue, bool SkillFilesOnly = false);

    private const string SecretGuidance = "Remplacez la valeur par une référence {{secret.NAME}} ; la valeur réelle reste dans le vault du projet.";
    private const string ParameterGuidance = "Remplacez la valeur spécifique par un paramètre {{input.NAME}}.";

    private static readonly Rule[] Rules =
    [
        new("probable-secret",
            new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled),
            SecretGuidance, MaskValue: false),
        new("probable-secret",
            new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{6,}", RegexOptions.Compiled),
            SecretGuidance, MaskValue: true),
        new("probable-secret",
            new Regex(@"(?:sk-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,})\b", RegexOptions.Compiled),
            SecretGuidance, MaskValue: true),
        // Keyword assignments: the value must contain a digit to keep prose ("secret : utilisez…") exportable.
        new("probable-secret",
            new Regex(@"(?i)[\w-]*(?:api[_-]?key|apikey|secret|token|password|passwd|pwd|access[_-]?key|credential)[\w-]*[""']?\s*[:=]\s*[""']?(?!\{\{)(?<value>(?=[^\s""',;]*\d)[^\s""',;]{8,})", RegexOptions.Compiled),
            SecretGuidance, MaskValue: true),
        new("url-credentials",
            new Regex(@"(?i)\b[a-z][a-z0-9+.-]*://[^/\s:@]+:(?<value>[^@/\s]+)@", RegexOptions.Compiled),
            SecretGuidance, MaskValue: true),
        new("authorization-header",
            new Regex(@"(?i)[""']?(?:authorization|proxy-authorization|x-api-key|x-auth-token|cookie)[""']?\s*[:=]\s*[""']?(?!\{\{)(?<value>[^\s""'][^""']*)", RegexOptions.Compiled),
            SecretGuidance, MaskValue: true),
        new("authorization-header",
            new Regex(@"\bBearer\s+(?!\{\{)(?<value>[A-Za-z0-9._~+/=-]{12,})", RegexOptions.Compiled),
            SecretGuidance, MaskValue: true),
        new("absolute-path",
            new Regex(@"(?i)(?<![a-z0-9])[a-z]:[\\/][^\s""'<>|]*|\\\\[a-z0-9_.$-]+\\[^\s""'<>|]*|(?<![\w.])/(?:home|Users|root)/[A-Za-z0-9._-]+[^\s""'<>|]*", RegexOptions.Compiled),
            ParameterGuidance, MaskValue: false),
        new("out-of-folder-reference",
            new Regex(@"\.\.[\\/][^\s""'<>|]*", RegexOptions.Compiled),
            "Copiez le fichier dans le dossier de la skill ou déclarez un prérequis explicite ; aucune référence hors dossier n’est embarquée.",
            MaskValue: false, SkillFilesOnly: true),
    ];

    public static void ScanText(string archivePath, string content, bool isSkillFile, List<PipelineExportFinding> findings)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            foreach (var rule in Rules)
            {
                if (rule.SkillFilesOnly && !isSkillFile) continue;
                foreach (Match match in rule.Pattern.Matches(lines[index]))
                    findings.Add(new PipelineExportFinding(
                        rule.Category, archivePath, index + 1, BuildExcerpt(match, rule.MaskValue), rule.Guidance));
            }
        }
    }

    private static string BuildExcerpt(Match match, bool maskValue)
    {
        var excerpt = match.Value;
        if (maskValue)
        {
            var value = match.Groups["value"].Success ? match.Groups["value"].Value : match.Value;
            excerpt = match.Groups["value"].Success
                ? match.Value.Replace(value, Mask(value), StringComparison.Ordinal)
                : Mask(match.Value);
        }
        return excerpt.Length <= 120 ? excerpt : excerpt[..120] + "…";
    }

    private static string Mask(string value) =>
        value.Length <= 4 ? "****" : value[..4] + new string('*', Math.Min(8, value.Length - 4)) + "…";
}
