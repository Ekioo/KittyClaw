using KittyClaw.Core.Models;

namespace KittyClaw.Core.Automation;

/// <summary>Explicit effort capabilities for the models offered in chat.</summary>
public static class ModelEffort
{
    /// <summary>Chat sends this explicit initial value instead of inheriting CLI settings.</summary>
    public static string Initial(string? model) => Options(model).Count > 0 ? "medium" : "";

    public static string Effective(string? model, string? effort) =>
        Options(model).Contains(effort ?? "") ? effort! : Initial(model);

    public static IReadOnlyList<string> Options(string? model)
    {
        if (model is null) return [];
        var name = model.StartsWith("claude:") ? model[7..] : model;
        if (ClaudeModelCatalog.Models.Contains(name))
            return ["low", "medium", "high", "xhigh", "max"];
        if (CodexCli.Models.Contains(model))
            return model.EndsWith("luna", StringComparison.Ordinal)
                ? ["low", "medium", "high", "xhigh", "max"]
                : ["low", "medium", "high", "xhigh", "max", "ultra"];
        if (name is "claude-opus-4-6" or "claude-sonnet-4-6")
            return ["low", "medium", "high", "max"];
        if (name == "claude-opus-4-5") return ["low", "medium", "high"];
        return [];
    }
}
