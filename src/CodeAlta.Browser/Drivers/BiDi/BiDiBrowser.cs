using System.Collections.Concurrent;
using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Launch;
using WebDriverBiDi;
using WebDriverBiDi.BrowsingContext;
using WebDriverBiDi.Session;
using BrowserModule = WebDriverBiDi.Browser;

namespace CodeAlta.Browser.Drivers.BiDi;

/// <summary>
/// A Firefox CodeAlta drives over WebDriver BiDi, through the <c>WebDriverBiDi</c> package, on the endpoint Firefox
/// opens itself: one it started, or the user's own it started on their profile. Events are subscribed for the tabs of
/// the sessions only, never for the other tabs of the user.
/// </summary>
internal sealed class BiDiBrowserConnection : IBrowserConnection
{
    private readonly BrowserProcess? _process;
    private readonly ConcurrentDictionary<BiDiBrowserContext, byte> _contexts = new();
    // The page each browsing context belongs to: a tab, or a frame of one.
    private readonly ConcurrentDictionary<string, BiDiPage> _owners = new(StringComparer.Ordinal);
    private int _disposed;
    private volatile bool _lost;

    private BiDiBrowserConnection(InstalledBrowser browser, BiDiDriver driver, BrowserProcess? process)
    {
        Browser = browser;
        Driver = driver;
        _process = process;
    }

    /// <inheritdoc />
    public InstalledBrowser Browser { get; }

    /// <summary>Gets the driver of the package.</summary>
    public BiDiDriver Driver { get; }

    /// <inheritdoc />
    public bool IsClosed => _lost || Volatile.Read(ref _disposed) != 0 || (_process?.HasExited ?? false);

    /// <summary>Starts Firefox on a profile of CodeAlta's and connects to it; the browser ends with the connection.</summary>
    public static async Task<BiDiBrowserConnection> LaunchAsync(InstalledBrowser browser, BrowserLaunchOptions options, CancellationToken cancellationToken, bool writePreferences = true)
    {
        var launch = await FirefoxLauncher.LaunchAsync(options, writePreferences, cancellationToken).ConfigureAwait(false);
        try
        {
            return await ConnectAsync(browser, launch.Endpoint, launch.Process, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await launch.Process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Connects to the BiDi endpoint of a Firefox and opens a session of the protocol on it.</summary>
    public static async Task<BiDiBrowserConnection> ConnectAsync(InstalledBrowser browser, Uri endpoint, BrowserProcess? process, CancellationToken cancellationToken)
    {
        var driver = new BiDiDriver(TimeSpan.FromSeconds(60));
        try
        {
            await driver.StartAsync(endpoint.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            // Dialogs stay open until a tool answers them, as with CDP.
            var session = await driver.Session.NewSessionAsync(new NewCommandParameters
            {
                // The proxy is asked as the default one, the system's: WebDriverBiDi 0.0.63 cannot read the empty proxy
                // Firefox answers with when none is asked.
                Capabilities = new CapabilitiesRequest
                {
                    AlwaysMatch = new CapabilityRequest { UnhandledPromptBehavior = new UserPromptHandler { Default = UserPromptHandlerType.Ignore }, Proxy = new SystemProxyConfiguration() },
                },
            }).ConfigureAwait(false);
            var version = session.Capabilities.BrowserVersion;
            var connection = new BiDiBrowserConnection(browser with { Version = string.IsNullOrEmpty(version) ? browser.Version : version }, driver, process);
            connection.Observe();
            // Only the tabs that open and close are told for every tab: what a tab does is subscribed per tab.
            await driver.Session.SubscribeAsync(new SubscribeCommandParameters([driver.BrowsingContext.OnContextCreated.EventName, driver.BrowsingContext.OnContextDestroyed.EventName])).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await driver.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Observe()
    {
        Driver.OnConnectionLost.AddObserver(_ => _lost = true);
        Driver.BrowsingContext.OnContextCreated.AddObserver(item =>
        {
            if (item.Parent is { } parent && _owners.TryGetValue(parent, out var owner)) _owners[item.BrowsingContextId] = owner;
            foreach (var context in _contexts.Keys) context.OnContextCreated(item.BrowsingContextId, item.Parent, item.OriginalOpener, item.UserContextId);
        });
        Driver.BrowsingContext.OnContextDestroyed.AddObserver(item =>
        {
            if (_owners.TryRemove(item.BrowsingContextId, out var page) && page.Id == item.BrowsingContextId) page.MarkClosed();
        });
        Driver.BrowsingContext.OnNavigationStarted.AddObserver(item => Owner(item.BrowsingContextId)?.OnNavigationStarted(item.BrowsingContextId, item.Url));
        Driver.BrowsingContext.OnLoad.AddObserver(item => Owner(item.BrowsingContextId)?.OnLoad(item.BrowsingContextId, item.Url));
        Driver.BrowsingContext.OnNavigationFailed.AddObserver(item => Owner(item.BrowsingContextId)?.OnLoad(item.BrowsingContextId, item.Url));
        Driver.BrowsingContext.OnNavigationAborted.AddObserver(item => Owner(item.BrowsingContextId)?.OnLoad(item.BrowsingContextId, item.Url));
        Driver.BrowsingContext.OnFragmentNavigated.AddObserver(item => Owner(item.BrowsingContextId)?.OnFragment(item.BrowsingContextId, item.Url));
        Driver.BrowsingContext.OnUserPromptOpened.AddObserver(item => Owner(item.BrowsingContextId)?.OnPromptOpened(item));
        Driver.BrowsingContext.OnUserPromptClosed.AddObserver(item => Owner(item.BrowsingContextId)?.OnPromptClosed());
        Driver.Log.OnEntryAdded.AddObserver(item => (item.Source.BrowsingContextId is { } id ? Owner(id) : null)?.OnLog(item));
        Driver.Network.OnBeforeRequestSent.AddObserver(item => (item.BrowsingContextId is { } id ? Owner(id) : null)?.OnRequest(item));
        Driver.Network.OnResponseCompleted.AddObserver(item => (item.BrowsingContextId is { } id ? Owner(id) : null)?.OnResponse(item));
        Driver.Network.OnFetchError.AddObserver(item => (item.BrowsingContextId is { } id ? Owner(id) : null)?.OnFetchError(item));
    }

    private BiDiPage? Owner(string contextId) => _owners.GetValueOrDefault(contextId);

    internal void Own(string contextId, BiDiPage page) => _owners[contextId] = page;

    private string[]? _supportedEvents;

    /// <summary>
    /// Subscribes the events of a tab and its frames: the ones this browser knows, found at the first tab, since a
    /// subscription that names an event the browser does not have fails whole.
    /// </summary>
    internal async Task SubscribeAsync(string contextId)
    {
        if (Volatile.Read(ref _supportedEvents) is { } known)
        {
            await Driver.Session.SubscribeAsync(new SubscribeCommandParameters(known, contexts: [contextId])).ConfigureAwait(false);
            return;
        }

        var supported = new List<string>();
        foreach (var name in PageEvents)
        {
            try
            {
                await Driver.Session.SubscribeAsync(new SubscribeCommandParameters([name], contexts: [contextId])).ConfigureAwait(false);
                supported.Add(name);
            }
            catch (WebDriverBiDiException)
            {
                // This browser does not send it.
            }
        }

        Volatile.Write(ref _supportedEvents, [.. supported]);
    }

    /// <summary>The events a tab subscribes, for itself and its frames.</summary>
    private string[] PageEvents =>
    [
        Driver.BrowsingContext.OnNavigationStarted.EventName, Driver.BrowsingContext.OnLoad.EventName, Driver.BrowsingContext.OnNavigationFailed.EventName,
        Driver.BrowsingContext.OnNavigationAborted.EventName, Driver.BrowsingContext.OnFragmentNavigated.EventName,
        Driver.BrowsingContext.OnUserPromptOpened.EventName, Driver.BrowsingContext.OnUserPromptClosed.EventName,
        Driver.Log.OnEntryAdded.EventName, Driver.Network.OnBeforeRequestSent.EventName, Driver.Network.OnResponseCompleted.EventName, Driver.Network.OnFetchError.EventName,
    ];

    /// <inheritdoc />
    public async Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        string userContext = "default";
        if (options.Isolated)
        {
            var created = await Driver.Browser.CreateUserContextAsync(new BrowserModule.CreateUserContextCommandParameters
            {
                Proxy = options.Proxy is { } proxy ? new ManualProxyConfiguration { HttpProxy = proxy, SslProxy = proxy } : null,
                UnhandledPromptBehavior = new UserPromptHandler { Default = UserPromptHandlerType.Ignore },
            }).ConfigureAwait(false);
            userContext = created.UserContextId;
            try
            {
                var download = new BrowserModule.SetDownloadBehaviorCommandParameters
                {
                    DownloadBehavior = options.DownloadDirectory is { } folder ? new BrowserModule.DownloadBehaviorAllowed(folder) : new BrowserModule.DownloadBehaviorDenied(),
                };
                download.UserContexts.Add(userContext);
                await Driver.Browser.SetDownloadBehaviorAsync(download).ConfigureAwait(false);
            }
            catch (WebDriverBiDiException)
            {
                // A Firefox without the command saves downloads where its profile says, under the temporary profile.
            }
        }
        else if (_process is not null)
        {
            // A profile of CodeAlta's in a Firefox it started: the browser's own setting, for every session of it. The user's
            // own Firefox keeps where it saves.
            try
            {
                await Driver.Browser.SetDownloadBehaviorAsync(new BrowserModule.SetDownloadBehaviorCommandParameters
                {
                    DownloadBehavior = options.DownloadDirectory is { } folder ? new BrowserModule.DownloadBehaviorAllowed(folder) : new BrowserModule.DownloadBehaviorDenied(),
                }).ConfigureAwait(false);
            }
            catch (WebDriverBiDiException)
            {
                // A Firefox without the command saves downloads where its profile says.
            }
        }

        var context = new BiDiBrowserContext(this, userContext, options);
        _contexts[context] = 0;
        return context;
    }

    internal void Forget(BiDiBrowserContext context) => _contexts.TryRemove(context, out _);

    /// <summary>Ends the browser CodeAlta started, or leaves the user's own as it is.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var context in _contexts.Keys) await context.DisposeAsync().ConfigureAwait(false);
        if (_process is not null && !_lost)
        {
            try { await Driver.Browser.CloseAsync(new BrowserModule.CloseCommandParameters()).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is WebDriverBiDiException or TimeoutException) { /* the process is ended below */ }
        }
        else if (!_lost)
        {
            try { await Driver.Session.EndAsync(new EndCommandParameters()).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is WebDriverBiDiException or TimeoutException) { /* the user's browser keeps going */ }
        }

        try { await Driver.DisposeAsync().ConfigureAwait(false); }
        catch (WebDriverBiDiException) { /* it closed first */ }
        if (_process is not null)
        {
            await _process.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await _process.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>The tabs of one session in a Firefox: a user context of its own, or windows of a shared profile.</summary>
internal sealed class BiDiBrowserContext : IBrowserContext
{
    private readonly BiDiBrowserConnection _browser;
    private readonly ConcurrentDictionary<string, BiDiPage> _pages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _adopting = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private int _disposed;

    internal BiDiBrowserContext(BiDiBrowserConnection browser, string userContext, BrowserContextOptions options)
    {
        _browser = browser;
        UserContext = userContext;
        Options = options;
    }

    /// <summary>Gets the user context: one of the session's own, or <c>default</c> in a shared profile.</summary>
    public string UserContext { get; }

    /// <summary>Gets how the tabs are opened.</summary>
    public BrowserContextOptions Options { get; }

    /// <summary>Gets the connection.</summary>
    public BiDiBrowserConnection Connection => _browser;

    /// <inheritdoc />
    public InstalledBrowser Browser => _browser.Browser;

    /// <inheritdoc />
    public BrowserCapabilities Capabilities { get; } = new(CpuThrottling: false, NetworkThrottling: false, Tracing: false, HeapSnapshots: false);

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
        var pages = Pages;
        var shared = UserContext == "default";
        var parameters = new CreateCommandParameters(shared && pages.Count == 0 ? CreateType.Window : CreateType.Tab)
        {
            IsCreatedInBackground = background,
            // A tab opens beside the tabs of the session, in their window, not in a window of the user.
            ReferenceContextId = pages.Count != 0 ? ((BiDiPage)pages[^1]).Id : null,
            UserContextId = shared ? null : UserContext,
        };
        var created = await _browser.Driver.BrowsingContext.CreateAsync(parameters).ConfigureAwait(false);
        return await AdoptAsync(created.BrowsingContextId, raise: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BiDiPage> AdoptAsync(string contextId, bool raise, CancellationToken cancellationToken)
    {
        if (_pages.TryGetValue(contextId, out var known)) return known;
        var page = new BiDiPage(this, contextId);
        if (!_pages.TryAdd(contextId, page)) return _pages[contextId];
        _browser.Own(contextId, page);
        lock (_order) _order.Add(contextId);
        await page.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (raise) PageOpened?.Invoke(page);
        return page;
    }

    internal void OnContextCreated(string contextId, string? parent, string? opener, string userContext)
    {
        if (parent is not null || IsClosed || _pages.ContainsKey(contextId)) return;
        // A tab of the session's own user context, or a popup of one of its tabs: never a tab of the user.
        var ours = (UserContext != "default" && userContext == UserContext) || (opener is not null && _pages.ContainsKey(opener));
        if (!ours || !_adopting.TryAdd(contextId, 0)) return;
        _ = AdoptLaterAsync(contextId);
    }

    private async Task AdoptLaterAsync(string contextId)
    {
        // A tab created by NewPageAsync is adopted there; one the page opened is adopted here, and reported.
        await Task.Delay(100).ConfigureAwait(false);
        try
        {
            if (!_pages.ContainsKey(contextId)) await AdoptAsync(contextId, raise: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebDriverBiDiException)
        {
            // It closed at once.
        }
        finally
        {
            _adopting.TryRemove(contextId, out _);
        }
    }

    /// <summary>Closes the tabs of the session and its user context; the tabs of the user are left as they are.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _browser.Forget(this);
        foreach (var page in _pages.Values)
        {
            if (page.IsClosed || _browser.IsClosed) continue;
            try { await page.CloseAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (WebDriverBiDiException) { /* gone */ }
        }

        if (UserContext != "default" && !_browser.IsClosed)
        {
            try { await _browser.Driver.Browser.RemoveUserContextAsync(new BrowserModule.RemoveUserContextCommandParameters(UserContext)).ConfigureAwait(false); }
            catch (WebDriverBiDiException) { /* it ended with the browser */ }
        }
    }
}
