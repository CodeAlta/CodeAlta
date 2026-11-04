using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Automations.Integrations;

/// <summary>A message of a Slack channel.</summary>
/// <param name="EventId">The id of the event that delivered it (<c>Ev…</c>), or the channel and time of the message.</param>
/// <param name="Team">The workspace.</param>
/// <param name="Channel">The channel.</param>
/// <param name="User">Who wrote it.</param>
/// <param name="Text">What it says.</param>
/// <param name="Ts">Its time stamp, which is its id in the channel.</param>
/// <param name="ThreadTs">The thread it is in; null for a message of the channel.</param>
/// <param name="Mention">Whether it mentions the app (an <c>app_mention</c> event).</param>
internal sealed record SlackMessage(string EventId, string? Team, string Channel, string? User, string Text, string Ts, string? ThreadTs, bool Mention)
{
    /// <summary>The thread a reply goes to: the message's own thread, or the message itself.</summary>
    public string ReplyThread => ThreadTs ?? Ts;
}

/// <summary>
/// The Web API of Slack for the triggers: whether a bot token works, the address of a Socket Mode connection (with the
/// app-level token), the latest message of a channel, and a reply in a thread.
/// </summary>
internal sealed class SlackClient
{
    /// <summary>The address of the Web API.</summary>
    internal static readonly Uri DefaultApi = new("https://slack.com/api/");

    private const string Service = "Slack";
    private readonly HttpClient _http;
    private readonly Uri _api;

    /// <summary>Creates the client.</summary>
    /// <param name="http">The client of the host.</param>
    /// <param name="api">The address of the Web API; <see cref="DefaultApi"/> but in tests.</param>
    internal SlackClient(HttpClient http, Uri? api = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        (_http, _api) = (http, api ?? DefaultApi);
    }

    /// <summary>Says which workspace and bot a bot token is.</summary>
    /// <returns><c>team</c> and <c>user</c> of the bot.</returns>
    internal Task<IntegrationResult<(string Team, string User)>> AuthTestAsync(string botToken, CancellationToken cancellationToken)
        => SendAsync(botToken, "auth.test", null, static root => (root.Text("team") ?? "?", root.Text("user") ?? "?"), cancellationToken);

    /// <summary>Opens a Socket Mode connection: the address to connect to, valid once.</summary>
    internal Task<IntegrationResult<Uri>> OpenConnectionAsync(string appToken, CancellationToken cancellationToken)
        => SendAsync(appToken, "apps.connections.open", null, static root => Uri.TryCreate(root.Text("url"), UriKind.Absolute, out var url) && url.Scheme is "wss" or "ws" ? url : null,
            cancellationToken);

    /// <summary>Reads the latest message of a channel, as a sample of what the trigger sees.</summary>
    internal Task<IntegrationResult<SlackMessage>> LatestAsync(string botToken, string channel, CancellationToken cancellationToken)
        => SendAsync(botToken, "conversations.history", [new("channel", channel), new("limit", "1")], root =>
            root.Member("messages") is { ValueKind: JsonValueKind.Array } messages && messages.GetArrayLength() > 0 && messages[0] is var message && message.Text("ts") is { } ts
                ? new SlackMessage(channel + ":" + ts, null, channel, message.Text("user"), message.Text("text") ?? string.Empty, ts, message.Text("thread_ts"), false)
                : null, cancellationToken);

    /// <summary>Posts a reply in the thread of a message.</summary>
    /// <returns>The time stamp of the reply.</returns>
    internal Task<IntegrationResult<string>> ReplyAsync(string botToken, string channel, string thread, string text, CancellationToken cancellationToken)
        => SendAsync(botToken, "chat.postMessage", [new("channel", channel), new("thread_ts", thread), new("text", text)],
            static root => root.Text("ts"), cancellationToken);

    // Every method of the Web API takes a form; Slack answers 200 with ok = false and an error code for what it refuses.
    private async Task<IntegrationResult<T>> SendAsync<T>(string token, string method, IEnumerable<KeyValuePair<string, string>>? form, Func<JsonElement, T?> read, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_api, method)) { Content = new FormUrlEncodedContent(form ?? []) }.Bearer(token);
        return await IntegrationHttp.SendAsync(_http, request, Service, (root, _) =>
        {
            if (!root.Flag("ok")) return IntegrationResult<T>.Fail($"Slack refused {method}: {IntegrationHttp.Line(root.Text("error") ?? "no reason given")}.");
            return read(root) is { } value ? IntegrationResult<T>.Of(value) : IntegrationResult<T>.Fail($"Slack answered {method} without what it was asked.");
        }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// One Socket Mode listener of Slack: it opens a connection with the app-level token, acknowledges each envelope at once,
/// hands the messages of the events API to its callback, and connects again when Slack asks it to or the connection
/// drops, with a growing delay, until it is stopped. Nothing connects into the machine: it dials out to Slack.
/// </summary>
internal sealed class SlackSocketListener
{
    private const int MaximumFrameBytes = 1024 * 1024;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongestDelay = TimeSpan.FromMinutes(5);

    private readonly SlackClient _client;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connect;
    private readonly TimeProvider _time;

    /// <summary>Creates a listener.</summary>
    /// <param name="client">The Web API, which gives the address of each connection.</param>
    /// <param name="connect">Connects a WebSocket to an address; null for a <see cref="ClientWebSocket"/>.</param>
    /// <param name="time">The clock of the delays.</param>
    internal SlackSocketListener(SlackClient client, Func<Uri, CancellationToken, Task<WebSocket>>? connect = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        (_client, _connect, _time) = (client, connect ?? ConnectAsync, time ?? TimeProvider.System);
    }

    /// <summary>Gets what went wrong last; null while connected, or before.</summary>
    internal string? Problem { get; private set; }

    /// <summary>Gets whether a connection is open.</summary>
    internal bool Connected { get; private set; }

    /// <summary>Listens until stopped.</summary>
    /// <param name="appToken">The app-level token.</param>
    /// <param name="received">Given each message, after its envelope was acknowledged.</param>
    /// <param name="changed">Told when <see cref="Connected"/> or <see cref="Problem"/> changes.</param>
    /// <param name="cancellationToken">Stops the listener.</param>
    /// <returns>A task that completes once stopped.</returns>
    internal async Task RunAsync(string appToken, Func<SlackMessage, Task> received, Action? changed, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appToken);
        ArgumentNullException.ThrowIfNull(received);
        var delay = FirstDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var opened = await _client.OpenConnectionAsync(appToken, cancellationToken).ConfigureAwait(false);
            var refresh = false;
            if (opened.Value is { } url)
            {
                try
                {
                    using var socket = await _connect(url, cancellationToken).ConfigureAwait(false);
                    (Connected, Problem) = (true, null);
                    changed?.Invoke();
                    delay = FirstDelay;
                    refresh = await ReadAsync(socket, received, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is WebSocketException or IOException or HttpRequestException or JsonException
                    || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    Problem = "The Socket Mode connection of Slack dropped: " + IntegrationHttp.Line(exception.Message);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    if (Connected)
                    {
                        Connected = false;
                        changed?.Invoke();
                    }
                }
            }
            else
            {
                Problem = opened.Problem;
                changed?.Invoke();
            }

            // Asked by Slack to connect again: at once.
            if (refresh) continue;
            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = delay * 2 > LongestDelay ? LongestDelay : delay * 2;
        }
    }

    /// <summary>Reads a message of Slack from the payload of an <c>events_api</c> envelope; null for another event.</summary>
    internal static SlackMessage? ReadMessage(JsonElement payload)
    {
        var @event = payload.Member("event");
        var type = @event.Text("type");
        if (type is not ("message" or "app_mention")) return null;
        // What bots write (this automation's own replies included) and the edits of messages start nothing.
        if (@event.Text("subtype") is not null || @event.Text("bot_id") is not null) return null;
        if (@event.Text("channel") is not { Length: > 0 } channel || @event.Text("ts") is not { Length: > 0 } ts) return null;
        var id = payload.Text("event_id") ?? channel + ":" + ts;
        return new SlackMessage(id, payload.Text("team_id"), channel, @event.Text("user"), @event.Text("text") ?? string.Empty, ts, @event.Text("thread_ts"), type == "app_mention");
    }

    // True when Slack asked for a new connection, false when the connection closed.
    private async Task<bool> ReadAsync(WebSocket socket, Func<SlackMessage, Task> received, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            frame.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return false;
                if (frame.Length + result.Count > MaximumFrameBytes) throw new WebSocketException("Slack sent a frame larger than can be read.");
                frame.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            using var document = JsonDocument.Parse(frame.ToArray());
            var root = document.RootElement;
            // Each envelope is acknowledged at once, before its work: Slack sends it again otherwise.
            if (root.Text("envelope_id") is { Length: > 0 } envelope)
            {
                var ack = Encoding.UTF8.GetBytes("{\"envelope_id\":" + JsonSerializer.Serialize(envelope, IntegrationJsonContext.Default.String) + "}");
                await socket.SendAsync(ack, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }

            switch (root.Text("type"))
            {
                case "disconnect":
                    // Slack refreshes its connections now and then: a new one is opened at once.
                    return true;
                case "events_api" when ReadMessage(root.Member("payload")) is { } message:
                    await received(message).ConfigureAwait(false);
                    break;
            }
        }

        return false;
    }

    private static async Task<WebSocket> ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>The source-generated contracts the clients of other tools write.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class IntegrationJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
