using System;
using System.Collections.Generic;
using System.Linq;
using KittyClaw.Core.Models;

namespace KittyClaw.Web.Services;

public enum ColumnSortMode
{
    Manual,
    Title,
    Priority,
    Assignee,
    CreatedAt,
    DueDate
}

public enum SortDirection
{
    Ascending,
    Descending
}

public sealed record ColumnSortSetting(ColumnSortMode Mode, SortDirection Direction);

public sealed class BoardSortState
{
    private readonly Dictionary<string, ColumnSortSetting> _byKey = new(StringComparer.Ordinal);

    public ColumnSortSetting Get(string slug, string column)
        => _byKey.TryGetValue(Key(slug, column), out var s) ? s : new ColumnSortSetting(ColumnSortMode.Manual, SortDirection.Ascending);

    public void Set(string slug, string column, ColumnSortMode mode, SortDirection direction)
    {
        var key = Key(slug, column);
        if (mode == ColumnSortMode.Manual) _byKey.Remove(key);
        else _byKey[key] = new ColumnSortSetting(mode, direction);
    }

    public void Clear(string slug, string column) => _byKey.Remove(Key(slug, column));

    public IReadOnlyDictionary<string, ColumnSortSetting> SnapshotForSlug(string slug)
    {
        var prefix = slug + "\u0000";
        return _byKey
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key.Substring(prefix.Length), kv => kv.Value);
    }

    public void LoadForSlug(string slug, IReadOnlyDictionary<string, ColumnSortSetting> settings)
    {
        var prefix = slug + "\u0000";
        foreach (var key in _byKey.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _byKey.Remove(key);
        foreach (var (col, setting) in settings)
            _byKey[Key(slug, col)] = setting;
    }

    private static string Key(string slug, string column) => slug + "\u0000" + column;

    public static IEnumerable<TicketSummary> ApplySort(
        IEnumerable<TicketSummary> tickets,
        ColumnSortMode mode,
        SortDirection direction)
    {
        if (mode == ColumnSortMode.Manual) return tickets;

        var list = tickets as IList<TicketSummary> ?? tickets.ToList();
        if (list.Count == 0) return list;

        return mode switch
        {
            ColumnSortMode.Title => OrderBy(list, t => t.Title ?? string.Empty,
                Comparer<string>.Create((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase)),
                direction, nullsLast: false),
            ColumnSortMode.Priority => OrderBy(list, t => (int)t.Priority, Comparer<int>.Default, direction, nullsLast: false),
            ColumnSortMode.Assignee => OrderByNullable(list, t => t.AssignedTo,
                Comparer<string>.Create((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase)),
                direction),
            ColumnSortMode.CreatedAt => OrderBy(list, t => t.CreatedAt, Comparer<DateTime>.Default, direction, nullsLast: false),
            ColumnSortMode.DueDate => OrderByNullable(list, t => t.DueDate, Comparer<DateTime>.Default, direction),
            _ => list
        };
    }

    private static IEnumerable<TicketSummary> OrderBy<TKey>(
        IList<TicketSummary> list,
        Func<TicketSummary, TKey> keySelector,
        IComparer<TKey> comparer,
        SortDirection direction,
        bool nullsLast)
    {
        var effective = direction == SortDirection.Ascending
            ? comparer
            : Comparer<TKey>.Create((a, b) => comparer.Compare(b, a));
        return list.OrderBy(keySelector, effective);
    }

    private static IEnumerable<TicketSummary> OrderByNullable<TKey>(
        IList<TicketSummary> list,
        Func<TicketSummary, TKey?> keySelector,
        IComparer<TKey> valueComparer,
        SortDirection direction)
        where TKey : struct
    {
        var dir = direction == SortDirection.Ascending ? 1 : -1;
        return list.OrderBy(t => t, Comparer<TicketSummary>.Create((a, b) =>
        {
            var ka = keySelector(a);
            var kb = keySelector(b);
            if (!ka.HasValue && !kb.HasValue) return 0;
            if (!ka.HasValue) return 1;
            if (!kb.HasValue) return -1;
            return dir * valueComparer.Compare(ka.Value, kb.Value);
        }));
    }

    private static IEnumerable<TicketSummary> OrderByNullable(
        IList<TicketSummary> list,
        Func<TicketSummary, string?> keySelector,
        IComparer<string> valueComparer,
        SortDirection direction)
    {
        var dir = direction == SortDirection.Ascending ? 1 : -1;
        return list.OrderBy(t => t, Comparer<TicketSummary>.Create((a, b) =>
        {
            var ka = keySelector(a);
            var kb = keySelector(b);
            if (ka is null && kb is null) return 0;
            if (ka is null) return 1;
            if (kb is null) return -1;
            return dir * valueComparer.Compare(ka, kb);
        }));
    }
}
