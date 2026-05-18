namespace KittyClaw.Web.Services;

public sealed class EscapeKeyStack
{
    private readonly List<Entry> _entries = new();
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public IDisposable Push(Action onEscape)
    {
        if (onEscape is null) throw new ArgumentNullException(nameof(onEscape));
        var entry = new Entry(onEscape, this);
        lock (_gate) _entries.Add(entry);
        return entry;
    }

    public bool HandleEscape()
    {
        Entry? top;
        lock (_gate)
        {
            if (_entries.Count == 0) return false;
            top = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            top.MarkRemoved();
        }
        top.Handler();
        return true;
    }

    private void Remove(Entry entry)
    {
        lock (_gate)
        {
            var idx = _entries.IndexOf(entry);
            if (idx >= 0) _entries.RemoveAt(idx);
        }
    }

    private sealed class Entry : IDisposable
    {
        private readonly EscapeKeyStack _owner;
        private bool _removed;

        public Entry(Action handler, EscapeKeyStack owner)
        {
            Handler = handler;
            _owner = owner;
        }

        public Action Handler { get; }

        public void MarkRemoved() => _removed = true;

        public void Dispose()
        {
            if (_removed) return;
            _removed = true;
            _owner.Remove(this);
        }
    }
}
