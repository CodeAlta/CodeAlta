using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// Serves a mobile view of CodeAlta (the recent sessions, their timelines and a composer) from an in-process server, and shows its
/// address as a QR code in the Mobile tab. A private network of the settings makes the view reachable (<see cref="IPluginServices.PrivateNetworks"/>):
/// Tailscale Serve on the tailnet, a Dev Tunnel of Microsoft, or a network a plugin brings; the plugin starts the server, asks the network to
/// publish the view, and shows what it says. It runs
/// while it is turned on in Settings &gt; Plugins, which is off until the user turns it on; the provider, its ports and the access of the
/// phones are on its settings page, Settings &gt; Extensions &gt; Mobile.
/// </summary>
[Plugin("mobile", DisplayName = "Mobile", Description = "The sessions of this computer on a phone, through Tailscale, Dev Tunnels or a private network of a plugin.", Frontends = PluginFrontends.Desktop)]
public sealed class MobilePlugin : PluginBase
{
    /// <summary>The id of the Mobile tab.</summary>
    internal const string CanvasId = "mobile";

    /// <summary>The name of the module of the application's build that draws the tab.</summary>
    internal const string AppModule = "mobile";

    /// <summary>The id of the settings page of the plugin, in Settings &gt; Extensions.</summary>
    internal const string SettingsCanvasId = "mobile-settings";

    /// <summary>The name of the module of the application's build that draws the settings page.</summary>
    internal const string SettingsModule = "mobile-settings";

    /// <summary>The port of the server on this computer, unless the state of the plugin says another.</summary>
    internal const int DefaultPort = 2584;

    /// <summary>The HTTPS port of this computer on the tailnet, unless the state of the plugin says another.</summary>
    internal const int DefaultHttpsPort = 2584;

    /// <summary>The lowest port the settings page accepts: the ones below are the system's.</summary>
    internal const int MinimumPort = 1024;

    /// <summary>The highest port.</summary>
    internal const int MaximumPort = 65535;

    private const string SettingsName = "mobile.json";
    private static readonly TimeSpan FlushPeriod = TimeSpan.FromMilliseconds(250);

    private const string NetworkStateName = "mobile-networks.json";

    // The networks of the tests; null for those of the host.
    private readonly Func<IReadOnlyList<IPrivateNetworkProvider>>? _networks;
    private readonly MobileLive _live;
    private readonly MobilePushEvents _pushEvents = new();
    private volatile MobilePush? _push;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _canvases = new();
    private readonly List<IPluginCanvasRpc> _open = [];
    private readonly List<IPluginCanvasRpc> _settingsPages = [];
    private volatile MobileSettings _settings = new();
    private volatile MobileAccess? _access;
    private MobileServer? _server;
    // The port the server listens on now: a change of it starts the server again.
    private int? _listeningPort;
    // The network of now and what publishes the view through it.
    private IPrivateNetworkProvider? _network;
    private IPrivateNetworkPublisher? _provider;
    private volatile MobileState _state = MobileState.Starting;
    private bool _stopped;
    // Whether the last reading of the asks failed: it is logged once.
    private bool _askingFailed;
    // Whether the last reading of the permission requests failed: it is logged once.
    private bool _approvingFailed;

    /// <summary>Creates the plugin with the private networks of the host.</summary>
    public MobilePlugin()
        : this(networks: null)
    {
    }

    /// <summary>Creates the plugin with private networks of the tests; null for those of the host.</summary>
    internal MobilePlugin(Func<IReadOnlyList<IPrivateNetworkProvider>>? networks)
    {
        _networks = networks;
        _live = new MobileLive { SteerProbe = CanSteerAsync };
    }

    /// <summary>The network of settings that name none: Tailscale, when this computer has it.</summary>
    internal const string DefaultProvider = "tailscale";

    /// <summary>The networks that publish the view, in the order the settings page offers them.</summary>
    internal IReadOnlyList<IPrivateNetworkProvider> Networks()
        => [.. (_networks?.Invoke() ?? Services.PrivateNetworks.Providers).Where(static network => network.Capabilities.HasFlag(PrivateNetworkCapabilities.Publish))];

    // The network of an id, or null when this computer has none with it now (a plugin that brings it may not run yet).
    private IPrivateNetworkProvider? NetworkOf(string id) => Networks().FirstOrDefault(network => string.Equals(network.Id, id, StringComparison.OrdinalIgnoreCase));

    // Makes the publisher of a network with a host of its own: what a publisher that was replaced reports afterwards is ignored.
    private (IPrivateNetworkProvider Network, IPrivateNetworkPublisher Publisher)? Create(IPrivateNetworkProvider? network)
    {
        if (network is null) return null;
        var host = new Host(this, network.Id);
        IPrivateNetworkPublisher? publisher;
        try
        {
            publisher = network.CreatePublisher(host);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A network a plugin brings that fails publishes nothing.
            Logger.Warn(exception, $"Mobile: the network {network.Id} could not publish the view.");
            publisher = null;
        }

        if (publisher is null) return null;
        host.Provider = publisher;
        return (network, publisher);
    }

    // The network the settings name, else the default one, else the first that publishes.
    private (IPrivateNetworkProvider Network, IPrivateNetworkPublisher Publisher)? CreateOfSettings()
        => Create(NetworkOf(_settings.Mode)) ?? Create(NetworkOf(DefaultProvider)) ?? Networks().Select(Create).FirstOrDefault(static created => created is not null);

    // What a network is given to publish: the port of the view, and the port of the network for a network that takes one.
    private PrivateNetworkPublication Publication(IPrivateNetworkProvider network)
        => new(_settings.Port, network.Capabilities.HasFlag(PrivateNetworkCapabilities.PublicPort) ? _settings.HttpsPort : null);

    /// <summary>Gets what the plugin serves now.</summary>
    internal MobileState State => _state;

    /// <summary>Gets who may use the view, once the plugin read it.</summary>
    internal MobileAccess? Access => _access;

    /// <summary>Gets the pairing token the QR code shows in this run, or null until the Mobile tab asked for it.</summary>
    internal string? PairingToken => _access?.PairingToken;

    /// <inheritdoc />
    /// <remarks>Deltas are observed: the dot turns blue while reasoning streams, and the line says "Writing…" while an answer is written.</remarks>
    public override bool ObservesContentDeltas => true;

    /// <inheritdoc />
    public override async ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
    {
        if (Context.Host.Frontend != PluginFrontends.Desktop) return;
        // The server starts in the background: the window does not wait for the provider.
        _ = Tasks.Run("mobile-start", StartAsync, new PluginTaskOptions { SessionRetentionIndependent = true, Description = "Starts the mobile view." });
        Tasks.Run("mobile-live", FlushAsync, new PluginTaskOptions { LongRunning = true, SessionRetentionIndependent = true, Description = "Pushes the state of the sessions to the phones." });
        Tasks.Run("mobile-push", PushAsync, new PluginTaskOptions { LongRunning = true, SessionRetentionIndependent = true, Description = "Notifies installed phone apps when sessions finish or need the person." });
        await base.OnActivatedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.OnDeactivatingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _live.Observe(context.Event, context.ProjectId);
        if (_push is not null) _pushEvents.Observe(context.Event);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _push, null)?.Dispose();
    }

    /// <summary>Reads the settings, starts the server and has the provider publish it: what activation runs in the background.</summary>
    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A plugin turned off before its start ran starts nothing.
            if (_stopped) return;
            _settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            (_network, _provider) = CreateOfSettings() is { } created ? created : (null, null);
            await StartServerAsync(cancellationToken).ConfigureAwait(false);
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await ChangedAsync().ConfigureAwait(false);
    }

    // The access of the phones is read with the settings. A file of before kept the token itself: the phones it signed in keep
    // their access through its hash, and the token leaves the file.
    private async Task<MobileSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await Services.State.ReadJsonAsync<MobileSettings>(PluginStateScope.User, SettingsName, cancellationToken).ConfigureAwait(false) ?? new MobileSettings();
        var access = await MobileAccess.LoadAsync(Services.State, cancellationToken).ConfigureAwait(false);
        if (MobileToken.IsValid(settings.Token)) await access.AdoptAsync(settings.Token!, cancellationToken).ConfigureAwait(false);
        _access = access;
        _push = await MobilePush.LoadAsync(Services.State, access, cancellationToken).ConfigureAwait(false);
        var valid = settings with
        {
            Token = null,
            Port = IsPort(settings.Port) ? settings.Port : DefaultPort,
            HttpsPort = IsPort(settings.HttpsPort) ? settings.HttpsPort : DefaultHttpsPort,
            // A network this computer has not now stays chosen: the plugin that brings it may start later.
            Mode = IsNetworkId(settings.Mode) ? settings.Mode : DefaultProvider,
        };
        if (valid != settings) await Services.State.WriteJsonAsync(PluginStateScope.User, SettingsName, valid, cancellationToken).ConfigureAwait(false);
        return valid;
    }

    // Starts the server on the loopback address, at the port of the settings: the provider publishes it.
    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        if (!Services.AppFiles.HasFiles)
        {
            _state = _state with { Server = "failed", ServerDetail = "This application has no page for phones." };
            return;
        }

        var port = _settings.Port;
        var sessions = new MobileSessions(Services.Alta, Services.Spaces, _live);
        var attachments = Path.Combine(Context.Host.UserDataDirectory, "plugin-data", "mobile");
        Directory.CreateDirectory(attachments);
        MobileApi.RemoveStaleAttachments(attachments);
        var server = new MobileServer(new MobileServerOptions
        {
            Port = port, Access = _access!, Hosts = () => _state.PublicHost is { } host ? [host] : [],
            Files = Services.AppFiles, Sessions = sessions, Api = new MobileApi(Services.Alta, sessions, attachments),
            Asks = new MobileAsks(Services.Asks, sessions), Permissions = new MobilePermissions(Services.Permissions, sessions, null, Services.Alta), Live = _live, Push = _push, Logger = Logger,
        });
        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            _server = server;
            _listeningPort = port;
            _state = _state with { Server = "listening", ServerDetail = null };
            Logger.Info($"Mobile: the mobile view listens on http://127.0.0.1:{port}");
        }
        catch (Exception exception) when (exception is IOException { InnerException: SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } } or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
        {
            await server.DisposeAsync().ConfigureAwait(false);
            _state = _state with { Server = "port-in-use", ServerDetail = $"Port {port} of this computer is used by another program, such as another CodeAlta that serves the mobile view." };
            Logger.Warn($"Mobile: port {port} is used by another program.");
        }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            _state = _state with { Server = "failed", ServerDetail = exception.Message };
            Logger.Warn(exception, "Mobile: the mobile view could not start.");
        }
    }

    private async Task StopServerAsync()
    {
        _listeningPort = null;
        if (Interlocked.Exchange(ref _server, null) is { } server) await server.DisposeAsync().ConfigureAwait(false);
    }

    private static readonly PrivateNetworkStatus Publishing = PrivateNetworkStatus.Pending("Publishing the mobile view…");
    private static readonly PrivateNetworkStatus NoNetwork = new("failed", null, "This computer has no private network that publishes the view.");
    private static readonly PrivateNetworkStatus NotListening = new("failed", null, "The mobile view does not listen on this computer, so it is not published.");

    // Has the provider publish the view the server serves; with the gate held. Without a server, nothing is published: the provider
    // says so rather than staying at a step that runs.
    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        if (_provider is not { } provider || _network is not { } network)
        {
            _state = _state with { Provider = NoNetwork };
            return;
        }

        if (_server is null)
        {
            _state = _state with { Provider = NotListening };
            return;
        }

        _state = _state with { Provider = Publishing };
        await ChangedAsync().ConfigureAwait(false);
        PrivateNetworkStatus status;
        try
        {
            status = await provider.PublishAsync(Publication(network), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // The command line of the network did not start or could not be read, or a network a plugin brings failed.
            Logger.Warn(exception, $"Mobile: {network.Id} could not be asked to publish the mobile view.");
            status = new PrivateNetworkStatus("failed", null, $"{network.DisplayName} could not be asked: {exception.Message}");
        }

        if (ReferenceEquals(provider, _provider)) SetProvider(status);
    }

    // What the provider does now, logged once for every provider; with the gate held.
    private void SetProvider(PrivateNetworkStatus status)
    {
        _state = _state with { Provider = status };
        Logger.Info($"Mobile: {_network?.Id ?? _settings.Mode}: {status.State}: {status.Detail}");
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopped = true;
            await StopServerAsync().ConfigureAwait(false);
            // What the provider published goes with the plugin; turning the plugin on again publishes it again.
            _network = null;
            if (Interlocked.Exchange(ref _provider, null) is { } provider)
            {
                await provider.UnpublishAsync(cancellationToken).ConfigureAwait(false);
                await provider.DisposeAsync().ConfigureAwait(false);
            }

            _state = MobileState.Stopped;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Has the provider publish again: it was installed, signed in, or what kept phones out was fixed since; a server that did not start
    // is started again first, unless it just failed (the settings changed).
    private async Task<MobileStatus> RetryAsync(CancellationToken cancellationToken, bool startServer = true)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return Status();
            // The network the settings name, now that the plugin that brings it may run; or one, when there was none.
            if (_provider is null || !string.Equals(_network?.Id, _settings.Mode, StringComparison.OrdinalIgnoreCase) && NetworkOf(_settings.Mode) is not null)
                await ReplaceAsync(CreateOfSettings()).ConfigureAwait(false);
            if (_server is null && startServer) await StartServerAsync(cancellationToken).ConfigureAwait(false);
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await ChangedAsync().ConfigureAwait(false);
        return Status();
    }

    // Runs an action of the provider the tab offers (a sign-in), with the gate held so that it does not cross a publication: what it
    // does at once is shown, and its outcome comes with the changes it reports.
    private async Task<MobileStatus> ActAsync(string action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped || _provider is not { } provider) return Status();
            PrivateNetworkStatus? status;
            try
            {
                status = await provider.ActAsync(action, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                Logger.Warn(exception, $"Mobile: the action {action} of {_network?.Id ?? _settings.Mode} failed.");
                status = new PrivateNetworkStatus("failed", null, exception.Message);
            }

            if (status is not null) SetProvider(status);
            else Logger.Warn($"Mobile: the network {_network?.Id ?? _settings.Mode} knows no action {action}.");
        }
        finally
        {
            _gate.Release();
        }

        await ChangedAsync().ConfigureAwait(false);
        return Status();
    }

    // A new pairing token, and no phone signed in: every phone has to scan the new code. The streams of the phones end, and they
    // cannot sign in again.
    internal async Task<MobileSettingsView> ResetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_access is { } access) await access.ResetAsync(cancellationToken).ConfigureAwait(false);
            _server?.Disconnect();
            Logger.Info("Mobile: the access was reset; the phones have to scan the new code.");
        }
        finally
        {
            _gate.Release();
        }

        await ChangedAsync().ConfigureAwait(false);
        return Settings();
    }

    /// <summary>Whether a port is one the settings accept: the system keeps those below 1024.</summary>
    internal static bool IsPort(int? port) => port is >= MinimumPort and <= MaximumPort;

    // Whether a saved network id is one: a network unknown now is kept, since a plugin may bring it later.
    private static bool IsNetworkId(string? id) => id is { Length: > 0 and <= 64 } && id.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // Puts another network in the place of the one of now, which takes off what it published first; with the gate held.
    private async Task ReplaceAsync((IPrivateNetworkProvider Network, IPrivateNetworkPublisher Publisher)? next)
    {
        if (_provider is { } old)
        {
            await old.UnpublishAsync(CancellationToken.None).ConfigureAwait(false);
            await old.DisposeAsync().ConfigureAwait(false);
        }

        (_network, _provider) = next is { } created ? created : (null, null);
    }

    /// <summary>
    /// Changes the settings: they are saved, the provider of before takes off what it published, the server listens as the provider of now
    /// says, and the provider publishes the view in the background; the settings page hears the outcome with <c>mobile.settings.changed</c>.
    /// </summary>
    /// <returns>The settings now, with <see cref="MobileSettingsView.Error"/> when the change is refused.</returns>
    internal async Task<MobileSettingsView> SetAsync(MobileSettingsChange request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsPort(request.Port)) return Settings() with { Error = $"The port on this computer must be a number from {MinimumPort} to {MaximumPort}." };
        var id = request.Provider ?? _settings.Mode;
        var chosen = NetworkOf(id);
        if (chosen is null) return Settings() with { Error = "Phones connect through " + string.Join(" or ", Networks().Select(static network => network.DisplayName)) + "." };
        if (chosen.Capabilities.HasFlag(PrivateNetworkCapabilities.PublicPort) && request.HttpsPort is { } asked && !IsPort(asked))
            return Settings() with { Error = $"The port of {chosen.DisplayName} must be a number from {MinimumPort} to {MaximumPort}." };
        MobileSettingsView view;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return Settings() with { Error = "The plugin is stopped." };
            var old = _settings;
            var next = old with
            {
                Mode = chosen.Id, Port = request.Port!.Value, HttpsPort = request.HttpsPort ?? old.HttpsPort,
            };
            if (next == old && _provider is not null) return Settings();
            // What was published goes first, with the settings it was published with; then the settings of now.
            if (_provider is null || !string.Equals(_network?.Id, chosen.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (Create(chosen) is not { } created) return Settings() with { Error = $"{chosen.DisplayName} cannot publish the view." };
                await ReplaceAsync(created).ConfigureAwait(false);
            }
            else
            {
                await _provider.UnpublishAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _settings = next;
            await Services.State.WriteJsonAsync(PluginStateScope.User, SettingsName, _settings, CancellationToken.None).ConfigureAwait(false);
            Logger.Info($"Mobile: phones now connect through {chosen.Id}, to port {next.Port} of this computer.");
            if (_server is null || next.Port != _listeningPort)
            {
                // The phones that were connected reconnect to the new address once they scan it.
                await StopServerAsync().ConfigureAwait(false);
                _state = _state with { Server = "starting", ServerDetail = null };
                await StartServerAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _state = _state with { Provider = Publishing };
            // The answer is the state of the change itself, read before the provider publishes: one that answers at once would otherwise
            // give the outcome of the publication to some calls and not to others.
            view = Settings();
        }
        finally
        {
            _gate.Release();
        }

        _ = Tasks.Run("mobile-publish", async ct => await RetryAsync(ct, startServer: false).ConfigureAwait(false), new PluginTaskOptions { SessionRetentionIndependent = true, Description = "Publishes the mobile view." });
        await ChangedAsync().ConfigureAwait(false);
        return view;
    }

    // Pushes what changed four times a second, and a comment every 15 s so that proxies keep the streams open.
    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FlushPeriod);
        var ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var asks = await ReadAskingAsync(cancellationToken).ConfigureAwait(false);
                var permissions = await ReadApprovingAsync(cancellationToken).ConfigureAwait(false);
                _live.Flush(asks, permissions);
                if (_push is not null) _pushEvents.Pending(asks, permissions);
                if (++ticks % 60 == 0) _live.Broadcast("ping", "{}");
            }
        }
        catch (OperationCanceledException)
        {
            // The plugin stops.
        }
    }

    // One consumer owns deliveries; slow push services never block the agent or the stream to an open phone.
    private async ValueTask PushAsync(CancellationToken cancellationToken)
    {
        var sessions = new MobileSessions(Services.Alta, Services.Spaces, _live);
        try
        {
            await foreach (var notice in _pushEvents.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (_push is { } push) await push.SendAsync(notice, sessions, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
                {
                    // Exceptions of push services can contain a phone's endpoint and keys.
                    Logger.Warn("Mobile: a push notification could not be delivered.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Reads the asks the sessions wait on for the next flush. A host that cannot say stops nothing: the flush keeps those
    /// read before (null), and the failure is logged once until a reading succeeds again.
    /// </summary>
    internal async ValueTask<IReadOnlyList<PluginAskSummary>?> ReadAskingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var asking = await Services.Asks.ListAskingAsync(cancellationToken).ConfigureAwait(false);
            _askingFailed = false;
            return asking;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (!_askingFailed) Logger.Warn(exception, "Mobile: the asks of the sessions could not be read; the phones keep those read before.");
            _askingFailed = true;
            return null;
        }
    }

    /// <summary>
    /// Reads the permission requests the sessions wait on for the next flush. A host that cannot say stops nothing: the flush
    /// keeps those read before (null), and the failure is logged once until a reading succeeds again.
    /// </summary>
    internal async ValueTask<IReadOnlyList<PluginPermissionSummary>?> ReadApprovingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var approving = await Services.Permissions.ListWaitingAsync(cancellationToken).ConfigureAwait(false);
            _approvingFailed = false;
            return approving;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (!_approvingFailed) Logger.Warn(exception, "Mobile: the permission requests of the sessions could not be read; the phones keep those read before.");
            _approvingFailed = true;
            return null;
        }
    }

    private async Task<bool> CanSteerAsync(string sessionId)
    {
        var result = await MobileAlta.RunAsync(Services.Alta, ["session", "status", sessionId], null, CancellationToken.None).ConfigureAwait(false);
        return result.OfType("alta.session.status").Any(static status => MobileAlta.Flag(status, "isRunning"));
    }

    // The pairing token lives in memory only: after a restart, the tab that shows the QR code makes a new one.
    private async Task<MobileStatus> PairedAsync(Func<MobileStatus> status, CancellationToken cancellationToken)
        => await PairedAsync(() => Task.FromResult(status()), cancellationToken).ConfigureAwait(false);

    private async Task<MobileStatus> PairedAsync(Func<Task<MobileStatus>> status, CancellationToken cancellationToken)
    {
        if (_access is { } access) await access.PairAsync(cancellationToken).ConfigureAwait(false);
        return await status().ConfigureAwait(false);
    }

    /// <summary>What the Mobile tab shows.</summary>
    internal MobileStatus Status()
    {
        var state = _state;
        var url = ServedUrl(state);
        var connect = url is not null && _access?.PairingToken is { } token ? $"{url}/#k={token}" : null;
        return new MobileStatus(state.Server, _listeningPort ?? _settings.Port, state.ServerDetail, View(state), connect, connect is null ? null : url,
            connect is null ? null : MobileQr.Rows(connect));
    }

    /// <summary>What the settings page shows: the provider and its settings, and the address while the view is served.</summary>
    internal MobileSettingsView Settings()
    {
        var state = _state;
        var settings = _settings;
        return new MobileSettingsView(settings.Port, settings.HttpsPort, ServedUrl(state), state.Server, state.ServerDetail, View(state))
        {
            Providers = [.. Networks().Select(static network => new MobileNetworkView(network.Id, network.DisplayName, network.Description,
                network.Capabilities.HasFlag(PrivateNetworkCapabilities.PublicPort)))],
        };
    }

    private MobileProviderView View(MobileState state)
        => new(_network?.Id ?? _settings.Mode, state.Provider.State, state.Provider.Url, state.Provider.Detail, state.Provider.FixUrl, state.Provider.Working)
        {
            Name = _network?.DisplayName ?? _settings.Mode,
            Actions = [.. state.Provider.Actions.Select(static action => new MobileActionView(action.Id, action.Label))],
        };

    // The address phones open while the view is served.
    private static string? ServedUrl(MobileState state)
        => state is { Server: "listening", Provider.State: "serving" } ? state.Provider.Url : null;

    private async Task ChangedAsync()
    {
        Services.Ui.InvalidateButtons();
        IPluginCanvasRpc[] tabs, pages;
        lock (_canvases) (tabs, pages) = ([.. _open], [.. _settingsPages]);
        // A tab opened before the plugin read its access gets its QR code now.
        if (tabs.Length > 0) await PublishAsync(tabs, "mobile.changed", await PairedAsync(Status, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        if (pages.Length > 0) await PublishAsync(pages, "mobile.settings.changed", Settings()).ConfigureAwait(false);
    }

    private async Task PublishAsync<T>(IPluginCanvasRpc[] canvases, string name, T value)
    {
        foreach (var rpc in canvases)
        {
            try { await rpc.PublishAsync(name, value).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { Logger.Debug($"Mobile: a page did not get the change: {exception.Message}"); }
        }
    }

    /// <inheritdoc />
    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        yield return PluginUi.Button(PluginButtonPlace.TitleBar, "mobile", "smartphone", "Connect a phone") with
        {
            Canvas = CanvasId,
            GetState = _ => _state switch
            {
                var state when ServedUrl(state) is not null => new PluginButtonState { Tone = PluginStatusTone.Success, Tooltip = $"Mobile: served at {state.Provider.Url}" },
                { Server: "starting" } or { Provider.Working: true } => new PluginButtonState { Badge = PluginButtonBadge.Busy, Tooltip = "Mobile: starting" },
                _ => new PluginButtonState { Badge = PluginButtonBadge.Dot, Tooltip = "Mobile: phones cannot connect" },
            },
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell(CanvasId, "Opens the Mobile tab: the QR code a phone opens the sessions of this computer with.", async (_, cancellationToken) =>
        {
            var result = await Services.Canvases.OpenAsync(CanvasId, null, cancellationToken).ConfigureAwait(false);
            return result.Requested ? PluginCommandResult.Handled : PluginCommandResult.Message("The Mobile tab cannot be shown here.");
        }) with
        {
            Label = "Mobile: connect a phone",
            SearchText = "mobile phone qr tailscale dev tunnels connect",
            KeyBinding = new PluginKeyBinding(new PluginKeyGesture('G', PluginKeyModifiers.Ctrl), new PluginKeyGesture('M')),
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = CanvasId,
            Title = "Mobile",
            Description = "The QR code a phone opens the sessions of this computer with, and what keeps it from connecting.",
            Icon = "smartphone",
            Scope = PluginCanvasScope.Application,
            Open = (canvas, _) =>
            {
                // The calls of the tab are registered before the view that names its module is returned.
                canvas.Rpc.JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = MobileJsonContext.Default };
                // The tab shows the QR code: it makes the pairing token of this run when there is none yet.
                canvas.Rpc.Handle<MobileEmpty, MobileStatus>("mobile.status", async (_, ct) => await PairedAsync(Status, ct).ConfigureAwait(false));
                canvas.Rpc.Handle<MobileEmpty, MobileStatus>("mobile.retry", async (_, ct) => await PairedAsync(() => RetryAsync(ct), ct).ConfigureAwait(false));
                // The actions a state of the provider offers (a sign-in); their outcome comes with the changes.
                canvas.Rpc.Handle<MobileAction, MobileStatus>("mobile.act", async (request, ct) => await PairedAsync(() => ActAsync(request.Action, ct), ct).ConfigureAwait(false));
                Track(canvas, _open);
                return ValueTask.FromResult(new PluginCanvasView { Script = PluginScript.App(AppModule) });
            },
            Describe = (_, _) => ValueTask.FromResult<string?>(DescribeTab()),
        };
        yield return new PluginCanvasContribution
        {
            Id = SettingsCanvasId,
            Title = "Mobile",
            Description = "How phones connect (Tailscale or Dev Tunnels), the ports of the mobile view, and the access of the phones.",
            Icon = "smartphone",
            Scope = PluginCanvasScope.Application,
            Placement = PluginCanvasPlacement.Settings,
            Open = (canvas, _) =>
            {
                canvas.Rpc.JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = MobileJsonContext.Default };
                canvas.Rpc.Handle<MobileEmpty, MobileSettingsView>("mobile.settings.get", (_, _) => ValueTask.FromResult(Settings()));
                canvas.Rpc.Handle<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", async (request, ct) => await SetAsync(request, ct).ConfigureAwait(false));
                canvas.Rpc.Handle<MobileEmpty, MobileSettingsView>("mobile.reset", async (_, ct) => await ResetAsync(ct).ConfigureAwait(false));
                Track(canvas, _settingsPages);
                return ValueTask.FromResult(new PluginCanvasView { Script = PluginScript.App(SettingsModule) });
            },
            Describe = (_, _) => ValueTask.FromResult<string?>(DescribeTab()),
        };
    }

    // An open page hears the changes until it closes.
    private void Track(PluginCanvasContext canvas, List<IPluginCanvasRpc> pages)
    {
        lock (_canvases) pages.Add(canvas.Rpc);
        canvas.Closed.Register(() => { lock (_canvases) pages.Remove(canvas.Rpc); });
    }

    // What an agent reads of the tab: the state, never the token.
    private string DescribeTab()
    {
        var state = _state;
        var text = new StringBuilder($"# Mobile\n\nThe sessions of this computer on a phone: a page served by CodeAlta and published through the private network {_network?.DisplayName ?? _settings.Mode}.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"- Server: {state.Server} on port {_listeningPort ?? _settings.Port}{(state.ServerDetail is { } detail ? $" ({detail})" : "")}\n");
        text.Append(CultureInfo.InvariantCulture, $"- Provider: {state.Provider.State}: {state.Provider.Detail}\n");
        if (ServedUrl(state) is { } url) text.Append(CultureInfo.InvariantCulture, $"- Address: {url} (the QR code adds the access token)\n");
        return text.ToString();
    }

    /// <summary>What the plugin serves: its server, and what the provider does with it.</summary>
    /// <param name="Server"><c>starting</c>, <c>listening</c>, <c>port-in-use</c>, <c>failed</c> or <c>stopped</c>.</param>
    /// <param name="ServerDetail">Why the server does not listen.</param>
    /// <param name="Provider">What the provider does.</param>
    internal sealed record MobileState(string Server, string? ServerDetail, PrivateNetworkStatus Provider)
    {
        internal static MobileState Starting { get; } = new("starting", null, PrivateNetworkStatus.Pending("Starting…"));

        internal static MobileState Stopped { get; } = new("stopped", null, new PrivateNetworkStatus("stopped", null, "The mobile view is not served."));

        /// <summary>
        /// The host the requests that come from outside name: the name of the address the provider gives, with its port. A provider that
        /// names the loopback address instead (a tunnel) is let in by the server as a request of this computer.
        /// </summary>
        internal string? PublicHost => Provider.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : null;
    }

    // What the plugin gives the publisher of a network: the values it keeps (in a file of the plugin, by network), its log, its
    // background, and a way to say what changed. A publisher that was replaced (the settings changed, the plugin stopped) says nothing any more.
    private sealed class Host(MobilePlugin plugin, string network) : IPrivateNetworkHost
    {
        internal IPrivateNetworkPublisher? Provider { get; set; }

        public async Task<string?> ReadStateAsync(string key, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            var values = await plugin.Services.State.ReadJsonAsync<Dictionary<string, string>>(PluginStateScope.User, NetworkStateName, cancellationToken).ConfigureAwait(false);
            return values is not null && values.TryGetValue(Key(key), out var value) ? value : null;
        }

        public async Task WriteStateAsync(string key, string? value, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            var values = await plugin.Services.State.ReadJsonAsync<Dictionary<string, string>>(PluginStateScope.User, NetworkStateName, cancellationToken).ConfigureAwait(false) ?? [];
            if (value is null) values.Remove(Key(key));
            else values[Key(key)] = value;
            await plugin.Services.State.WriteJsonAsync(PluginStateScope.User, NetworkStateName, values, cancellationToken).ConfigureAwait(false);
        }

        // The values of each network apart: one never reads what another one kept.
        private string Key(string key) => network + "/" + key;

        public async Task ReportAsync(PrivateNetworkStatus status)
        {
            await plugin._gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsCurrent) return;
                plugin.SetProvider(status);
            }
            finally
            {
                plugin._gate.Release();
            }

            await plugin.ChangedAsync().ConfigureAwait(false);
        }

        public async Task RepublishAsync()
        {
            if (IsCurrent) await plugin.RetryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private bool IsCurrent => Provider is not null && ReferenceEquals(Provider, plugin._provider) && !plugin._stopped;

        public void Run(string name, string description, Func<CancellationToken, ValueTask> work)
            => _ = plugin.Tasks.Run("mobile-" + name, work, new PluginTaskOptions { SessionRetentionIndependent = true, Description = description });

        public void Log(string line) => plugin.Logger.Info("Mobile: " + line);
    }
}

/// <summary>What the plugin keeps between starts, in its state of the user.</summary>
internal sealed record MobileSettings
{
    /// <summary>
    /// Gets the access token a file of before kept itself. It is read once, its hash taken over by <see cref="MobileAccess"/>,
    /// and never written again: the file keeps no secret.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; init; }

    /// <summary>Gets the port of the server on this computer.</summary>
    public int Port { get; init; } = MobilePlugin.DefaultPort;

    /// <summary>Gets the port of the network the view is published at, for a network that takes one (the HTTPS port of the tailnet).</summary>
    public int HttpsPort { get; init; } = MobilePlugin.DefaultHttpsPort;

    /// <summary>Gets the id of the private network phones connect through: <c>tailscale</c>, <c>devtunnel</c>, or one a plugin brings.</summary>
    public string Mode { get; init; } = MobilePlugin.DefaultProvider;
}

/// <summary>An action of the provider the tab asks for.</summary>
/// <param name="Action">Its name, such as <c>signin-github</c>.</param>
internal sealed record MobileAction(string Action);

/// <summary>The input of a call that takes none.</summary>
internal sealed record MobileEmpty;

/// <summary>What the provider does, as the pages show it.</summary>
/// <param name="Id">The id of the provider.</param>
/// <param name="State">Its state, as <see cref="PrivateNetworkStatus.State"/> says it.</param>
/// <param name="Url">The address phones open, while the view is served.</param>
/// <param name="Detail">A line for people.</param>
/// <param name="FixUrl">The page that fixes the state, when there is one.</param>
/// <param name="Working">Whether the provider is at a step that runs: the pages wait, with no problem shown.</param>
internal sealed record MobileProviderView(string Id, string State, string? Url, string Detail, string? FixUrl, bool Working)
{
    /// <summary>Gets the name of the network, for people.</summary>
    public string Name { get; init; } = Id;

    /// <summary>Gets the actions its state offers, such as a sign-in.</summary>
    public IReadOnlyList<MobileActionView> Actions { get; init; } = [];
}

/// <summary>An action a state of a network offers, as a page shows it.</summary>
/// <param name="Action">What <c>mobile.act</c> is given.</param>
/// <param name="Label">The label of its button.</param>
internal sealed record MobileActionView(string Action, string Label);

/// <summary>A private network phones can connect through, as the settings page offers it.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name, for people.</param>
/// <param name="Description">What it needs, for people.</param>
/// <param name="PublicPort">Whether the view is published at a port of the network the page asks for.</param>
internal sealed record MobileNetworkView(string Id, string Name, string Description, bool PublicPort);

/// <summary>What the Mobile tab shows.</summary>
/// <param name="Server">What the server does.</param>
/// <param name="Port">Its port on this computer.</param>
/// <param name="ServerDetail">Why it does not listen.</param>
/// <param name="Provider">What the provider does.</param>
/// <param name="ConnectUrl">The address a phone opens, with the access token; null while no phone can connect.</param>
/// <param name="Address">The same without the token.</param>
/// <param name="Qr">The QR code of <paramref name="ConnectUrl"/>, one string of <c>0</c> and <c>1</c> per row.</param>
internal sealed record MobileStatus(string Server, int Port, string? ServerDetail, MobileProviderView Provider, string? ConnectUrl, string? Address, IReadOnlyList<string>? Qr);

/// <summary>The settings the settings page asks for.</summary>
/// <param name="Provider">The id of the provider; null keeps the one of now.</param>
/// <param name="Port">The port of the server on this computer.</param>
/// <param name="HttpsPort">The HTTPS port on the tailnet, for Tailscale; null keeps the one of now.</param>
internal sealed record MobileSettingsChange(string? Provider, int? Port, int? HttpsPort);

/// <summary>What the settings page shows.</summary>
/// <param name="Port">The port of the server on this computer.</param>
/// <param name="HttpsPort">The HTTPS port on the tailnet, for Tailscale.</param>
/// <param name="Address">The address of the view while it is served, without the access token; null otherwise.</param>
/// <param name="Server">What the server does: <c>starting</c>, <c>listening</c>, <c>port-in-use</c>, <c>failed</c> or <c>stopped</c>.</param>
/// <param name="ServerDetail">Why it does not listen.</param>
/// <param name="Provider">What the provider does.</param>
internal sealed record MobileSettingsView(int Port, int HttpsPort, string? Address, string Server, string? ServerDetail, MobileProviderView Provider)
{
    /// <summary>Gets why the settings that were asked for are refused, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the networks phones can connect through, in the order the page offers them.</summary>
    public IReadOnlyList<MobileNetworkView> Providers { get; init; } = [];
}

[System.Text.Json.Serialization.JsonSerializable(typeof(MobileEmpty))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileSettings))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileAccessFile))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileSettingsChange))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileAction))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileSettingsView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class MobileJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
