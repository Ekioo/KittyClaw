namespace KittyClaw.Core.Models;

/// <summary>
/// Single source of truth for the Claude models KittyClaw offers in its model
/// selectors (action editor, chat drawer, dashboard tiles, member defaults).
/// Each string is passed verbatim to <c>claude --model</c>. The <c>-1m</c>
/// suffixes are KittyClaw routing variants that request the 1M-token context
/// window on Opus; models that ship 1M context by default (Fable 5, Sonnet 5)
/// have no such variant.
/// </summary>
public static class ClaudeModelCatalog
{
    /// <summary>Model used when a member or action has no explicit override.</summary>
    public const string DefaultModel = "claude-sonnet-4-6";

    /// <summary>Selectable Claude models, ordered most-capable first.</summary>
    public static readonly IReadOnlyList<string> Models = new[]
    {
        "claude-fable-5",
        "claude-opus-4-8",
        "claude-opus-4-8-1m",
        "claude-opus-4-7",
        "claude-opus-4-7-1m",
        "claude-sonnet-5",
        DefaultModel,
        "claude-haiku-4-5",
    };
}
