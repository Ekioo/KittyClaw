using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

public sealed class ColumnVisitTests
{
    [Fact]
    public async Task Column_visit_changes_only_on_transitions_and_survives_return_and_restart()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Column visits");
        var columns = await new ColumnService(projects).ListColumnsAsync(project.Slug);
        await using (var db = projects.GetProjectDb(project.Slug))
        {
            var ticket = new Ticket { Title = "Visit", PipelineId = columns[0].PipelineId, ColumnId = columns[0].Id };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            Assert.Equal(0, ticket.ColumnVisit);

            ticket.Title = "Edited without moving";
            await db.SaveChangesAsync();
            await db.Entry(ticket).ReloadAsync();
            Assert.Equal(0, ticket.ColumnVisit);

            ticket.ColumnId = columns[1].Id;
            await db.SaveChangesAsync();
            await db.Entry(ticket).ReloadAsync();
            Assert.Equal(1, ticket.ColumnVisit);

            // Raw SQL covers transition writers outside the tracked ticket service.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tickets SET ColumnId = {columns[0].Id} WHERE Id = {ticket.Id}");
            await db.Entry(ticket).ReloadAsync();
            Assert.Equal(2, ticket.ColumnVisit);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tickets SET ColumnId = {columns[0].Id} WHERE Id = {ticket.Id}");
        }
        await using var reopened = new ProjectService(temp.Path).GetProjectDb(project.Slug);
        Assert.Equal(2, (await reopened.Tickets.SingleAsync()).ColumnVisit);
    }
}
