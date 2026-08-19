using KittyClaw.Core.Models;

namespace KittyClaw.Core.Automation;

/// <summary>Per-run boundary enforcement mode. Observe keeps the #168 observation-only behavior;
/// Enforce requires a provider whose adapter can intercept every protected boundary class
/// before the effect, and fails the dispatch closed otherwise.</summary>
public enum BoundaryEnforcementMode { Observe, Enforce }

public enum RuntimeEnforcementLevel { Enforced, ObservationOnly }

public sealed record RuntimeEnforcementCapability(
    CliProvider Provider, BoundaryActionClass Boundary, RuntimeEnforcementLevel Level,
    string Mechanism, string? Exclusion)
{
    public bool Protected => Level == RuntimeEnforcementLevel.Enforced;
}

/// <summary>
/// The single source of truth for which provider × boundary pairs may be advertised as enforced.
/// Owner decision on ticket #170: a pair is Enforced only when its adapter intercepts the action
/// before the external or destructive effect (provider-native pre-effect hook routed through the
/// KittyClaw runtime broker). Post-hoc JSONL observation is never enforcement; providers without a
/// reliable pre-effect hook stay ObservationOnly and protected dispatch on them fails closed.
/// </summary>
public static class RuntimeEnforcementCapabilities
{
    /// <summary>Claude Code (and the Ollama local-model path, which uses the same CLI transport)
    /// exposes PreToolUse hooks that run before every tool effect and can deny it.</summary>
    public const string ClaudeHookMechanism = "Claude Code PreToolUse hook -> KittyClaw runtime broker";

    public const string StreamMechanism = "provider JSONL stream";

    private const string StreamExclusion =
        "Observation only: JSONL events can arrive after the provider already started the effect, " +
        "and the CLI offers no reliable pre-effect hook in non-interactive mode.";

    public static IReadOnlyList<RuntimeEnforcementCapability> Catalogue { get; } =
        Enum.GetValues<CliProvider>().SelectMany(provider => Enum.GetValues<BoundaryActionClass>()
            .Select(boundary => provider == CliProvider.Claude
                ? new RuntimeEnforcementCapability(provider, boundary, RuntimeEnforcementLevel.Enforced,
                    ClaudeHookMechanism, null)
                : new RuntimeEnforcementCapability(provider, boundary, RuntimeEnforcementLevel.ObservationOnly,
                    StreamMechanism, StreamExclusion)))
        .ToArray();

    public static bool CanAdvertiseProtection(CliProvider provider, BoundaryActionClass boundary) =>
        Catalogue.Any(x => x.Provider == provider && x.Boundary == boundary && x.Protected);

    public static IReadOnlyList<BoundaryActionClass> UnenforceableBoundaries(CliProvider provider) =>
        Catalogue.Where(x => x.Provider == provider && !x.Protected).Select(x => x.Boundary).ToArray();
}
