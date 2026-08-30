using System.Diagnostics.CodeAnalysis;

namespace EmbyNian.Infrastructure;

/// <summary>
/// A bounded cache that throws away whatever was used least recently. <see cref="TryGet"/> counts as a
/// use, so what stays is what is being looked at.
/// <para>
/// It exists for the decoded posters the shelves show. Those cannot simply be kept — a screen of forty
/// covers is tens of megabytes of decoded surface, and a library page scrolls past thousands — and they
/// cannot simply be dropped either, which is what the card used to do: scrolling back up re-read every
/// poster from disk and decoded it again. A ceiling on the count is the middle answer, and the ceiling
/// is the caller's to choose because only the caller knows how big one entry is.
/// </para>
/// </summary>
public sealed class LruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;

    /// <summary>Most recently used first, so the one to evict is always the last.</summary>
    private readonly LinkedList<Entry> _order = new();

    private readonly object _gate = new();

    public LruCache(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "容量至少为 1");

        Capacity = capacity;
        _entries = new Dictionary<TKey, LinkedListNode<Entry>>(capacity);
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>The keys held, most recently used first. For tests: eviction order is the whole contract.</summary>
    public IReadOnlyList<TKey> Keys
    {
        get
        {
            lock (_gate) return _order.Select(entry => entry.Key).ToList();
        }
    }

    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                value = default!;
                return false;
            }

            Promote(node);
            value = node.Value.Value;
            return true;
        }
    }

    /// <summary>Adds or replaces, and evicts down to <see cref="Capacity"/>.</summary>
    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.Value = new Entry(key, value);
                Promote(existing);
                return;
            }

            var node = new LinkedListNode<Entry>(new Entry(key, value));
            _order.AddFirst(node);
            _entries[key] = node;

            while (_entries.Count > Capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _entries.Remove(oldest.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
        }
    }

    /// <summary>
    /// Moves a node to the front. Removing first is what makes re-adding it legal — a node still owned
    /// by a list cannot be inserted into one — and it keeps the value's identity, so nothing that holds
    /// the node sees a different object.
    /// </summary>
    private void Promote(LinkedListNode<Entry> node)
    {
        _order.Remove(node);
        _order.AddFirst(node);
    }

    private readonly record struct Entry(TKey Key, TValue Value);
}
