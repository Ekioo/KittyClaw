using System.Text.Json;
using System.Text.Json.Serialization;

namespace KittyClaw.QaRunner;

/// <summary>
/// JSON shape consumed by <see cref="ScenarioRunner"/>. The qa-tester agent
/// authors one of these per ticket. Kept deliberately small in v1; extend the
/// action types as the QA library grows.
/// </summary>
public sealed class Scenario
{
    public List<ScenarioAction> Setup { get; set; } = new();
    public List<ScenarioAction> Actions { get; set; } = new();
    public ScenarioVerdict Verdict { get; set; } = new();
}

public sealed class ScenarioAction
{
    public required string Type { get; set; }

    // Common fields — only the relevant ones are read per action type.
    public string? Url { get; set; }
    public string? Selector { get; set; }
    public string? Property { get; set; }
    public string? Expected { get; set; }
    public string? Value { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Project { get; set; }
    public string? WorkspacePath { get; set; }
    public int? Ms { get; set; }

    // Generic api action fields
    public string? Method { get; set; }
    public string? Path { get; set; }
    public JsonElement? Body { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    /// <summary>Maps variable name to dotted JSON path (e.g. "ticketId" → "id").</summary>
    public Dictionary<string, string>? Extract { get; set; }

    // assertJson fields
    public string? JsonPath { get; set; }

    // Convenience shortcut fields (createTicket, assignTicket, setStatus)
    public string? Title { get; set; }
    public string? Status { get; set; }
    public string? Priority { get; set; }
    public string? AssignedTo { get; set; }
    public string? CreatedBy { get; set; }
}

public sealed class ScenarioVerdict
{
    /// <summary>"all-asserts-pass" (default) or "manual" (let the caller decide).</summary>
    public string PassOn { get; set; } = "all-asserts-pass";
}

public sealed class ScenarioResult
{
    public required string Verdict { get; set; }   // "PASS" | "FAIL"
    public string? Notes { get; set; }
    public List<ScreenshotEntry> Screenshots { get; set; } = new();
    public List<AssertionEntry> Assertions { get; set; } = new();
}

public sealed class ScreenshotEntry
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? LocalPath { get; set; }
    public string? UploadedUrl { get; set; }
}

public sealed class AssertionEntry
{
    public required string Selector { get; set; }
    public required string Property { get; set; }
    public string? Expected { get; set; }
    public string? Actual { get; set; }
    public required bool Passed { get; set; }
}

[JsonSerializable(typeof(Scenario))]
[JsonSerializable(typeof(ScenarioResult))]
internal partial class ScenarioJsonContext : JsonSerializerContext { }
