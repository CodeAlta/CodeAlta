using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode mobile</c>.</summary>
internal sealed class NodeMobileCommandOptions
{
    /// <summary>Gets the profile.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets the command and its words.</summary>
    public List<string> Words { get; } = [];

    /// <summary>Gets or sets the private network phones connect through.</summary>
    public string? Network { get; set; }

    /// <summary>Gets or sets the port of the page on this machine.</summary>
    public string? Port { get; set; }

    /// <summary>Gets or sets the HTTPS port of the page on the tailnet.</summary>
    public string? HttpsPort { get; set; }
}

/// <summary>
/// <c>altanode mobile</c>: the page of the phones of the node that runs on this profile, through its local control, as the
/// Mobile tab and its settings page of CodeAlta Desktop show it: its state, the QR code a phone scans, its settings, and
/// the access of the phones. The code it prints connects one phone, for a few minutes.
/// </summary>
internal sealed class NodeMobileCommands(TextWriter output, TextWriter error)
{
    /// <summary>Gets or sets what stops a call to the node, for tests; none by default.</summary>
    public CancellationToken Stop { get; init; }

    /// <summary>Gets or sets how long the command waits for the network to publish the page.</summary>
    public TimeSpan PublishWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Runs the command.</summary>
    /// <param name="options">The command and its options.</param>
    /// <returns>The exit code.</returns>
    public async ValueTask<int> RunAsync(NodeMobileCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var command = options.Words is [var first, ..] ? first : "status";
        if (options.Words.Count > 1) return await UsageAsync($"mobile {command} takes no argument.").ConfigureAwait(false);
        if (command is not ("status" or "on" or "off" or "set" or "reset"))
            return await UsageAsync($"'{command}' is not a command of mobile: on, off, set or reset; mobile alone shows the code a phone scans.").ConfigureAwait(false);
        var settings = options.Network is not null || options.Port is not null || options.HttpsPort is not null;
        if (settings && command is not ("set" or "on")) return await UsageAsync("--network, --port and --https-port go with mobile on and mobile set.").ConfigureAwait(false);
        if (command == "set" && !settings) return await UsageAsync("mobile set takes --network, --port or --https-port.").ConfigureAwait(false);
        int? port = null, httpsPort = null;
        if (options.Port is { } portText && (port = ParsePort(portText)) is null) return await UsageAsync("--port takes a number from 1024 to 65535.").ConfigureAwait(false);
        if (options.HttpsPort is { } httpsText && (httpsPort = ParsePort(httpsText)) is null) return await UsageAsync("--https-port takes a number from 1024 to 65535.").ConfigureAwait(false);

        var control = NodeLocalControl.Read(profile);
        if (!control.NodeListens)
        {
            await error.WriteLineAsync(control.Status.IsHeld
                ? "altanode mobile: the node of this profile listens nowhere (--no-listen), so it takes no command here. Start it without --no-listen, or use a CodeAlta Desktop that manages it."
                : "altanode mobile: no node runs on this profile: start it with altanode run, or its service.").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        try
        {
            var (client, identity) = await control.ConnectAsync(Stop).ConfigureAwait(false);
            using (identity)
            await using (client.ConfigureAwait(false))
            {
                return command switch
                {
                    "on" => await OnAsync(client, settings, options.Network, port, httpsPort).ConfigureAwait(false),
                    "off" => await OffAsync(client).ConfigureAwait(false),
                    "set" => await SetAsync(client, options.Network, port, httpsPort).ConfigureAwait(false),
                    "reset" => await ResetAsync(client).ConfigureAwait(false),
                    _ => await ShowAsync(client, wait: false).ConfigureAwait(false),
                };
            }
        }
        catch (FleetException exception)
        {
            await error.WriteLineAsync("altanode mobile: " + exception.Message).ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }
    }

    private async Task<int> OnAsync(FleetNodeClient client, bool settings, string? network, int? port, int? httpsPort)
    {
        var state = await client.EnableMobileAsync(true, Stop).ConfigureAwait(false);
        if (!state.Running)
        {
            await error.WriteLineAsync("altanode mobile: " + (state.Refusal ?? "the page of the phones did not start.")).ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        return settings ? await SetAsync(client, network, port, httpsPort).ConfigureAwait(false) : await ShowAsync(client, wait: true).ConfigureAwait(false);
    }

    private async Task<int> OffAsync(FleetNodeClient client)
    {
        await client.EnableMobileAsync(false, Stop).ConfigureAwait(false);
        await output.WriteLineAsync("Mobile: off. The page is no longer served, and the phones keep their access for when it is turned on again.").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    private async Task<int> ResetAsync(FleetNodeClient client)
    {
        if (!await RequireRunningAsync(client).ConfigureAwait(false)) return NodeExitCodes.Failure;
        await client.InvokeMobileAsync("mobile.reset", null, Stop).ConfigureAwait(false);
        await output.WriteLineAsync("Mobile: every phone is disconnected. altanode mobile shows a code for a phone to connect again.").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    private async Task<int> SetAsync(FleetNodeClient client, string? network, int? port, int? httpsPort)
    {
        if (!await RequireRunningAsync(client).ConfigureAwait(false)) return NodeExitCodes.Failure;
        using var before = JsonDocument.Parse(await client.InvokeMobileAsync("mobile.settings.get", null, Stop).ConfigureAwait(false));
        var payload = new StringBuilder("{");
        payload.Append("\"provider\":").Append(network is null ? "null" : JsonSerializer.Serialize(network, NodeMobileJson.Default.String));
        payload.Append(CultureInfo.InvariantCulture, $",\"port\":{port ?? before.RootElement.GetProperty("port").GetInt32()}");
        payload.Append(",\"httpsPort\":").Append(httpsPort?.ToString(CultureInfo.InvariantCulture) ?? "null").Append('}');
        using var after = JsonDocument.Parse(await client.InvokeMobileAsync("mobile.settings.set", payload.ToString(), Stop).ConfigureAwait(false));
        if (Text(after.RootElement, "error") is { } refused)
        {
            await error.WriteLineAsync("altanode mobile: " + refused).ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        return await ShowAsync(client, wait: true).ConfigureAwait(false);
    }

    private async Task<bool> RequireRunningAsync(FleetNodeClient client)
    {
        var state = await client.GetMobileAsync(Stop).ConfigureAwait(false);
        if (state.Running) return true;
        await error.WriteLineAsync("altanode mobile: " + (state.Refusal ?? "the page of the phones is off: altanode mobile on turns it on.")).ConfigureAwait(false);
        return false;
    }

    // The state of the page, and a code for one phone when it is served. After a change the network takes a moment to publish.
    private async Task<int> ShowAsync(FleetNodeClient client, bool wait)
    {
        var state = await client.GetMobileAsync(Stop).ConfigureAwait(false);
        if (!state.Running)
        {
            await output.WriteLineAsync("Mobile: off. " + (state.Refusal ?? "altanode mobile on serves the sessions of this machine to a phone.")).ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        var limit = DateTime.UtcNow + (wait ? PublishWait : TimeSpan.Zero);
        JsonDocument status;
        while (true)
        {
            status = JsonDocument.Parse(await client.InvokeMobileAsync("mobile.status", null, Stop).ConfigureAwait(false));
            var provider = status.RootElement.GetProperty("provider");
            var working = Text(status.RootElement, "server") == "starting" || provider.TryGetProperty("working", out var busy) && busy.ValueKind == JsonValueKind.True;
            if (!working || DateTime.UtcNow >= limit) break;
            status.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(500), Stop).ConfigureAwait(false);
        }

        using (status)
        {
            if (Text(status.RootElement, "served") is null)
            {
                await WriteProblemAsync(status.RootElement).ConfigureAwait(false);
                return NodeExitCodes.Failure;
            }
        }

        using var paired = JsonDocument.Parse(await client.InvokeMobileAsync("mobile.pair", null, Stop).ConfigureAwait(false));
        var root = paired.RootElement;
        var network = root.GetProperty("provider");
        await output.WriteLineAsync($"Mobile: served at {Text(root, "served")} through {Text(network, "name") ?? Text(network, "id")}.").ConfigureAwait(false);
        if (root.TryGetProperty("qr", out var qr) && qr.ValueKind == JsonValueKind.Array)
        {
            var minutes = root.TryGetProperty("pairingSeconds", out var seconds) && seconds.TryGetInt32(out var left) ? Math.Max(1, (int)Math.Round(left / 60.0)) : 5;
            await output.WriteLineAsync($"Scan this code with the phone within {minutes} minute{(minutes == 1 ? "" : "s")}; it connects one phone:").ConfigureAwait(false);
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteAsync(Draw([.. qr.EnumerateArray().Select(static row => row.GetString() ?? "")])).ConfigureAwait(false);
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync("Or open on the phone: " + Text(root, "connectUrl")).ConfigureAwait(false);
        }

        var phones = root.TryGetProperty("phones", out var count) && count.TryGetInt32(out var connected) ? connected : 0;
        await output.WriteLineAsync(phones == 0 ? "No phone is connected yet."
            : $"{phones} phone{(phones == 1 ? " is" : "s are")} connected. altanode mobile reset disconnects every phone.").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    // What keeps phones out, as the Mobile tab says it, with the page that fixes it when the network names one.
    private async Task WriteProblemAsync(JsonElement status)
    {
        var provider = status.GetProperty("provider");
        if (Text(status, "server") is { } server && server != "listening")
        {
            await error.WriteLineAsync($"altanode mobile: the page does not listen on port {status.GetProperty("port").GetInt32()} of this machine"
                + (Text(status, "serverDetail") is { } detail ? ": " + detail : ".")).ConfigureAwait(false);
            if (server == "port-in-use") await error.WriteLineAsync("  altanode mobile set --port <n> --https-port <n> serves it on another port.").ConfigureAwait(false);
            return;
        }

        await error.WriteLineAsync($"altanode mobile: phones cannot connect through {Text(provider, "name") ?? Text(provider, "id")}: {Text(provider, "detail") ?? Text(provider, "state")}").ConfigureAwait(false);
        if (Text(provider, "fixUrl") is { } fix) await error.WriteLineAsync("  " + fix).ConfigureAwait(false);
        if (provider.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array && actions.GetArrayLength() > 0)
            await error.WriteLineAsync("  It asks for a sign-in a window makes: sign the network in on this machine with its own command line, then run altanode mobile again.").ConfigureAwait(false);
    }

    /// <summary>Draws a QR code with half blocks, two rows of modules a line, dark on light with a margin of two modules, as terminals with a dark or a light background both read it.</summary>
    /// <param name="rows">The modules, one string of <c>0</c> and <c>1</c> a row.</param>
    /// <returns>The lines of the drawing.</returns>
    internal static string Draw(IReadOnlyList<string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        const int Margin = 2;
        var size = rows.Count;
        bool Dark(int x, int y) => y >= 0 && y < size && x >= 0 && x < rows[y].Length && rows[y][x] == '1';
        var text = new StringBuilder();
        // Black on white, set explicitly: a code inverted by the colors of the terminal is one many cameras do not read.
        for (var y = -Margin; y < size + Margin; y += 2)
        {
            text.Append("  \u001b[30;47m");
            for (var x = -Margin; x < size + Margin; x++)
            {
                var (top, bottom) = (Dark(x, y), Dark(x, y + 1));
                text.Append(top && bottom ? '\u2588' : top ? '\u2580' : bottom ? '\u2584' : ' ');
            }

            text.Append("\u001b[0m").AppendLine();
        }

        return text.ToString();
    }

    private static int? ParsePort(string text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1024 and <= 65535 ? port : null;

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private async Task<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class NodeMobileJson : System.Text.Json.Serialization.JsonSerializerContext;
