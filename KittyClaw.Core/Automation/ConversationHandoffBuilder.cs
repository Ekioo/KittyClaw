using System.Text;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Automation;

/// <summary>Builds a bounded transcript when an interactive chat changes CLI provider.</summary>
public static class ConversationHandoffBuilder
{
    private const int MaxMessages = 12;
    private const int MaxCharacters = 12_000;
    private const int MaxCharactersPerMessage = 900;

    public static string? Build(
        IReadOnlyList<ChatMessageRow> history,
        string? previousProvider,
        CliProvider nextProvider)
    {
        var conversational = history
            .Where(message => message.Role is "user" or "assistant")
            .TakeLast(MaxMessages)
            .ToList();
        if (conversational.Count == 0) return null;

        var previous = string.IsNullOrWhiteSpace(previousProvider)
            ? "a previous model"
            : previousProvider;
        var sb = new StringBuilder();
        sb.AppendLine("## Conversation handoff");
        sb.AppendLine();
        sb.AppendLine($"The owner changed the chat model from {previous} to {nextProvider}. " +
                      "Use the transcript below as conversation context and answer the owner's current message normally. " +
                      "Do not repeat the transcript unless asked.");
        sb.AppendLine();
        sb.AppendLine("<conversation-history>");

        foreach (var message in conversational)
        {
            var label = message.Role == "user" ? "Owner" : "Assistant";
            var text = message.Text
                .Replace("<conversation-history>", "&lt;conversation-history&gt;", StringComparison.OrdinalIgnoreCase)
                .Replace("</conversation-history>", "&lt;/conversation-history&gt;", StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (text.Length == 0) continue;
            if (text.Length > MaxCharactersPerMessage)
                text = text[..MaxCharactersPerMessage] + "…";

            var remaining = MaxCharacters - sb.Length;
            if (remaining <= label.Length + 8) break;
            if (text.Length > remaining - label.Length - 4)
                text = text[..Math.Max(0, remaining - label.Length - 5)] + "…";
            sb.Append(label).Append(": ").AppendLine(text);
        }

        sb.AppendLine("</conversation-history>");
        return sb.ToString();
    }
}
