using System.Net;
using System.Text;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace KittyClaw.Core.Tests.Api;

public sealed class ApiFileSecurityTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");

    [Fact]
    public async Task Rejected_chat_image_batch_leaves_no_partial_files()
    {
        var runId = Guid.NewGuid().ToString("N");
        var valid = new ChatImageDto("data:image/png;base64," + Convert.ToBase64String(Png), "image/png", "../ignored.png", Png.Length);
        var fake = new ChatImageDto(Convert.ToBase64String("<svg onload='alert(1)'/>"u8), "image/png", "fake.png", 1);
        var result = await Endpoints.PersistChatImagesAsync([valid, fake], "", runId);
        Assert.NotNull(result.RejectReason);
        Assert.Null(result.Paths);
        Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "KittyClaw", "chat-images", runId)));
    }

    [Theory]
    [InlineData("IMAGE/PNG", true)]
    [InlineData("image/jpeg", false)]
    public async Task Chat_images_use_signature_and_case_insensitive_MIME(string mime, bool accepted)
    {
        var result = await Endpoints.PersistChatImagesAsync(
            [new ChatImageDto(Convert.ToBase64String(Png), mime, "untrusted.exe", 1)], "", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(accepted, result.RejectReason is null);
            if (accepted) Assert.EndsWith(".png", Assert.Single(result.Paths!));
        }
        finally
        {
            if (result.Paths is not null)
                foreach (var path in result.Paths) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public async Task Chat_image_size_is_bounded_even_with_forged_size_metadata()
    {
        var result = await Endpoints.PersistChatImagesAsync(
            [new ChatImageDto(new string('A', 8 * 1024 * 1024), "image/png", "large.png", 1)], "", Guid.NewGuid().ToString("N"));
        Assert.NotNull(result.RejectReason);
        Assert.Null(result.Paths);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(8 * 1024 * 1024, true)]
    [InlineData(8 * 1024 * 1024 + 1024, false)]
    public async Task Pipeline_stream_is_bounded_without_a_content_length(int length, bool accepted)
    {
        using var source = new MemoryStream(new byte[length]);
        var result = await Endpoints.ReadBoundedPipelineArchiveAsync(source, default);
        Assert.Equal(accepted, result is not null);
        Assert.True(source.Position <= 8 * 1024 * 1024 + 1);
    }

    [Fact]
    public async Task Raw_dashboard_SVG_is_sandboxed_and_image_upload_rejects_active_content()
    {
        using var dir = new TempDir();
        var before = Environment.GetEnvironmentVariable("KITTYCLAW_DATA_DIR");
        Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", dir.Path);
        try
        {
            await using var factory = new Factory();
            using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://localhost") });
            var projects = factory.Services.GetRequiredService<ProjectService>();
            var project = await projects.CreateProjectAsync("File security audit");
            var tile = Path.Combine(projects.ResolveWorkspacePath(project), ".dashboard", "unsafe");
            Directory.CreateDirectory(tile);
            const string svg = "<svg xmlns='http://www.w3.org/2000/svg' onload='alert(document.domain)'/>";
            await File.WriteAllTextAsync(Path.Combine(tile, "output.svg"), svg);
            using var response = await client.GetAsync($"/api/projects/{project.Slug}/dashboard/tiles/unsafe/output/raw");
            response.EnsureSuccessStatusCode();
            Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
            Assert.Contains("default-src 'none'; style-src 'unsafe-inline'; sandbox",
                response.Headers.GetValues("Content-Security-Policy"));
            Assert.Equal(svg, await response.Content.ReadAsStringAsync());

            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(svg)), "file", "pretend.png");
            using var rejected = await client.PostAsync("/api/images", form);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "uploads")));
            using var validForm = new MultipartFormDataContent();
            validForm.Add(new ByteArrayContent(Png), "file", "../../untrusted.svg");
            using var accepted = await client.PostAsync("/api/images", validForm);
            accepted.EnsureSuccessStatusCode();
            Assert.EndsWith(".png", Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "uploads"))));
        }
        finally { Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", before); }
    }

    private sealed class Factory : WebApplicationFactory<CreateProjectRequest>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
