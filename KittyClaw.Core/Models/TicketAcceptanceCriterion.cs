namespace KittyClaw.Core.Models;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum AcceptanceStatus { NotEvaluated, PASS, FAIL, PARTIAL }

public sealed record CriterionEvidence(string Content, string MediaType = "text/plain", string? FileName = null);
public sealed record TicketAcceptanceCriterion(string Id, string Text, AcceptanceStatus Status = AcceptanceStatus.NotEvaluated, CriterionEvidence? Evidence = null)
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
public sealed record SaveAcceptanceCriterionRequest(string Text, AcceptanceStatus Status = AcceptanceStatus.NotEvaluated, CriterionEvidence? Evidence = null);
