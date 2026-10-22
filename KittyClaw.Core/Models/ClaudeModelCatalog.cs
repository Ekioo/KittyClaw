namespace KittyClaw.Core.Models;

/// <summary>
/// Single source of truth for the Claude models KittyClaw offers in its model
/// selectors (action editor, chat drawer, dashboard tiles, member defaults).
/// Each string is passed verbatim to <c>claude --model</c>. Current flagship
/// models already ship with a 1M-token context window, so the catalog contains
/// only official model identifiers rather than synthetic context variants.
/// </summary>
public static class ClaudeModelCatalog
{
    /// <summary>Model used when a member or action has no explicit override.</summary>
    public const string DefaultModel = "claude-sonnet-5-5";

    /// <summary>Selectable Claude models, ordered most-capable first.</summary>
    public static readonly IReadOnlyList<string> Models = new[]
    {
        "claude-fable-5-1",
        "claude-opus-5-5",
        DefaultModel,
        "claude-haiku-5-5",
    };
}
