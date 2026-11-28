using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace KittyClaw.Core.Tests.Api;

public sealed class TicketAttachmentHttpTests : IClassFixture<TicketAttachmentHttpTests.Factory>
{
    private readonly Factory _factory;
    public TicketAttachmentHttpTests(Factory factory) => _factory = factory;

    private static MultipartFormDataContent Upload(byte[] bytes, string name = "report.txt", string? author = "agent")
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", name);
        if (author is not null) form.Add(new StringContent(author), "author");
        return form;
    }
    private async Task<(HttpClient client, string slug, int id)> Ticket()
    {
        var client = _factory.CreateClient();
        var project = await client.PostAsJsonAsync("/api/projects", new { name = "Attachments " + Guid.NewGuid().ToString("N") });
        project.EnsureSuccessStatusCode();
        var slug = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var ticket = await client.PostAsJsonAsync($"/api/projects/{slug}/tickets", new CreateTicketRequest("File test", "owner", "Todo"));
        ticket.EnsureSuccessStatusCode();
        return (client, slug, (await ticket.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Upload_lists_downloads_and_links_the_file_without_executing_active_content()
    {
        var (client, slug, id) = await Ticket(); using var _ = client;
        var path = $"/api/projects/{slug}/tickets/{id}/attachments";
        var bytes = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        using var form = Upload(bytes, "../../report.html");
        var response = await client.PostAsync(path, form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var attachment = await response.Content.ReadFromJsonAsync<TicketAttachmentResponse>();
        Assert.NotNull(attachment); Assert.Equal("report.html", attachment.FileName);
        Assert.Equal(bytes.Length, attachment.Size); Assert.Equal(attachment.Url, response.Headers.Location!.ToString());
        var list = await client.GetFromJsonAsync<TicketAttachmentResponse[]>(path);
        Assert.Equal(attachment.Id, Assert.Single(list!).Id);
        var download = await client.GetAsync(attachment.Url);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains(download.Headers.GetValues("Content-Security-Policy"), policy => policy.Contains("sandbox"));
        var ticket = await client.GetStringAsync($"/api/projects/{slug}/tickets/{id}");
        Assert.Contains(attachment.Url, ticket);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{slug}/tickets/{id + 1}/attachments/{attachment.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/missing/tickets/{id}/attachments/{attachment.Id}")).StatusCode);
    }

    [Fact]
    public async Task Rejected_requests_leave_no_attachment_or_comment()
    {
        var (client, slug, id) = await Ticket(); using var _ = client;
        var path = $"/api/projects/{slug}/tickets/{id}/attachments";
        foreach (var form in new[] { Upload([]), Upload([1], author: null), Upload([1], name: "..") })
        {
            using (form) Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, form)).StatusCode);
        }
        using var oversized = Upload(new byte[TicketAttachmentService.MaximumBytes + 65537]);
        var oversizedResponse = await client.PostAsync(path, oversized);
        Assert.True(oversizedResponse.StatusCode == HttpStatusCode.RequestEntityTooLarge,
            $"{oversizedResponse.StatusCode}: {await oversizedResponse.Content.ReadAsStringAsync()}");
        using var unboundedForm = Upload(new byte[TicketAttachmentService.MaximumBytes + 65537]);
        using var unknownLength = new UnknownLengthContent(unboundedForm);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync(path, unknownLength)).StatusCode);
        using var two = Upload([1]); two.Add(new ByteArrayContent([2]), "file", "other.txt");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, two)).StatusCode);
        using var absent = Upload([1]);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/projects/{slug}/tickets/{id + 1}/attachments", absent)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<TicketAttachmentResponse[]>(path))!);
        var ticket = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Empty(ticket.GetProperty("comments").EnumerateArray());
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly HttpContent _inner;
        public UnknownLengthContent(HttpContent inner)
        { _inner = inner; Headers.ContentType = inner.Headers.ContentType; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task Retries_are_deduplicated_conflicting_payloads_refused_and_deleted_uploads_cannot_resurrect()
    {
        var (client, slug, id) = await Ticket(); using var _ = client;
        var path = $"/api/projects/{slug}/tickets/{id}/attachments";
        var key = Guid.NewGuid().ToString("N");
        async Task<HttpResponseMessage> Send(byte value)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Upload([value]) };
            request.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(request);
        }
        var responses = await Task.WhenAll(Send(42), Send(42));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var replay = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        var attachments = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<TicketAttachmentResponse>()));
        Assert.Equal(attachments[0]!.Id, attachments[1]!.Id);
        Assert.Equal(64, attachments[0]!.Sha256.Length);
        Assert.Single((await client.GetFromJsonAsync<TicketAttachmentResponse[]>(path))!);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(43)).StatusCode);
        var ticket = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Single(ticket.GetProperty("comments").EnumerateArray());
        var attachment = attachments[0]!;
        for (var i = 0; i < 2; i++)
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, attachment.Url) { Content = JsonContent.Create(new DeleteTicketAttachmentRequest("owner")) };
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(attachment.Url)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<TicketAttachmentResponse[]>(path))!);
        Assert.Equal(HttpStatusCode.Gone, (await Send(42)).StatusCode);
        Assert.False(File.Exists(_factory.Services.GetRequiredService<TicketAttachmentService>().BlobPath(attachment.Id)));
        Assert.DoesNotContain(attachment.Url, await client.GetStringAsync($"/api/projects/{slug}/tickets/{id}"));
    }

    public sealed class Factory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "kittyclaw-attachments-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previous = Environment.GetEnvironmentVariable("KITTYCLAW_DATA_DIR");
        public Factory()
        {
            Directory.CreateDirectory(_directory);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", _directory);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", _previous);
            try { Directory.Delete(_directory, true); } catch { }
        }
    }
}
