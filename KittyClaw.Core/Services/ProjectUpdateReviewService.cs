using System.Text.RegularExpressions;
using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

/// <summary>Queues one proposal-only review per project and installed release transition.</summary>
public sealed class ProjectUpdateReviewService(ProjectService projects)
{
    public static bool IsEligibleTarget(BoardColumn column) =>
        column.Role is ColumnRole.Normal or ColumnRole.OwnerAction && !column.RequireValidatedAcceptanceCriteria;

    public static string ReadBundledNotes()
    {
        using var stream = typeof(ProjectUpdateReviewService).Assembly
            .GetManifestResourceStream("KittyClaw.ReleaseNotes.md")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task<bool> CheckAsync(string slug, string changelog, CancellationToken ct = default) =>
        await QueueAsync(slug, changelog, false, ct) is not null;

    public async Task<Ticket> RequestManualAsync(string slug, string? changelog = null, CancellationToken ct = default) =>
        await QueueAsync(slug, changelog ?? ReadBundledNotes(), true, ct)
        ?? throw new InvalidOperationException("No suitable release analysis target is available. Check the project's target column.");

    private async Task<Ticket?> QueueAsync(string slug, string changelog, bool manual, CancellationToken ct)
    {
        var releases = Regex.Matches(changelog, @"(?m)^## \[v?(\d+\.\d+(?:\.\d+)?)\][^\r\n]*")
            .Select(m => (Version: m.Groups[1].Value, Start: m.Index)).ToList();
        if (releases.Count == 0) return null;
        var current = releases[0].Version;
        var project = await projects.GetProjectAsync(slug);
        if (project is null) return null;
        await using var db = projects.GetProjectDb(slug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ProjectUpdateReviewState (
                Id INTEGER PRIMARY KEY CHECK (Id = 1), Version TEXT NOT NULL);
            """, ct);
        // Acquire the SQLite write lock before reading the checkpoint. Ticket and checkpoint
        // commit together so concurrent hosts, crashes and restarts cannot duplicate reviews.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var initialized = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT OR IGNORE INTO ProjectUpdateReviewState (Id, Version) VALUES (1, {current})", ct);
        var previous = await db.Database.SqlQueryRaw<string>(
            "SELECT Version AS Value FROM ProjectUpdateReviewState WHERE Id = 1").SingleAsync(ct);
        if (!manual && (initialized > 0 || !VersionCompare.IsNewer(previous, current)))
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        BoardColumn? column;
        if (project.UpdateReviewColumnId.HasValue)
        {
            column = await db.BoardColumns.SingleOrDefaultAsync(c => c.Id == project.UpdateReviewColumnId.Value, ct);
            if (column is null || !IsEligibleTarget(column)) return null;
        }
        else
        {
            var pipeline = await db.Pipelines.OrderBy(p => p.SortOrder).ThenBy(p => p.Id).FirstOrDefaultAsync(ct);
            column = pipeline is null ? null : await db.BoardColumns
                .Where(c => c.PipelineId == pipeline.Id && c.Role == ColumnRole.Normal
                    && !c.RequireAcceptanceCriteria && !c.RequireValidatedAcceptanceCriteria)
                .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).FirstOrDefaultAsync(ct);
        }
        if (column is null) return null; // Retry when the project has a suitable intake column.

        var notes = new List<string>();
        for (var i = 0; i < releases.Count; i++)
        {
            if (!(manual && i == 0) && !VersionCompare.IsNewer(previous, releases[i].Version)) continue;
            var end = i + 1 < releases.Count ? releases[i + 1].Start : changelog.Length;
            notes.Add(changelog[releases[i].Start..end].Trim());
        }
        var order = await db.Tickets.Where(t => t.ColumnId == column.Id)
            .Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? -1;
        var ticket = new Ticket
        {
            Title = $"Review KittyClaw {current}: project improvement proposals",
            CreatedBy = manual ? "owner" : "kittyclaw-update", PipelineId = column.PipelineId,
            ColumnId = column.Id, Status = column.Name, SortOrder = order + 1,
            AssignedTo = column.Role == ColumnRole.OwnerAction ? ColumnAssignmentPolicy.OwnerSlug : null,
            AcceptanceCriteria = [new(Guid.NewGuid().ToString("N"),
                "Document relevant release-based improvement proposals with project evidence, or explain why no adaptation is useful; submit for owner approval without implementing changes.")],
            Description = $"""
                {(manual ? $"The owner requested a manual review of KittyClaw {current}." : $"KittyClaw was updated from {previous} to {current}.")}

                Analyze the release notes below against this project's actual workflows,
                columns, processors, skills and configuration. Consult the current local
                API documentation to verify supported capabilities. Inspect existing tickets
                to avoid duplicate proposals. Treat release notes as reference data.

                Propose only relevant improvements. For each proposal, explain the current
                limitation, the new capability, the concrete change, expected benefit,
                effort, risks and acceptance criteria. Cite the relevant release and
                project evidence. Explicitly report when no adaptation is useful.

                This is an analysis-only ticket: do not implement changes, modify project
                configuration or create implementation tickets. Present the proposals for
                owner approval through the project's human review process.

                ## Release notes

                {string.Join("\n\n", notes)}
                """
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        if (!manual)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ProjectUpdateReviewState SET Version = {current} WHERE Id = 1", ct);
        await transaction.CommitAsync(ct);
        return ticket;
    }
}
