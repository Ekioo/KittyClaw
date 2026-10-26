using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KittyClaw.Core.Tests.Api;

[Collection("MCP endpoint environment")]
public sealed class TicketImprovementEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_RestAndMcp_SaveTogetherPreserveEvidenceAndRejectReplay(bool useMcp)
    {
        using var root = new McpEndpointTests.ApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.AddSingleton<ITicketImprovementAnalyzer>(new FakeAnalyzer())));
        using var client = factory.CreateClient();
        var project = await factory.Services.GetRequiredService<ProjectService>().CreateProjectAsync("apply");
        var tickets = factory.Services.GetRequiredService<TicketService>();
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Improve", "Original");
        var preserved = await tickets.SaveAcceptanceCriterionAsync(project.Slug, ticket.Id, null,
            new("Existing result", AcceptanceStatus.PASS, new("Verified")));
        var analysis = await client.PostAsJsonAsync(
            $"/api/projects/{project.Slug}/tickets/{ticket.Id}/improvement/analyze", new { split = false });
        analysis.EnsureSuccessStatusCode();
        var proposal = (await analysis.Content.ReadFromJsonAsync<TicketImprovementResponse>())!;
        Assert.Equal(DateTimeKind.Utc, proposal.TicketUpdatedAt.Kind);
        var request = new ApplyTicketImprovementRequest("reviewer", proposal.TicketUpdatedAt,
            proposal.Proposal.Description, ["Existing result", .. proposal.Proposal.AcceptanceCriteria]);

        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(client.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
        }, client, loggerFactory: null, ownsHttpClient: false));
        if (useMcp)
        {
            var applied = await mcp.CallToolAsync("apply_ticket_improvement", new Dictionary<string, object?>
            { ["project"] = project.Slug, ["id"] = ticket.Id, ["request"] = request });
            Assert.False(applied.IsError == true,
                string.Join("\n", applied.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        }
        else
        {
            var applied = await client.PostAsJsonAsync(
                $"/api/projects/{project.Slug}/tickets/{ticket.Id}/improvement/apply", request);
            Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        }
        var after = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!;
        Assert.Equal(request.Description, after.Description);
        Assert.Equal(request.AcceptanceCriteria, after.AcceptanceCriteria.Select(c => c.Text));
        Assert.Equal(preserved, after.AcceptanceCriteria[0]);
        Assert.Equal(AcceptanceStatus.Pending, after.AcceptanceCriteria[1].Status);
        Assert.Null(after.AcceptanceCriteria[1].Evidence);
        Assert.Contains(after.Activities, a => a.Author == "reviewer" && a.Text.Contains("Applied AI ticket improvement"));

        var stale = request with { Description = "Must not overwrite", AcceptanceCriteria = ["Wrong"] };
        var restReplay = await client.PostAsJsonAsync(
            $"/api/projects/{project.Slug}/tickets/{ticket.Id}/improvement/apply", stale);
        Assert.Equal(HttpStatusCode.Conflict, restReplay.StatusCode);
        var mcpReplay = await mcp.CallToolAsync("apply_ticket_improvement", new Dictionary<string, object?>
        { ["project"] = project.Slug, ["id"] = ticket.Id, ["request"] = stale });
        Assert.True(mcpReplay.IsError);
        Assert.Contains("409", string.Join("\n", mcpReplay.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        var unchanged = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!;
        Assert.Equal(after.Description, unchanged.Description);
        Assert.Equal(after.AcceptanceCriteriaJson, unchanged.AcceptanceCriteriaJson);
        Assert.Equal(after.UpdatedAt, unchanged.UpdatedAt);
        Assert.Equal(after.Activities.Count, unchanged.Activities.Count);
    }

    [Fact]
    public async Task Apply_RejectsMissingRevisionInvalidCriteriaAndUnrelatedChangesWithoutWriting()
    {
        using var factory = new McpEndpointTests.ApiFactory();
        using var client = factory.CreateClient();
        var project = await factory.Services.GetRequiredService<ProjectService>().CreateProjectAsync("stale");
        var tickets = factory.Services.GetRequiredService<TicketService>();
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Original title", "Original description");
        var request = new ApplyTicketImprovementRequest("worker", ticket.UpdatedAt, "Improved", ["Result"]);
        var url = $"/api/projects/{project.Slug}/tickets/{ticket.Id}/improvement/apply";
        foreach (var invalid in new[] { request with { ExpectedUpdatedAt = null },
            request with { ExpectedUpdatedAt = DateTimeOffset.MinValue }, request with { Author = " " },
            request with { AcceptanceCriteria = [" "] }, request with { Description = "" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, invalid)).StatusCode);
        // A comment changes the revision even though description and criteria stay identical.
        await tickets.AddCommentAsync(project.Slug, ticket.Id, "New instructions", "owner");
        var before = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, request)).StatusCode);
        var after = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!;
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.AcceptanceCriteriaJson, after.AcceptanceCriteriaJson);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Activities.Count, after.Activities.Count);
        foreach (var slug in new[] { "missing", project.Slug })
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(
                $"/api/projects/{slug}/tickets/999999/improvement/apply", request)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestAndMcp_ReturnSameProposalWithoutChangingTicket(bool split)
    {
        var analyzer = new FakeAnalyzer();
        using var root = new McpEndpointTests.ApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.AddSingleton<ITicketImprovementAnalyzer>(analyzer)));
        using var client = factory.CreateClient();
        var projects = factory.Services.GetRequiredService<ProjectService>();
        var project = await projects.CreateProjectAsync("improvement-api");
        var tickets = factory.Services.GetRequiredService<TicketService>();
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Investigate", "Original scope");
        var url = $"/api/projects/{project.Slug}/tickets/{ticket.Id}/improvement/analyze";

        var rest = await client.PostAsJsonAsync(url, new AnalyzeTicketImprovementRequest(split));
        rest.EnsureSuccessStatusCode();
        var proposal = (await rest.Content.ReadFromJsonAsync<TicketImprovementResponse>())!;
        Assert.Equal(ticket.Id, proposal.TicketId);
        Assert.Equal(ticket.UpdatedAt, proposal.TicketUpdatedAt);
        Assert.Equal("Four points", proposal.Proposal.Analysis);

        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(client.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, client, loggerFactory: null, ownsHttpClient: false));
        var tool = (await mcp.ListToolsAsync()).Single(t => t.Name == "analyze_ticket_improvement");
        Assert.False(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tool.ProtocolTool.Annotations?.IdempotentHint);
        var result = await mcp.CallToolAsync("analyze_ticket_improvement", new Dictionary<string, object?>
        {
            ["project"] = project.Slug, ["id"] = ticket.Id, ["request"] = new { split },
        });
        Assert.False(result.IsError == true);
        var json = JsonDocument.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        Assert.Equal("Four points", json.RootElement.GetProperty("proposal").GetProperty("analysis").GetString());
        Assert.Equal(new[] { split, split }, analyzer.Calls);
        var after = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!;
        Assert.Equal(ticket.Description, after.Description);
        Assert.Equal(ticket.AcceptanceCriteriaJson, after.AcceptanceCriteriaJson);
        Assert.Equal(ticket.UpdatedAt, after.UpdatedAt);
        Assert.Empty(after.SubTickets);
    }

    [Fact]
    public async Task Rest_MissingResourcesDoNotLaunchAnalysis_AndAnalysisFailureReturns422()
    {
        var analyzer = new FakeAnalyzer { Fail = true };
        using var root = new McpEndpointTests.ApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.AddSingleton<ITicketImprovementAnalyzer>(analyzer)));
        using var client = factory.CreateClient();
        var project = await factory.Services.GetRequiredService<ProjectService>().CreateProjectAsync("failure");
        foreach (var slug in new[] { "missing-project", project.Slug })
        {
            var missing = await client.PostAsJsonAsync($"/api/projects/{slug}/tickets/99999/improvement/analyze", new { });
            Assert.True(missing.StatusCode == HttpStatusCode.NotFound,
                $"{slug}: {missing.StatusCode}: {await missing.Content.ReadAsStringAsync()}");
        }
        Assert.Empty(analyzer.Calls);
        var ticket = await factory.Services.GetRequiredService<TicketService>().CreateTicketAsync(project.Slug, "Failure");
        var response = await client.PostAsJsonAsync($"/api/projects/{project.Slug}/tickets/{ticket.Id}/improvement/analyze", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Single(analyzer.Calls);
    }

    private sealed class FakeAnalyzer : ITicketImprovementAnalyzer
    {
        public List<bool> Calls { get; } = [];
        public bool Fail { get; init; }

        public Task<TicketImprovement> AnalyzeAsync(string slug, Ticket ticket, CancellationToken ct, bool split = false)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(split);
            if (Fail) throw new InvalidOperationException("Analysis failed");
            return Task.FromResult(new TicketImprovement("Deeper scope", ["Observable result"],
                split ? [new("One", "First", ["One result"]), new("Two", "Second", ["Two result"])] : [],
                "Four points"));
        }
    }
}
