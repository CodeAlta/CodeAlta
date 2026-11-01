using System.Net;
using CodeAlta.Agent;
using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Plugin.Browser;

/// <summary>
/// The browser of one session that turned the tools on: the browser and profile it chose, its tabs once they are open,
/// the sites the user allowed it in their own browser, and the guards of what its tabs load.
/// </summary>
/// <remarks>
/// The tabs close after a while without a call, and open again at the next one; the choice stays until the session
/// gives the tools back or the application exits.
/// </remarks>
internal sealed class BrowserAgentSession : IAsyncDisposable
{
    private readonly BrowserPluginHost _host;
    private readonly BrowserPool _pool;
    private readonly Func<BrowserSettings> _settings;
    private readonly Func<string, CancellationToken, ValueTask> _allowAlways;
    private readonly string? _fallbackFolder;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _allowedSites = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deniedSites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string?>> _asking = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private BrowserSession? _session;
    private BrowserSandboxProxy? _proxy;
    private int _calls;
    private int _disposed;

    /// <summary>Creates the browser of a session.</summary>
    /// <param name="host">What the application gives the plugin.</param>
    /// <param name="pool">The browsers the sessions share.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="target">The browser and profile.</param>
    /// <param name="source">Who chose the browser.</param>
    /// <param name="reason">Why it was chosen.</param>
    /// <param name="sandbox">The sandbox of the session, or null.</param>
    /// <param name="settings">The settings now.</param>
    /// <param name="allowAlways">Keeps a site the user allowed from now on in the settings.</param>
    /// <param name="fallbackFolder">The folder of the project, used when the folder of the session is not known.</param>
    public BrowserAgentSession(BrowserPluginHost host, BrowserPool pool, string sessionId, BrowserTarget target, BrowserChoiceSource source, string reason,
        BrowserSandbox? sandbox, Func<BrowserSettings> settings, Func<string, CancellationToken, ValueTask> allowAlways, string? fallbackFolder)
    {
        _host = host;
        _pool = pool;
        SessionId = sessionId;
        Target = target;
        Source = source;
        Reason = reason;
        Sandbox = sandbox;
        _settings = settings;
        _allowAlways = allowAlways;
        _fallbackFolder = fallbackFolder;
        AllowScriptEvaluation = settings().EvaluateScript;
    }

    /// <summary>Gets the session.</summary>
    public string SessionId { get; }

    /// <summary>Gets the browser and profile.</summary>
    public BrowserTarget Target { get; }

    /// <summary>Gets who chose the browser.</summary>
    public BrowserChoiceSource Source { get; }

    /// <summary>Gets why the browser was chosen.</summary>
    public string Reason { get; }

    /// <summary>Gets the sandbox of the session when it turned the tools on, or null.</summary>
    public BrowserSandbox? Sandbox { get; }

    /// <summary>Gets whether the session has <c>evaluate_script</c>.</summary>
    public bool AllowScriptEvaluation { get; }

    /// <summary>Gets whether the session browses in the user's own browser.</summary>
    public bool IsUserProfile => Target.Profile.Kind == BrowserProfileKind.User;

    /// <summary>Gets the number of open tabs of the session; 0 while its browser is closed.</summary>
    public int PageCount => Volatile.Read(ref _session)?.PageCount ?? 0;

    /// <summary>Gets whether the tabs of the session are open.</summary>
    public bool IsOpen => Volatile.Read(ref _session)?.Browser is not null;

    /// <summary>Gets the sites the user allowed for this session in their own browser.</summary>
    public IReadOnlyList<string> AllowedSites
    {
        get
        {
            lock (_gate) return [.. _allowedSites.Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>The tools of the session's browser, as their definitions; the same for every session of an engine.</summary>
    public IReadOnlyList<BrowserToolDefinition> Tools => BrowserSession.ToolsFor(BrowserTools.Create(AllowScriptEvaluation), Target.Browser.Engine);

    /// <summary>Runs a tool in the tabs of the session, opening its browser when it is closed.</summary>
    /// <exception cref="OperationCanceledException">The call was canceled.</exception>
    public async Task<BrowserToolResult> CallAsync(string tool, System.Text.Json.JsonElement arguments, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Interlocked.Increment(ref _calls);
        try
        {
            return await Session().CallAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _calls);
        }
    }

    private BrowserSession Session()
    {
        lock (_gate)
        {
            return _session ??= new BrowserSession(new BrowserSessionOptions
            {
                OpenContext = OpenContextAsync,
                Files = FilesAsync,
                CheckNavigation = (url, token) => CheckAsync(url, BrowserRequestKind.Page, asking: true, token),
                AllowScriptEvaluation = AllowScriptEvaluation,
            });
        }
    }

    /// <summary>Closes the tabs of the session when no tool was called for a while; the next call opens them again.</summary>
    /// <returns>Whether they were closed.</returns>
    public async Task<bool> CloseIfIdleAsync(TimeSpan idle)
    {
        BrowserSession? session;
        lock (_gate)
        {
            session = _session;
            if (session is null || Volatile.Read(ref _calls) != 0 || DateTimeOffset.UtcNow - session.LastUsed < idle) return false;
            _session = null;
        }

        await CloseAsync(session).ConfigureAwait(false);
        return true;
    }

    /// <summary>Closes the tabs of the session now; the next call opens them again.</summary>
    /// <returns>Whether they were open.</returns>
    public async Task<bool> CloseTabsAsync()
    {
        BrowserSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        if (session is null) return false;
        var open = session.Browser is not null;
        await CloseAsync(session).ConfigureAwait(false);
        return open;
    }

    private async Task CloseAsync(BrowserSession session)
    {
        await session.DisposeAsync().ConfigureAwait(false);
        BrowserSandboxProxy? proxy;
        lock (_gate)
        {
            proxy = _session is null ? _proxy : null;
            if (proxy is not null) _proxy = null;
        }

        if (proxy is not null) await proxy.Owner.DisposeAsync().ConfigureAwait(false);
        DeleteIfEmpty(DownloadFolder);
    }

    // The folder the downloads of the session go to: kept once something was downloaded there.
    private string DownloadFolder => Path.Combine(_host.Root, "downloads", SafeName(SessionId));

    private static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A download is being written, or the folder is in use: it stays.
        }
    }

    private async Task<IBrowserContext> OpenContextAsync(CancellationToken cancellationToken)
    {
        string? proxy = null;
        // A sandboxed session's tabs reach the network of its profile only, through a proxy of their own.
        if (Sandbox?.StartProxy is { } start)
        {
            lock (_gate) proxy = _proxy?.Address;
            if (proxy is null)
            {
                var started = await start(cancellationToken).ConfigureAwait(false);
                BrowserSandboxProxy? extra = null;
                lock (_gate)
                {
                    if (_proxy is null) _proxy = started;
                    else extra = started;
                    proxy = _proxy.Address;
                }

                if (extra is not null) await extra.Owner.DisposeAsync().ConfigureAwait(false);
            }
        }

        var options = new BrowserContextOptions
        {
            Isolated = Target.Profile.Kind == BrowserProfileKind.Isolated,
            Proxy = proxy,
            // A sandboxed session saves nothing outside its sandbox; the others save in a folder of their own, and nothing
            // is ever opened.
            DownloadDirectory = Sandbox is null && !IsUserProfile ? Directory.CreateDirectory(DownloadFolder).FullName : null,
            Gate = (url, kind, token) => CheckAsync(url, kind, asking: false, token),
            GateDocuments = IsUserProfile,
            BlockedPorts = ReservedPorts(),
        };
        return await _pool.OpenContextAsync(Target, options, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<int> ReservedPorts() => _host.ReservedPorts?.Invoke() ?? [];

    /// <summary>Where the tools of the session read and write files.</summary>
    public async ValueTask<BrowserFiles> FilesAsync(CancellationToken cancellationToken)
    {
        string? folder = null;
        if (_host.WorkFolder is { } resolve)
        {
            try
            {
                folder = await resolve(SessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The session is not known any more: it works in the folder of its project.
            }
        }

        folder = string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder) ? _fallbackFolder : folder;
        var files = Path.Combine(_host.Root, "files", SafeName(SessionId));
        if (Sandbox is { } sandbox)
        {
            var writable = sandbox.WriteRoots.Count != 0 ? sandbox.WriteRoots : folder is null ? [files] : [folder];
            return new BrowserFiles(folder ?? writable[0], [.. sandbox.ReadRoots, .. writable], writable, writable[0])
            {
                Denied = sandbox.Denied,
                ReadOnly = sandbox.ReadOnly,
            };
        }

        var shared = Path.Combine(_host.Root, "files");
        return folder is null
            ? new BrowserFiles(shared, [shared], [shared], files)
            : new BrowserFiles(folder, [folder, shared], [folder, shared], files);
    }

    /// <summary>
    /// Decides whether the session may load an address: a <c>file:</c> address inside its folders only, never a port of
    /// CodeAlta, and in the user's own browser only a site the user allowed.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <param name="kind">What it loads.</param>
    /// <param name="asking">Whether the user may be asked for a site: a tool of the session asks to load it.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>Null when it may, else why not.</returns>
    public async ValueTask<string?> CheckAsync(Uri url, BrowserRequestKind kind, bool asking, CancellationToken cancellationToken)
    {
        if (IsLoopback(url) && url.Port is var port && ReservedPorts().Contains(port))
        {
            return $"{url.GetLeftPart(UriPartial.Authority)} is a port of CodeAlta itself: a page of the browser never reaches it.";
        }

        if (kind == BrowserRequestKind.Resource) return null;
        if (url.Scheme == Uri.UriSchemeFile)
        {
            var files = await FilesAsync(cancellationToken).ConfigureAwait(false);
            return files.CanRead(url.LocalPath)
                ? null
                : $"The file of {url} is outside the folders of this session ({string.Join(", ", files.ReadRoots)}), or looks like it holds secrets: it is not loaded.";
        }

        if (kind == BrowserRequestKind.Page && url.Scheme is not ("http" or "https" or "about"))
        {
            return $"A {url.Scheme}: address is not loaded: the tools load http, https and file addresses.";
        }

        if (!IsUserProfile || url.Scheme == "about") return null;
        if (BrowserSites.Of(url) is not { } site) return $"{url} has no site the user could allow: it is not loaded in the user's browser.";
        return await SiteRefusalAsync(site, kind == BrowserRequestKind.Page && (asking || Volatile.Read(ref _calls) != 0), cancellationToken).ConfigureAwait(false);
    }

    // A site of the user's browser: allowed in the settings or for this session, else asked once while a tool of the
    // session runs, the frames of the page included. A frame of a site not allowed is refused without a question: the
    // agent loads that site itself to have the user asked.
    private async ValueTask<string?> SiteRefusalAsync(string site, bool canAsk, CancellationToken cancellationToken)
    {
        var settings = _settings().UserProfile;
        if (!settings.Enabled) return "The user turned off the use of their own browser in Settings > Extensions > Browser.";
        if (BrowserSites.Matches(site, settings.BlockedSites)) return $"The user blocked {site} in their own browser, in Settings > Extensions > Browser.";
        if (BrowserSites.Matches(site, settings.AllowedSites)) return null;
        Task<string?> asked;
        lock (_gate)
        {
            if (BrowserSites.Matches(site, _allowedSites)) return null;
            if (_deniedSites.Contains(site)) return $"The user did not allow this session to act on {site} in their own browser.";
            if (!canAsk) return $"{site} is not one of the sites the user allowed in their own browser: load it with navigate_page or new_page to have the user asked.";
            if (!_asking.TryGetValue(site, out asked!)) _asking[site] = asked = AskSiteAsync(site);
        }

        return await asked.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> AskSiteAsync(string site)
    {
        try
        {
            var decision = await AskAsync(site, _lifetime.Token).ConfigureAwait(false);
            switch (decision.Kind)
            {
                case AgentPermissionDecisionKind.AllowAlways:
                    lock (_gate) _allowedSites.Add(site);
                    await _allowAlways(site, CancellationToken.None).ConfigureAwait(false);
                    return null;
                case AgentPermissionDecisionKind.AllowOnce:
                    lock (_gate) _allowedSites.Add(site);
                    return null;
                case AgentPermissionDecisionKind.Deny:
                    lock (_gate) _deniedSites.Add(site);
                    return $"The user did not allow this session to act on {site} in their own browser.";
                default:
                    return $"The user was not asked about {site}: the request was canceled.";
            }
        }
        finally
        {
            lock (_gate) _asking.Remove(site);
        }
    }

    /// <summary>
    /// Asks the user, on the card of the session, to let it use their own browser (a null site), or act on a site there.
    /// </summary>
    public async Task<AgentPermissionDecision> AskAsync(string? site, CancellationToken cancellationToken)
    {
        if (_host.RequestPermission is not { } ask || !_host.HasUser) return new(AgentPermissionDecisionKind.Deny);
        return await ask(SessionId, Request(SessionId, Target, site), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The request that asks the user about their own browser, or a site of it.</summary>
    public static AgentBrowserPermissionRequest Request(string sessionId, BrowserTarget target, string? site)
        => new(new ModelProviderId("browser"), sessionId, DateTimeOffset.UtcNow, null, $"browser-{Guid.NewGuid():N}", target.Browser.Name, ProfileName(target.Browser), site);

    /// <summary>The name of the profile of the user's browser a session acts in.</summary>
    public static string ProfileName(InstalledBrowser browser)
        => (browser.Engine == BrowserEngine.Firefox ? BrowserChoice.FirefoxUserProfile(browser) : browser.Profiles.FirstOrDefault(static profile => profile.IsDefault) ?? browser.Profiles.FirstOrDefault())?.Name
            ?? "Default";

    /// <summary>Records that the user let the session act on a site, for the session.</summary>
    public void AllowSite(string site)
    {
        lock (_gate)
        {
            _deniedSites.Remove(site);
            _allowedSites.Add(site);
        }
    }

    private static bool IsLoopback(Uri url)
        => url.IsLoopback || url.Host is "0.0.0.0" or "[::]" || (IPAddress.TryParse(url.Host.Trim('[', ']'), out var address) && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)));

    /// <summary>A name for a folder of a session.</summary>
    internal static string SafeName(string sessionId)
    {
        var name = new string([.. sessionId.Select(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')]);
        return name.Length is > 0 and <= 80 ? name : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId)))[..32];
    }

    /// <summary>Closes the tabs of the session, and ends any question it waits on.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await CloseTabsAsync().ConfigureAwait(false);
        BrowserSandboxProxy? proxy;
        lock (_gate)
        {
            proxy = _proxy;
            _proxy = null;
        }

        if (proxy is not null) await proxy.Owner.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
