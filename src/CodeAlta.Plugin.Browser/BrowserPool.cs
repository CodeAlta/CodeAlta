using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Drivers.BiDi;
using CodeAlta.Browser.Drivers.Cdp;
using CodeAlta.Browser.Launch;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Plugin.Browser;

/// <summary>What a session browses with: the browser, the profile, and whether it has a window.</summary>
/// <param name="Browser">The browser.</param>
/// <param name="Profile">The profile.</param>
/// <param name="Headless">Whether the browser runs without a window; the user's own browser always has its windows.</param>
internal sealed record BrowserTarget(InstalledBrowser Browser, BrowserProfileSpec Profile, bool Headless)
{
    /// <summary>The key of the browser process the sessions of this target share.</summary>
    public string Key => $"{Browser.Id}|{Browser.ExecutablePath}|{Profile}|{(Profile.Kind == BrowserProfileKind.User ? false : Headless)}";
}

/// <summary>
/// The browsers CodeAlta drives: one process per browser, profile and window mode, which the sessions share with a
/// context or a window each. A browser CodeAlta started ends when its last session leaves it; the user's own Chrome or
/// Edge stays connected while CodeAlta runs, since Chrome asks the user at each new connection.
/// </summary>
internal sealed class BrowserPool : IAsyncDisposable
{
    private readonly BrowserPluginHost _host;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    public BrowserPool(BrowserPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    private sealed class Entry(BrowserTarget target)
    {
        public BrowserTarget Target { get; } = target;

        public Task<IBrowserConnection>? Connecting { get; set; }

        // The user's Firefox CodeAlta started on their profile: let go of, never ended.
        public BrowserProcess? Released { get; set; }

        public int Leases { get; set; }

        public bool KeepOpen => Target.Profile.Kind == BrowserProfileKind.User && Target.Browser.Engine == BrowserEngine.Chromium;
    }

    /// <summary>Gets the browsers that run now, with the number of sessions in each.</summary>
    public IReadOnlyList<(BrowserTarget Target, int Sessions)> Running
    {
        get
        {
            lock (_gate) return [.. _entries.Values.Where(static entry => entry.Connecting is { IsCompletedSuccessfully: true }).Select(static entry => (entry.Target, entry.Leases))];
        }
    }

    /// <summary>Opens the tabs of a session in the browser of a target, starting or connecting to it first.</summary>
    /// <exception cref="BrowserToolException">The browser cannot be started or reached, with what to do.</exception>
    public async Task<IBrowserContext> OpenContextAsync(BrowserTarget target, BrowserContextOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        Entry entry;
        Task<IBrowserConnection> connecting;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(target.Key, out entry!)) _entries[target.Key] = entry = new Entry(target);
            if (entry.Connecting is { IsCompleted: true } done && (!done.IsCompletedSuccessfully || done.Result.IsClosed)) entry.Connecting = null;
            entry.Connecting ??= ConnectAsync(entry);
            connecting = entry.Connecting;
            entry.Leases++;
        }

        try
        {
            var connection = await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
            var context = await connection.CreateContextAsync(options, cancellationToken).ConfigureAwait(false);
            return new LeasedContext(context, () => ReleaseAsync(entry));
        }
        catch
        {
            await ReleaseAsync(entry).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IBrowserConnection> ConnectAsync(Entry entry)
    {
        // The start is the browser's, not the first caller's: a session that stops waiting leaves it to the next one.
        var target = entry.Target;
        var browser = target.Browser;
        try
        {
            switch (target.Profile.Kind, browser.Engine)
            {
                case (BrowserProfileKind.User, BrowserEngine.Chromium):
                    return await AttachChromiumAsync(browser).ConfigureAwait(false);
                case (BrowserProfileKind.User, BrowserEngine.Firefox):
                {
                    var (connection, process) = await StartUserFirefoxAsync(browser).ConfigureAwait(false);
                    lock (_gate) entry.Released = process;
                    return connection;
                }
                case (_, BrowserEngine.Chromium):
                {
                    var launch = await ChromiumLauncher.LaunchAsync(LaunchOptions(target), CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        return await CdpBrowserConnection.ConnectAsync(browser, launch.Endpoint, launch.Process, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        await launch.Process.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
                case (_, BrowserEngine.Firefox):
                    return await BiDiBrowserConnection.LaunchAsync(browser, LaunchOptions(target), CancellationToken.None).ConfigureAwait(false);
                default:
                    throw new BrowserToolException("not-supported", $"{browser.Name} is driven through its own MCP server: run `alta mcp activate safari`.");
            }
        }
        catch (BrowserLaunchException exception)
        {
            throw new BrowserToolException("launch-failed", exception.Message, exception);
        }
    }

    private BrowserLaunchOptions LaunchOptions(BrowserTarget target)
    {
        var temporary = target.Profile.Kind == BrowserProfileKind.Isolated;
        var folder = temporary
            ? Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N")[..12])
            : PersistentFolder(target.Profile.Name ?? BrowserProfileSpec.DefaultName, target.Browser);
        return new BrowserLaunchOptions { Executable = target.Browser.ExecutablePath, ProfileDirectory = folder, Temporary = temporary, Headless = target.Headless, StartTimeout = _host.StartTimeout };
    }

    /// <summary>Gets the folder of the temporary profiles, which the browsers delete when they end.</summary>
    public string TemporaryRoot => Path.Combine(_host.Root, "temp");

    /// <summary>Gets the folder of the persistent profiles: one folder per name, with a folder per browser in it.</summary>
    public string PersistentRoot => Path.Combine(_host.Root, "profiles");

    /// <summary>The folder of a persistent profile for a browser: each browser has its own format.</summary>
    public string PersistentFolder(string name, InstalledBrowser browser) => Path.Combine(PersistentRoot, name, browser.Id);

    // Chrome and Edge 144+ write DevToolsActivePort in their user data folder once the user allowed remote debugging in
    // chrome://inspect/#remote-debugging; Chrome then asks the user to allow each new connection.
    private static async Task<IBrowserConnection> AttachChromiumAsync(InstalledBrowser browser)
    {
        var scheme = browser.Id.StartsWith("edge", StringComparison.Ordinal) ? "edge" : "chrome";
        var how = $"In {browser.Name}, open {scheme}://inspect/#remote-debugging and turn on remote debugging, then call the tool again.";
        if (browser.DataDirectory is not { } userData || ChromiumLauncher.ReadPortFile(Path.Combine(userData, "DevToolsActivePort")) is not { } endpoint)
        {
            throw new BrowserToolException("remote-debugging-off", $"{browser.Name} does not let CodeAlta connect to it. Start {browser.Name} if it is not running. {how}");
        }

        try
        {
            return await CdpBrowserConnection.ConnectAsync(browser, endpoint, process: null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CodeAlta.Browser.Cdp.CdpException or System.Net.WebSockets.WebSocketException or IOException or TimeoutException)
        {
            throw new BrowserToolException("remote-debugging-refused",
                $"{browser.Name} refused the connection, or the user did not allow it in the dialog {browser.Name} shows ({exception.Message}). {how}", exception);
        }
    }

    // Firefox opens its remote port only when it starts with it: CodeAlta starts it on the user's profile, after the user
    // closed it, and never writes the preferences of automation there.
    private async Task<(IBrowserConnection Connection, BrowserProcess Process)> StartUserFirefoxAsync(InstalledBrowser browser)
    {
        if (BrowserChoice.FirefoxUserProfile(browser) is not { } profile)
        {
            throw new BrowserToolException("no-profile", $"{browser.Name} lists no profile of the user in profiles.ini.");
        }

        if (FirefoxLauncher.ProfileInUse(profile.Directory, () => _host.Environment.IsRunning("firefox")))
        {
            throw new BrowserToolException("profile-in-use",
                $"{browser.Name} runs on the profile \"{profile.Name}\": Firefox lets CodeAlta drive it only when CodeAlta starts it. Ask the user to close {browser.Name}, then call the tool again: CodeAlta starts it on the same profile.");
        }

        var launch = await FirefoxLauncher.LaunchAsync(new BrowserLaunchOptions
        {
            Executable = browser.ExecutablePath, ProfileDirectory = profile.Directory, Temporary = false, StartTimeout = _host.StartTimeout,
        }, writePreferences: false, CancellationToken.None).ConfigureAwait(false);
        try
        {
            return (await BiDiBrowserConnection.ConnectAsync(browser, launch.Endpoint, process: null, CancellationToken.None).ConfigureAwait(false), launch.Process);
        }
        catch
        {
            launch.Process.Release();
            throw;
        }
    }

    private async Task ReleaseAsync(Entry entry)
    {
        Task<IBrowserConnection>? close = null;
        BrowserProcess? released = null;
        lock (_gate)
        {
            entry.Leases--;
            if (entry.Leases > 0 || entry.KeepOpen) return;
            close = entry.Connecting;
            released = entry.Released;
            entry.Connecting = null;
            entry.Released = null;
            _entries.Remove(entry.Target.Key);
        }

        await CloseAsync(close, released).ConfigureAwait(false);
    }

    private static async Task CloseAsync(Task<IBrowserConnection>? connecting, BrowserProcess? released)
    {
        if (connecting is not null)
        {
            try
            {
                var connection = await connecting.ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // A browser that did not start, or is gone: nothing is left to close.
            }
        }

        released?.Release();
    }

    /// <summary>Deletes the temporary profiles a CodeAlta that ended too early left behind; a profile in use is left.</summary>
    public void DeleteLeftovers()
    {
        try
        {
            if (!Directory.Exists(TemporaryRoot)) return;
            foreach (var folder in Directory.EnumerateDirectories(TemporaryRoot))
            {
                // An hour old at least: another CodeAlta of this state root may be starting a browser there.
                if (Directory.GetLastWriteTimeUtc(folder) > DateTime.UtcNow.AddHours(-1)) continue;
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* a browser still runs there */ }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing to clean, or nothing that can be.
        }
    }

    /// <summary>Ends the browsers CodeAlta started, and lets go of the user's own.</summary>
    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = [.. _entries.Values];
            _entries.Clear();
        }

        await Task.WhenAll(entries.Select(entry => CloseAsync(entry.Connecting, entry.Released))).ConfigureAwait(false);
    }

    /// <summary>A context that gives its browser back to the pool once it is closed.</summary>
    private sealed class LeasedContext(IBrowserContext inner, Func<Task> release) : IBrowserContext
    {
        private int _disposed;

        public InstalledBrowser Browser => inner.Browser;

        public BrowserCapabilities Capabilities => inner.Capabilities;

        public IReadOnlyList<IBrowserPage> Pages => inner.Pages;

        public bool IsClosed => inner.IsClosed;

        public event Action<IBrowserPage>? PageOpened
        {
            add => inner.PageOpened += value;
            remove => inner.PageOpened -= value;
        }

        public Task<IBrowserPage> NewPageAsync(bool background, CancellationToken cancellationToken) => inner.NewPageAsync(background, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await release().ConfigureAwait(false);
            }
        }
    }
}
