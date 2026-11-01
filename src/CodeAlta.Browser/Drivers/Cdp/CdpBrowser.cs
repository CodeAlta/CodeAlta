using System.Collections.Concurrent;
using CodeAlta.Browser.Cdp;
using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Launch;

namespace CodeAlta.Browser.Drivers.Cdp;

/// <summary>
/// A Chromium browser CodeAlta drives over the DevTools Protocol: one it started on a profile of its own, or the
/// user's own browser it attached to. Only the tabs CodeAlta opens, and the popups they open, are attached.
/// </summary>
internal sealed class CdpBrowserConnection : IBrowserConnection
{
    private readonly BrowserProcess? _process;
    private readonly ConcurrentDictionary<CdpBrowserContext, byte> _contexts = new();
    private int _disposed;

    private CdpBrowserConnection(InstalledBrowser browser, CdpConnection connection, BrowserProcess? process, string userAgent)
    {
        Browser = browser;
        Connection = connection;
        _process = process;
        UserAgent = userAgent;
    }

    /// <inheritdoc />
    public InstalledBrowser Browser { get; }

    /// <summary>Gets the connection.</summary>
    public CdpConnection Connection { get; }

    /// <summary>Gets the user agent the browser sends by itself.</summary>
    public string UserAgent { get; }

    /// <summary>Gets whether CodeAlta started the browser, and ends it with the connection.</summary>
    public bool OwnsBrowser => _process is not null;

    /// <inheritdoc />
    public bool IsClosed => Connection.IsClosed || (_process?.HasExited ?? false);

    /// <summary>Connects to a browser's endpoint; with a process, the browser is CodeAlta's and ends with the connection.</summary>
    public static async Task<CdpBrowserConnection> ConnectAsync(InstalledBrowser browser, Uri endpoint, BrowserProcess? process, CancellationToken cancellationToken)
    {
        var connection = await CdpConnection.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        try
        {
            var version = await connection.Browser.Browser.GetVersionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var result = new CdpBrowserConnection(browser with { Version = version.Product.Contains('/') ? version.Product[(version.Product.IndexOf('/') + 1)..] : browser.Version }, connection, process, version.UserAgent);
            result.Subscribe();
            // The targets the browser creates are told, without attaching: a popup of a tab of a session is attached then.
            await connection.Browser.Target.SetDiscoverTargetsAsync(new TargetSetDiscoverTargetsParams { Discover = true }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Subscribe()
    {
        Connection.Browser.Target.OnTargetCreated(item =>
        {
            foreach (var context in _contexts.Keys) context.OnTargetCreated(item.TargetInfo);
        });
        Connection.Browser.Target.OnTargetInfoChanged(item =>
        {
            foreach (var context in _contexts.Keys) context.OnTargetInfoChanged(item.TargetInfo);
        });
        Connection.Browser.Target.OnTargetDestroyed(item =>
        {
            foreach (var context in _contexts.Keys) context.OnTargetDestroyed(item.TargetId);
        });
        Connection.Browser.Target.OnTargetCrashed(item =>
        {
            foreach (var context in _contexts.Keys) context.OnTargetDestroyed(item.TargetId);
        });
    }

    /// <inheritdoc />
    public async Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? contextId = null;
        if (options.Isolated)
        {
            var created = await Connection.Browser.Target.CreateBrowserContextAsync(new TargetCreateBrowserContextParams
            {
                DisposeOnDetach = true,
                ProxyServer = options.Proxy is { } proxy ? $"http://{proxy}" : null,
                ProxyBypassList = options.Proxy is null ? null : "<-loopback>",
            }, cancellationToken).ConfigureAwait(false);
            contextId = created.BrowserContextId;
        }

        // Without a context of its own, the setting would be the whole browser's: the user's own browser keeps where it
        // saves its downloads.
        if (options.Isolated || OwnsBrowser)
        {
            try
            {
                await Connection.Browser.Browser.SetDownloadBehaviorAsync(new BrowserSetDownloadBehaviorParams
                {
                    Behavior = options.DownloadDirectory is null ? "deny" : "allow",
                    DownloadPath = options.DownloadDirectory,
                    BrowserContextId = contextId,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (CdpException) when (!options.Isolated)
            {
                // A browser that refuses it saves downloads where its profile says.
            }
        }

        var context = new CdpBrowserContext(this, contextId, options);
        _contexts[context] = 0;
        return context;
    }

    internal void Forget(CdpBrowserContext context) => _contexts.TryRemove(context, out _);

    /// <summary>Ends the browser CodeAlta started, or leaves the user's own as it is.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var context in _contexts.Keys) await context.DisposeAsync().ConfigureAwait(false);
        if (_process is not null && !Connection.IsClosed)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await Connection.Browser.Browser.CloseAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is CdpException or OperationCanceledException)
            {
                // It goes when its process is ended below.
            }
        }

        await Connection.DisposeAsync().ConfigureAwait(false);
        if (_process is not null)
        {
            await _process.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await _process.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The tabs of one session in a Chromium browser: a browser context of its own (a temporary profile's), or windows of
/// a shared profile (the persistent one, or the user's own), where only the tabs of the session are attached.
/// </summary>
internal sealed class CdpBrowserContext : IBrowserContext
{
    private readonly CdpBrowserConnection _browser;
    private readonly string? _contextId;
    private readonly ConcurrentDictionary<string, CdpPage> _pages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _attaching = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private int _disposed;

    internal CdpBrowserContext(CdpBrowserConnection browser, string? contextId, BrowserContextOptions options)
    {
        _browser = browser;
        _contextId = contextId;
        Options = options;
    }

    /// <summary>Gets how the tabs are opened.</summary>
    public BrowserContextOptions Options { get; }

    /// <summary>Gets the connection.</summary>
    public CdpConnection Connection => _browser.Connection;

    /// <summary>Gets the user agent of the browser.</summary>
    public string DefaultUserAgent => _browser.UserAgent;

    /// <inheritdoc />
    public InstalledBrowser Browser => _browser.Browser;

    /// <inheritdoc />
    public BrowserCapabilities Capabilities { get; } = new(CpuThrottling: true, NetworkThrottling: true, Tracing: true, HeapSnapshots: true);

    /// <inheritdoc />
    public IReadOnlyList<IBrowserPage> Pages
    {
        get
        {
            lock (_order) return [.. _order.Select(id => _pages.GetValueOrDefault(id)).Where(static page => page is { IsClosed: false })!];
        }
    }

    /// <inheritdoc />
    public bool IsClosed => Volatile.Read(ref _disposed) != 0 || _browser.IsClosed;

    /// <inheritdoc />
    public event Action<IBrowserPage>? PageOpened;

    /// <inheritdoc />
    public async Task<IBrowserPage> NewPageAsync(bool background, CancellationToken cancellationToken)
    {
        var first = Pages.Count == 0;
        string targetId;
        if (_contextId is null && !first && Pages[^1] is CdpPage opener)
        {
            // In a shared profile, a tab opens beside the tabs of the session, not in a window of the user: from one
            // of them, as a link with target=_blank does.
            targetId = await OpenBesideAsync(opener, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var created = await Connection.Browser.Target.CreateTargetAsync(new TargetCreateTargetParams
            {
                Url = "about:blank",
                BrowserContextId = _contextId,
                Background = background,
                // The first tab of a session in a shared profile gets a window of its own.
                NewWindow = _contextId is null && first ? true : null,
            }, cancellationToken).ConfigureAwait(false);
            targetId = created.TargetId;
        }

        return await AdoptAsync(targetId, raise: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> OpenBesideAsync(CdpPage opener, CancellationToken cancellationToken)
    {
        var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(TargetTargetInfo info)
        {
            if (info.Type == "page" && info.OpenerId == opener.TargetId) created.TrySetResult(info.TargetId);
        }

        lock (_watchers) _watchers.Add(Watch);
        try
        {
            await opener.EvaluateAsync("window.open('about:blank', '_blank')", isFunction: false, [], cancellationToken).ConfigureAwait(false);
            return await created.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_watchers) _watchers.Remove(Watch);
        }
    }

    // What waits for a tab to open: a tab that a tab of the session opens beside it.
    private readonly List<Action<TargetTargetInfo>> _watchers = [];

    private async Task<CdpPage> AdoptAsync(string targetId, bool raise, CancellationToken cancellationToken)
    {
        if (_pages.TryGetValue(targetId, out var known)) return known;
        var page = await CdpPage.AttachAsync(this, targetId, cancellationToken).ConfigureAwait(false);
        if (!_pages.TryAdd(targetId, page))
        {
            page.DisposeSubscriptions();
            return _pages[targetId];
        }

        lock (_order) _order.Add(targetId);
        if (raise) PageOpened?.Invoke(page);
        return page;
    }

    internal void OnTargetCreated(TargetTargetInfo info)
    {
        if (info.Type != "page" || IsClosed) return;
        Action<TargetTargetInfo>[] watchers;
        lock (_watchers) watchers = [.. _watchers];
        foreach (var watcher in watchers) watcher(info);
        // A tab of the session's own context, or a popup of one of its tabs: never a tab of the user.
        var ours = (_contextId is not null && info.BrowserContextId == _contextId) || (info.OpenerId is { } opener && _pages.ContainsKey(opener));
        if (!ours || _pages.ContainsKey(info.TargetId) || !_attaching.TryAdd(info.TargetId, 0)) return;
        _ = AdoptLaterAsync(info.TargetId);
    }

    private async Task AdoptLaterAsync(string targetId)
    {
        // A tab created by NewPageAsync is adopted there; one the page opened is adopted here, and reported.
        await Task.Delay(100).ConfigureAwait(false);
        try
        {
            if (!_pages.ContainsKey(targetId)) await AdoptAsync(targetId, raise: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (CdpException)
        {
            // It closed at once.
        }
        finally
        {
            _attaching.TryRemove(targetId, out _);
        }
    }

    internal void OnTargetInfoChanged(TargetTargetInfo info)
    {
        if (_pages.TryGetValue(info.TargetId, out var page)) page.UpdateInfo(info);
    }

    internal void OnTargetDestroyed(string targetId)
    {
        if (_pages.TryGetValue(targetId, out var page)) page.MarkClosed();
    }

    /// <summary>Closes the tabs of the session and its context; the tabs of the user are left as they are.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _browser.Forget(this);
        foreach (var page in _pages.Values)
        {
            page.DisposeSubscriptions();
            if (!page.IsClosed && !Connection.IsClosed)
            {
                try { await page.CloseAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (CdpException) { /* gone */ }
            }
        }

        if (_contextId is not null && !Connection.IsClosed)
        {
            try { await Connection.Browser.Target.DisposeBrowserContextAsync(new TargetDisposeBrowserContextParams { BrowserContextId = _contextId }).ConfigureAwait(false); }
            catch (CdpException) { /* it ended with the browser */ }
        }
    }
}
