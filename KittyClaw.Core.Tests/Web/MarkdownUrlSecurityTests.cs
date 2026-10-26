using KittyClaw.Core.Services;
using KittyClaw.Web.Markdown;
using Markdig;
using Microsoft.Playwright;

namespace KittyClaw.Core.Tests.Web;

public sealed class MarkdownUrlSecurityTests
{
    public static IEnumerable<object[]> UnsafeUrls()
    {
        foreach (var renderer in new[] { "chat", "comments", "dashboard" })
        foreach (var url in new[]
        {
            "javascript:window.__securityAuditXss=1",
            "JaVaScRiPt:window.__securityAuditXss=1",
            "javascript&#x3a;window.__securityAuditXss=1",
            "java&#x09;script:window.__securityAuditXss=1",
            "data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==",
            "vbscript:msgbox",
            "file:///private/secret.txt",
            "custom-handler:run-command"
        }) yield return [renderer, url];
    }

    public static IEnumerable<object[]> SafeUrls()
    {
        foreach (var renderer in new[] { "chat", "comments", "dashboard" })
        foreach (var url in new[]
        {
            "https://example.test/page", "http://example.test/page", "mailto:owner@example.test",
            "tel:+33123456789", "/projects/audit#ticket-1", "../notes/readme.md", "#section", "?time=12:30"
        }) yield return [renderer, url];
    }

    private static string Render(string renderer, string markdown) => renderer switch
    {
        "chat" => ChatMarkdownRenderer.Render(markdown),
        "comments" => Markdown.ToHtml(markdown, CommentMarkdownPipeline.Build()),
        "dashboard" => TileRenderer.RenderByExtension(markdown, "audit.md"),
        _ => throw new ArgumentException(nameof(renderer))
    };

    [Theory]
    [MemberData(nameof(UnsafeUrls))]
    public void Unsafe_link_and_image_schemes_are_neutralized(string renderer, string url)
    {
        Assert.Contains("href=\"#\"", Render(renderer, $"[audit link]({url})"));
        Assert.Contains("src=\"#\"", Render(renderer, $"![audit image]({url})"));
    }

    [Theory]
    [MemberData(nameof(SafeUrls))]
    public void Safe_links_keep_their_destination(string renderer, string url)
    {
        Assert.Contains($"href=\"{url}\"", Render(renderer, $"[audit link]({url})"));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("comments")]
    [InlineData("dashboard")]
    public void Reference_links_and_autolinks_also_use_the_URL_policy(string renderer)
    {
        var reference = Render(renderer, "[audit][ref]\n\n[ref]: javascript:window.__securityAuditXss=1");
        Assert.Contains("href=\"#\"", reference);
        var autolink = Render(renderer, "<javascript:window.__securityAuditXss=1>");
        Assert.Contains("href=\"#\"", autolink);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("comments")]
    [InlineData("dashboard")]
    public async Task Clicking_untrusted_Markdown_cannot_execute_a_javascript_URL(string renderer)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true, Channel = Environment.GetEnvironmentVariable("KITTYCLAW_TEST_BROWSER_CHANNEL")
        });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/*", route => route.AbortAsync());
        await page.SetContentAsync(Render(renderer, "[audit link](javascript:window.__securityAuditXss=1)"));
        await page.GetByRole(AriaRole.Link, new() { Name = "audit link" }).ClickAsync();
        Assert.False(await page.EvaluateAsync<bool>("window.__securityAuditXss === 1"));
    }
}
