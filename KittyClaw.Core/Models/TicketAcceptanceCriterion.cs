namespace KittyClaw.Core.Models;

[System.Text.Json.Serialization.JsonConverter(typeof(AcceptanceStatusConverter))]
public enum AcceptanceStatus { Pending = 0, PASS = 1, NeedsRework = 2, ReadyToEvaluate = 4 }

public sealed class AcceptanceStatusConverter : System.Text.Json.Serialization.JsonConverter<AcceptanceStatus>
{
    public override AcceptanceStatus Read(ref System.Text.Json.Utf8JsonReader reader, Type type, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number && reader.TryGetInt32(out var number))
            return number switch { 0 => AcceptanceStatus.Pending, 1 => AcceptanceStatus.PASS, 2 or 3 => AcceptanceStatus.NeedsRework, 4 => AcceptanceStatus.ReadyToEvaluate, _ => throw new System.Text.Json.JsonException("Invalid acceptance status.") };
        if (reader.TokenType != System.Text.Json.JsonTokenType.String) throw new System.Text.Json.JsonException("Invalid acceptance status.");
        var value = reader.GetString();
        if (string.Equals(value, "NotEvaluated", StringComparison.OrdinalIgnoreCase)) return AcceptanceStatus.Pending;
        if (string.Equals(value, "FAIL", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "PARTIAL", StringComparison.OrdinalIgnoreCase)) return AcceptanceStatus.NeedsRework;
        if (Enum.TryParse<AcceptanceStatus>(value, true, out var status) && Enum.IsDefined(status)) return status;
        throw new System.Text.Json.JsonException("Invalid acceptance status.");
    }
    public override void Write(System.Text.Json.Utf8JsonWriter writer, AcceptanceStatus value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

public sealed record CriterionEvidence(string Content, string MediaType = "text/plain", string? FileName = null);
public sealed record TicketAcceptanceCriterion(string Id, string Text, AcceptanceStatus Status = AcceptanceStatus.Pending, CriterionEvidence? Evidence = null)
{
    /// <summary>Keep binary payloads out of text-only routing requests while tracking every evidence revision.</summary>
    public object ToRoutingContext() => new
    {
        Id, Text, Status,
        evidence = Evidence is null ? null : new
        {
            Evidence.MediaType, Evidence.FileName,
            content = Evidence.MediaType.StartsWith("image/")
                ? "Attached image; full evidence is available through get_ticket."
                : Evidence.Content[..Math.Min(4000, Evidence.Content.Length)],
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Evidence.Content)))
        }
    };
}
public sealed record SaveAcceptanceCriterionRequest(string Text, AcceptanceStatus Status = AcceptanceStatus.Pending, CriterionEvidence? Evidence = null);
