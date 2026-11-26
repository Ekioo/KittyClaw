using System.Globalization;

namespace KittyClaw.Core.Models;

public static class ProjectAvatarInitials
{
    public static string FromName(string? name)
    {
        var words = (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";

        var first = StringInfo.GetNextTextElement(words[0]);
        var second = words.Length > 1
            ? StringInfo.GetNextTextElement(words[1])
            : words[0].Length > first.Length
                ? StringInfo.GetNextTextElement(words[0], first.Length)
                : "";
        return (first + second).ToUpperInvariant();
    }
}
