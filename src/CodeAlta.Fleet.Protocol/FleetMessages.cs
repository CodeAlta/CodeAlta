using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// A frame of the fleet protocol: a JSON object whose <c>t</c> property names its kind.
/// </summary>
/// <remarks>
/// The kinds are the envelopes (<see cref="FleetRequest"/>, <see cref="FleetResponse"/>, <see cref="FleetEvent"/>),
/// the application heartbeat (<see cref="FleetPing"/>, <see cref="FleetPong"/>) and the frames of the handshake
/// (<see cref="FleetHelloChallenge"/>, <see cref="FleetHello"/>, <see cref="FleetWelcome"/>,
/// <see cref="FleetHandshakeError"/>). <see cref="FleetMessageSerializer"/> reads and writes them.
/// </remarks>
public abstract record FleetMessage
{
    /// <summary>Gets the kind of the frame, the value of its <c>t</c> property, which <see cref="FleetMessageSerializer"/> writes first.</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>
/// A request: <c>{ "t": "req", "id", "m", "p", "idem", "tp" }</c>.
/// </summary>
/// <param name="Id">The id of the request, unique on its connection; the response carries it back.</param>
/// <param name="Method">The method, such as <c>sessions.send</c>.</param>
/// <param name="Params">The parameters, or <see langword="null"/> when the method takes none.</param>
/// <param name="IdempotencyKey">The idempotency key of a side-effecting request, so that a retry is answered with the first receipt.</param>
/// <param name="TraceParent">The W3C <c>traceparent</c> of the caller, so that work across machines is one trace.</param>
public sealed record FleetRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("m")] string Method,
    [property: JsonPropertyName("p")] JsonElement? Params = null,
    [property: JsonPropertyName("idem")] string? IdempotencyKey = null,
    [property: JsonPropertyName("tp")] string? TraceParent = null) : FleetMessage
{
    /// <summary>The value of <c>t</c> for a request.</summary>
    public const string Kind = "req";

    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => Kind;
}

/// <summary>
/// A response: <c>{ "t": "res", "id", "ok": true, "r" }</c> or <c>{ "t": "res", "id", "ok": false, "err" }</c>.
/// </summary>
/// <param name="Id">The id of the request it answers.</param>
/// <param name="Ok">Whether the request succeeded.</param>
/// <param name="Result">The result of a request that succeeded, or <see langword="null"/>.</param>
/// <param name="Error">The error of a request that failed.</param>
public sealed record FleetResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("r")] JsonElement? Result = null,
    [property: JsonPropertyName("err")] FleetError? Error = null) : FleetMessage
{
    /// <summary>The value of <c>t</c> for a response.</summary>
    public const string Kind = "res";

    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => Kind;

    /// <summary>Creates the response of a request that succeeded.</summary>
    /// <param name="id">The id of the request.</param>
    /// <param name="result">The result, or <see langword="null"/>.</param>
    /// <returns>The response.</returns>
    public static FleetResponse Success(string id, JsonElement? result) => new(id, true, result);

    /// <summary>Creates the response of a request that failed.</summary>
    /// <param name="id">The id of the request.</param>
    /// <param name="error">The error.</param>
    /// <returns>The response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static FleetResponse Failure(string id, FleetError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new FleetResponse(id, false, null, error);
    }
}

/// <summary>
/// An event: <c>{ "t": "evt", "ch", "e", "seq", "p" }</c>.
/// </summary>
/// <param name="Event">The name of the event, such as <c>session.display</c>.</param>
/// <param name="Payload">The payload, or <see langword="null"/>.</param>
/// <param name="Channel">The channel of the event when it belongs to one.</param>
/// <param name="Sequence">The sequence of the event in its channel or stream, when it has one.</param>
public sealed record FleetEvent(
    [property: JsonPropertyName("e")] string Event,
    [property: JsonPropertyName("p")] JsonElement? Payload = null,
    [property: JsonPropertyName("ch")] long? Channel = null,
    [property: JsonPropertyName("seq")] long? Sequence = null) : FleetMessage
{
    /// <summary>The value of <c>t</c> for an event.</summary>
    public const string Kind = "evt";

    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => Kind;
}

/// <summary>
/// An error: <c>{ "code", "hint", "message", "retryAfter" }</c>. The code is stable (<see cref="FleetErrorCodes"/>),
/// the hint says what to do (<see cref="FleetErrorHints"/>), so that a client reacts instead of retrying blindly.
/// </summary>
/// <param name="Code">The stable code of the error.</param>
/// <param name="Hint">What the receiver should do, or <see langword="null"/>.</param>
/// <param name="Message">A description for people, without sensitive data, or <see langword="null"/>.</param>
/// <param name="RetryAfterSeconds">The delay before a retry, for <see cref="FleetErrorCodes.ServerBusy"/>.</param>
public sealed record FleetError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("hint")] string? Hint = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("retryAfter")] int? RetryAfterSeconds = null);

/// <summary>
/// The application heartbeat: <c>{ "t": "ping", "seq", "p" }</c>, answered by a <see cref="FleetPong"/>.
/// </summary>
/// <param name="Sequence">The sequence of the ping on its connection.</param>
/// <param name="Payload">State the heartbeat carries, or <see langword="null"/>.</param>
public sealed record FleetPing(
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("p")] JsonElement? Payload = null) : FleetMessage
{
    /// <summary>The value of <c>t</c> for a ping.</summary>
    public const string Kind = "ping";

    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => Kind;
}

/// <summary>
/// The answer to a <see cref="FleetPing"/>: <c>{ "t": "pong", "seq" }</c>.
/// </summary>
/// <param name="Sequence">The sequence of the ping it answers.</param>
public sealed record FleetPong(
    [property: JsonPropertyName("seq")] long Sequence) : FleetMessage
{
    /// <summary>The value of <c>t</c> for a pong.</summary>
    public const string Kind = "pong";

    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => Kind;
}

/// <summary>A range of protocol versions: <c>{ "min", "max" }</c>.</summary>
/// <param name="Min">The oldest version supported.</param>
/// <param name="Max">The newest version supported.</param>
public sealed record FleetProtocolRange(
    [property: JsonPropertyName("min")] int Min,
    [property: JsonPropertyName("max")] int Max)
{
    /// <summary>Gets whether the range is valid: positive versions, <see cref="Min"/> not above <see cref="Max"/>.</summary>
    [JsonIgnore]
    public bool IsValid => Min > 0 && Min <= Max;

    /// <summary>Finds the highest version two ranges have in common.</summary>
    /// <param name="local">The range of this side.</param>
    /// <param name="remote">The range of the other side.</param>
    /// <param name="version">The highest common version, or 0 when there is none.</param>
    /// <returns><see langword="true"/> when the ranges have a version in common.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="local"/> or <paramref name="remote"/> is <see langword="null"/>.</exception>
    public static bool TryNegotiate(FleetProtocolRange local, FleetProtocolRange remote, out int version)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        version = 0;
        if (!local.IsValid || !remote.IsValid) return false;
        var highest = Math.Min(local.Max, remote.Max);
        if (highest < Math.Max(local.Min, remote.Min)) return false;
        version = highest;
        return true;
    }
}

/// <summary>
/// The limits a server or a node announces: <c>{ "maxFrame", "heartbeat", "maxChannels" }</c>.
/// </summary>
/// <param name="MaxFrameBytes">The largest frame, in bytes, either side may send.</param>
/// <param name="HeartbeatMilliseconds">The period of the application ping, in milliseconds.</param>
/// <param name="MaxChannels">The most channels a connection may open, when limited.</param>
public sealed record FleetConnectionPolicy(
    [property: JsonPropertyName("maxFrame")] int MaxFrameBytes,
    [property: JsonPropertyName("heartbeat")] int HeartbeatMilliseconds,
    [property: JsonPropertyName("maxChannels")] int? MaxChannels = null)
{
    /// <summary>The default largest frame: 4 MiB.</summary>
    public const int DefaultMaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>The default heartbeat: 25 seconds, under the idle limits of proxies (60 to 100 seconds).</summary>
    public const int DefaultHeartbeatMilliseconds = 25_000;

    /// <summary>Creates the default policy.</summary>
    /// <returns>A policy with <see cref="DefaultMaxFrameBytes"/> and <see cref="DefaultHeartbeatMilliseconds"/>.</returns>
    public static FleetConnectionPolicy CreateDefault() => new(DefaultMaxFrameBytes, DefaultHeartbeatMilliseconds);
}

/// <summary>What a client is: <c>{ "kind", "version", "os" }</c>.</summary>
/// <param name="Kind">The kind of client, such as <c>desktop</c>, <c>node</c>, <c>tui</c> or <c>phone</c>.</param>
/// <param name="Version">The version of the client application.</param>
/// <param name="Os">The operating system, such as <c>windows</c>, <c>macos</c> or <c>linux</c>.</param>
public sealed record FleetClientInfo(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("os")] string Os);

/// <summary>The device of a client and its proof of possession of the device key: <c>{ "id", "proof" }</c>.</summary>
/// <param name="Id">The id of the device.</param>
/// <param name="Proof">The signature of <see cref="FleetProofContext.GetSignedBytes"/> with the device key.</param>
public sealed record FleetDeviceProof(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("proof")] string Proof);

/// <summary>What a client resumes from: <c>{ "cursors" }</c>.</summary>
/// <param name="Cursors">The cursors of the client, whose shape belongs to the services; <see langword="null"/> for none.</param>
public sealed record FleetResume(
    [property: JsonPropertyName("cursors")] JsonElement? Cursors = null);

/// <summary>
/// The first frame of a server or a node: <c>{ "t": "hello.challenge", "nonce", "time", "protocol", "policy" }</c>.
/// </summary>
/// <param name="Nonce">A random value the client's proof signs, base64url.</param>
/// <param name="Time">The time of the server, for display and skew checks; ordering never relies on it.</param>
/// <param name="Protocol">The versions the server supports.</param>
/// <param name="Policy">The limits of the connection.</param>
public sealed record FleetHelloChallenge(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("protocol")] FleetProtocolRange Protocol,
    [property: JsonPropertyName("policy")] FleetConnectionPolicy Policy) : FleetMessage
{
    /// <summary>The value of <c>t</c> for a challenge.</summary>
    public const string Kind = "hello.challenge";

    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => Kind;
}

/// <summary>
/// The answer of a client to the challenge: <c>{ "t": "hello", "protocol", "client", "device", "resume" }</c>.
/// </summary>
/// <param name="Protocol">The versions the client supports.</param>
/// <param name="Client">What the client is.</param>
/// <param name="Device">The device of the client and its proof.</param>
/// <param name="Resume">What the client resumes from, or <see langword="null"/>.</param>
public sealed record FleetHello(
    [property: JsonPropertyName("protocol")] FleetProtocolRange Protocol,
    [property: JsonPropertyName("client")] FleetClientInfo Client,
    [property: JsonPropertyName("device")] FleetDeviceProof Device,
    [property: JsonPropertyName("resume")] FleetResume? Resume = null) : FleetMessage
{
    /// <summary>The value of <c>t</c> for a hello.</summary>
    public const string Kind = "hello";

    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => Kind;
}

/// <summary>
/// The acceptance of a client: <c>{ "t": "welcome", "protocol", "features", "cursors", "policy" }</c>.
/// </summary>
/// <param name="Protocol">The version both sides speak from now on.</param>
/// <param name="Features">The features the server offers; features are announced, never inferred from a version.</param>
/// <param name="Policy">The limits of the connection.</param>
/// <param name="Cursors">The cursors the server holds for the client, or <see langword="null"/>.</param>
public sealed record FleetWelcome(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("features")] IReadOnlyList<string> Features,
    [property: JsonPropertyName("policy")] FleetConnectionPolicy Policy,
    [property: JsonPropertyName("cursors")] JsonElement? Cursors = null) : FleetMessage
{
    /// <summary>The value of <c>t</c> for a welcome.</summary>
    public const string Kind = "welcome";

    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => Kind;
}

/// <summary>
/// The refusal of a handshake: <c>{ "t": "error", "err": { "code", "hint" } }</c>, after which the connection closes.
/// </summary>
/// <param name="Error">Why the handshake failed and what to do.</param>
public sealed record FleetHandshakeError(
    [property: JsonPropertyName("err")] FleetError Error) : FleetMessage
{
    /// <summary>The value of <c>t</c> for a handshake error.</summary>
    public const string Kind = "error";

    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => Kind;
}
