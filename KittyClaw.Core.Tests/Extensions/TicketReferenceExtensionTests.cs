using Markdig;
using KittyClaw.Core.Extensions;

namespace KittyClaw.Core.Tests.Extensions;

public class TicketReferenceExtensionTests
{
    private static string Render(
        string markdown,
        string slug = "todo",
        Dictionary<int, string>? tickets = null,
        Dictionary<string, Dictionary<int, string>>? crossProjectTickets = null)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Use(new TicketReferenceExtension(slug, tickets ?? [], crossProjectTickets ?? []))
            .Build();
        return Markdown.ToHtml(markdown, pipeline).Trim();
    }

    // Same-project references

    [Fact]
    public void SameProject_KnownId_RendersLink()
    {
        var tickets = new Dictionary<int, string> { [42] = "Fix login bug" };
        var html = Render("See #42 please", tickets: tickets);
        Assert.Contains("href=\"/board/todo/ticket/42\"", html);
        Assert.Contains("ticket-ref", html);
        Assert.Contains("Fix login bug", html);
    }

    [Fact]
    public void SameProject_UnknownId_RendersPlainText()
    {
        var html = Render("See #99 please", tickets: []);
        Assert.DoesNotContain("ticket-ref", html);
        Assert.Contains("#99", html);
    }

    [Fact]
    public void SameProject_FollowedByAlphanumeric_NoMatch()
    {
        var tickets = new Dictionary<int, string> { [42] = "Fix login bug" };
        var html = Render("#42abc", tickets: tickets);
        Assert.DoesNotContain("ticket-ref", html);
    }

    // Cross-project references

    [Fact]
    public void CrossProject_KnownSlugAndId_RendersLink()
    {
        var cross = new Dictionary<string, Dictionary<int, string>>
        {
            ["crm"] = new() { [10] = "CRM ticket" }
        };
        var html = Render("See #crm:10", crossProjectTickets: cross);
        Assert.Contains("href=\"/board/crm/ticket/10\"", html);
        Assert.Contains("ticket-ref", html);
        Assert.Contains("CRM ticket", html);
    }

    [Fact]
    public void CrossProject_UnknownId_RendersInvalidBadge()
    {
        var cross = new Dictionary<string, Dictionary<int, string>>
        {
            ["crm"] = new() { [10] = "CRM ticket" }
        };
        var html = Render("See #crm:99", crossProjectTickets: cross);
        Assert.Contains("ticket-ref-invalid", html);
        Assert.Contains("#crm:99", html);
        Assert.DoesNotContain("href=", html);
    }

    [Fact]
    public void CrossProject_UnknownSlug_RendersInvalidBadge()
    {
        var html = Render("See #ghost:5", crossProjectTickets: []);
        Assert.Contains("ticket-ref-invalid", html);
        Assert.Contains("#ghost:5", html);
    }

    [Fact]
    public void CrossProject_HyphenatedSlug_RendersLink()
    {
        var cross = new Dictionary<string, Dictionary<int, string>>
        {
            ["api-gateway"] = new() { [156] = "Rate limiting" }
        };
        var html = Render("See #api-gateway:156", crossProjectTickets: cross);
        Assert.Contains("href=\"/board/api-gateway/ticket/156\"", html);
        Assert.Contains("Rate limiting", html);
    }

    [Fact]
    public void CrossProject_FollowedByAlphanumeric_NoMatch()
    {
        var cross = new Dictionary<string, Dictionary<int, string>>
        {
            ["crm"] = new() { [10] = "CRM ticket" }
        };
        var html = Render("#crm:10abc", crossProjectTickets: cross);
        Assert.DoesNotContain("href=", html);
    }

    [Fact]
    public void BothSameAndCrossProject_InOneParagraph()
    {
        var tickets = new Dictionary<int, string> { [1] = "Local ticket" };
        var cross = new Dictionary<string, Dictionary<int, string>>
        {
            ["crm"] = new() { [2] = "CRM ticket" }
        };
        var html = Render("Local #1 and cross-project #crm:2", tickets: tickets, crossProjectTickets: cross);
        Assert.Contains("href=\"/board/todo/ticket/1\"", html);
        Assert.Contains("href=\"/board/crm/ticket/2\"", html);
    }
}
