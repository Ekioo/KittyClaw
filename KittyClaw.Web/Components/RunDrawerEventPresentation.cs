using System.Text;
using System.Text.RegularExpressions;
using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Components;

public sealed record RunDrawerEntry(StreamEvent Event, StderrPresentation? Stderr = null);

public sealed record StderrPresentation(
    DateTime At,
    string Summary,
    string Preview,
    string Raw,
    int EventCount,
    bool IsTruncated);

public static partial class RunDrawerEventPresentation
{
    internal const int PreviewMaxCharacters = 1200;
    internal const int PreviewMaxLines = 12;

    public static IReadOnlyList<RunDrawerEntry> Group(IEnumerable<StreamEvent> events)
    {
        var result = new List<RunDrawerEntry>();
        List<StreamEvent>? stderr = null;

        void FlushStderr()
        {
            if (stderr is not { Count: > 0 }) return;
            var raw = Sanitize(string.Join('\n', stderr.Select(ev => ev.Text)));
            result.Add(new RunDrawerEntry(stderr[0], new StderrPresentation(
                stderr[0].At,
                ExtractSummary(raw),
                CreatePreview(raw, out var truncated),
                raw,
                stderr.Count,
                truncated)));
            stderr = null;
        }

        foreach (var ev in events)
        {
            if (ev.Kind == "stderr")
            {
                (stderr ??= []).Add(ev);
                continue;
            }

            FlushStderr();
            result.Add(new RunDrawerEntry(ev));
        }

        FlushStderr();
        return result;
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var value = OscSequence().Replace(text, "");
        value = AnsiSequence().Replace(value, "");
        value = value.Replace("\r\n", "\n").Replace('\r', '\n');

        var clean = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\n' or '\t' || !char.IsControl(c))
                clean.Append(c);
        }
        return clean.ToString();
    }

    public static string ExtractSummary(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Command failed";

        // Error pages often wrap the useful exception in markup. Decode nothing here: the
        // result is rendered as text, and leaving entities encoded avoids turning input into HTML.
        var searchable = HtmlTag().Replace(raw, " ");
        searchable = Regex.Replace(searchable, @"[ \t]+", " ");
        foreach (var line in searchable.Split('\n').Select(line => line.Trim()))
        {
            if (line.Length == 0) continue;
            var match = ExceptionLine().Match(line);
            if (match.Success)
                return Limit($"{match.Groups["type"].Value}: {match.Groups["message"].Value.Trim()}", 220);
        }

        foreach (var line in searchable.Split('\n').Select(line => line.Trim()))
        {
            if (line.Length == 0) continue;
            if (ActionableFailureLine().IsMatch(line))
                return Limit(line, 220);
        }

        return "Command failed";
    }

    private static string CreatePreview(string raw, out bool truncated)
    {
        var lines = raw.Split('\n');
        var selected = string.Join('\n', lines.Take(PreviewMaxLines));
        if (selected.Length > PreviewMaxCharacters)
            selected = selected[..PreviewMaxCharacters];
        truncated = lines.Length > PreviewMaxLines || raw.Length > selected.Length;
        return truncated ? selected.TrimEnd() + "\n…" : selected;
    }

    private static string Limit(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", RegexOptions.Compiled)]
    private static partial Regex OscSequence();

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|[@-_])", RegexOptions.Compiled)]
    private static partial Regex AnsiSequence();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Compiled)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?<type>(?:[A-Za-z_][\w+`]*\.)*[A-Za-z_][\w+`]*(?:Exception|Error))\s*:\s*(?<message>[^\r\n<]+)", RegexOptions.Compiled)]
    private static partial Regex ExceptionLine();

    [GeneratedRegex(@"\b(?:timed?\s*out|timeout|HTTP\s+[45]\d\d|status(?:\s+code)?\s*[=:]?\s*[45]\d\d|connection\s+(?:refused|reset)|permission\s+denied|unauthorized|forbidden)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ActionableFailureLine();
}
