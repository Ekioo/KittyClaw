using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

/// <summary>
/// Observation-only boundary detector. It records potential approval boundaries but never
/// changes, delays, or rejects a provider tool call.
/// </summary>
public sealed partial class BoundaryObservationService(ProjectService projects, ILogger<BoundaryObservationService> logger)
{
    private const string AdapterVersion = "observation-v1";

    public void Observe(AgentRun run, StreamEvent streamEvent)
    {
        try
        {
            if (streamEvent.Kind == "tool_result")
            {
                UpdateOutcome(run, streamEvent.CorrelationId, NormalizeOutcome(streamEvent.Text));
                return;
            }
            if (streamEvent.Kind is "result" or "error" or "max_turns")
            {
                var outcome = streamEvent.Kind == "result" ? ResultOutcome(streamEvent.Detail) : "failed";
                UpdateOutcome(run, null, outcome, updateAllPending: true);
                return;
            }
            if (streamEvent.Kind != "tool_use" || string.IsNullOrWhiteSpace(streamEvent.Detail)) return;

            var match = Classify(streamEvent.Text, streamEvent.Detail);
            if (match is null) return;

            using var connection = Open(run.ProjectSlug);
            EnsureSchema(connection);
            if (match.Value.ActionClass == BoundaryActionClass.NewNetworkDestination &&
                !RegisterNewDestination(connection, run.ProjectSlug, match.Value.ResourceDisplay, streamEvent.At))
                return;
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO BoundaryObservations
                (ObservationId,ProjectSlug,RunId,TicketId,AgentSlug,ProviderName,ObservedAt,ToolName,
                 ArgumentsHash,Classification,ResourceKind,ResourceDisplay,Outcome,IsFalsePositive,CorrelationId)
                VALUES ($id,$project,$run,$ticket,$agent,$provider,$at,$tool,$hash,$classification,
                        $resourceKind,$resourceDisplay,'unknown',NULL,$correlation)
                """;
            var argumentsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(streamEvent.Detail)));
            var observationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{run.ProjectSlug}\n{run.RunId}\n{streamEvent.At:O}\n{streamEvent.Text}\n{argumentsHash}\n{AdapterVersion}")));
            Add(command, "$id", observationId);
            Add(command, "$project", run.ProjectSlug);
            Add(command, "$run", run.RunId);
            Add(command, "$ticket", run.TicketId);
            Add(command, "$agent", run.AgentName);
            Add(command, "$provider", run.CliVersion?.Provider ?? "unknown");
            Add(command, "$at", streamEvent.At.ToUniversalTime().ToString("O"));
            Add(command, "$tool", streamEvent.Text);
            Add(command, "$hash", argumentsHash);
            Add(command, "$classification", match.Value.ActionClass.ToString());
            Add(command, "$resourceKind", match.Value.ResourceKind);
            Add(command, "$resourceDisplay", match.Value.ResourceDisplay);
            Add(command, "$correlation", streamEvent.CorrelationId);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // Observation must never become an enforcement point or fail an agent run.
            logger.LogWarning(ex, "Boundary observation failed for run {RunId}", run.RunId);
        }
    }

    public async Task<IReadOnlyList<BoundaryObservation>> QueryAsync(string projectSlug, string? runId = null)
    {
        await using var connection = Open(projectSlug);
        EnsureSchema(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ObservationId,ProjectSlug,RunId,TicketId,AgentSlug,ProviderName,ObservedAt,ToolName,
                   ArgumentsHash,Classification,ResourceKind,ResourceDisplay,Outcome,IsFalsePositive
            FROM BoundaryObservations WHERE $run IS NULL OR RunId=$run ORDER BY ObservedAt
            """;
        Add(command, "$run", runId);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<BoundaryObservation>();
        while (await reader.ReadAsync())
            result.Add(Read(reader));
        return result;
    }

    public async Task ReviewAsync(string projectSlug, string observationId, bool isFalsePositive)
    {
        await using var connection = Open(projectSlug);
        EnsureSchema(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE BoundaryObservations SET IsFalsePositive=$value WHERE ObservationId=$id";
        Add(command, "$value", isFalsePositive ? 1 : 0);
        Add(command, "$id", observationId);
        if (await command.ExecuteNonQueryAsync() != 1)
            throw new InvalidOperationException("Boundary observation not found.");
    }

    public async Task<BoundaryObservationMetrics> MetricsAsync(string projectSlug, int ordinaryRunWindow = 20)
    {
        if (ordinaryRunWindow is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(ordinaryRunWindow));
        await using var connection = Open(projectSlug);
        EnsureSchema(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RecentRuns AS (
              SELECT RunId, MAX(ObservedAt) LastObserved FROM ObservedRuns
              GROUP BY RunId ORDER BY LastObserved DESC LIMIT $window
            )
            SELECT (SELECT COUNT(*) FROM RecentRuns),
                   COUNT(o.ObservationId),
                   SUM(CASE WHEN o.IsFalsePositive IS NOT NULL THEN 1 ELSE 0 END),
                   SUM(CASE WHEN o.IsFalsePositive=1 THEN 1 ELSE 0 END)
            FROM RecentRuns r LEFT JOIN BoundaryObservations o ON o.RunId=r.RunId
            """;
        Add(command, "$window", ordinaryRunWindow);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var runs = reader.GetInt32(0);
        var potential = reader.GetInt32(1);
        var reviewed = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        var falsePositives = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
        var normalized = runs == 0 ? 0 : potential * 20d / runs;
        return new(runs, potential, reviewed, falsePositives, normalized,
            reviewed == 0 ? null : falsePositives / (double)reviewed,
            runs >= ordinaryRunWindow && normalized < 1d);
    }

    public void RecordRun(AgentRun run)
    {
        try
        {
            using var connection = Open(run.ProjectSlug);
            EnsureSchema(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO ObservedRuns (RunId,ProjectSlug,ObservedAt) VALUES ($run,$project,$at)";
            Add(command, "$run", run.RunId);
            Add(command, "$project", run.ProjectSlug);
            Add(command, "$at", run.StartedAt.ToUniversalTime().ToString("O"));
            command.ExecuteNonQuery();
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not record observed run {RunId}", run.RunId); }
    }

    /// <summary>True when the destination is loopback/private or already recorded for the project.
    /// Used by the runtime gate so only genuinely new outbound destinations require approval.</summary>
    public bool IsKnownOrLocalDestination(string projectSlug, string host)
    {
        var normalized = NormalizeDestinationHost(host);
        if (IsLocalDestination(normalized)) return true;
        using var connection = Open(projectSlug);
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM KnownNetworkDestinations WHERE ProjectSlug=$project AND Host=$host";
        Add(command, "$project", projectSlug);
        Add(command, "$host", normalized);
        return command.ExecuteScalar() is not null;
    }

    internal static (BoundaryActionClass ActionClass, string ResourceKind, string ResourceDisplay)? Classify(string tool, string detail)
    {
        var command = ExtractCommand(detail);
        var normalized = $"{tool} {NormalizeGitCommand(command)}".ToLowerInvariant();
        if (PushRegex().IsMatch(normalized)) return (BoundaryActionClass.PushOrPullRequest, "repository", "repository mutation");
        if (PublishRegex().IsMatch(normalized)) return (BoundaryActionClass.PublishOrDeploy, "deployment-target", "publication or deployment target");
        if (SecretRegex().IsMatch(normalized)) return (BoundaryActionClass.SecretAccess, "secret", "redacted secret resource");
        if (DestructiveRegex().IsMatch(normalized)) return (BoundaryActionClass.DestructiveOperation, "local-resource", "destructive local operation");
        if (NetworkRegex().IsMatch(normalized)) return (BoundaryActionClass.NewNetworkDestination, "network-destination", ExtractHost(command));
        return null;
    }

    private SqliteConnection Open(string projectSlug)
    {
        var databasePath = projects.GetProjectDbPath(projectSlug);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SqliteConnection($"Data Source={databasePath};Default Timeout=30");
        connection.Open();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ObservedRuns (RunId TEXT PRIMARY KEY,ProjectSlug TEXT NOT NULL,ObservedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS BoundaryObservations (
              ObservationId TEXT PRIMARY KEY,ProjectSlug TEXT NOT NULL,RunId TEXT NOT NULL,TicketId INTEGER NULL,
              AgentSlug TEXT NOT NULL,ProviderName TEXT NOT NULL,ObservedAt TEXT NOT NULL,ToolName TEXT NOT NULL,
              ArgumentsHash TEXT NOT NULL,Classification TEXT NOT NULL,ResourceKind TEXT NOT NULL,
              ResourceDisplay TEXT NOT NULL,Outcome TEXT NOT NULL,IsFalsePositive INTEGER NULL,
              CorrelationId TEXT NULL);
            CREATE TABLE IF NOT EXISTS KnownNetworkDestinations (
              ProjectSlug TEXT NOT NULL,Host TEXT NOT NULL,FirstSeenAt TEXT NOT NULL,
              PRIMARY KEY(ProjectSlug,Host));
            CREATE INDEX IF NOT EXISTS IX_BoundaryObservations_Run ON BoundaryObservations(ProjectSlug,RunId,ObservedAt);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "BoundaryObservations", "CorrelationId", "TEXT NULL");
    }

    private void UpdateOutcome(AgentRun run, string? correlationId, string outcome, bool updateAllPending = false)
    {
        using var connection = Open(run.ProjectSlug);
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = correlationId is not null
            ? "UPDATE BoundaryObservations SET Outcome=$outcome WHERE ProjectSlug=$project AND RunId=$run AND CorrelationId=$correlation AND Outcome='unknown'"
            : updateAllPending
                ? "UPDATE BoundaryObservations SET Outcome=$outcome WHERE ProjectSlug=$project AND RunId=$run AND Outcome='unknown'"
                : "UPDATE BoundaryObservations SET Outcome=$outcome WHERE ObservationId=(SELECT ObservationId FROM BoundaryObservations WHERE ProjectSlug=$project AND RunId=$run AND Outcome='unknown' ORDER BY ObservedAt DESC LIMIT 1)";
        Add(command, "$outcome", outcome);
        Add(command, "$project", run.ProjectSlug);
        Add(command, "$run", run.RunId);
        if (correlationId is not null) Add(command, "$correlation", correlationId);
        command.ExecuteNonQuery();
    }

    private static bool RegisterNewDestination(SqliteConnection connection, string projectSlug, string host, DateTime observedAt)
    {
        host = NormalizeDestinationHost(host);
        if (IsLocalDestination(host)) return false;
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO KnownNetworkDestinations (ProjectSlug,Host,FirstSeenAt) VALUES ($project,$host,$at)";
        Add(command, "$project", projectSlug);
        Add(command, "$host", host);
        Add(command, "$at", observedAt.ToUniversalTime().ToString("O"));
        return command.ExecuteNonQuery() == 1;
    }

    private static bool IsLocalDestination(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var normalizedHost = host.Trim('[', ']');
        if (!IPAddress.TryParse(normalizedHost, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }

    private static string NormalizeDestinationHost(string host)
    {
        var normalized = host.Trim().Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(normalized, out var address)) return address.ToString().ToLowerInvariant();
        try
        {
            return new IdnMapping().GetAscii(normalized).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return normalized.ToLowerInvariant();
        }
    }

    private static string NormalizeOutcome(string value) => value.ToLowerInvariant() switch
    {
        "success" or "succeeded" or "completed" => "succeeded",
        "cancelled" or "canceled" => "cancelled",
        "failure" or "failed" or "error" => "failed",
        _ => "unknown",
    };

    private static string ResultOutcome(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "unknown";
        try
        {
            using var document = JsonDocument.Parse(detail);
            if (document.RootElement.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
                return "failed";
            var status = document.RootElement.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
            return status is null ? "succeeded" : NormalizeOutcome(status);
        }
        catch { return "unknown"; }
    }

    private static string NormalizeGitCommand(string command)
    {
        var tokens = Regex.Matches(command, @"(?:[^\s\""']+|\""[^\""']*\""|'[^']*')+").Select(m => m.Value).ToList();
        var git = tokens.FindIndex(t => t.Equals("git", StringComparison.OrdinalIgnoreCase));
        if (git < 0) return command;
        var index = git + 1;
        while (index < tokens.Count && tokens[index].StartsWith('-'))
        {
            var option = tokens[index++];
            if (option is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace" && index < tokens.Count)
                index++;
        }
        return index < tokens.Count ? $"git {string.Join(' ', tokens.Skip(index))}" : command;
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table})";
        using var reader = inspect.ExecuteReader();
        while (reader.Read()) if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private static BoundaryObservation Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3),
        reader.GetString(4), reader.GetString(5), DateTime.Parse(reader.GetString(6)).ToUniversalTime(), reader.GetString(7),
        reader.GetString(8), Enum.Parse<BoundaryActionClass>(reader.GetString(9)), reader.GetString(10), reader.GetString(11),
        reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetInt32(13) == 1);

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string ExtractCommand(string detail)
    {
        try
        {
            using var document = JsonDocument.Parse(detail);
            foreach (var property in new[] { "command", "cmd", "url", "path", "query" })
                if (document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? string.Empty;
        }
        catch { }
        return detail;
    }

    private static string ExtractHost(string value)
    {
        var match = UrlRegex().Match(value);
        return match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
            ? NormalizeDestinationHost(uri.Host.Trim('[', ']'))
            : "unknown network destination";
    }

    [GeneratedRegex(@"\bgit\s+push\b|\bgh\s+pr\s+(create|merge|close|reopen)\b", RegexOptions.IgnoreCase)] private static partial Regex PushRegex();
    [GeneratedRegex(@"\b(npm\s+publish|dotnet\s+nuget\s+push|docker\s+push|kubectl\s+(apply|delete)|helm\s+(install|upgrade|uninstall)|vercel\s+(deploy|--prod)|wrangler\s+deploy|terraform\s+apply)\b", RegexOptions.IgnoreCase)] private static partial Regex PublishRegex();
    [GeneratedRegex(@"(^|[\\/\s])\.env(\b|[\\/])|\b(get-content|cat|type)\b[^\r\n]*(secret|credential|token|api[_-]?key|private[_-]?key)", RegexOptions.IgnoreCase)] private static partial Regex SecretRegex();
    [GeneratedRegex(@"\b(rm\s+-r[f]?|remove-item\b[^\r\n]*-recurse|del\s+/[sq]|drop\s+(table|database)|truncate\s+table|git\s+reset\s+--hard|git\s+clean\s+-[a-z]*f)\b", RegexOptions.IgnoreCase)] private static partial Regex DestructiveRegex();
    [GeneratedRegex(@"\b(curl|wget|invoke-webrequest|invoke-restmethod)\b[^\r\n]*https?://", RegexOptions.IgnoreCase)] private static partial Regex NetworkRegex();
    [GeneratedRegex(@"https?://[^\s'\""<>]+", RegexOptions.IgnoreCase)] private static partial Regex UrlRegex();
    [GeneratedRegex(@"^172\.(1[6-9]|2\d|3[01])\.")] private static partial Regex Private172Regex();
}
