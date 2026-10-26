using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Api;

public sealed class TicketAcceptanceCriteriaHttpTests(TicketLabelsPatchTests.ApiFactory factory)
    : IClassFixture<TicketLabelsPatchTests.ApiFactory>
{
    [Fact]
    public async Task RestRoundTripIncludesEvidenceAndValidatesInputs()
    {
        using var client = factory.CreateClient();
        var projectResponse = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("acceptance-http-" + Guid.NewGuid().ToString("N")[..8]));
        projectResponse.EnsureSuccessStatusCode();
        var slug = (await projectResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString();
        var created = await client.PostAsJsonAsync($"/api/projects/{slug}/tickets", new CreateTicketRequest("Feature", "owner", "Todo"));
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var url = $"/api/projects/{slug}/tickets/{id}";
        var added = await client.PostAsJsonAsync(url + "/acceptance-criteria", new { text = "It works" });
        added.EnsureSuccessStatusCode();
        var criterion = await added.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", criterion.GetProperty("status").GetString());
        var criterionUrl = url + "/acceptance-criteria/" + criterion.GetProperty("id").GetString();
        var update = await client.PutAsJsonAsync(criterionUrl, new { text = "It works", status = "PASS", evidence = new { content = "{\"passed\":true}", mediaType = "application/json" } });
        update.EnsureSuccessStatusCode();
        var read = await client.GetFromJsonAsync<JsonElement>(url);
        var saved = Assert.Single(read.GetProperty("acceptanceCriteria").EnumerateArray());
        Assert.Equal("ReadyToEvaluate", saved.GetProperty("status").GetString());
        Assert.Equal("{\"passed\":true}", saved.GetProperty("evidence").GetProperty("content").GetString());
        var invalid = await client.PutAsJsonAsync(criterionUrl, new { text = "It works", status = "PASS", evidence = new { content = "oops", mediaType = "application/json" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(criterionUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(criterionUrl)).StatusCode);
    }
}
