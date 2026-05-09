using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax.Inlines;

namespace KittyClaw.Core.Extensions;

/// <summary>
/// Markdig extension that transforms #N and #{slug}:{N} into clickable ticket links.
/// Same-project references (#N) are validated against <paramref name="tickets"/>.
/// Cross-project references (#{slug}:{N}) are validated against <paramref name="crossProjectTickets"/>;
/// syntactically valid but unresolved references render as a warning badge.
/// </summary>
public class TicketReferenceExtension(
    string slug,
    Dictionary<int, string> tickets,
    Dictionary<string, Dictionary<int, string>>? crossProjectTickets = null) : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline) =>
        pipeline.InlineParsers.Insert(0, new TicketReferenceParser(slug, tickets, crossProjectTickets ?? []));

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }
}

public class TicketReferenceParser : InlineParser
{
    private readonly string _slug;
    private readonly Dictionary<int, string> _tickets;
    private readonly Dictionary<string, Dictionary<int, string>> _crossProjectTickets;

    public TicketReferenceParser(
        string slug,
        Dictionary<int, string> tickets,
        Dictionary<string, Dictionary<int, string>> crossProjectTickets)
    {
        OpeningCharacters = ['#'];
        _slug = slug;
        _tickets = tickets;
        _crossProjectTickets = crossProjectTickets;
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var afterHash = slice.PeekChar(1);
        if (!char.IsLetterOrDigit(afterHash)) return false;

        slice.NextChar(); // skip '#'

        // Read the leading token: letters, digits, hyphens (either slug or plain number)
        var tokenStart = slice.Start;
        while (char.IsLetterOrDigit(slice.CurrentChar) || slice.CurrentChar == '-')
            slice.NextChar();
        var tokenLen = slice.Start - tokenStart;
        if (tokenLen == 0) return false;
        var token = slice.Text.Substring(tokenStart, tokenLen);

        if (slice.CurrentChar == ':' && char.IsAsciiDigit(slice.PeekChar(1)))
        {
            // Cross-project reference: #{slug}:{id}
            slice.NextChar(); // skip ':'
            var numStart = slice.Start;
            while (char.IsAsciiDigit(slice.CurrentChar))
                slice.NextChar();
            var numLen = slice.Start - numStart;
            if (numLen == 0) return false;
            if (!int.TryParse(slice.Text.Substring(numStart, numLen), out var ticketId)) return false;

            // Don't match if immediately followed by alphanumeric (e.g. #crm:42abc)
            if (char.IsLetterOrDigit(slice.CurrentChar) || slice.CurrentChar == '_') return false;

            var refSlug = token.ToLowerInvariant();
            string html;
            if (_crossProjectTickets.TryGetValue(refSlug, out var projectTickets) &&
                projectTickets.TryGetValue(ticketId, out var crossTitle))
            {
                var escaped = System.Net.WebUtility.HtmlEncode(crossTitle);
                html = $"<a href=\"/board/{refSlug}/ticket/{ticketId}\" class=\"ticket-ref\" title=\"{escaped}\">#{refSlug}:{ticketId} \u2014 {escaped}</a>";
            }
            else
            {
                html = $"<span class=\"ticket-ref ticket-ref-invalid\" title=\"Unknown reference\">#{refSlug}:{ticketId}</span>";
            }
            processor.Inline = new HtmlInline(html);
            return true;
        }
        else if (int.TryParse(token, out var sameProjectId))
        {
            // Same-project reference: #{id}
            if (!_tickets.TryGetValue(sameProjectId, out var title)) return false;
            if (char.IsLetterOrDigit(slice.CurrentChar) || slice.CurrentChar == '_') return false;

            var escaped = System.Net.WebUtility.HtmlEncode(title);
            processor.Inline = new HtmlInline(
                $"<a href=\"/board/{_slug}/ticket/{sameProjectId}\" class=\"ticket-ref\" title=\"{escaped}\">#{sameProjectId} \u2014 {escaped}</a>");
            return true;
        }

        return false;
    }
}
