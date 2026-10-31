namespace KittyClaw.Web.Services;

public static class ProjectNavigationUrls
{
    public static string Page(string slug, int? pipelineId, string? page = null, string? returnView = null)
    {
        var path = $"/board/{Uri.EscapeDataString(slug)}";
        if (!string.IsNullOrEmpty(page)) path += $"/{page}";
        if (pipelineId is int id) path += $"?pipeline={id}";
        if (returnView == "dashboard") path += (pipelineId.HasValue ? "&" : "?") + "view=dashboard";
        return path;
    }

    public static string Return(string slug, int? pipelineId, string? returnView)
        => Page(slug, pipelineId, returnView == "dashboard" ? "dashboard" : null);
}
