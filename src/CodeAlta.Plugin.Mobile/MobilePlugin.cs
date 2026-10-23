using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;
using QRCoder;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// Serves a mobile view of CodeAlta (the recent sessions) from an in-process server on the loopback address,
/// publishes it on the tailnet with Tailscale Serve when Tailscale runs, and shows the address as a QR code.
/// </summary>
[Plugin(DisplayName = "Mobile", Description = "A mobile view of the sessions, served on this computer and on your tailnet, with a QR code to connect.", Frontends = PluginFrontends.Desktop)]
public sealed class MobilePlugin : PluginBase
{
    internal const string CanvasId = "mobile";
    internal const string CommandName = "mobile";
    internal const int DefaultPort = 2584;
    internal const int PageSize = 6;
    internal const string QrFileName = "codealta-mobile-qr.png";

    private readonly MobileLive _live;

    /// <summary>Creates the plugin; its live state asks alta whether a busy session can be steered.</summary>
    public MobilePlugin() => _live = new MobileLive { SteerProbe = CanSteerAsync };

    private async Task<bool> CanSteerAsync(string sessionId)
    {
        var result = await Services.Alta.InvokeAsync(["session", "status", sessionId]).ConfigureAwait(false);
        foreach (var line in result.TranscriptJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.Contains("\"alta.session.status\"", StringComparison.Ordinal) && JsonNode.Parse(line)?["isRunning"] is JsonValue v && v.TryGetValue<bool>(out var running))
                return running;
        return false;
    }
    private MobileServer? _server;
    private bool _startFailed;
    private PluginTaskHandle? _job;
    private string? _token;
    private volatile MobileTailscaleStatus _tailscale = MobileTailscaleStatus.Pending;
    private volatile string _serverState = "starting";

    /// <summary>The file the QR code of the address is written to.</summary>
    public static string QrPngPath => Path.Combine(Path.GetTempPath(), QrFileName);

    internal int Port { get; private set; } = DefaultPort;

    /// <summary>The address a phone opens, without the token.</summary>
    internal string BaseUrl => _tailscale.Url ?? $"http://127.0.0.1:{Port}";

    public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
    {
        if (Context.Host.Frontend != PluginFrontends.Desktop) return ValueTask.CompletedTask;
        _job = Tasks.Run("mobile-server", RunAsync,
            new PluginTaskOptions { LongRunning = true, SessionRetentionIndependent = true, Description = "Serves the mobile view of CodeAlta." });
        return ValueTask.CompletedTask;
    }

    public override async ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default) => await StopAsync().ConfigureAwait(false);

    public override async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = await Services.State.ReadJsonAsync<MobileConfig>(PluginStateScope.User, "mobile.json", cancellationToken).ConfigureAwait(false) ?? new MobileConfig();
            if (!MobileToken.IsValid(config.Token) || !(config.Port is > 0 and < 65536))
            {
                config = config with { Token = MobileToken.IsValid(config.Token) ? config.Token : MobileToken.Create(), Port = config.Port is > 0 and < 65536 ? config.Port : DefaultPort };
                await Services.State.WriteJsonAsync(PluginStateScope.User, "mobile.json", config, cancellationToken).ConfigureAwait(false);
            }

            if (int.TryParse(Environment.GetEnvironmentVariable("CODEALTA_MOBILE_PORT"), out var envPort) && envPort is > 0 and < 65536) config = config with { Port = envPort };
            _token = config.Token!;
            Port = config.Port;

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _serverState = "failed: " + exception.Message;
            Logger.Warn(exception, "The mobile view could not start.");
            return;
        }

        // Turning the plugin off in Settings > Plugins saves `enabled = false` in [plugins.mobile] of the global
        // configuration, which the host applies to a built-in plugin only at the next start: the plugin reads it every
        // 2 s and stops (or starts again) its server and Tailscale Serve at once.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var ticks = 0;
        try
        {
            do
            {
                if (ticks % 8 == 0)
                {
                    var enabled = MobileSpaces.Enabled();
                    if (!enabled) _startFailed = false;   // turning it on again tries again
                    if (enabled && _server is null && !_startFailed) await StartServingAsync(cancellationToken).ConfigureAwait(false);
                    else if (!enabled && _server is not null)
                    {
                        Logger.Info("Mobile: turned off in Settings; stopping the server and Tailscale Serve.");
                        await StopAsync().ConfigureAwait(false);
                        _serverState = "off";
                        await RefreshAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                // Push what changed four times a second, and a comment every 15 s so proxies keep the streams open.
                _live.Flush();
                if (++ticks % 60 == 0) _live.Broadcast("ping", "{}");
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task StartServingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var server = new MobileServer(Port, _token!, ReadSessionsAsync, Snapshot, _live, Logger) { SessionEvents = ReadSessionEventsAsync, Api = new MobileApi(Services.Alta) };
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            _server = server;
            _serverState = "listening";
            Logger.Info($"The mobile view listens on http://127.0.0.1:{Port}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _serverState = "failed: " + exception.Message;
            _startFailed = true;
            Logger.Warn(exception, "The mobile view could not start.");
            return;
        }

        try
        {
            _tailscale = await MobileTailscale.ServeAsync(Port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _tailscale = new("failed", null, "Tailscale could not be asked: " + exception.Message);
        }

        Logger.Info($"Mobile: {_tailscale.Detail}");
        // A simulated failure (CODEALTA_MOBILE_SIMULATE_TAILSCALE) leaves the checks the page shows (not the QR, which
        // carries the token) where a test reads them.
        if (Environment.GetEnvironmentVariable("CODEALTA_MOBILE_SIMULATE_TAILSCALE") is { Length: > 0 } simulated)
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), $"codealta-mobile-canvas-{simulated}.html"), (TailscaleFix() ?? "") + "<p class=\"alta-muted\">" + WebUtility.HtmlEncode(_tailscale.Detail) + "</p>"); } catch (IOException) { }
        WriteQrPng();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Services.Ui.InvalidateButtons();
        try { await Services.Canvases.InvalidateAsync(CanvasId, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException) { Logger.Debug($"Mobile canvas refresh: {exception.Message}"); }
    }

    /// <inheritdoc />
    /// <remarks>Deltas are observed: the dot turns blue while reasoning streams, and the line follows the text as it is written.</remarks>
    public override bool ObservesContentDeltas => true;

    /// <inheritdoc />
    public override ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _live.Observe(context.Event);
        return ValueTask.CompletedTask;
    }

    private async ValueTask StopAsync()
    {
        var server = Interlocked.Exchange(ref _server, null);
        if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
        _serverState = "stopped";
        // What this instance published on the tailnet goes with it; turning the plugin on again serves it again.
        if (_tailscale.State == "serving")
        {
            _tailscale = new("stopped", null, await MobileTailscale.UnserveAsync().ConfigureAwait(false));
            Logger.Info($"Mobile: {_tailscale.Detail}");
        }
    }

    private object Snapshot() => new { server = _serverState, port = Port, tailscale = _tailscale.State, url = _tailscale.Url, detail = _tailscale.Detail };

    /// <summary>
    /// The timeline of a session after a sequence number, from the session journal the desktop timeline is built from,
    /// read with <c>alta session events</c> a page at a time. Tool output is left out: the phone shows the calls.
    /// </summary>
    private async Task<JsonElement> ReadSessionEventsAsync(string sessionId, long since, CancellationToken cancellationToken)
    {
        var events = new JsonArray();
        string? error = null;
        for (var page = 0; page < 40; page++)
        {
            var result = await Services.Alta.InvokeAsync(["session", "events", sessionId, "--since", since.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--limit", "400", "--no-tool-output", "--include", "user,assistant,reasoning,tool,host",
                "--fields", "sequenceNumber,timestamp,kind,content,metadata"], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) { error = result.Error ?? "The session could not be read."; break; }
            var truncated = false;
            var count = 0;
            foreach (var line in result.TranscriptJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (JsonException) { continue; }
                if (node is not JsonObject record) continue;
                var type = (string?)record["type"];
                if (type == "alta.session.events.summary") { truncated = record["truncated"] is JsonValue t && t.TryGetValue<bool>(out var tr) && tr; continue; }
                if (type == "alta.result" && record["truncated"] is JsonValue rt && rt.TryGetValue<bool>(out var r) && r) truncated = true;
                if (type != "alta.session.event") continue;
                var sequence = record["sequenceNumber"] is JsonValue sv && sv.TryGetValue<long>(out var seq) ? seq : 0;
                if (sequence > since) since = sequence;
                count++;
                var kind = (string?)record["kind"] ?? "";
                if (kind is "host.usageupdated" or "host.modelchanged") continue;
                var text = string.Concat((record["content"] as JsonArray ?? []).Select(static part => (string?)part?["text"] ?? ""));
                if (text.Length > 20000) text = text[..20000] + "…";
                events.Add(new JsonObject
                {
                    ["seq"] = sequence, ["at"] = (string?)record["timestamp"], ["kind"] = kind, ["text"] = text,
                    ["activityId"] = (string?)record["metadata"]?["activityId"],
                });
            }

            if (!truncated || count == 0) break;
        }

        return JsonSerializer.SerializeToElement(new JsonObject { ["since"] = since, ["events"] = events, ["error"] = error });
    }

    /// <summary>The six sessions of the latest activity, read with <c>alta session list</c>.</summary>
    private async Task<JsonElement> ReadSessionsAsync(int offset, string? project, CancellationToken cancellationToken)
    {
        // The spaces the plugin is turned on in (Settings > Plugins) say which sessions the phone shows: those of every
        // space when it is in all of them, else the union of `alta session list --space` of each of its spaces.
        var spaces = MobileSpaces.Read();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sessions = new List<JsonObject>();
        IEnumerable<string?> scopes = spaces is null ? new string?[] { null } : spaces.Cast<string?>();
        var exitCode = 0; string? listError = null;
        foreach (var space in scopes)
        {
        List<string> args = ["session", "list", "--state", "all", "--limit", "500"];
        if (!string.IsNullOrWhiteSpace(project)) args.AddRange(["--project", project]);
        if (space is not null) args.AddRange(["--space", space]);
        var result = await Services.Alta.InvokeAsync(args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) { exitCode = result.ExitCode; listError ??= result.Error; }
        foreach (var line in result.TranscriptJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            if (node is not JsonObject record) continue;
            var kind = (string?)record["type"] ?? (string?)record["kind"] ?? (string?)record["record"];
            if (kind is not null && kind.StartsWith("alta.", StringComparison.Ordinal) && !kind.Contains("session", StringComparison.OrdinalIgnoreCase)) continue;
            if (record["sessionId"] is null && record["id"] is null) continue;
            // Archive on the phone archives a session (alta session archive): an archived session leaves the list, as on the desktop.
            if (string.Equals(Text(record, "status", "state"), "archived", StringComparison.OrdinalIgnoreCase) || record["archived"] is JsonValue a && a.TryGetValue<bool>(out var archived) && archived) continue;
            if (seen.Add(Text(record, "sessionId", "id") ?? "")) sessions.Add(record);
        }
        }

        // A page of six, newest first; the phone asks for the next one with its offset.
        var ordered = sessions.OrderByDescending(LastActivity).ToList();
        var recent = ordered.Skip(Math.Max(0, offset)).Take(PageSize).ToList();
        var array = new JsonArray([.. recent.Select(record => (JsonNode)new JsonObject
        {
            ["id"] = Text(record, "sessionId", "id"),
            ["title"] = Text(record, "title", "name", "summary"),
            // A session item names its project by id; the folder it works in is the name people know.
            ["project"] = Text(record, "projectName") ?? FolderName(Text(record, "workingDirectory")) ?? Text(record, "projectRef", "projectId"),
            ["status"] = Text(record, "state", "status"),
            ["busy"] = record["isRunning"]?.DeepClone() ?? record["busy"]?.DeepClone(),
            ["summary"] = Text(record, "latestSummary"),
            ["live"] = _live.Describe(Text(record, "sessionId", "id") ?? "")?.DeepClone() is JsonObject live
                ? live
                : new JsonObject { ["live"] = record["isRunning"] is JsonValue running && running.TryGetValue<bool>(out var isRunning) && isRunning ? MobileLive.Running : MobileLive.Idle },
            ["lastActivity"] = LastActivity(record) == DateTimeOffset.MinValue ? null : LastActivity(record).ToString("O"),
        })]);
        var payload = new JsonObject
        {
            ["exitCode"] = exitCode, ["error"] = listError, ["spaces"] = spaces is null ? null : new JsonArray([.. spaces.Select(static s => (JsonNode)s)]), ["sessions"] = array,
            ["machine"] = Environment.MachineName, ["offset"] = offset, ["next"] = offset + recent.Count, ["hasMore"] = ordered.Count > offset + recent.Count, ["project"] = project,
        };
        return JsonSerializer.SerializeToElement(payload);
    }

    private static string? Text(JsonObject record, params string[] names)
    {
        foreach (var name in names)
        {
            if (record[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) return text;
            if (record[name] is JsonObject inner && Text(inner, "name", "title", "id") is { } nested) return nested;
        }

        return null;
    }

    private static string? FolderName(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : null;

    private static DateTimeOffset LastActivity(JsonObject record)
    {
        foreach (var name in new[] { "lastActiveAt", "lastActivityAt", "lastActivity", "updatedAt", "lastUpdatedAt", "modifiedAt", "lastTurnAt", "createdAt" })
            if (Text(record, name) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                return at;
        return DateTimeOffset.MinValue;
    }

    private string ConnectUrl => $"{BaseUrl}/?t={_token}";

    private void WriteQrPng()
    {
        if (_token is null) return;
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(ConnectUrl, QRCodeGenerator.ECCLevel.H);
            File.WriteAllBytes(QrPngPath, MobileQr.Png(data.ModuleMatrix.Select(static row => row.Cast<bool>().ToArray()).ToArray(), 12));
            Logger.Info($"Mobile: QR code written to {QrPngPath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Mobile: the QR code could not be written: {exception.Message}");
        }
    }

    private string Matrix()
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(ConnectUrl, QRCodeGenerator.ECCLevel.H);
        return string.Join('|', data.ModuleMatrix.Select(static row => string.Concat(row.Cast<bool>().Select(static on => on ? '1' : '0'))));
    }

    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        yield return PluginUi.Button(PluginButtonPlace.TitleBar, "mobile", "smartphone", "Connect a phone") with
        {
            Canvas = CanvasId,
            GetState = _ => new PluginButtonState
            {
                Tone = _tailscale.State == "serving" ? PluginStatusTone.Success : PluginStatusTone.Info,
                Tooltip = _serverState == "listening" ? $"Mobile view: {BaseUrl}" : $"Mobile view: {_serverState}",
            },
        };
    }

    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell(CommandName, "Shows the QR code a phone opens the mobile view of CodeAlta with.", async (context, cancellationToken) =>
        {
            await Services.Canvases.OpenAsync(CanvasId, null, cancellationToken).ConfigureAwait(false);
            return PluginCommandResult.Handled;
        }) with
        {
            Label = "Mobile: connect a phone",
            SearchText = "mobile phone qr tailscale connect",
            KeyBinding = new PluginKeyBinding(new PluginKeyGesture('G', PluginKeyModifiers.Ctrl), new PluginKeyGesture('M')),
        };
    }

    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = CanvasId,
            Title = "Mobile",
            Description = "The QR code and address a phone opens the mobile view of CodeAlta with, and the state of the server and of Tailscale.",
            Icon = "smartphone",
            Scope = PluginCanvasScope.Application,
            Open = (_, _) => ValueTask.FromResult(PluginCanvasView.Rendered((_, _) => ValueTask.FromResult(Render())) with
            {
                Title = "Mobile",
                Status = _tailscale.State,
                Script = PluginScript.Inline(QrScript),
            }),
            Describe = (_, _) => ValueTask.FromResult<string?>($"Server: {_serverState} on 127.0.0.1:{Port}. Tailscale: {_tailscale.Detail}. QR code file: {QrPngPath}."),
        };
    }

    // The checks show only when one fails, each with its fix; when Tailscale serves, the QR and the link are all.
    private string? TailscaleFix()
    {
        const string Dns = "https://console.tailscale.com/admin/dns";
        return _tailscale.State switch
        {
            "not-installed" => "<p><strong>Tailscale is not installed.</strong> The phone reaches this computer through Tailscale. "
                + "Install it from <a href=\"https://tailscale.com/download\">https://tailscale.com/download</a>, sign in on this computer and on the phone with the same account, then restart CodeAlta.</p>",
            "not-running" => "<p><strong>Tailscale is not running or not signed in.</strong> Start Tailscale and sign in, then restart CodeAlta.</p>",
            "serve-disabled" or "serve-failed" => "<p><strong>Tailscale could not publish the mobile view (<code>tailscale serve</code> failed).</strong> "
                + $"Open <a href=\"{Dns}\">{Dns}</a>, enable <strong>MagicDNS</strong> and <strong>HTTPS Certificates</strong>, then restart CodeAlta to retry.</p>",
            "pending" or "serving" => null,
            _ => "<p><strong>Tailscale could not be checked.</strong> Make sure Tailscale runs and you are signed in, then restart CodeAlta.</p>",
        };
    }

    private string Render()
    {
        var html = new StringBuilder();
        html.Append("<div class=\"alta-stack\">");
        if (_token is not null && _serverState == "listening")
        {
            html.Append("<p class=\"alta-muted\">qr:").Append(Matrix()).Append("</p>");
            var url = WebUtility.HtmlEncode(BaseUrl);
            html.Append("<p><a href=\"").Append(url).Append("\">").Append(url).Append("</a></p>");
        }
        else
        {
            html.Append("<p>The mobile server is ").Append(WebUtility.HtmlEncode(_serverState)).Append(".</p>");
        }

        var fix = TailscaleFix();
        if (fix is not null) html.Append(fix).Append("<p class=\"alta-muted\">").Append(WebUtility.HtmlEncode(_tailscale.Detail)).Append("</p>");
        html.Append("</div>");
        return html.ToString();
    }

    // Draws the matrix the fragment carries ("qr:" then rows of 0 and 1 joined by "|") on a canvas: a fragment has no image.
    private const string QrScript = """
        export function mount(root) {
          const holder = [...root.querySelectorAll("p")].find(p => p.textContent.startsWith("qr:"));
          if (!holder) return;
          const rows = holder.textContent.slice(3).split("|");
          holder.style.display = "none";
          const size = rows.length, scale = Math.max(4, Math.floor(320 / size));
          const canvas = document.createElement("canvas");
          canvas.width = canvas.height = size * scale;
          canvas.style.background = "#fff";
          canvas.style.borderRadius = "8px";
          canvas.style.margin = "8px 0";
          canvas.setAttribute("aria-label", "QR code of the mobile view");
          const g = canvas.getContext("2d");
          g.fillStyle = "#fff"; g.fillRect(0, 0, canvas.width, canvas.height);
          g.fillStyle = "#000";
          rows.forEach((row, y) => { for (let x = 0; x < row.length; x++) if (row[x] === "1") g.fillRect(x * scale, y * scale, scale, scale); });
          // The Tailscale mark in the middle, as MobileQr draws it in the PNG (the code is level H: it reads through it).
          const w = canvas.width, side = Math.round(w * 0.21), x0 = (w - side) / 2, r = side * 0.18;
          g.fillStyle = "#fff"; g.beginPath(); g.roundRect(x0, x0, side, side, r); g.fill();
          const step = side * 0.7 / 3, dot = step * 0.38, start = x0 + side * 0.15 + step / 2;
          for (let row = 0; row < 3; row++) for (let col = 0; col < 3; col++) {
            g.fillStyle = row === 1 || (row === 2 && col === 1) ? "#242424" : "#c8c8c8";
            g.beginPath(); g.arc(start + col * step, start + row * step, dot, 0, Math.PI * 2); g.fill();
          }
          holder.after(canvas);
          return () => canvas.remove();
        }
        """;

    internal sealed record MobileConfig
    {
        public string? Token { get; init; }
        public int Port { get; init; } = DefaultPort;
    }
}
