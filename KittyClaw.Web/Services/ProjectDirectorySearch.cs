using KittyClaw.Core.Models;

namespace KittyClaw.Web.Services;

public static class ProjectDirectorySearch
{
    public static bool Matches(Project project, IReadOnlyList<BoardColumnSummary> columns, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(term =>
            project.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || project.Slug.Contains(term, StringComparison.OrdinalIgnoreCase)
            || columns.Any(column => column.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || column.PipelineName.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }
}
