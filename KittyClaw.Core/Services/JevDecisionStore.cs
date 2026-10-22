using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KittyClaw.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace KittyClaw.Core.Services;

public sealed record JevProposalDecision(string ProposalId, int TicketId, bool Accepted, DateTime DecidedAt,
    string Actor, string? Reason, JevRoutingProposal Proposal, int? AppliedTargetColumnId);
public sealed record JevRejectionFeedback(string ProposalId, string? RejectedChoice, string? Reason);

/// <summary>Immutable human decisions retain the exact proposal revision, including after model refresh.</summary>
public sealed class JevDecisionStore(ProjectService projects)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string ProposalId(JevRoutingProposal proposal) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(proposal, Json))));

    internal static async Task PrepareAsync(TodoDbContext db)
    {
        await JevQualificationService.InitializeAsync(db);
        await TicketService.EnsureActivityTableAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS JevProposalDecisions (
                ProposalId TEXT NOT NULL PRIMARY KEY, TicketId INTEGER NOT NULL, Payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_JevProposalDecisions_TicketId ON JevProposalDecisions(TicketId)
            """);
    }

    public async Task<List<JevProposalDecision>> ListAsync(string slug, int ticketId, CancellationToken ct = default)
    {
        await using var db = projects.GetProjectDb(slug);
        await PrepareAsync(db);
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Payload FROM JevProposalDecisions WHERE TicketId = @id ORDER BY json_extract(Payload, '$.decidedAt') DESC";
        var id = command.CreateParameter(); id.ParameterName = "@id"; id.Value = ticketId; command.Parameters.Add(id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<JevProposalDecision>();
        while (await reader.ReadAsync(ct)) results.Add(JsonSerializer.Deserialize<JevProposalDecision>(reader.GetString(0), Json)!);
        return results;
    }

    internal static async Task<JevRoutingProposal?> FindCurrentAsync(TodoDbContext db, int ticketId, string proposalId)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT Payload FROM JevObservations WHERE TicketId = @id UNION ALL SELECT Payload FROM JevPostRunObservations WHERE TicketId = @id";
        var id = command.CreateParameter(); id.ParameterName = "@id"; id.Value = ticketId; command.Parameters.Add(id);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var proposal = JsonSerializer.Deserialize<JevRoutingProposal>(reader.GetString(0), Json)!;
            if (ProposalId(proposal) == proposalId) return proposal;
        }
        return null;
    }

    internal static async Task SaveAsync(TodoDbContext db, JevProposalDecision decision)
    {
        var payload = JsonSerializer.Serialize(decision, Json);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JevProposalDecisions (ProposalId, TicketId, Payload) VALUES ({decision.ProposalId}, {decision.TicketId}, {payload})");
        db.ActivityEntries.Add(new()
        {
            TicketId = decision.TicketId, Author = decision.Actor, CreatedAt = decision.DecidedAt,
            Text = $"JEV proposal {decision.ProposalId[..12]} {(decision.Accepted ? "accepted" : "rejected")}: {decision.Proposal.Destinations.GetValueOrDefault(decision.Proposal.Choice ?? "", "none")}." +
                (decision.Accepted ? " Applied through the normal workflow." : " Excluded from requalification of this ticket context.") +
                (string.IsNullOrWhiteSpace(decision.Reason) ? "" : $" Reason: {decision.Reason}")
        });
    }
}
