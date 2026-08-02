using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public sealed class ApprovalRegistryService(ProjectService projects)
{
    private const int MaxTicketGrantHours = 24;

    public async Task<ApprovalRequestRecord> RegisterRequestAsync(string projectSlug, ApprovalRequestInput input)
    {
        ValidateRequest(input);
        await using var connection = await OpenAsync(projectSlug);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO ApprovalRequests
              (RequestId,DedupeKey,SchemaVersion,ProjectSlug,ActionClass,Operation,ResourceKind,ResourceCanonicalId,ResourceDisplay,Reason,RiskSummary,ScopeRequested,DurationRequestedSeconds,ProviderName,ProviderCliVersion,AdapterVersion,EnforcementProfile,RunId,TicketId,AgentSlug,ObservedAt,ExpiresAt,ToolName,RedactedArgumentsHash,EvidenceSource)
            VALUES
              ($requestId,$dedupeKey,$schemaVersion,$projectSlug,$actionClass,$operation,$resourceKind,$resourceCanonicalId,$resourceDisplay,$reason,$riskSummary,$scopeRequested,$duration,$provider,$cli,$adapter,$profile,$runId,$ticketId,$agent,$observed,$expires,$tool,$argsHash,$source)
            ON CONFLICT(ProjectSlug,DedupeKey) DO NOTHING;
            """, Params(input, projectSlug));
        var requestId = await ScalarAsync<string>(connection, transaction,
            "SELECT RequestId FROM ApprovalRequests WHERE ProjectSlug=$projectSlug AND DedupeKey=$dedupeKey", ("$projectSlug", projectSlug), ("$dedupeKey", input.DedupeKey));
        await transaction.CommitAsync();
        return (await GetRequestAsync(connection, requestId!))!;
    }

    public async Task<ApprovalDecisionRecord> DecideAsync(string projectSlug, ApprovalDecisionInput input)
    {
        await using var connection = await OpenAsync(projectSlug);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var request = await GetRequestAsync(connection, input.RequestId, transaction)
            ?? throw new InvalidOperationException("Approval request not found.");
        if (request.ExpiresAt <= input.CreatedAt) throw new InvalidOperationException("Approval request has expired.");
        if (input.ExpiresAt <= input.CreatedAt) throw new InvalidOperationException("Decision expiry must be after creation.");
        if (input.Kind == ApprovalDecisionKind.AllowForTicket && input.ExpiresAt > input.CreatedAt.AddHours(MaxTicketGrantHours))
            throw new InvalidOperationException("Ticket approval cannot exceed 24 hours.");
        var active = await ScalarAsync<long>(connection, transaction,
            "SELECT COUNT(*) FROM ApprovalDecisions WHERE RequestId=$requestId AND SupersededByDecisionId IS NULL", ("$requestId", input.RequestId));
        if (active > (input.SupersededDecisionId is null ? 0 : 1))
            throw new InvalidOperationException("Approval request already has an active decision.");
        if (input.SupersededDecisionId is not null)
        {
            var superseded = await ExecuteAsync(connection, transaction,
                "UPDATE ApprovalDecisions SET SupersededByDecisionId=$newId WHERE DecisionId=$oldId AND RequestId=$requestId AND SupersededByDecisionId IS NULL",
                ("$newId", input.DecisionId), ("$oldId", input.SupersededDecisionId), ("$requestId", input.RequestId));
            if (superseded != 1) throw new InvalidOperationException("Decision to supersede is not active.");
        }
        await ExecuteAsync(connection, transaction, """
            INSERT INTO ApprovalDecisions (DecisionId,RequestId,Kind,Actor,CreatedAt,ExpiresAt,Scope,Reason,SupersededDecisionId)
            VALUES ($id,$requestId,$kind,$actor,$created,$expires,$scope,$reason,$superseded)
            """, ("$id", input.DecisionId), ("$requestId", input.RequestId), ("$kind", input.Kind.ToString()),
            ("$actor", input.Actor), ("$created", ToDb(input.CreatedAt)), ("$expires", ToDb(input.ExpiresAt)),
            ("$scope", input.Scope), ("$reason", input.Reason), ("$superseded", input.SupersededDecisionId));
        await transaction.CommitAsync();
        return new(input.DecisionId, input.RequestId, input.Kind, input.Actor, input.CreatedAt, input.ExpiresAt,
            input.Scope, input.Reason, input.SupersededDecisionId, null);
    }

    public async Task<ApprovalReceiptRecord> ConsumeOnceAsync(string projectSlug, ApprovalReceiptInput input)
    {
        await using var connection = await OpenAsync(projectSlug);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var decision = await ReadDecisionAsync(connection, transaction, input.DecisionId)
            ?? throw new InvalidOperationException("Approval decision not found.");
        if (decision.RequestId != input.RequestId) throw new InvalidOperationException("Decision does not match request.");
        if (decision.Kind != ApprovalDecisionKind.AllowOnce) throw new InvalidOperationException("Decision is not allow-once.");
        if (decision.ExpiresAt <= input.StartedAt) throw new InvalidOperationException("Approval decision has expired.");
        var changed = await ExecuteAsync(connection, transaction,
            "UPDATE ApprovalDecisions SET ConsumedAt=$at WHERE DecisionId=$id AND ConsumedAt IS NULL",
            ("$at", ToDb(input.StartedAt)), ("$id", input.DecisionId));
        if (changed != 1) throw new InvalidOperationException("Approval decision was already consumed.");
        var receipt = await InsertReceiptAsync(connection, transaction, input);
        await transaction.CommitAsync();
        return receipt;
    }

    public async Task<ApprovalReceiptRecord> AddReceiptAsync(string projectSlug, ApprovalReceiptInput input)
    {
        await using var connection = await OpenAsync(projectSlug);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var decision = await ReadDecisionAsync(connection, transaction, input.DecisionId)
            ?? throw new InvalidOperationException("Approval decision not found.");
        if (decision.RequestId != input.RequestId) throw new InvalidOperationException("Decision does not match request.");
        var receipt = await InsertReceiptAsync(connection, transaction, input);
        await transaction.CommitAsync();
        return receipt;
    }

    public async Task<IReadOnlyList<ApprovalRequestRecord>> QueryRequestsAsync(string projectSlug, ApprovalRegistryQuery query)
    {
        await using var connection = await OpenAsync(projectSlug);
        var conditions = new List<string> { "ProjectSlug=$project" };
        var parameters = new List<(string, object?)> { ("$project", projectSlug) };
        if (query.RunId is not null) { conditions.Add("RunId=$run"); parameters.Add(("$run", query.RunId)); }
        if (query.TicketId is not null) { conditions.Add("TicketId=$ticket"); parameters.Add(("$ticket", query.TicketId)); }
        if (query.Provider is not null) { conditions.Add("ProviderName=$provider"); parameters.Add(("$provider", query.Provider)); }
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT RequestId FROM ApprovalRequests WHERE {string.Join(" AND ", conditions)} ORDER BY ObservedAt";
        AddParameters(command, parameters.ToArray());
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        var result = new List<ApprovalRequestRecord>();
        foreach (var id in ids) result.Add((await GetRequestAsync(connection, id))!);
        return result;
    }

    public async Task<IReadOnlyList<ApprovalDecisionRecord>> QueryDecisionsAsync(string projectSlug, string? requestId = null)
    {
        await using var connection = await OpenAsync(projectSlug);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT DecisionId FROM ApprovalDecisions WHERE $requestId IS NULL OR RequestId=$requestId ORDER BY CreatedAt";
        command.Parameters.AddWithValue("$requestId", (object?)requestId ?? DBNull.Value);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        var result = new List<ApprovalDecisionRecord>();
        foreach (var id in ids) result.Add((await ReadDecisionAsync(connection, null, id))!);
        return result;
    }

    public async Task<IReadOnlyList<ApprovalReceiptRecord>> QueryReceiptsAsync(string projectSlug, string? requestId = null)
    {
        await using var connection = await OpenAsync(projectSlug);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT ReceiptId,DecisionId,RequestId,EnforcementPointId,AdapterVersion,EnforcementProfile,StartedAt,EndedAt,Outcome,RedactedEffectIdentity,PreviousHash,IntegrityHash FROM ApprovalReceipts WHERE $requestId IS NULL OR RequestId=$requestId ORDER BY Sequence";
        command.Parameters.AddWithValue("$requestId", (object?)requestId ?? DBNull.Value);
        var result = new List<ApprovalReceiptRecord>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadReceipt(reader));
        return result;
    }

    public async Task RecoverInterruptedAttemptsAsync(string projectSlug, DateTime recoveredAt)
    {
        await using var connection = await OpenAsync(projectSlug);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT d.DecisionId,d.RequestId,r.AdapterVersion,r.EnforcementProfile,d.ConsumedAt
            FROM ApprovalDecisions d JOIN ApprovalRequests r ON r.RequestId=d.RequestId
            WHERE d.ConsumedAt IS NOT NULL AND NOT EXISTS
              (SELECT 1 FROM ApprovalReceipts x WHERE x.DecisionId=d.DecisionId AND x.Outcome IN ('EffectSucceeded','EffectFailed','Unknown'))
            """;
        var rows = new List<(string Decision, string Request, string Adapter, string Profile, DateTime Started)>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), FromDb(reader.GetString(4))));
        foreach (var row in rows)
            await InsertReceiptAsync(connection, transaction, new(Guid.NewGuid().ToString("N"), row.Decision, row.Request,
                "restart-recovery", row.Adapter, row.Profile, row.Started, recoveredAt, ApprovalReceiptOutcome.Unknown, "unknown"));
        await transaction.CommitAsync();
    }

    private async Task<SqliteConnection> OpenAsync(string slug)
    {
        var dbPath = projects.GetProjectDbPath(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var connection = new SqliteConnection($"Data Source={dbPath};Default Timeout=30");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<ApprovalRequestRecord?> GetRequestAsync(SqliteConnection connection, string id, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT RequestId,DedupeKey,SchemaVersion,ProjectSlug,ActionClass,Operation,ResourceKind,ResourceCanonicalId,ResourceDisplay,Reason,RiskSummary,ScopeRequested,DurationRequestedSeconds,ProviderName,ProviderCliVersion,AdapterVersion,EnforcementProfile,RunId,TicketId,AgentSlug,ObservedAt,ExpiresAt,ToolName,RedactedArgumentsHash,EvidenceSource FROM ApprovalRequests WHERE RequestId=$id";
        command.Parameters.AddWithValue("$id", id);
        await using var r = await command.ExecuteReaderAsync(); if (!await r.ReadAsync()) return null;
        var expires = FromDb(r.GetString(21));
        var state = expires <= DateTime.UtcNow ? "expired" : await ResolveStateAsync(connection, transaction, id);
        return new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9),N(r,10),N(r,11),NI(r,12),r.GetString(13),r.GetString(14),r.GetString(15),r.GetString(16),r.GetString(17),r.GetInt32(18),r.GetString(19),FromDb(r.GetString(20)),expires,N(r,22),N(r,23),N(r,24),state);
    }

    private static async Task<string> ResolveStateAsync(SqliteConnection c, SqliteTransaction? t, string id)
    {
        var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText="SELECT Kind,ExpiresAt,ConsumedAt FROM ApprovalDecisions WHERE RequestId=$id AND SupersededByDecisionId IS NULL ORDER BY CreatedAt DESC LIMIT 1"; cmd.Parameters.AddWithValue("$id",id);
        await using var r=await cmd.ExecuteReaderAsync(); if(!await r.ReadAsync()) return "pending";
        if(FromDb(r.GetString(1))<=DateTime.UtcNow) return "expired";
        if(!r.IsDBNull(2)) return "consumed";
        return r.GetString(0)==nameof(ApprovalDecisionKind.Deny) ? "denied" : "allowed";
    }

    private static async Task<ApprovalDecisionRecord?> ReadDecisionAsync(SqliteConnection c, SqliteTransaction? t, string id)
    { var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="SELECT DecisionId,RequestId,Kind,Actor,CreatedAt,ExpiresAt,Scope,Reason,SupersededDecisionId,ConsumedAt FROM ApprovalDecisions WHERE DecisionId=$id";cmd.Parameters.AddWithValue("$id",id);await using var r=await cmd.ExecuteReaderAsync();if(!await r.ReadAsync())return null;return new(r.GetString(0),r.GetString(1),Enum.Parse<ApprovalDecisionKind>(r.GetString(2)),r.GetString(3),FromDb(r.GetString(4)),FromDb(r.GetString(5)),r.GetString(6),r.GetString(7),N(r,8),r.IsDBNull(9)?null:FromDb(r.GetString(9))); }

    private static async Task<ApprovalReceiptRecord> InsertReceiptAsync(SqliteConnection c, SqliteTransaction t, ApprovalReceiptInput i)
    {
        if (i.EndedAt < i.StartedAt)
            throw new InvalidOperationException("Receipt end time cannot be before its start time.");

        var previous=await ScalarAsync<string>(c,t,"SELECT IntegrityHash FROM ApprovalReceipts ORDER BY Sequence DESC LIMIT 1")??"GENESIS";
        var payload=$"{i.ReceiptId}|{i.DecisionId}|{i.RequestId}|{i.EnforcementPointId}|{i.AdapterVersion}|{i.EnforcementProfile}|{ToDb(i.StartedAt)}|{ToDb(i.EndedAt)}|{i.Outcome}|{i.RedactedEffectIdentity}|{previous}";
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        await ExecuteAsync(c,t,"INSERT INTO ApprovalReceipts (ReceiptId,DecisionId,RequestId,EnforcementPointId,AdapterVersion,EnforcementProfile,StartedAt,EndedAt,Outcome,RedactedEffectIdentity,PreviousHash,IntegrityHash) VALUES ($id,$decision,$request,$point,$adapter,$profile,$started,$ended,$outcome,$effect,$previous,$hash)",("$id",i.ReceiptId),("$decision",i.DecisionId),("$request",i.RequestId),("$point",i.EnforcementPointId),("$adapter",i.AdapterVersion),("$profile",i.EnforcementProfile),("$started",ToDb(i.StartedAt)),("$ended",ToDb(i.EndedAt)),("$outcome",i.Outcome.ToString()),("$effect",i.RedactedEffectIdentity),("$previous",previous),("$hash",hash));
        return new(i.ReceiptId,i.DecisionId,i.RequestId,i.EnforcementPointId,i.AdapterVersion,i.EnforcementProfile,i.StartedAt,i.EndedAt,i.Outcome,i.RedactedEffectIdentity,previous,hash);
    }

    private static ApprovalReceiptRecord ReadReceipt(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),FromDb(r.GetString(6)),FromDb(r.GetString(7)),Enum.Parse<ApprovalReceiptOutcome>(r.GetString(8)),r.GetString(9),r.GetString(10),r.GetString(11));
    private static void ValidateRequest(ApprovalRequestInput i) { if(string.IsNullOrWhiteSpace(i.DedupeKey)||string.IsNullOrWhiteSpace(i.RequestId))throw new InvalidOperationException("Request ID and dedupe key are required.");if(i.ExpiresAt<=i.ObservedAt)throw new InvalidOperationException("Request expiry must be after observation.");if(string.IsNullOrWhiteSpace(i.RedactedArgumentsHash)&&!string.IsNullOrWhiteSpace(i.ToolName))throw new InvalidOperationException("Tool evidence must contain only a redacted arguments hash."); }
    private static (string,object?)[] Params(ApprovalRequestInput i,string p)=>[("$requestId",i.RequestId),("$dedupeKey",i.DedupeKey),("$schemaVersion",i.SchemaVersion),("$projectSlug",p),("$actionClass",i.ActionClass),("$operation",i.Operation),("$resourceKind",i.ResourceKind),("$resourceCanonicalId",i.ResourceCanonicalId),("$resourceDisplay",i.ResourceDisplay),("$reason",i.Reason),("$riskSummary",i.RiskSummary),("$scopeRequested",i.ScopeRequested),("$duration",i.DurationRequestedSeconds),("$provider",i.ProviderName),("$cli",i.ProviderCliVersion),("$adapter",i.AdapterVersion),("$profile",i.EnforcementProfile),("$runId",i.RunId),("$ticketId",i.TicketId),("$agent",i.AgentSlug),("$observed",ToDb(i.ObservedAt)),("$expires",ToDb(i.ExpiresAt)),("$tool",i.ToolName),("$argsHash",i.RedactedArgumentsHash),("$source",i.EvidenceSource)];
    private static async Task<int> ExecuteAsync(SqliteConnection c,SqliteTransaction t,string sql,params (string,object?)[] p){var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText=sql;AddParameters(cmd,p);return await cmd.ExecuteNonQueryAsync();}
    private static async Task<T?> ScalarAsync<T>(SqliteConnection c,SqliteTransaction t,string sql,params (string,object?)[] p){var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText=sql;AddParameters(cmd,p);var v=await cmd.ExecuteScalarAsync();return v is null or DBNull?default:(T)Convert.ChangeType(v,typeof(T));}
    private static void AddParameters(SqliteCommand c,params (string,object?)[] p){foreach(var (n,v) in p)c.Parameters.AddWithValue(n,v??DBNull.Value);}
    private static string ToDb(DateTime d)=>d.ToUniversalTime().ToString("O"); private static DateTime FromDb(string s)=>DateTime.Parse(s,null,System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static string? N(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i); private static int? NI(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt32(i);

    private const string Schema="""
        PRAGMA foreign_keys=ON;
        CREATE TABLE IF NOT EXISTS ApprovalRequests (RequestId TEXT PRIMARY KEY,DedupeKey TEXT NOT NULL,SchemaVersion INTEGER NOT NULL,ProjectSlug TEXT NOT NULL,ActionClass TEXT NOT NULL,Operation TEXT NOT NULL,ResourceKind TEXT NOT NULL,ResourceCanonicalId TEXT NOT NULL,ResourceDisplay TEXT NOT NULL,Reason TEXT NOT NULL,RiskSummary TEXT NULL,ScopeRequested TEXT NULL,DurationRequestedSeconds INTEGER NULL,ProviderName TEXT NOT NULL,ProviderCliVersion TEXT NOT NULL,AdapterVersion TEXT NOT NULL,EnforcementProfile TEXT NOT NULL,RunId TEXT NOT NULL,TicketId INTEGER NOT NULL,AgentSlug TEXT NOT NULL,ObservedAt TEXT NOT NULL,ExpiresAt TEXT NOT NULL,ToolName TEXT NULL,RedactedArgumentsHash TEXT NULL,EvidenceSource TEXT NULL,UNIQUE(ProjectSlug,DedupeKey));
        CREATE INDEX IF NOT EXISTS IX_ApprovalRequests_Query ON ApprovalRequests(ProjectSlug,TicketId,RunId,ProviderName);
        CREATE TABLE IF NOT EXISTS ApprovalDecisions (DecisionId TEXT PRIMARY KEY,RequestId TEXT NOT NULL REFERENCES ApprovalRequests(RequestId),Kind TEXT NOT NULL,Actor TEXT NOT NULL,CreatedAt TEXT NOT NULL,ExpiresAt TEXT NOT NULL,Scope TEXT NOT NULL,Reason TEXT NOT NULL,SupersededDecisionId TEXT NULL, SupersededByDecisionId TEXT NULL,ConsumedAt TEXT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS IX_ApprovalDecisions_Active ON ApprovalDecisions(RequestId) WHERE SupersededByDecisionId IS NULL;
        CREATE TABLE IF NOT EXISTS ApprovalReceipts (Sequence INTEGER PRIMARY KEY AUTOINCREMENT,ReceiptId TEXT NOT NULL UNIQUE,DecisionId TEXT NOT NULL REFERENCES ApprovalDecisions(DecisionId),RequestId TEXT NOT NULL REFERENCES ApprovalRequests(RequestId),EnforcementPointId TEXT NOT NULL,AdapterVersion TEXT NOT NULL,EnforcementProfile TEXT NOT NULL,StartedAt TEXT NOT NULL,EndedAt TEXT NOT NULL,Outcome TEXT NOT NULL,RedactedEffectIdentity TEXT NOT NULL,PreviousHash TEXT NOT NULL,IntegrityHash TEXT NOT NULL UNIQUE);
        CREATE INDEX IF NOT EXISTS IX_ApprovalReceipts_Request ON ApprovalReceipts(RequestId,Sequence);
        """;
}
