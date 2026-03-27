using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax.Inlines;

namespace Todo.Web.Extensions;

/// <summary>
/// Markdig extension that transforms #N into a clickable link to the ticket,
/// showing the ticket title as label (e.g. #4 — Fix login bug).
/// Only matches when the ticket ID exists in the provided dictionary.
/// </summary>
public class TicketReferenceExtension(string slug, Dictionary<int, string> tickets) : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline) =>
        pipeline.InlineParsers.Insert(0, new TicketReferenceParser(slug, tickets));

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }
}

public class TicketReferenceParser : InlineParser
{
    private readonly string _slug;
    private readonly Dictionary<int, string> _tickets;

    public TicketReferenceParser(string slug, Dictionary<int, string> tickets)
    {
        OpeningCharacters = ['#'];
        _slug = slug;
        _tickets = tickets;
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        // Peek at char after '#' — must be a digit
        if (!char.IsAsciiDigit(slice.PeekChar(1))) return false;

        slice.NextChar(); // skip '#'

        // Read all consecutive digits
        var numStart = slice.Start;
        while (char.IsAsciiDigit(slice.CurrentChar))
            slice.NextChar();

        var numLen = slice.Start - numStart;
        if (numLen == 0) return false;

        var numStr = slice.Text.Substring(numStart, numLen);
        if (!int.TryParse(numStr, out var ticketId)) return false;
        if (!_tickets.TryGetValue(ticketId, out var title)) return false;

        // Don't match if immediately followed by alphanumeric (e.g. #42abc)
        if (char.IsLetterOrDigit(slice.CurrentChar) || slice.CurrentChar == '_') return false;

        var escaped = System.Net.WebUtility.HtmlEncode(title);
        processor.Inline = new HtmlInline(
            $"<a href=\"/board/{_slug}/ticket/{ticketId}\" class=\"ticket-ref\" title=\"{escaped}\">#{ticketId} \u2014 {escaped}</a>");
        return true;
    }
}
