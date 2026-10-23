namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// Keeps the MCP servers a session uses connected between its tool calls, from the listing of their tools at the start
/// of its runs to its last call.
/// </summary>
/// <remarks>
/// A server can hold state from one call to the next (a browser snapshot whose element ids the next call uses), and
/// starting it for every call, or for every run that lists its tools, costs seconds. Each session gets its own
/// connections, used by one call or listing at a time. They are closed when a call could not be completed (the next one
/// starts afresh), after they stayed unused for <see cref="DefaultIdleLifetime"/>, and with the plugin. A listing keeps
/// the connections that answer, and tries again at the next use the servers that could not be started.
/// </remarks>
internal sealed class McpSessionConnections : IAsyncDisposable
{
    /// <summary>How long the connections of a session stay open without a call or a listing.</summary>
    internal static readonly TimeSpan DefaultIdleLifetime = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _idleLifetime;
    private readonly TimeProvider _time;
    private ITimer? _sweeper;
    private bool _disposed;

    public McpSessionConnections()
        : this(configCache: null)
    {
    }

    public McpSessionConnections(McpConfigCache? configCache)
        : this(DefaultIdleLifetime, TimeProvider.System, configCache)
    {
    }

    public McpSessionConnections(McpConfigCache? configCache, bool advertiseApps)
        : this(DefaultIdleLifetime, TimeProvider.System, configCache, advertiseApps)
    {
    }

    internal McpSessionConnections(TimeSpan idleLifetime, TimeProvider time, McpConfigCache? configCache = null, bool advertiseApps = false)
    {
        ArgumentNullException.ThrowIfNull(time);
        _idleLifetime = idleLifetime;
        _time = time;
        ConfigCache = configCache;
        AdvertiseApps = advertiseApps;
    }

    /// <summary>The cache of configuration the runtimes of the sessions read through, or null.</summary>
    internal McpConfigCache? ConfigCache { get; }

    /// <summary>Whether the servers are told that the host shows the views of MCP Apps.</summary>
    internal bool AdvertiseApps { get; }

    /// <summary>Creates a runtime as the ones of the sessions: same configuration cache, same capabilities.</summary>
    internal McpRuntimeService CreateRuntime(string? userHomeDirectory) => new(userHomeDirectory, ConfigCache, AdvertiseApps);

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
        return await UseAsync(sessionId, request, async entry =>
        {
            var result = await entry.Runtime.CallToolAsync(request, server, tool, arguments, diagnostics, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                // The server did not start, the call timed out or the connection broke: what is cached
                // is no longer trusted. The next call connects again.
                await entry.Runtime.DisposeAsync().ConfigureAwait(false);
                entry.Runtime = CreateRuntime(request.UserHomeDirectory);
            }

            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a request of the view of a tool call (MCP Apps) on the connections of its session: the server the model called
    /// is the one the view talks to, with its state. A refusal leaves the connections open; a call or a read that broke
    /// closes them, as a broken call of the model does.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The connections were closed with the plugin.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled.</exception>
    public async Task<T?> UseForViewAsync<T>(
        string sessionId,
        McpRuntimeRequest request,
        Func<McpRuntimeService, IList<McpRuntimeDiagnostic>, Task<T?>> use,
        IList<McpRuntimeDiagnostic> diagnostics,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(use);
        ArgumentNullException.ThrowIfNull(diagnostics);
        return await UseAsync(sessionId, request, async entry =>
        {
            var result = await use(entry.Runtime, diagnostics).ConfigureAwait(false);
            if (result is null && diagnostics.Any(static diagnostic => diagnostic.Code is "tool_timeout" or "tool_call_failed" or "resource_timeout" or "resource_read_failed"))
            {
                await entry.Runtime.DisposeAsync().ConfigureAwait(false);
                entry.Runtime = CreateRuntime(request.UserHomeDirectory);
            }
            else
            {
                entry.Runtime.ForgetFailedServers();
            }

            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the tools of servers on the connections of a session, as the start of its run does: a server started for
    /// the listing stays connected for the calls that follow, and a connection already open answers without a new start.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The connections were closed with the plugin.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled.</exception>
    public async Task<McpRuntimeToolSearchResult> ListToolsForServersAsync(
        string sessionId,
        McpRuntimeRequest request,
        IReadOnlyCollection<string> serverKeys,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(serverKeys);
        return await UseAsync(sessionId, request, async entry =>
        {
            try
            {
                return await entry.Runtime.ListToolsForServersAsync(request, serverKeys, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // A server that could not be started is tried again by the next call or listing, as a new start would be.
                entry.Runtime.ForgetFailedServers();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    // Runs one use of the connections of a session: a call or a listing, one at a time.
    private async Task<T> UseAsync<T>(string sessionId, McpRuntimeRequest request, Func<Entry, Task<T>> use, CancellationToken cancellationToken)
    {
        // A session attached again with another identity has other servers: the ones of its last identity close when idle.
        var key = string.Join('\n', sessionId, request.ProjectDirectory, request.UserHomeDirectory, McpRuntimeService.EnvironmentKey(request.Environment));
        while (true)
        {
            Entry entry;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_entries.TryGetValue(key, out entry!))
                {
                    _entries[key] = entry = new Entry(CreateRuntime(request.UserHomeDirectory));
                    _sweeper ??= _time.CreateTimer(static state => _ = ((McpSessionConnections)state!).CloseIdleAsync(), this,
                        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
                }
            }

            await entry.Calls.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Closed while this use waited for its turn: the session starts with new connections.
                if (entry.Closed) continue;
                return await use(entry).ConfigureAwait(false);
            }
            finally
            {
                // Also after a canceled use: the idle time counts from the end of the last one.
                entry.LastUsed = _time.GetUtcNow();
                entry.Calls.Release();
            }
        }
    }

    /// <summary>Closes the connections that no call or listing used for the idle lifetime.</summary>
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
