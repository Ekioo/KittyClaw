using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace KittyClaw.Web.Components;

internal static class SharedRenderHelpers
{
    public static string Truncate(string? s, int max = 80) =>
        s is null ? "" : s.Length <= max ? s : s[..max] + "…";

    public static string GetToolPreview(string toolName, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail) || detail == "{}") return "";
        try
        {
            using var doc = JsonDocument.Parse(detail);
            var root = doc.RootElement;
            return toolName switch
            {
                "Bash" => Truncate(root.TryGetProperty("command", out var c) ? c.GetString() : null),
                "Read" => Truncate(root.TryGetProperty("file_path", out var fp) ? fp.GetString() : null),
                "Write" => Truncate(root.TryGetProperty("file_path", out var fp2) ? fp2.GetString() : null),
                "Edit" => Truncate(root.TryGetProperty("file_path", out var fp3) ? fp3.GetString() : null),
                "Glob" => Truncate(root.TryGetProperty("pattern", out var p) ? p.GetString() : null),
                "Grep" => Truncate(root.TryGetProperty("pattern", out var gp) ? gp.GetString() : null),
                "WebFetch" or "WebSearch" => Truncate(root.TryGetProperty("url", out var u) ? u.GetString()
                    : root.TryGetProperty("query", out var q) ? q.GetString() : null),
                _ => Truncate(root.EnumerateObject().Select(x => x.Value.GetString()).FirstOrDefault())
            };
        }
        catch { return ""; }
    }

    public static bool IsJson(string text)
    {
        var t = text.TrimStart();
        if (t.Length == 0) return false;
        if (t[0] != '{' && t[0] != '[') return false;
        try { JsonDocument.Parse(t); return true; } catch { return false; }
    }

    public static string FormatJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return json; }
    }

    private static readonly Regex _jsonTokenRe = new(
        @"(""(?:[^""\\]|\\.)*"")\s*(:)|""(?:[^""\\]|\\.)*""|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|true|false|null",
        RegexOptions.Compiled);

    public static string HighlightJson(string? json)
    {
        var pretty = FormatJson(json);
        if (string.IsNullOrWhiteSpace(pretty)) return "";
        var sb = new StringBuilder();
        var pos = 0;
        foreach (Match m in _jsonTokenRe.Matches(pretty))
        {
            if (m.Index > pos)
                sb.Append(HttpUtility.HtmlEncode(pretty[pos..m.Index]));
            var encoded = HttpUtility.HtmlEncode(m.Value);
            if (m.Groups[2].Success)
            {
                var keyEncoded = HttpUtility.HtmlEncode(m.Groups[1].Value);
                sb.Append($"<span class=\"json-key\">{keyEncoded}</span>{m.Groups[2].Value}");
            }
            else if (m.Value.StartsWith('"'))
                sb.Append($"<span class=\"json-string\">{encoded}</span>");
            else if (m.Groups[3].Success)
                sb.Append($"<span class=\"json-number\">{encoded}</span>");
            else
                sb.Append(m.Value switch
                {
                    "true" or "false" => $"<span class=\"json-bool\">{encoded}</span>",
                    "null" => $"<span class=\"json-null\">{encoded}</span>",
                    _ => encoded
                });
            pos = m.Index + m.Length;
        }
        if (pos < pretty.Length)
            sb.Append(HttpUtility.HtmlEncode(pretty[pos..]));
        return sb.ToString();
    }
}
