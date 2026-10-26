using KittyClaw.Core.Models;

namespace KittyClaw.Web.Services;

/// <summary>Transient navigation requests for connected views of the same project.</summary>
public sealed class BoardFocusService
{
    public event Action<string, string>? Requested;

    public static bool MatchesProjectView(string path, string slug)
    {
        var projectPath = $"/board/{Uri.EscapeDataString(slug)}";
        return path.Equals(projectPath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(projectPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static string TicketUrl(string slug, Ticket ticket)
    {
        var path = $"/board/{Uri.EscapeDataString(slug)}";
        if (ticket.ParentId is int parent) path += $"/sub/{parent}";
        return $"{path}?pipeline={ticket.PipelineId}&focus={ticket.Id}&focusRequest={Guid.NewGuid():N}";
    }

    public string Request(string slug, Ticket ticket)
    {
        var url = TicketUrl(slug, ticket);
        Requested?.Invoke(slug, url);
        return url;
    }

    public string RequestTransfer(string sourceProject, string targetProject, Ticket ticket)
    {
        var url = Request(targetProject, ticket);
        if (!string.Equals(sourceProject, targetProject, StringComparison.OrdinalIgnoreCase))
            Requested?.Invoke(sourceProject, url);
        return url;
    }
}
