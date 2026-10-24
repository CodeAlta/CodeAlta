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
/// loopback address, publishes it on the tailnet with Tailscale Serve on an HTTPS port of its own, and shows the address as
/// a QR code in the Mobile tab. It runs while it is turned on in Settings &gt; Plugins, which is off until the user turns it on; its
/// ports and the access of the phones are on its settings page, Settings &gt; Extensions &gt; Mobile.
/// </summary>
[Plugin("mobile", DisplayName = "Mobile", Description = "The sessions of this computer on a phone, through Tailscale.", Frontends = PluginFrontends.Desktop)]
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

    /// <summary>The lowest port the settings page accepts: the ones below are the system's.</summary>
    internal const int MinimumPort = 1024;

    /// <summary>The highest port.</summary>
    internal const int MaximumPort = 65535;

    private const string SettingsName = "mobile.json";
    private static readonly TimeSpan FlushPeriod = TimeSpan.FromMilliseconds(250);

    private readonly IMobileTailscaleCli _cli;
    private readonly MobileLive _live;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _canvases = new();
    private readonly List<IPluginCanvasRpc> _open = [];
    private readonly List<IPluginCanvasRpc> _settingsPages = [];
    private volatile MobileSettings _settings = new();
    private MobileServer? _server;
    private volatile MobileState _state = MobileState.Starting;
    // The ports Tailscale serves the view with now, as this instance published them: what is taken off when the ports change or the plugin stops.
    private (int Port, int HttpsPort)? _published;
    private bool _stopped;

    /// <summary>Creates the plugin with the Tailscale command line of this computer.</summary>
    public MobilePlugin()
        : this(new MobileTailscaleCli())
    {
    }

    /// <summary>Creates the plugin with a Tailscale command line of the tests.</summary>
    internal MobilePlugin(IMobileTailscaleCli cli)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _live = new MobileLive { SteerProbe = CanSteerAsync };
    }

    /// <summary>Gets what the plugin serves now.</summary>
    internal MobileState State => _state;

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

    // The token is made at the first start and kept: a phone that scanned the code once stays signed in.
    private async Task<MobileSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await Services.State.ReadJsonAsync<MobileSettings>(PluginStateScope.User, SettingsName, cancellationToken).ConfigureAwait(false) ?? new MobileSettings();
        var valid = settings with
        {
            Token = MobileToken.IsValid(settings.Token) ? settings.Token : MobileToken.Create(),
            Port = IsPort(settings.Port) ? settings.Port : DefaultPort,
            HttpsPort = IsPort(settings.HttpsPort) ? settings.HttpsPort : DefaultHttpsPort,
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
        var server = new MobileServer(new MobileServerOptions
        {
            Port = _settings.Port, Token = () => _settings.Token!, Hosts = () => _state.TailnetHost is { } host ? [host] : [],
            Files = Services.AppFiles, Sessions = sessions, Api = new MobileApi(Services.Alta, sessions), Live = _live, Logger = Logger,
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

    // Tailscale publishes the server on the tailnet; without it the view is reachable on this computer only.
    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        _state = _state with { Tailscale = MobileTailscaleStatus.Pending };
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

    // Takes off the tailnet what this instance published, with the ports it was published with.
    private async Task UnpublishAsync(CancellationToken cancellationToken)
    {
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

    // A new token: every phone has to scan the new code. The streams of the phones end, and they cannot sign in again.
    internal async Task<MobileSettingsView> ResetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _settings = _settings with { Token = MobileToken.Create() };
            await Services.State.WriteJsonAsync(PluginStateScope.User, SettingsName, _settings, cancellationToken).ConfigureAwait(false);
            _server?.Disconnect();
            Logger.Info("Mobile: the access token was reset; the phones have to scan the new code.");
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
        var republish = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return Settings() with { Error = "The plugin is stopped." };
            var old = _settings;
            if (old.Port == request.Port && old.HttpsPort == request.HttpsPort) return Settings();
            _settings = old with { Port = request.Port!.Value, HttpsPort = request.HttpsPort!.Value };
            await Services.State.WriteJsonAsync(PluginStateScope.User, SettingsName, _settings, CancellationToken.None).ConfigureAwait(false);
            Logger.Info($"Mobile: the ports are now {_settings.Port} on this computer and {_settings.HttpsPort} on the tailnet.");
            if (old.Port != _settings.Port || _server is null)
            {
                // The phones that were connected reconnect to the new address once they scan it.
                if (Interlocked.Exchange(ref _server, null) is { } server) await server.DisposeAsync().ConfigureAwait(false);
                _state = _state with { Server = "starting", ServerDetail = null };
                await StartServerAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _state = _state with { Tailscale = MobileTailscaleStatus.Pending };
            republish = true;
        }
        finally
        {
            _gate.Release();
        }

        if (republish) _ = Tasks.Run("mobile-publish", RepublishAsync, new PluginTaskOptions { SessionRetentionIndependent = true, Description = "Publishes the mobile view on its new port." });
        await ChangedAsync().ConfigureAwait(false);
        return Settings();
    }

    // Takes off what was published with the old ports, then publishes with the ports of now. Two changes in a row each run it: the second finds the
    // view published with the ports of the first, or nothing published, and ends on the ports of now.
    private async ValueTask RepublishAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            if (_published is { } published && published != (_settings.Port, _settings.HttpsPort)) await UnpublishAsync(cancellationToken).ConfigureAwait(false);
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
                _live.Flush();
                if (++ticks % 60 == 0) _live.Broadcast("ping", "{}");
            }
        }
        catch (OperationCanceledException)
        {
            // The plugin stops.
        }
    }

    private async Task<bool> CanSteerAsync(string sessionId)
    {
        var result = await MobileAlta.RunAsync(Services.Alta, ["session", "status", sessionId], null, CancellationToken.None).ConfigureAwait(false);
        return result.OfType("alta.session.status").Any(static status => MobileAlta.Flag(status, "isRunning"));
    }

    /// <summary>What the Mobile tab shows.</summary>
    internal MobileStatus Status()
    {
        var state = _state;
        var url = state.Tailscale.Url;
        var connect = state.Server == "listening" && url is not null && _settings.Token is { } token ? $"{url}/#k={token}" : null;
        return new MobileStatus(state.Server, _settings.Port, state.ServerDetail,
            new MobileTailscaleView(state.Tailscale.State, url, state.Tailscale.Detail, state.Tailscale.FixUrl),
            connect, connect is null ? null : url, connect is null ? null : MobileQr.Rows(connect));
    }

    /// <summary>What the settings page shows: the ports, and the address while the view is served.</summary>
    internal MobileSettingsView Settings()
    {
        var state = _state;
        var settings = _settings;
        var address = state is { Server: "listening", Tailscale.State: "serving" } ? state.Tailscale.Url : null;
        return new MobileSettingsView(settings.Port, settings.HttpsPort, address, state.Server, state.ServerDetail, new MobileSettingsTailscale(state.Tailscale.State, state.Tailscale.Detail));
    }

    private async Task ChangedAsync()
    {
        Services.Ui.InvalidateButtons();
        IPluginCanvasRpc[] tabs, pages;
        lock (_canvases) (tabs, pages) = ([.. _open], [.. _settingsPages]);
        if (tabs.Length > 0) await PublishAsync(tabs, "mobile.changed", Status()).ConfigureAwait(false);
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
                { Server: "listening", Tailscale.State: "serving" } => new PluginButtonState { Tone = PluginStatusTone.Success, Tooltip = "Mobile: served on the tailnet" },
                { Server: "starting" } or { Tailscale.State: "pending" } => new PluginButtonState { Badge = PluginButtonBadge.Busy, Tooltip = "Mobile: starting" },
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
            SearchText = "mobile phone qr tailscale connect",
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
                canvas.Rpc.Handle<MobileEmpty, MobileStatus>("mobile.status", (_, _) => ValueTask.FromResult(Status()));
                canvas.Rpc.Handle<MobileEmpty, MobileStatus>("mobile.retry", async (_, ct) => await RetryAsync(ct).ConfigureAwait(false));
                Track(canvas, _open);
                return ValueTask.FromResult(new PluginCanvasView { Script = PluginScript.App(AppModule) });
            },
            Describe = (_, _) => ValueTask.FromResult<string?>(DescribeTab()),
        };
        yield return new PluginCanvasContribution
        {
            Id = SettingsCanvasId,
            Title = "Mobile",
            Description = "The ports the mobile view is served on, and the access of the phones.",
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
        var text = new StringBuilder("# Mobile\n\nThe sessions of this computer on a phone: a page served on 127.0.0.1 and published on the tailnet with Tailscale Serve.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"- Server: {state.Server} on 127.0.0.1:{_settings.Port}{(state.ServerDetail is { } detail ? $" ({detail})" : "")}\n");
        text.Append(CultureInfo.InvariantCulture, $"- Tailscale: {state.Tailscale.State}: {state.Tailscale.Detail}\n");
        if (state.Tailscale.Url is { } url) text.Append(CultureInfo.InvariantCulture, $"- Address: {url} (the QR code adds the access token)\n");
        return text.ToString();
    }

    /// <summary>What the plugin serves: its server, and what Tailscale does with it.</summary>
    /// <param name="Server"><c>starting</c>, <c>listening</c>, <c>port-in-use</c>, <c>failed</c> or <c>stopped</c>.</param>
    /// <param name="ServerDetail">Why the server does not listen.</param>
    /// <param name="Tailscale">What Tailscale does.</param>
    internal sealed record MobileState(string Server, string? ServerDetail, MobileTailscaleStatus Tailscale)
    {
        internal static MobileState Starting { get; } = new("starting", null, MobileTailscaleStatus.Pending);

        internal static MobileState Stopped { get; } = new("stopped", null, MobileTailscaleStatus.Stopped);

        /// <summary>The host the requests that come through Tailscale name: the name of this computer on the tailnet, with the HTTPS port.</summary>
        internal string? TailnetHost => Tailscale.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : null;
    }
}

/// <summary>What the plugin keeps between starts, in its state of the user.</summary>
internal sealed record MobileSettings
{
    /// <summary>Gets the access token of the phones.</summary>
    public string? Token { get; init; }

    /// <summary>Gets the port of the server on the loopback address.</summary>
    public int Port { get; init; } = MobilePlugin.DefaultPort;

    /// <summary>Gets the HTTPS port of this computer on the tailnet.</summary>
    public int HttpsPort { get; init; } = MobilePlugin.DefaultHttpsPort;
}

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
internal sealed record MobileStatus(string Server, int Port, string? ServerDetail, MobileTailscaleView Tailscale, string? ConnectUrl, string? Address, IReadOnlyList<string>? Qr);

/// <summary>The ports the settings page asks for.</summary>
/// <param name="Port">The port of the server on the loopback address.</param>
/// <param name="HttpsPort">The HTTPS port of this computer on the tailnet.</param>
internal sealed record MobilePorts(int? Port, int? HttpsPort);

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
    /// <summary>Gets why the ports that were asked for are refused, or null.</summary>
    public string? Error { get; init; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(MobileEmpty))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileSettings))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobilePorts))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MobileSettingsView))]
internal sealed partial class MobileJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
