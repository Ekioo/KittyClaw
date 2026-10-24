using System.Net;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace KittyClaw.Core.Tests.Web;

public sealed class BrowserSecurityHeadersTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Protects_responses_and_preserves_endpoint_policies(int status)
    {
        using var server = new TestServer(new WebHostBuilder().Configure(app =>
        {
            app.UseMiddleware<BrowserSecurityHeadersMiddleware>();
            app.Run(async context =>
            {
                context.Response.StatusCode = status;
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                await context.Response.WriteAsync("test");
            });
        }));
        using var client = server.CreateClient();
        using var response = await client.GetAsync("/");
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        var policies = response.Headers.GetValues("Content-Security-Policy").ToArray();
        Assert.Contains("default-src 'none'; sandbox", policies);
        Assert.Contains("frame-ancestors 'none'; object-src 'none'; base-uri 'self'", policies);
    }
}
