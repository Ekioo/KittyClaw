using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketLabelTests
{
    private static (TicketService tickets, LabelService labels, string slug) BuildSut(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = projects.CreateProjectAsync("label-test").GetAwaiter().GetResult();
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var labels = new LabelService(projects);
        return (tickets, labels, project.Slug);
    }

    [Fact]
    public async Task SetTicketLabels_RemovesLabel_WhenCalledWithSubset()
    {
        using var tmp = new TempDir();
        var (svc, lblSvc, slug) = BuildSut(tmp);

        var label1 = await lblSvc.CreateLabelAsync(slug, "bug", "#ff0000");
        var label2 = await lblSvc.CreateLabelAsync(slug, "feature", "#00ff00");
        var ticket = await svc.CreateTicketAsync(slug, "T1", labelIds: [label1.Id, label2.Id]);

        var ok = await svc.SetTicketLabelsAsync(slug, ticket.Id, [label1.Id]);
        Assert.True(ok);

        var refreshed = await svc.GetTicketAsync(slug, ticket.Id);
        Assert.NotNull(refreshed);
        Assert.Single(refreshed.Labels);
        Assert.Equal(label1.Id, refreshed.Labels[0].Id);
    }

    [Fact]
    public async Task SetTicketLabels_RemovesAllLabels_WhenCalledWithEmptyList()
    {
        using var tmp = new TempDir();
        var (svc, lblSvc, slug) = BuildSut(tmp);

        var label = await lblSvc.CreateLabelAsync(slug, "bug", "#ff0000");
        var ticket = await svc.CreateTicketAsync(slug, "T1", labelIds: [label.Id]);

        var ok = await svc.SetTicketLabelsAsync(slug, ticket.Id, []);
        Assert.True(ok);

        var refreshed = await svc.GetTicketAsync(slug, ticket.Id);
        Assert.NotNull(refreshed);
        Assert.Empty(refreshed.Labels);
    }
}
