using System.Net;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class JevQualificationTests
{
    [Fact]
    public async Task Observation_persists_without_mutation_and_deduplicates_after_restart()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Jev test");
        var columns = new ColumnService(projects);
        var board = await columns.ListColumnsAsync(project.Slug);
        var source = board[0]; var target = board[1];
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var processor = await processors.SaveAsync(project.Slug, source.Id, "Qualification", "Qualify tickets", null,
            true, 20, [], [], [], defaultTargetColumnId: target.Id, jevObservationOnly: true);
        Assert.True((await processors.GetAsync(project.Slug, source.Id))!.JevObservationOnly);
        // Old clients omitting the new field must preserve observation mode.
        processor = await processors.SaveAsync(project.Slug, source.Id, "Qualification", "Qualify tickets", null,
            true, 20, [], [], [], defaultTargetColumnId: target.Id);
        Assert.True(processor.JevObservationOnly);
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Fix a bug", status: source.Name,
            pipelineId: source.PipelineId, columnId: source.Id);
        var executions = new ColumnExecutionService(projects, tickets);
        Assert.Null(await executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow));
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        await vault.SetAsync(project.Slug, JevQualificationService.SecretName, "test-secret");
        var handler = new FakeHandler(target.Id);
        using var client = new HttpClient(handler);
        var service = new JevQualificationService(projects, vault, client);
        await service.ObserveAsync(project.Slug, processor, ticket, board, CancellationToken.None);
        var proposal = await service.GetAsync(project.Slug, ticket.Id);
        Assert.Null(proposal!.Error);
        Assert.Equal($"column-{target.Id}", proposal.Choice);
        Assert.Equal(source.Id, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
        Assert.Equal(ticket.UpdatedAt, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.UpdatedAt);
        var restarted = new JevQualificationService(projects, vault, client);
        await restarted.ObserveAsync(project.Slug, processor, ticket, board, CancellationToken.None);
        Assert.Equal(1, handler.Count);
        processor.JevObservationOnly = false;
        ticket.Description = "Changed";
        await restarted.ObserveAsync(project.Slug, processor, ticket, board, CancellationToken.None);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Missing_credentials_are_visible_and_do_not_make_http_calls()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Missing key");
        var board = await new ColumnService(projects).ListColumnsAsync(project.Slug);
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Qualify", status: board[0].Name,
            pipelineId: board[0].PipelineId, columnId: board[0].Id);
        var processor = new ColumnProcessor { Name = "Qualifier", ColumnId = board[0].Id,
            DefaultTargetColumnId = board[1].Id, JevObservationOnly = true };
        var handler = new FakeHandler(board[1].Id);
        using var client = new HttpClient(handler);
        var service = new JevQualificationService(projects,
            new ProjectSecretVault(temp.Path, new TestSecretProtector()), client,
            new JevConfigurationService(new ProjectSecretVault(temp.Path, new TestSecretProtector()), temp.Path, () => null, new TestSecretProtector()));
        await service.ObserveAsync(project.Slug, processor, ticket, board, CancellationToken.None);
        var proposal = await service.GetAsync(project.Slug, ticket.Id);
        Assert.Contains("JEV is unavailable", proposal!.Error);
        Assert.Null(proposal.Choice);
        Assert.Equal(0, handler.Count);
        Assert.Equal(board[0].Id, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
    }

    [Fact]
    public void Invalid_or_unknown_destinations_are_rejected()
    {
        var destinations = new Dictionary<string, string> { ["none"] = "No match", ["column-2"] = "Review" };
        Assert.Throws<InvalidOperationException>(() => JevQualificationService.ValidateAnswer(destinations,
            "column-999", 1, new() { ["none"] = 0, ["column-2"] = 1 }));
        Assert.Throws<InvalidOperationException>(() => JevQualificationService.ValidateAnswer(destinations,
            "none", double.NaN, new() { ["none"] = 1, ["column-2"] = 0 }));
        Assert.Throws<InvalidOperationException>(() => JevQualificationService.ValidateAnswer(destinations,
            "none", 1, new() { ["none"] = 1 }));
    }

    [Fact]
    public void Failure_routes_are_not_business_routing_candidates()
    {
        var processor = new ColumnProcessor { Name = "Test", ColumnId = 1, TechnicalFailureColumnId = 3,
            DefaultTargetColumnId = 2, Routes = [new("needs-review", 2)] };
        var options = JevQualificationService.BuildDestinations(processor,
            [new() { Id = 2, Name = "Review" }, new() { Id = 3, Name = "Failed" }]);
        Assert.Equal(2, options.Count);
        Assert.Contains("needs-review", options["column-2"]);
        Assert.Contains("none", options.Keys);
    }

    private sealed class FakeHandler(int target) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++;
            Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Contains("criteria", await request.Content!.ReadAsStringAsync(ct));
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                model = "jev-test", answers = new { routing = new { type = "choice", choice = $"column-{target}",
                    confidence = 0.9, probabilities = new Dictionary<string, double> { [$"column-{target}"] = 0.95, ["none"] = 0.05 } } },
                usage = new { input_tokens = 100, output_tokens = 20 }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }
    }
}
