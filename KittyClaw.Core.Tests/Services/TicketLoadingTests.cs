using System.Diagnostics;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Xunit.Abstractions;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketLoadingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GetTicket_preserves_large_history_and_labels()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Ticket loading");
        var tickets = new TicketService(projects, new MemberService(projects));
        var labels = new LabelService(projects);
        var labelIds = new List<int>();
        for (var i = 0; i < 8; i++)
            labelIds.Add((await labels.CreateLabelAsync(project.Slug, $"Label {i}", "#ff0000")).Id);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Large history", labelIds: labelIds);
        var start = DateTime.UtcNow.AddDays(-1);
        await using (var db = projects.GetProjectDb(project.Slug))
        {
            for (var i = 199; i >= 0; i--)
            {
                db.Comments.Add(new Comment { TicketId = ticket.Id, Content = $"Comment {i}", CreatedAt = start.AddSeconds(i) });
                db.Set<ActivityEntry>().Add(new ActivityEntry { TicketId = ticket.Id, Text = $"Activity {i}", CreatedAt = start.AddSeconds(i) });
            }
            await db.SaveChangesAsync();
        }

        var timer = Stopwatch.StartNew();
        var loaded = await tickets.GetTicketAsync(project.Slug, ticket.Id);
        timer.Stop();
        output.WriteLine($"Large ticket load: {timer.ElapsedMilliseconds} ms");
        Assert.NotNull(loaded);
        Assert.Equal(Enumerable.Range(0, 200).Select(i => $"Comment {i}"), loaded.Comments.Select(c => c.Content));
        Assert.Equal(Enumerable.Range(0, 200).Select(i => $"Activity {i}"),
            loaded.Activities.Where(a => a.Text.StartsWith("Activity ")).Select(a => a.Text));
        Assert.Equal(labelIds.Order(), loaded.Labels.Select(l => l.Id).Order());
        var recent = await tickets.GetTicketAsync(project.Slug, ticket.Id, 30);
        Assert.NotNull(recent);
        Assert.True(recent.HasMoreComments);
        Assert.Equal(Enumerable.Range(170, 30).Select(i => $"Comment {i}"), recent.Comments.Select(c => c.Content));
        var expanded = await tickets.GetTicketAsync(project.Slug, ticket.Id, 60);
        Assert.NotNull(expanded);
        Assert.Equal(Enumerable.Range(140, 60).Select(i => $"Comment {i}"), expanded.Comments.Select(c => c.Content));
        var complete = await tickets.GetTicketAsync(project.Slug, ticket.Id, 210);
        Assert.NotNull(complete);
        Assert.False(complete.HasMoreComments);
        Assert.Equal(200, complete.Comments.Count);
        Assert.Null(await tickets.GetTicketAsync(project.Slug, int.MaxValue));
    }
}
