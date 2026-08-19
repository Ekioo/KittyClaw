namespace KittyClaw.Core.Automation;

/// <summary>DeepSeek models exposed through its Anthropic-compatible Claude Code integration.</summary>
public static class DeepSeekModelCatalog
{
    public const string ModelPrefix = "deepseek:";
    public const string ApiKeySecretName = "DEEPSEEK_API_KEY";

    public static readonly IReadOnlyList<string> Models =
    [
        "deepseek:deepseek-v4-pro[1m]",
        "deepseek:deepseek-v4-flash",
    ];

    public static bool IsDeepSeekModel(string? model) =>
        model?.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public static string ToCliModel(string model) =>
        IsDeepSeekModel(model) ? model[ModelPrefix.Length..] : model;

    public static Task<IReadOnlyList<string>> ListModelsAsync() => Task.FromResult(Models);
}
