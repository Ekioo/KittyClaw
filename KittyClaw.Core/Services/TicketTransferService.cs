using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

/// <summary>
/// Moves a ticket and its complete sub-ticket tree between project databases.  SQLite's
/// ATTACH command lets the source deletion and destination copy commit atomically.
/// </summary>
public sealed class TicketTransferService(
    ProjectService projects,
    TicketService tickets,
    ColumnService columns,
    MemberService members,
    LabelService labels)
{
    public async Task<TicketTransferResult> TransferAsync(string sourceProject, int ticketId, string targetProject, string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new InvalidOperationException("The transfer actor is required.");
        if (string.Equals(sourceProject, targetProject, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The source and target projects must be different.");
        if (await projects.GetProjectAsync(sourceProject) is null)
            throw new InvalidOperationException($"Source project '{sourceProject}' does not exist.");
        if (await projects.GetProjectAsync(targetProject) is null)
            throw new InvalidOperationException($"Target project '{targetProject}' does not exist.");

        // Bring legacy databases up to the current ticket schema before taking the snapshot.
        await tickets.ListTicketsAsync(sourceProject);
        await tickets.ListTicketsAsync(targetProject);
        var targetColumns = await columns.ListColumnsAsync(targetProject);
        var targetMembers = await members.ListMembersAsync(targetProject);
        await labels.ListLabelsAsync(targetProject);

        var sourcePath = projects.GetProjectDbPath(sourceProject);
        var targetPath = projects.GetProjectDbPath(targetProject);
        await using var source = new TodoDbContext(sourcePath);
        var allSourceTickets = await source.Tickets
            .Include(t => t.Comments)
            .Include(t => t.Activities)
            .Include(t => t.Labels)
            .ToListAsync();
        var root = allSourceTickets.FirstOrDefault(t => t.Id == ticketId)
            ?? throw new InvalidOperationException($"Ticket #{ticketId} does not exist in project '{sourceProject}'.");

        var transferTickets = CollectTree(root, allSourceTickets);
        var transferIds = transferTickets.Select(t => t.Id).ToHashSet();
        if (root.ParentId is not null && !transferIds.Contains(root.ParentId.Value))
            throw new InvalidOperationException("The ticket has a parent outside its sub-ticket tree. Transfer the parent ticket instead to preserve the relationship.");

        await using var target = new TodoDbContext(targetPath);
        var ids = transferIds.ToList();
        var conflictingTicketIds = await target.Tickets.Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync();
        var commentIds = transferTickets.SelectMany(t => t.Comments).Select(c => c.Id).ToList();
        var conflictingCommentIds = commentIds.Count == 0 ? [] : await target.Comments.Where(c => commentIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
        var activityIds = transferTickets.SelectMany(t => t.Activities).Select(a => a.Id).ToList();
        var conflictingActivityIds = activityIds.Count == 0 ? [] : await target.ActivityEntries.Where(a => activityIds.Contains(a.Id)).Select(a => a.Id).ToListAsync();
        if (conflictingTicketIds.Count > 0 || conflictingCommentIds.Count > 0 || conflictingActivityIds.Count > 0)
            throw new InvalidOperationException($"Transfer would overwrite existing identifiers in '{targetProject}' (tickets: {Join(conflictingTicketIds)}, comments: {Join(conflictingCommentIds)}, activities: {Join(conflictingActivityIds)}).");

        var targetColumnNames = targetColumns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetPipelineId = targetColumns.Select(c => c.PipelineId).Distinct().Single();
        var targetColumnIds = targetColumns.ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        var missingColumns = transferTickets.SelectMany(t => new[] { t.Status, t.ScheduleTarget })
            .Where(name => !string.IsNullOrWhiteSpace(name) && !targetColumnNames.Contains(name!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missingColumns.Count > 0)
            throw new InvalidOperationException($"Target project is missing required board columns: {string.Join(", ", missingColumns)}.");

        var assignees = transferTickets.Select(t => t.AssignedTo).Where(a => !string.IsNullOrWhiteSpace(a)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var targetMemberSlugs = targetMembers.Select(m => m.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingMembers = assignees.Where(a => !targetMemberSlugs.Contains(a)).ToList();
        if (missingMembers.Count > 0)
            throw new InvalidOperationException($"Target project is missing assignees: {string.Join(", ", missingMembers)}.");

        var sourceLabels = transferTickets.SelectMany(t => t.Labels).GroupBy(l => l.Id).Select(g => g.First()).ToList();
        var targetLabels = await target.Labels.ToListAsync();
        var labelMap = new Dictionary<int, int>();
        foreach (var sourceLabel in sourceLabels)
        {
            var existing = targetLabels.FirstOrDefault(l => string.Equals(l.Name, sourceLabel.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !string.Equals(existing.Color, sourceLabel.Color, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Target label '{sourceLabel.Name}' has color '{existing.Color}', which cannot losslessly map source color '{sourceLabel.Color}'.");
            if (existing is not null) labelMap[sourceLabel.Id] = existing.Id;
        }

        var transferredAt = DateTime.UtcNow;
        await using var connection = new SqliteConnection($"Data Source={sourcePath}");
        await connection.OpenAsync();
        await using var attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE $targetPath AS target";
        attach.Parameters.AddWithValue("$targetPath", targetPath);
        await attach.ExecuteNonQueryAsync();
        try
        {
            await using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var sourceLabel in sourceLabels.Where(l => !labelMap.ContainsKey(l.Id)))
                {
                    await ExecuteAsync(connection, transaction, "INSERT INTO target.Labels (Name, Color) VALUES ($name, $color)", ("$name", sourceLabel.Name), ("$color", sourceLabel.Color));
                    labelMap[sourceLabel.Id] = Convert.ToInt32(await ScalarAsync(connection, transaction, "SELECT last_insert_rowid()"));
                }

                foreach (var ticket in transferTickets)
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO target.Tickets (Id, PipelineId, ColumnId, Title, Description, Status, Priority, SortOrder, AssignedTo, CreatedBy, CreatedAt, UpdatedAt, ParentId, BlocksParent, FireAt, ScheduleTarget, AgentTokens, AgentCostUsd)
                        VALUES ($id, $pipelineId, $columnId, $title, $description, $status, $priority, $sortOrder, $assignedTo, $createdBy, $createdAt, $updatedAt, $parentId, $blocksParent, $fireAt, $scheduleTarget, $agentTokens, $agentCostUsd)
                        """, TicketParameters(ticket, targetPipelineId, targetColumnIds[ticket.Status]));

                foreach (var comment in transferTickets.SelectMany(t => t.Comments))
                    await ExecuteAsync(connection, transaction, "INSERT INTO target.Comments (Id, TicketId, Content, Author, CreatedAt) VALUES ($id, $ticketId, $content, $author, $createdAt)",
                        ("$id", comment.Id), ("$ticketId", comment.TicketId), ("$content", comment.Content), ("$author", comment.Author), ("$createdAt", comment.CreatedAt));
                foreach (var activity in transferTickets.SelectMany(t => t.Activities))
                    await ExecuteAsync(connection, transaction, "INSERT INTO target.ActivityEntries (Id, TicketId, Author, Text, CreatedAt) VALUES ($id, $ticketId, $author, $text, $createdAt)",
                        ("$id", activity.Id), ("$ticketId", activity.TicketId), ("$author", activity.Author), ("$text", activity.Text), ("$createdAt", activity.CreatedAt));
                foreach (var ticket in transferTickets)
                foreach (var label in ticket.Labels)
                    await ExecuteAsync(connection, transaction, "INSERT INTO target.TicketLabels (TicketsId, LabelsId) VALUES ($ticketId, $labelId)", ("$ticketId", ticket.Id), ("$labelId", labelMap[label.Id]));

                var maxActivityId = Convert.ToInt64(await ScalarAsync(connection, transaction, """
                    SELECT COALESCE(MAX(Id), 0)
                    FROM (
                        SELECT Id FROM ActivityEntries
                        UNION ALL
                        SELECT Id FROM target.ActivityEntries
                    )
                    """));
                if (maxActivityId >= int.MaxValue)
                    throw new InvalidOperationException("No activity identifier is available for the transfer audit event.");
                var auditActivityId = checked((int)maxActivityId + 1);

                await ExecuteAsync(connection, transaction, "INSERT INTO target.ActivityEntries (Id, TicketId, Author, Text, CreatedAt) VALUES ($id, $ticketId, $author, $text, $createdAt)",
                    ("$id", auditActivityId), ("$ticketId", root.Id), ("$author", actor), ("$text", $"Transferred from project '{sourceProject}' to '{targetProject}' by '{actor}' at {transferredAt:O}."), ("$createdAt", transferredAt));

                foreach (var id in transferIds)
                {
                    await ExecuteAsync(connection, transaction, "DELETE FROM TicketLabels WHERE TicketsId = $id", ("$id", id));
                    await ExecuteAsync(connection, transaction, "DELETE FROM Comments WHERE TicketId = $id", ("$id", id));
                    await ExecuteAsync(connection, transaction, "DELETE FROM ActivityEntries WHERE TicketId = $id", ("$id", id));
                    await ExecuteAsync(connection, transaction, "DELETE FROM Tickets WHERE Id = $id", ("$id", id));
                }
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                throw new InvalidOperationException("Ticket transfer failed; both projects were left unchanged.", ex);
            }
        }
        finally
        {
            await using var detach = connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE target";
            await detach.ExecuteNonQueryAsync();
        }
        return new TicketTransferResult(root.Id, transferTickets.Count, sourceProject, targetProject, transferredAt);
    }

    private static List<Ticket> CollectTree(Ticket root, List<Ticket> all)
    {
        var children = all.Where(t => t.ParentId is not null).GroupBy(t => t.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<Ticket>();
        var pending = new Stack<Ticket>([root]);
        var seen = new HashSet<int>();
        while (pending.TryPop(out var ticket))
        {
            if (!seen.Add(ticket.Id)) throw new InvalidOperationException("The ticket relationship graph contains a cycle.");
            result.Add(ticket);
            if (children.TryGetValue(ticket.Id, out var descendants))
                foreach (var child in descendants) pending.Push(child);
        }
        return result;
    }

    private static (string Name, object? Value)[] TicketParameters(Ticket t, int targetPipelineId, int targetColumnId) =>
    [ ("$id", t.Id), ("$pipelineId", targetPipelineId), ("$columnId", targetColumnId), ("$title", t.Title), ("$description", t.Description), ("$status", t.Status), ("$priority", (int)t.Priority), ("$sortOrder", t.SortOrder), ("$assignedTo", t.AssignedTo), ("$createdBy", t.CreatedBy), ("$createdAt", t.CreatedAt), ("$updatedAt", t.UpdatedAt), ("$parentId", t.ParentId), ("$blocksParent", t.BlocksParent), ("$fireAt", t.FireAt), ("$scheduleTarget", t.ScheduleTarget), ("$agentTokens", t.AgentTokens), ("$agentCostUsd", t.AgentCostUsd) ];

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> ScalarAsync(SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    private static string Join(IEnumerable<int> ids) => string.Join(", ", ids.DefaultIfEmpty(-1).Where(i => i >= 0));
}

public sealed record TicketTransferResult(int TicketId, int TicketCount, string SourceProject, string TargetProject, DateTime TransferredAt);
