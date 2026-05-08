using System.Text.RegularExpressions;

namespace KittyClaw.ClaudeMock;

internal static class ScenarioMatcher
{
    private static readonly Regex Marker = new(@"<!--\s*scenario:\s*([a-zA-Z0-9_\-]+)\s*-->", RegexOptions.Compiled);

    public static string? FromPrompt(string prompt)
    {
        var m = Marker.Match(prompt);
        return m.Success ? m.Groups[1].Value : null;
    }
}
