namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// The files of skills CodeAlta read and checked, kept in memory for the process, by server, URI and digest, within a
/// budget of bytes: the least recently used leave first. A file is served from it only for the digest it was checked
/// with. Nothing is written to disk: a copy on disk would have to be checked again at each read.
/// </summary>
internal sealed class McpSkillContentCache
{
    /// <summary>The budget of the cache of a plugin: 64 MB.</summary>
    public const long DefaultMaximumBytes = 64L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Server, string Uri, string Digest), LinkedListNode<Item>> _items = [];
    private readonly LinkedList<Item> _order = new();
    private readonly long _maximumBytes;
    private long _bytes;

    public McpSkillContentCache(long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _maximumBytes = maximumBytes;
    }

    /// <summary>Gets the number of bytes kept.</summary>
    public long Bytes { get { lock (_gate) return _bytes; } }

    /// <summary>Gets the checked bytes of a file of a server for a digest.</summary>
    public bool TryGet(string server, string uri, string digest, out byte[] bytes)
    {
        lock (_gate)
        {
            if (_items.TryGetValue((server, uri, digest), out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                bytes = node.Value.Bytes;
                return true;
            }
        }

        bytes = [];
        return false;
    }

    /// <summary>Keeps the checked bytes of a file; a file larger than the budget is not kept.</summary>
    public void Add(string server, string uri, string digest, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > _maximumBytes) return;
        lock (_gate)
        {
            var key = (server, uri, digest);
            if (_items.ContainsKey(key)) return;
            var node = _order.AddFirst(new Item(key, bytes));
            _items.Add(key, node);
            _bytes += bytes.Length;
            while (_bytes > _maximumBytes && _order.Last is { } last)
            {
                _order.RemoveLast();
                _items.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes.Length;
            }
        }
    }

    private sealed record Item((string Server, string Uri, string Digest) Key, byte[] Bytes);
}
