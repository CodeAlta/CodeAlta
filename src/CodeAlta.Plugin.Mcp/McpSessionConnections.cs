namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// Keeps the MCP servers a session calls connected between its tool calls.
/// </summary>
/// <remarks>
/// A server can hold state from one call to the next (a browser snapshot whose element ids the next call
/// uses), and starting it for every call costs seconds. Each session gets its own connections, used by one
/// call at a time. They are closed when a call could not be completed (the next one starts afresh), after
/// they stayed unused for <see cref="DefaultIdleLifetime"/>, and with the plugin.
/// </remarks>
internal sealed class McpSessionConnections : IAsyncDisposable
{
    /// <summary>How long the connections of a session stay open without a call.</summary>
    internal static readonly TimeSpan DefaultIdleLifetime = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _idleLifetime;
    private readonly TimeProvider _time;
    private ITimer? _sweeper;
    private bool _disposed;

    public McpSessionConnections()
        : this(DefaultIdleLifetime, TimeProvider.System)
    {
    }

    internal McpSessionConnections(TimeSpan idleLifetime, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _idleLifetime = idleLifetime;
        _time = time;
    }

    /// <summary>The number of sessions that hold connections.</summary>
    internal int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Calls a tool on the connections of a session, opening them at its first call.</summary>
    /// <exception cref="ObjectDisposedException">The connections were closed with the plugin.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled.</exception>
    public async Task<McpRuntimeToolCallResult?> CallToolAsync(
        string sessionId,
        McpRuntimeRequest request,
        string server,
        string tool,
        IReadOnlyDictionary<string, object?> arguments,
        IList<McpRuntimeDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        var key = string.Join('\n', sessionId, request.ProjectDirectory, request.UserHomeDirectory);
        while (true)
        {
            Entry entry;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_entries.TryGetValue(key, out entry!))
                {
                    _entries[key] = entry = new Entry(new McpRuntimeService(request.UserHomeDirectory));
                    _sweeper ??= _time.CreateTimer(static state => _ = ((McpSessionConnections)state!).CloseIdleAsync(), this,
                        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
                }
            }

            await entry.Calls.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Closed while this call waited for its turn: the session starts with new connections.
                if (entry.Closed) continue;
                var result = await entry.Runtime.CallToolAsync(request, server, tool, arguments, diagnostics, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    // The server did not start, the call timed out or the connection broke: what is cached
                    // is no longer trusted. The next call connects again.
                    await entry.Runtime.DisposeAsync().ConfigureAwait(false);
                    entry.Runtime = new McpRuntimeService(request.UserHomeDirectory);
                }

                return result;
            }
            finally
            {
                // Also after a canceled call: the idle time counts from the end of the last call.
                entry.LastUsed = _time.GetUtcNow();
                entry.Calls.Release();
            }
        }
    }

    /// <summary>Closes the connections that no call used for the idle lifetime.</summary>
    internal async Task CloseIdleAsync()
    {
        List<Entry>? idle = null;
        lock (_gate)
        {
            if (_disposed) return;
            var now = _time.GetUtcNow();
            foreach (var (key, entry) in _entries)
            {
                // A session in a call is not idle, however long its previous call was ago.
                if (now - entry.LastUsed < _idleLifetime || !entry.Calls.Wait(0)) continue;
                entry.Closed = true;
                entry.Calls.Release();
                _entries.Remove(key);
                (idle ??= []).Add(entry);
            }

            if (_entries.Count == 0)
            {
                _sweeper?.Dispose();
                _sweeper = null;
            }
        }

        if (idle is null) return;
        foreach (var entry in idle) await CloseAsync(entry).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _sweeper?.Dispose();
            _sweeper = null;
            entries = [.. _entries.Values];
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            // A call in progress ends first: its server is not closed under it.
            await entry.Calls.WaitAsync().ConfigureAwait(false);
            entry.Closed = true;
            entry.Calls.Release();
            await CloseAsync(entry).ConfigureAwait(false);
        }
    }

    private static async Task CloseAsync(Entry entry)
    {
        try { await entry.Runtime.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            // A server that does not close cleanly is closed all the same: there is nobody to tell.
        }
    }

    private sealed class Entry(McpRuntimeService runtime)
    {
        public SemaphoreSlim Calls { get; } = new(1, 1);
        public McpRuntimeService Runtime { get; set; } = runtime;
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.MaxValue;
        public bool Closed { get; set; }
    }
}
