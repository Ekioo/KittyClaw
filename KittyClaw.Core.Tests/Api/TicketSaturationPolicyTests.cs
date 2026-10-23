using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KittyClaw.Core.Tests.Api;

[Collection("TicketSaturationPolicy")]
public sealed class TicketSaturationPolicyTests : IClassFixture<TicketSaturationPolicyTests.ApiFactory>, IDisposable
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public TicketSaturationPolicyTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task TicketCreationRemainsAvailableWithManyBlockedTickets()
    {
        var (slug, blocked) = await CreateProjectWithCustomBlockedColumnAsync();
        for (var i = 1; i <= 10; i++)
            (await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest($"Blocked {i}", "owner", blocked.Name, PipelineId: blocked.PipelineId, ColumnId: blocked.Id)))
                .EnsureSuccessStatusCode();

        var response = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
            new CreateTicketRequest("Ticket eight", "owner", "Todo"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var projects = _factory.Services.GetRequiredService<ProjectService>();
        await using var db = projects.GetProjectDb(slug);
        Assert.Equal(11, await db.Tickets.CountAsync());
        Assert.Equal(11, await db.ActivityEntries.CountAsync());
    }

    [Fact]
    public async Task ColumnCapacityIsOptionalAndWarningModeDoesNotBlock()
    {
        var (slug, column) = await CreateProjectWithCustomBlockedColumnAsync();
        var service = _factory.Services.GetRequiredService<ColumnService>();
        await service.UpdateColumnAsync(slug, column.Id, maxTickets: 1, capacityBlocking: false);
        for (var i = 0; i < 3; i++)
            (await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest($"Warning {i}", "owner", column.Name, PipelineId: column.PipelineId, ColumnId: column.Id)))
                .EnsureSuccessStatusCode();
        var saved = (await service.ListColumnsAsync(slug, column.PipelineId)).Single(c => c.Id == column.Id);
        Assert.Equal(1, saved.MaxTickets);
        Assert.False(saved.CapacityBlocking);
    }

    [Fact]
    public async Task BlockingCapacityRejectsArrivalsButAllowsEditsAndDeparture()
    {
        var (slug, column) = await CreateProjectWithCustomBlockedColumnAsync();
        var columns = _factory.Services.GetRequiredService<ColumnService>();
        var tickets = _factory.Services.GetRequiredService<TicketService>();
        await columns.UpdateColumnAsync(slug, column.Id, maxTickets: 1, capacityBlocking: true);
        var first = await tickets.CreateTicketAsync(slug, "First", status: column.Name, pipelineId: column.PipelineId, columnId: column.Id);
        var response = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
            new CreateTicketRequest("Rejected", "owner", column.Name, PipelineId: column.PipelineId, ColumnId: column.Id));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await tickets.UpdateTicketAsync(slug, first.Id, description: "Preserved edit");
        await tickets.ReorderTicketAsync(slug, first.Id, column.Name, 0);
        var outside = await columns.CreateColumnAsync(slug, "Outside", pipelineId: column.PipelineId);
        var second = await tickets.CreateTicketAsync(slug, "Second", status: outside.Name, pipelineId: outside.PipelineId, columnId: outside.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tickets.MoveTicketAsync(slug, second.Id, column.Name));
        Assert.Equal(outside.Id, (await tickets.GetTicketAsync(slug, second.Id))!.ColumnId);
        await tickets.MoveTicketAsync(slug, first.Id, outside.Name);
        await tickets.MoveTicketAsync(slug, second.Id, column.Name);
        Assert.Equal("Preserved edit", (await tickets.GetTicketAsync(slug, first.Id))!.Description);
        await columns.UpdateColumnAsync(slug, column.Id, maxTickets: 0);
        await tickets.CreateTicketAsync(slug, "Unlimited again", status: column.Name, pipelineId: column.PipelineId, columnId: column.Id);
    }

    [Fact]
    public async Task BlockingCapacitySerializesConcurrentArrivals()
    {
        var (slug, column) = await CreateProjectWithCustomBlockedColumnAsync();
        await _factory.Services.GetRequiredService<ColumnService>()
            .UpdateColumnAsync(slug, column.Id, maxTickets: 1, capacityBlocking: true);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest($"Concurrent {i}", "owner", column.Name, PipelineId: column.PipelineId, ColumnId: column.Id))));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
    }

    private async Task<(string Slug, BoardColumn Blocked)> CreateProjectWithCustomBlockedColumnAsync()
    {
        var projectResponse = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("saturation-" + Guid.NewGuid().ToString("N")[..8]));
        projectResponse.EnsureSuccessStatusCode();
        var slug = (await projectResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var pipelineResponse = await _client.PostAsJsonAsync($"/api/projects/{slug}/pipelines", new CreatePipelineRequest("Operations"));
        Assert.True(pipelineResponse.IsSuccessStatusCode, await pipelineResponse.Content.ReadAsStringAsync());
        var pipelineId = (await pipelineResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var columnResponse = await _client.PostAsJsonAsync($"/api/projects/{slug}/columns",
            new CreateColumnRequest("Needs vendor", PipelineId: pipelineId, Role: ColumnRole.Blocked));
        columnResponse.EnsureSuccessStatusCode();
        var columnJson = await columnResponse.Content.ReadFromJsonAsync<JsonElement>();
        var blocked = new BoardColumn
        {
            Id = columnJson.GetProperty("id").GetInt32(),
            PipelineId = columnJson.GetProperty("pipelineId").GetInt32(),
            Name = columnJson.GetProperty("name").GetString()!,
            Color = columnJson.GetProperty("color").GetString()!,
            SortOrder = columnJson.GetProperty("sortOrder").GetInt32(),
            Role = ColumnRole.Blocked
        };
        return (slug, blocked);
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-saturation-" + Guid.NewGuid().ToString("N"));

        public ApiFactory()
        {
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(Path.Combine(_dataDir, "settings.json"), """{"OnboardingSeen":true,"Language":"en"}""");
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", _dataDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", null);
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }
}

[CollectionDefinition("TicketSaturationPolicy", DisableParallelization = true)]
public sealed class TicketSaturationPolicyCollection;
