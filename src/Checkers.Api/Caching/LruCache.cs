namespace Checkers.Api.Caching;

/// <summary>
/// A thread-safe least-recently-used cache whose entries also expire a fixed time after they
/// were written. A read refreshes recency but not the expiry, so an answer is never served
/// longer than the TTL. A capacity of 0 disables caching.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _index;
    private readonly LinkedList<Entry> _recency = new(); // most recently used first
    private readonly Lock _lock = new();

    public LruCache(int capacity, TimeSpan ttl, TimeProvider time, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
        _ttl = ttl;
        _time = time;
        _index = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    /// <summary>Live entries. Drops expired ones first, which is O(n); meant for /healthz, not hot paths.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                var now = _time.GetUtcNow();
                for (var node = _recency.First; node is not null;)
                {
                    var next = node.Next;
                    if (node.Value.ExpiresAt <= now)
                    {
                        Remove(node);
                    }

                    node = next;
                }

                return _index.Count;
            }
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var node))
            {
                if (node.Value.ExpiresAt > _time.GetUtcNow())
                {
                    _recency.Remove(node);
                    _recency.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }

                Remove(node);
            }

            value = default!;
            return false;
        }
    }

    public void Set(TKey key, TValue value)
    {
        if (_capacity == 0 || _ttl <= TimeSpan.Zero)
        {
            return;
        }

        var entry = new Entry(key, value, _time.GetUtcNow() + _ttl);
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                Remove(existing);
            }

            _index[key] = _recency.AddFirst(entry);
            while (_index.Count > _capacity)
            {
                Remove(_recency.Last!);
            }
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _recency.Remove(node);
        _index.Remove(node.Value.Key);
    }

    private sealed record Entry(TKey Key, TValue Value, DateTimeOffset ExpiresAt);
}
