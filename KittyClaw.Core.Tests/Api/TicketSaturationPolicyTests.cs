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
    public async Task EighthTicketIsRejectedBeforePersistenceForCustomBlockedRole()
    {
        var (slug, blocked) = await CreateProjectWithCustomBlockedColumnAsync();
        for (var i = 1; i <= 7; i++)
            (await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest($"Blocked {i}", "owner", blocked.Name, PipelineId: blocked.PipelineId, ColumnId: blocked.Id)))
                .EnsureSuccessStatusCode();

        var response = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
            new CreateTicketRequest("Ticket eight", "owner", "Todo"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("blocked_ticket_limit_reached", error.GetProperty("code").GetString());
        Assert.Equal(7, error.GetProperty("blockedCount").GetInt32());
        Assert.Equal(7, error.GetProperty("blockedLimit").GetInt32());
        Assert.Contains(blocked.Id, error.GetProperty("blockedColumnIds").EnumerateArray().Select(x => x.GetInt32()));

        var projects = _factory.Services.GetRequiredService<ProjectService>();
        await using var db = projects.GetProjectDb(slug);
        Assert.Equal(7, await db.Tickets.CountAsync());
        Assert.Equal(7, await db.ActivityEntries.CountAsync());
    }

    [Fact]
    public async Task OwnerRecoveryOverrideCreatesTicketAndWritesAuditActivity()
    {
        var (slug, blocked) = await CreateProjectWithCustomBlockedColumnAsync();
        for (var i = 1; i <= 7; i++)
            (await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest($"Blocked {i}", "owner", blocked.Name, PipelineId: blocked.PipelineId, ColumnId: blocked.Id)))
                .EnsureSuccessStatusCode();

        var response = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets", new
        {
            title = "Unblock dependency",
            createdBy = "owner",
            status = "Todo",
            saturationOverride = new { kind = "recovery", reason = "Creates the task that clears the blocked queue" }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticketId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var ticket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{ticketId}");
        Assert.Contains(ticket.GetProperty("activities").EnumerateArray(), a =>
            a.GetProperty("text").GetString()!.Contains("recovery saturation override")
            && a.GetProperty("author").GetString() == "owner");
    }

    [Fact]
    public async Task RecoveryOverrideRequiresOwnerAndReason()
    {
        var (slug, _) = await CreateProjectWithCustomBlockedColumnAsync();
        var response = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets", new
        {
            title = "Not authorized",
            createdBy = "automation",
            status = "Todo",
            saturationOverride = new { kind = "recovery", reason = "bypass" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var projects = _factory.Services.GetRequiredService<ProjectService>();
        await using var db = projects.GetProjectDb(slug);
        Assert.Empty(await db.Tickets.ToListAsync());
        Assert.Empty(await db.ActivityEntries.ToListAsync());
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
