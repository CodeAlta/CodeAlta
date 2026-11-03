using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// The limits a session an agent starts on another machine runs within (<c>doc/fleet/access-control.md</c>, "Agents and
/// access"): at most what the session of the agent has on its own machine. The machine may narrow them further by its own
/// policy; it never widens them.
/// </summary>
/// <param name="DisabledTools">The tools, or patterns over tool names, the session does without: at least those its parent does without.</param>
/// <param name="PermissionMode">The widest permission mode it may run in (<c>default</c>, <c>acceptEdits</c>, <c>bypassPermissions</c>...); null for <c>default</c>.</param>
/// <param name="Sandbox">The sandbox profile it runs in at least; null when its parent runs without one.</param>
/// <param name="SubSessions">Whether it may start sessions of its own.</param>
/// <param name="Depth">How many agents of other machines are above it, from 1.</param>
public sealed record FleetAgentLimits(
    [property: JsonPropertyName("disabledTools")] IReadOnlyList<string> DisabledTools,
    [property: JsonPropertyName("permissionMode")] string? PermissionMode,
    [property: JsonPropertyName("sandbox")] string? Sandbox,
    [property: JsonPropertyName("subSessions")] bool SubSessions,
    [property: JsonPropertyName("depth")] int Depth);

/// <summary>
/// What an agent asks of another machine on behalf of a person: to start a session there (<see cref="FleetAgentRequestKinds.Start"/>),
/// or to send, queue or steer a prompt of a session there. The device of the person on whose behalf the agent runs signs it,
/// for one machine, one time and one nonce, bound to the space, the project, the session and the text it carries, so that
/// neither a server that relays it nor anyone who saw it can make the machine run anything else, or the same thing twice.
/// </summary>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="Kind">What is asked (<see cref="FleetAgentRequestKinds"/>).</param>
/// <param name="MachineId">The device of the machine that runs it.</param>
/// <param name="SignerDeviceId">The device that signs it: the one the channel of the request authenticates.</param>
/// <param name="SignerKey">The public key of the signer (the base64 of its DER SubjectPublicKeyInfo).</param>
/// <param name="Time">When it was signed; a machine refuses a request too far from its own time.</param>
/// <param name="Nonce">A random value; a machine refuses a nonce it has seen.</param>
/// <param name="ParentSessionId">The session of the agent that asks.</param>
/// <param name="ParentMachineName">The name of the machine that session runs on, as people know it.</param>
/// <param name="SpaceId">The space of that session: the session the request starts or reaches is in the same one.</param>
/// <param name="SessionId">The session a prompt goes to; null for a start.</param>
/// <param name="ProjectId">The project of the machine a start works in; null for a session outside a project, or a prompt.</param>
/// <param name="TextHash">The SHA-256 of the UTF-8 of the prompt, base64url (<see cref="FleetAgentRequestDocuments.HashText"/>); null for a start without one.</param>
/// <param name="Limits">What a started session runs within; null for a prompt.</param>
public sealed record FleetAgentRequest(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("machine")] string MachineId,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("key")] string SignerKey,
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("parent")] string ParentSessionId,
    [property: JsonPropertyName("parentMachine")] string ParentMachineName,
    [property: JsonPropertyName("space")] string SpaceId,
    [property: JsonPropertyName("session")] string? SessionId,
    [property: JsonPropertyName("project")] string? ProjectId,
    [property: JsonPropertyName("text")] string? TextHash,
    [property: JsonPropertyName("limits")] FleetAgentLimits? Limits)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>The kinds of <see cref="FleetAgentRequest"/>.</summary>
public static class FleetAgentRequestKinds
{
    /// <summary>Starts a session, the child of the agent's session, with its first prompt.</summary>
    public const string Start = "start";

    /// <summary>Sends a prompt that starts a turn of a session.</summary>
    public const string Send = "send";

    /// <summary>Queues a prompt for after the running turn of a session.</summary>
    public const string Queue = "queue";

    /// <summary>Gives a prompt to the running turn of a session.</summary>
    public const string Steer = "steer";
}

/// <summary>A request as it travels: the exact JSON text the signer signed, and the signature.</summary>
/// <param name="Payload">The JSON of a <see cref="FleetAgentRequest"/>, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of <see cref="FleetAgentRequestDocuments.GetSignedBytes"/>, r and s in base64url.</param>
public sealed record FleetSignedAgentRequest(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>Writes, reads and verifies the signed requests of agents to other machines.</summary>
public static class FleetAgentRequestDocuments
{
    /// <summary>How far from the time of the machine a request may be signed, either way.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(10);

    /// <summary>The most levels of agents of other machines above a session: a chain of starts across machines stops there.</summary>
    public const int MaxDepth = 3;

    /// <summary>The most tools or patterns a request names.</summary>
    public const int MaxDisabledTools = 256;

    private const string SignDomain = "codealta-agent-request/1\n";
    private const int MaxPayloadLength = 32 * 1024;
    private const int MaxIdLength = 256;

    /// <summary>Writes the JSON text of a request, the text a signer signs.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static string Serialize(FleetAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.Serialize(request, FleetAgentRequestJsonContext.Default.FleetAgentRequest);
    }

    /// <summary>Gets the bytes a signer signs for a request.</summary>
    /// <param name="payload">The JSON text of the request.</param>
    /// <returns>The UTF-8 of a domain line and the text.</returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is null or empty.</exception>
    public static byte[] GetSignedBytes(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Encoding.UTF8.GetBytes(SignDomain + payload);
    }

    /// <summary>Gets the hash a request binds a prompt with.</summary>
    /// <param name="text">The prompt.</param>
    /// <returns>The base64url of the SHA-256 of its UTF-8.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string HashText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Makes a new nonce.</summary>
    /// <returns>16 random bytes in base64url.</returns>
    public static string CreateNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Reads the JSON text of a request and checks what it says, apart from its signature.</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="request">The request read, or null.</param>
    /// <param name="reason">Why it does not hold, or null.</param>
    /// <returns>Whether it reads and holds.</returns>
    public static bool TryRead(string? payload, [NotNullWhen(true)] out FleetAgentRequest? request, [NotNullWhen(false)] out string? reason)
    {
        request = null;
        reason = "the agent request does not read";
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength) return false;
        FleetAgentRequest? read;
        try { read = JsonSerializer.Deserialize(payload, FleetAgentRequestJsonContext.Default.FleetAgentRequest); }
        catch (JsonException) { return false; }
        if (read is null) return false;
        if (read.Version != FleetAgentRequest.CurrentVersion)
        {
            reason = $"version {read.Version.ToString(CultureInfo.InvariantCulture)} of the agent request is not known";
            return false;
        }

        if (read.Kind is not (FleetAgentRequestKinds.Start or FleetAgentRequestKinds.Send or FleetAgentRequestKinds.Queue or FleetAgentRequestKinds.Steer))
        {
            reason = $"'{read.Kind}' is not a kind of agent request";
            return false;
        }

        if (!IsName(read.MachineId) || !IsName(read.SignerDeviceId) || string.IsNullOrEmpty(read.SignerKey) || !IsName(read.Nonce)
            || !IsName(read.ParentSessionId) || !IsName(read.ParentMachineName) || !IsName(read.SpaceId))
        {
            reason = "an agent request names its machine, signer, nonce, parent session, parent machine and space";
            return false;
        }

        if (read.Kind == FleetAgentRequestKinds.Start)
        {
            if (read.Limits is not { } limits || read.SessionId is not null)
            {
                reason = "a start names its limits, and no session";
                return false;
            }

            if (limits.Depth is < 1 or > MaxDepth)
            {
                reason = $"a chain of agents across machines is at most {MaxDepth.ToString(CultureInfo.InvariantCulture)} deep";
                return false;
            }

            if (limits.DisabledTools is null || limits.DisabledTools.Count > MaxDisabledTools || limits.DisabledTools.Any(static tool => !IsName(tool)))
            {
                reason = "the tools a start does without do not read";
                return false;
            }
        }
        else if (!IsName(read.SessionId) || read.TextHash is null || read.Limits is not null)
        {
            reason = "a prompt names its session and its text, and no limits";
            return false;
        }

        (request, reason) = (read, null);
        return true;
    }

    /// <summary>
    /// Verifies a request for a machine: it reads and holds, is of the kind expected, for this machine, was signed within
    /// <see cref="MaxSkew"/> of <paramref name="now"/>, by the device whose key the channel of the request authenticated
    /// (<paramref name="channelFingerprint"/>), with that key.
    /// </summary>
    /// <param name="signed">The signed request; null for none.</param>
    /// <param name="kind">The kind the call carries (<see cref="FleetAgentRequestKinds"/>).</param>
    /// <param name="machineId">The device of the machine that verifies.</param>
    /// <param name="channelFingerprint">The fingerprint of the key of the device at the other end of the channel (base64url of its SHA-256).</param>
    /// <param name="now">The time of the machine.</param>
    /// <param name="request">The request, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    public static bool TryVerify(FleetSignedAgentRequest? signed, string kind, string machineId, string channelFingerprint, DateTimeOffset now,
        [NotNullWhen(true)] out FleetAgentRequest? request, [NotNullWhen(false)] out string? reason)
    {
        request = null;
        if (signed is null || string.IsNullOrEmpty(signed.Signature)) return Refuse("the request of an agent is not signed by a device of its person", out reason);
        if (!TryRead(signed.Payload, out var read, out reason)) return false;
        if (read.Kind != kind) return Refuse($"the agent request is a '{read.Kind}', not a '{kind}'", out reason);
        if (string.IsNullOrEmpty(machineId) || read.MachineId != machineId) return Refuse("the agent request is for another machine", out reason);
        if ((now - read.Time).Duration() > MaxSkew) return Refuse("the agent request was signed too long ago, or in the future", out reason);
        if (!FleetDevicePublicKey.TryParse(read.SignerKey, out var key) || key.DeviceId != read.SignerDeviceId)
            return Refuse("the agent request names a key that is not its signer's", out reason);
        if (!string.Equals(key.Fingerprint, channelFingerprint, StringComparison.Ordinal))
            return Refuse("the agent request is signed by another device than the one that sends it", out reason);
        if (!key.Verify(GetSignedBytes(signed.Payload), signed.Signature)) return Refuse("the signature of the agent request does not verify", out reason);
        request = read;
        return true;
    }

    private static bool Refuse(string why, out string reason)
    {
        reason = why;
        return false;
    }

    private static bool IsName([NotNullWhen(true)] string? text) => !string.IsNullOrEmpty(text) && text.Length <= MaxIdLength && !text.Any(char.IsControl);
}

/// <summary>The source-generated contracts of the agent requests.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never, WriteIndented = false)]
[JsonSerializable(typeof(FleetAgentRequest))]
[JsonSerializable(typeof(FleetSignedAgentRequest))]
public sealed partial class FleetAgentRequestJsonContext : JsonSerializerContext;
