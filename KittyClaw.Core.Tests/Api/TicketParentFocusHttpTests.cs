using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Models;
using KittyClaw.Web.Api;
using KittyClaw.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KittyClaw.Core.Tests.Api;

public sealed class TicketParentFocusHttpTests(AtomicTicketPatchTests.ApiFactory factory)
    : IClassFixture<AtomicTicketPatchTests.ApiFactory>
{
    [Fact]
    public async Task ParentRoutesBindBodyAndOptionalDeleteQueryFocus()
    {
        using var client = factory.CreateClient();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var projectResponse = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("parent-focus-" + Guid.NewGuid().ToString("N")));
        projectResponse.EnsureSuccessStatusCode();
        var project = (await projectResponse.Content.ReadFromJsonAsync<Project>(jsonOptions))!;
        var baseUrl = $"/api/projects/{project.Slug}/tickets";
        var parentResponse = await client.PostAsJsonAsync(baseUrl, new CreateTicketRequest("Parent", "owner", "Backlog"));
        parentResponse.EnsureSuccessStatusCode();
        var parent = (await parentResponse.Content.ReadFromJsonAsync<Ticket>(jsonOptions))!;
        var childResponse = await client.PostAsJsonAsync(baseUrl, new CreateTicketRequest("Child", "owner", "Backlog"));
        childResponse.EnsureSuccessStatusCode();
        var child = (await childResponse.Content.ReadFromJsonAsync<Ticket>(jsonOptions))!;
        var focus = factory.Services.GetRequiredService<BoardFocusService>();
        var requests = new List<string>();
        void Capture(string slug, string url) { if (slug == project.Slug) requests.Add(url); }
        focus.Requested += Capture;
        try
        {
            var associated = await client.PutAsJsonAsync($"{baseUrl}/{child.Id}/parent", new { parentId = parent.Id, focus = true });
            Assert.Equal(HttpStatusCode.NoContent, associated.StatusCode);
            Assert.Contains($"/sub/{parent.Id}?pipeline=", Assert.Single(requests));
            requests.Clear();
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{baseUrl}/{child.Id}/parent?focus=true")).StatusCode);
            Assert.StartsWith($"/board/{project.Slug}?pipeline=", Assert.Single(requests));
            requests.Clear();
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"{baseUrl}/{child.Id}/parent", new { parentId = parent.Id })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{baseUrl}/{child.Id}/parent")).StatusCode);
            Assert.Empty(requests);
        }
        finally { focus.Requested -= Capture; }
    }
}
