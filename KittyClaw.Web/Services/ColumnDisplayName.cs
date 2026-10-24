using KittyClaw.Core.Models;

namespace KittyClaw.Web.Services;

/// <summary>Resolves internal column identities only at the presentation boundary.</summary>
public static class ColumnDisplayName
{
    public static bool IsColumn(string value) => TryGetId(value, out _);

    public static string Resolve(string value, IEnumerable<BoardColumn> columns, string unavailable)
        => TryGetId(value, out var id)
            ? columns.FirstOrDefault(column => column.Id == id)?.Name ?? unavailable
            : value;

    private static bool TryGetId(string value, out int id)
    {
        id = 0;
        return (value.StartsWith("column-", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("column:", StringComparison.OrdinalIgnoreCase))
            && int.TryParse(value.AsSpan(7), out id);
    }
}
