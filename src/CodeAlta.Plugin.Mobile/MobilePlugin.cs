using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// Serves a mobile view of CodeAlta (the recent sessions, their timelines and a composer) from an in-process server on the
/// loopback address, publishes it on the tailnet with Tailscale Serve on an HTTPS port of its own, or through a Dev Tunnel of
/// Microsoft, and shows the address as a QR code in the Mobile tab. It runs while it is turned on in Settings &gt; Plugins, which is off until the user turns it on; its
/// ports and the access of the phones are on its settings page, Settings &gt; Extensions &gt; Mobile.
/// </summary>
[Plugin("mobile", DisplayName = "Mobile", Description = "The sessions of this computer on a phone, through Tailscale or Dev Tunnels.", Frontends = PluginFrontends.Desktop)]
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

    /// <summary>The port of the server on the loopback address, unless the state of the plugin says another.</summary>
    internal const int DefaultPort = 2584;

    /// <summary>The HTTPS port of this computer on the tailnet, unless the state of the plugin says another.</summary>
    internal const int DefaultHttpsPort = 2584;

    /// <summary>The mode where Tailscale Serve publishes the view on the tailnet.</summary>
    internal const string TailscaleMode = "tailscale";

    /// <summary>The mode where a Dev Tunnel of Microsoft publishes the view at an HTTPS address any phone opens.</summary>
    internal const string DevTunnelMode = "devtunnel";

    /// <summary>The lowest port the settings page accepts: the ones below are the system's.</summary>
    internal const int MinimumPort = 1024;

    /// <summary>The highest port.</summary>
    internal const int MaximumPort = 65535;

    private const string SettingsName = "mobile.json";
    private static readonly TimeSpan FlushPeriod = TimeSpan.FromMilliseconds(250);

    private readonly IMobileTailscaleCli _cli;
    private readonly IMobileDevTunnelCli _tunnelCli;
    private readonly MobileLive _live;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _canvases = new();
    private readonly List<IPluginCanvasRpc> _open = [];
    private readonly List<IPluginCanvasRpc> _settingsPages = [];
    private volatile MobileSettings _settings = new();
    private volatile MobileAccess? _access;
    private MobileServer? _server;
    private volatile MobileState _state = MobileState.Starting;
    // The ports Tailscale serves the view with now, as this instance published them: what is taken off when the ports change or the plugin stops.
    private (int Port, int HttpsPort)? _published;
    // The host of the Dev Tunnel while it serves, and the port it serves: what is stopped when the mode or the port changes or the plugin stops.
    private IMobileHostedProcess? _tunnelHost;
    private int _tunnelPort;
    // The sign-in to Dev Tunnels that runs: Try again or another sign-in drops it.
    private CancellationTokenSource? _signIn;
    private bool _stopped;
    // Whether the last reading of the asks failed: it is logged once.
    private bool _askingFailed;
    // Whether the last reading of the permission requests failed: it is logged once.
    private bool _approvingFailed;

    /// <summary>Creates the plugin with the Tailscale and Dev Tunnels command lines of this computer.</summary>
    public MobilePlugin()
        : this(new MobileTailscaleCli(), new MobileDevTunnelCli())
    {
    }

    /// <summary>Creates the plugin with a Tailscale command line of the tests, and the Dev Tunnels one of this computer.</summary>
    internal MobilePlugin(IMobileTailscaleCli cli)
        : this(cli, new MobileDevTunnelCli())
    {
    }

    /// <summary>Creates the plugin with command lines of the tests.</summary>
    internal MobilePlugin(IMobileTailscaleCli cli, IMobileDevTunnelCli tunnelCli)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _tunnelCli = tunnelCli ?? throw new ArgumentNullException(nameof(tunnelCli));
        _live = new MobileLive { SteerProbe = CanSteerAsync };
    }

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
        // The server starts in the background: the window does not wait for Tailscale.
        _ = Tasks.Run("mobile-start", StartAsync, new PluginTaskOptions { SessionRetentionIndependent = true, Description = "Starts the mobile view." });
        Tasks.Run("mobile-live", FlushAsync, new PluginTaskOptions { LongRunning = true, SessionRetentionIndependent = true, Description = "Pushes the state of the sessions to the phones." });
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
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Reads the settings, starts the server and publishes it: what activation runs in the background.</summary>
    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A plugin turned off before its start ran starts nothing.
            if (_stopped) return;
            _settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            await StartServerAsync(cancellationToken).ConfigureAwait(false);
            if (_server is not null) await PublishAsync(cancellationToken).ConfigureAwait(false);
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
        var valid = settings with
        {
            Token = null,
            Port = IsPort(settings.Port) ? settings.Port : DefaultPort,
            HttpsPort = IsPort(settings.HttpsPort) ? settings.HttpsPort : DefaultHttpsPort,
            Mode = settings.Mode is DevTunnelMode ? DevTunnelMode : TailscaleMode,
        };
        if (valid != settings) await Services.State.WriteJsonAsync(PluginStateScope.User, SettingsName, valid, cancellationToken).ConfigureAwait(false);
        return valid;
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        if (!Services.AppFiles.HasFiles)
        {
            _state = _state with { Server = "failed", ServerDetail = "This application has no page for phones." };
            return;
        }

        var sessions = new MobileSessions(Services.Alta, Services.Spaces, _live);
        var attachments = Path.Combine(Context.Host.UserDataDirectory, "plugin-data", "mobile");
        Directory.CreateDirectory(attachments);
        MobileApi.RemoveStaleAttachments(attachments);
        var server = new MobileServer(new MobileServerOptions
        {
            Port = _settings.Port, Access = _access!, Hosts = () => _state.PublicHost is { } host ? [host] : [],
            Files = Services.AppFiles, Sessions = sessions, Api = new MobileApi(Services.Alta, sessions, attachments),
            Asks = new MobileAsks(Services.Asks, sessions), Permissions = new MobilePermissions(Services.Permissions, sessions), Live = _live, Logger = Logger,
        });
        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            _server = server;
            _state = _state with { Server = "listening", ServerDetail = null };
            Logger.Info($"Mobile: the mobile view listens on http://127.0.0.1:{_settings.Port}");
        }
        catch (Exception exception) when (exception is IOException { InnerException: SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } } or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
        {
            await server.DisposeAsync().ConfigureAwait(false);
            _state = _state with { Server = "port-in-use", ServerDetail = $"Port {_settings.Port} of this computer is used by another program, such as another CodeAlta that serves the mobile view." };
            Logger.Warn($"Mobile: port {_settings.Port} is used by another program.");
        }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            _state = _state with { Server = "failed", ServerDetail = exception.Message };
            Logger.Warn(exception, "Mobile: the mobile view could not start.");
        }
    }

    // Tailscale publishes the server on the tailnet, or a Dev Tunnel on the Internet; without it the view is reachable on this computer only.
    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        if (_settings.Mode == DevTunnelMode)
        {
            await PublishTunnelAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _state = _state with { Tailscale = MobileTailscaleStatus.Pending, DevTunnel = MobileDevTunnelStatus.Unused };
        await ChangedAsync().ConfigureAwait(false);
        MobileTailscaleStatus status;
        try
        {
            status = await new MobileTailscale(_cli).PublishAsync(_settings.Port, _settings.HttpsPort, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            status = new("failed", null, "Tailscale could not be asked: " + exception.Message);
        }

        _state = _state with { Tailscale = status };
        if (status.State == "serving") _published = (_settings.Port, _settings.HttpsPort);
        Logger.Info($"Mobile: {status.Detail}");
    }

    // Makes the Dev Tunnel of the settings serve the port of the server, and watches its host: a host that stops says so.
    private async Task PublishTunnelAsync(CancellationToken cancellationToken)
    {
        _state = _state with { Tailscale = MobileTailscaleStatus.Unused, DevTunnel = MobileDevTunnelStatus.Pending };
        await ChangedAsync().ConfigureAwait(false);
        await StopTunnelAsync(CancellationToken.None).ConfigureAwait(false);
        MobileDevTunnel.Published published;
        try
        {
            published = await new MobileDevTunnel(_tunnelCli, line => Logger.Info(line)).PublishAsync(_settings.Port, _settings.TunnelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            published = new(new MobileDevTunnelStatus("failed", null, "Dev Tunnels could not be asked: " + exception.Message), _settings.TunnelId, null);
        }

        if (published.TunnelId != _settings.TunnelId)
        {
            // The tunnel made now is kept: its address, which phones keep, stays the same at the next start.
            _settings = _settings with { TunnelId = published.TunnelId };
            await Services.State.WriteJsonAsync(PluginStateScope.User, SettingsName, _settings, CancellationToken.None).ConfigureAwait(false);
        }

        _state = _state with { DevTunnel = published.Status };
        Logger.Info($"Mobile: {published.Status.Detail}");
        if (published.Host is not { } host) return;
        _tunnelHost = host;
        _tunnelPort = _settings.Port;
        _ = Tasks.Run("mobile-tunnel", ct => WatchTunnelAsync(host, ct), new PluginTaskOptions { SessionRetentionIndependent = true, Description = "Watches the Dev Tunnel of the mobile view." });
    }

    // Reads what the host writes until it exits. A host that exits while it is the one of now (not stopped by the plugin) is said stopped.
    private async ValueTask WatchTunnelAsync(IMobileHostedProcess host, CancellationToken cancellationToken)
    {
        var last = "";
        try
        {
            await foreach (var line in host.Lines.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(line)) last = line.Trim();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_tunnelHost, host)) return;
            _tunnelHost = null;
            await host.DisposeAsync().ConfigureAwait(false);
            _state = _state with { DevTunnel = new MobileDevTunnelStatus("failed", null, "The Dev Tunnel stopped" + (last.Length > 0 ? ": " + last : ".")) };
            Logger.Warn($"Mobile: the Dev Tunnel stopped: {last}");
        }
        finally
        {
            _gate.Release();
        }

        await ChangedAsync().ConfigureAwait(false);
    }

    // Stops the host of the Dev Tunnel; the tunnel and its address stay in the account, for the next start.
    private async Task StopTunnelAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _tunnelHost, null) is not { } host) return;
        await host.DisposeAsync().ConfigureAwait(false);
        if (_tunnelPort != _settings.Port && _settings.TunnelId is { } tunnelId)
        {
            try { await new MobileDevTunnel(_tunnelCli, line => Logger.Info(line)).RemovePortAsync(tunnelId, _tunnelPort, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                Logger.Debug($"Mobile: the old port of the Dev Tunnel could not be removed: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Starts the sign-in to Dev Tunnels and answers at once: the tab hears, once the person signed in, the view published again. A
    /// sign-in of before that has not ended is dropped.
    /// </summary>
    internal async Task<MobileStatus> SignInAsync(bool github, CancellationToken cancellationToken)
    {
        var run = new CancellationTokenSource();
        if (Interlocked.Exchange(ref _signIn, run) is { } previous) await previous.CancelAsync().ConfigureAwait(false);
        _state = _state with
        {
            DevTunnel = new MobileDevTunnelStatus("signing-in", null, "Sign in in the browser that opens, then come back here."),
        };
        await ChangedAsync().ConfigureAwait(false);
        _ = Tasks.Run("mobile-devtunnel-signin", async _ => await RunSignInAsync(github, run).ConfigureAwait(false),
            new PluginTaskOptions { SessionRetentionIndependent = true, Description = "Signs in to Dev Tunnels." });
        return Status();
    }

    private async Task RunSignInAsync(bool github, CancellationTokenSource run)
    {
        bool signedIn;
        try
        {
            signedIn = await new MobileDevTunnel(_tunnelCli, line => Logger.Info(line)).SignInAsync(github, run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Logger.Warn($"Mobile: the sign-in of Dev Tunnels could not start: {exception.Message}");
            signedIn = false;
        }

        if (run.IsCancellationRequested) return;
        Interlocked.CompareExchange(ref _signIn, null, run);
        if (!signedIn)
        {
            _state = _state with { DevTunnel = new MobileDevTunnelStatus("not-signed-in", null, "The sign-in did not end. Sign in again, or run devtunnel user login in a terminal.") };
            await ChangedAsync().ConfigureAwait(false);
            return;
        }

        Logger.Info("Mobile: signed in to Dev Tunnels.");
        await RetryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // Takes off the tailnet what this instance published, with the ports it was published with, and stops the host of the Dev Tunnel.
    private async Task UnpublishAsync(CancellationToken cancellationToken)
    {
        await StopTunnelAsync(cancellationToken).ConfigureAwait(false);
        if (_published is not { } published) return;
        try
        {
            var outcome = await new MobileTailscale(_cli).UnpublishAsync(published.Port, published.HttpsPort, cancellationToken).ConfigureAwait(false);
            Logger.Info($"Mobile: {outcome}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Logger.Warn($"Mobile: Tailscale could not be asked to stop serving: {exception.Message}");
        }

        _published = null;
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopped = true;
            if (Interlocked.Exchange(ref _signIn, null) is { } signIn) await signIn.CancelAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _server, null) is { } server) await server.DisposeAsync().ConfigureAwait(false);
            // What this instance published on the tailnet goes with it; turning the plugin on again publishes it again.
            await UnpublishAsync(cancellationToken).ConfigureAwait(false);
            _state = MobileState.Stopped;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Asks Tailscale again: it was installed, signed in, or Serve was enabled since.
    private async Task<MobileStatus> RetryAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _signIn, null) is { } signIn) await signIn.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return Status();
            if (_server is null) await StartServerAsync(cancellationToken).ConfigureAwait(false);
            if (_server is not null) await PublishAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Changes the ports: they are saved, the server listens on its new port, and what Tailscale served with the old ports is taken off before
    /// the view is published with the new ones. The server answers at once; Tailscale is asked in the background, and the settings page
    /// hears the outcome with <c>mobile.settings.changed</c>.
    /// </summary>
    /// <returns>The settings now, with <see cref="MobileSettingsView.Error"/> when the ports are refused.</returns>
    internal async Task<MobileSettingsView> SetPortsAsync(MobilePorts request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsPort(request.Port)) return Settings() with { Error = $"The port on this computer must be a number from {MinimumPort} to {MaximumPort}." };
        if (!IsPort(request.HttpsPort)) return Settings() with { Error = $"The port on the tailnet must be a number from {MinimumPort} to {MaximumPort}." };
        if (request.Mode is not (null or TailscaleMode or DevTunnelMode)) return Settings() with { Error = "Phones connect through Tailscale or Dev Tunnels." };
        MobileSettingsView view;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return Settings() with { Error = "The plugin is stopped." };
            var old = _settings;
            var mode = request.Mode ?? old.Mode;
            if (old.Port == request.Port && old.HttpsPort == request.HttpsPort && old.Mode == mode) return Settings();
            _settings = old with { Port = request.Port!.Value, HttpsPort = request.HttpsPort!.Value, Mode = mode };
            await Services.State.WriteJsonAsync(PluginStateScope.User, SettingsName, _settings, CancellationToken.None).ConfigureAwait(false);
            Logger.Info(mode == DevTunnelMode
                ? $"Mobile: phones now connect through Dev Tunnels, to port {_settings.Port} of this computer."
                : $"Mobile: the ports are now {_settings.Port} on this computer and {_settings.HttpsPort} on the tailnet.");
            if (old.Port != _settings.Port || _server is null)
            {
                // The phones that were connected reconnect to the new address once they scan it.
                if (Interlocked.Exchange(ref _server, null) is { } server) await server.DisposeAsync().ConfigureAwait(false);
                _state = _state with { Server = "starting", ServerDetail = null };
                await StartServerAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _state = mode == DevTunnelMode
                ? _state with { Tailscale = MobileTailscaleStatus.Unused, DevTunnel = MobileDevTunnelStatus.Pending }
                : _state with { Tailscale = MobileTailscaleStatus.Pending, DevTunnel = MobileDevTunnelStatus.Unused };
            // The answer is the state of the change itself, read before Tailscale or Dev Tunnels are asked: one that answers at once would
            // otherwise give the outcome of the publication to some calls and not to others.
            view = Settings();
        }
        finally
        {
            _gate.Release();
        }

        _ = Tasks.Run("mobile-publish", RepublishAsync, new PluginTaskOptions { SessionRetentionIndependent = true, Description = "Publishes the mobile view on its new port." });
        await ChangedAsync().ConfigureAwait(false);
        return view;
    }

    // Takes off what was published with the old ports, then publishes with the ports of now. Two changes in a row each run it: the second finds the
    // view published with the ports of the first, or nothing published, and ends on the ports of now.
    private async ValueTask RepublishAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            // What the other mode published goes first: the tailnet or the tunnel, not both.
            if (_settings.Mode == DevTunnelMode)
            {
                if (_published is not null) await UnpublishAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await StopTunnelAsync(cancellationToken).ConfigureAwait(false);
                if (_published is { } published && published != (_settings.Port, _settings.HttpsPort)) await UnpublishAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_server is not null) await PublishAsync(cancellationToken).ConfigureAwait(false);
            else _state = _state with { Tailscale = new MobileTailscaleStatus("failed", null, "The mobile view does not listen on this computer, so Tailscale does not publish it.") };
        }
        finally
        {
            _gate.Release();
        }

        await ChangedAsync().ConfigureAwait(false);
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
                _live.Flush(await ReadAskingAsync(cancellationToken).ConfigureAwait(false), await ReadApprovingAsync(cancellationToken).ConfigureAwait(false));
                if (++ticks % 60 == 0) _live.Broadcast("ping", "{}");
            }
        }
        catch (OperationCanceledException)
        {
            // The plugin stops.
        }
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
        return new MobileStatus(state.Server, _settings.Port, state.ServerDetail,
            new MobileTailscaleView(state.Tailscale.State, state.Tailscale.Url, state.Tailscale.Detail, state.Tailscale.FixUrl),
            connect, connect is null ? null : url, connect is null ? null : MobileQr.Rows(connect))
        {
            Mode = _settings.Mode,
            DevTunnel = new MobileDevTunnelView(state.DevTunnel.State, state.DevTunnel.Url, state.DevTunnel.Detail, state.DevTunnel.FixUrl),
        };
    }

    /// <summary>What the settings page shows: the ports, and the address while the view is served.</summary>
    internal MobileSettingsView Settings()
    {
        var state = _state;
        var settings = _settings;
        return new MobileSettingsView(settings.Port, settings.HttpsPort, ServedUrl(state), state.Server, state.ServerDetail, new MobileSettingsTailscale(state.Tailscale.State, state.Tailscale.Detail))
        {
            Mode = settings.Mode, DevTunnel = new MobileSettingsTailscale(state.DevTunnel.State, state.DevTunnel.Detail),
        };
    }

    // The address phones open while the view is served: the one of the tailnet, or of the Dev Tunnel.
    private string? ServedUrl(MobileState state)
        => state.Server != "listening" ? null
            : _settings.Mode == DevTunnelMode ? (state.DevTunnel.State == "serving" ? state.DevTunnel.Url : null)
            : state.Tailscale.State == "serving" ? state.Tailscale.Url : null;

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
                var state when ServedUrl(state) is not null => new PluginButtonState
                {
                    Tone = PluginStatusTone.Success, Tooltip = _settings.Mode == DevTunnelMode ? "Mobile: served through Dev Tunnels" : "Mobile: served on the tailnet",
                },
                { Server: "starting" } or { Tailscale.State: "pending" } or { DevTunnel.State: "pending" or "signing-in" } => new PluginButtonState { Badge = PluginButtonBadge.Busy, Tooltip = "Mobile: starting" },
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
                // Dev Tunnels asks for an account once: the tab signs in with Microsoft or GitHub, in the browser of this computer.
                canvas.Rpc.Handle<MobileSignIn, MobileStatus>("mobile.devtunnel.signin", async (request, ct) =>
                    await PairedAsync(() => SignInAsync(request.Github, ct), ct).ConfigureAwait(false));
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
                canvas.Rpc.Handle<MobilePorts, MobileSettingsView>("mobile.settings.set", async (request, ct) => await SetPortsAsync(request, ct).ConfigureAwait(false));
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
        var tunnel = _settings.Mode == DevTunnelMode;
        var text = new StringBuilder(tunnel
            ? "# Mobile\n\nThe sessions of this computer on a phone: a page served on 127.0.0.1 and published through a Dev Tunnel of Microsoft.\n\n"
            : "# Mobile\n\nThe sessions of this computer on a phone: a page served on 127.0.0.1 and published on the tailnet with Tailscale Serve.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"- Server: {state.Server} on 127.0.0.1:{_settings.Port}{(state.ServerDetail is { } detail ? $" ({detail})" : "")}\n");
        if (tunnel) text.Append(CultureInfo.InvariantCulture, $"- Dev Tunnel: {state.DevTunnel.State}: {state.DevTunnel.Detail}\n");
        else text.Append(CultureInfo.InvariantCulture, $"- Tailscale: {state.Tailscale.State}: {state.Tailscale.Detail}\n");
        if (ServedUrl(state) is { } url) text.Append(CultureInfo.InvariantCulture, $"- Address: {url} (the QR code adds the access token)\n");
        return text.ToString();
    }

    /// <summary>What the plugin serves: its server, and what Tailscale does with it.</summary>
    /// <param name="Server"><c>starting</c>, <c>listening</c>, <c>port-in-use</c>, <c>failed</c> or <c>stopped</c>.</param>
    /// <param name="ServerDetail">Why the server does not listen.</param>
    /// <param name="Tailscale">What Tailscale does.</param>
    /// <param name="DevTunnel">What the Dev Tunnel does.</param>
    internal sealed record MobileState(string Server, string? ServerDetail, MobileTailscaleStatus Tailscale, MobileDevTunnelStatus DevTunnel)
    {
        internal static MobileState Starting { get; } = new("starting", null, MobileTailscaleStatus.Pending, MobileDevTunnelStatus.Unused);

        internal static MobileState Stopped { get; } = new("stopped", null, MobileTailscaleStatus.Stopped, MobileDevTunnelStatus.Stopped);

        /// <summary>
        /// The host the requests that come from outside name: the name of this computer on the tailnet, or the one of the Dev Tunnel, with
        /// the port. A tunnel that names the loopback address instead is let in by the server as a request of this computer.
        /// </summary>
        internal string? PublicHost => (DevTunnel.Url ?? Tailscale.Url) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : null;
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

    /// <summary>Gets the port of the server on the loopback address.</summary>
    public int Port { get; init; } = MobilePlugin.DefaultPort;

    /// <summary>Gets the HTTPS port of this computer on the tailnet.</summary>
    public int HttpsPort { get; init; } = MobilePlugin.DefaultHttpsPort;

    /// <summary>Gets how phones connect: <see cref="MobilePlugin.TailscaleMode"/> or <see cref="MobilePlugin.DevTunnelMode"/>.</summary>
    public string Mode { get; init; } = MobilePlugin.TailscaleMode;

    /// <summary>Gets the Dev Tunnel the plugin made, kept so that its address does not change; null until one is made.</summary>
    public string? TunnelId { get; init; }
}

/// <summary>How the tab signs in to Dev Tunnels.</summary>
/// <param name="Github">Whether with a GitHub account rather than a Microsoft one.</param>
internal sealed record MobileSignIn(bool Github);

/// <summary>The input of a call that takes none.</summary>
internal sealed record MobileEmpty;

/// <summary>What Tailscale does, as the Mobile tab shows it.</summary>
internal sealed record MobileTailscaleView(string State, string? Url, string Detail, string? FixUrl);

/// <summary>What the Mobile tab shows.</summary>
/// <param name="Server">What the server does.</param>
/// <param name="Port">Its port on the loopback address.</param>
/// <param name="ServerDetail">Why it does not listen.</param>
/// <param name="Tailscale">What Tailscale does.</param>
/// <param name="ConnectUrl">The address a phone opens, with the access token; null while no phone can connect.</param>
/// <param name="Address">The same without the token.</param>
/// <param name="Qr">The QR code of <paramref name="ConnectUrl"/>, one string of <c>0</c> and <c>1</c> per row.</param>
internal sealed record MobileStatus(string Server, int Port, string? ServerDetail, MobileTailscaleView Tailscale, string? ConnectUrl, string? Address, IReadOnlyList<string>? Qr)
{
    /// <summary>Gets how phones connect: <c>tailscale</c> or <c>devtunnel</c>.</summary>
    public string Mode { get; init; } = MobilePlugin.TailscaleMode;

    /// <summary>Gets what the Dev Tunnel does.</summary>
    public MobileDevTunnelView DevTunnel { get; init; } = new("unused", null, "", null);
}

/// <summary>What the Dev Tunnel does, as the Mobile tab shows it.</summary>
/// <param name="State">The state, as <see cref="MobileDevTunnelStatus.State"/> says it.</param>
/// <param name="Url">The address of the view, while the tunnel serves it.</param>
/// <param name="Detail">A line for people.</param>
/// <param name="FixUrl">The page that fixes the state: the install of the command line.</param>
internal sealed record MobileDevTunnelView(string State, string? Url, string Detail, string? FixUrl);

/// <summary>The ports the settings page asks for.</summary>
/// <param name="Port">The port of the server on the loopback address.</param>
/// <param name="HttpsPort">The HTTPS port of this computer on the tailnet.</param>
internal sealed record MobilePorts(int? Port, int? HttpsPort)
{
    /// <summary>Gets how phones connect: <c>tailscale</c> or <c>devtunnel</c>; null keeps the mode of now.</summary>
    public string? Mode { get; init; }
}

/// <summary>What Tailscale does, as the settings page shows it.</summary>
/// <param name="State">The state, as <see cref="MobileTailscaleStatus.State"/> says it.</param>
/// <param name="Detail">A line for people.</param>
internal sealed record MobileSettingsTailscale(string State, string Detail);

/// <summary>What the settings page shows.</summary>
/// <param name="Port">The port of the server on the loopback address.</param>
/// <param name="HttpsPort">The HTTPS port of this computer on the tailnet.</param>
/// <param name="Address">The address of the view on the tailnet while it is served, without the access token; null otherwise.</param>
/// <param name="Server">What the server does: <c>starting</c>, <c>listening</c>, <c>port-in-use</c>, <c>failed</c> or <c>stopped</c>.</param>
/// <param name="ServerDetail">Why it does not listen.</param>
/// <param name="Tailscale">What Tailscale does.</param>
internal sealed record MobileSettingsView(int Port, int HttpsPort, string? Address, string Server, string? ServerDetail, MobileSettingsTailscale Tailscale)
{
    /// <summary>Gets why the settings that were asked for are refused, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Gets how phones connect: <c>tailscale</c> or <c>devtunnel</c>.</summary>
    public string Mode { get; init; } = MobilePlugin.TailscaleMode;

    /// <summary>Gets what the Dev Tunnel does.</summary>
    public MobileSettingsTailscale DevTunnel { get; init; } = new("unused", "");
}

[System.Text.Json.Serialization.JsonSerializable(typeof(MobileEmpty))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileSettings))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileAccessFile))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobilePorts))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileSignIn))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileSettingsView))]
internal sealed partial class MobileJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
