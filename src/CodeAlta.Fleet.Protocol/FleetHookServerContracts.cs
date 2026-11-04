using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// The methods of the control connection for the hooks of the server (<c>doc/fleet/orchestration.md</c>, "Triggers"): a
/// tool that only pushes (incident.io, a webhook) sends its events to <c>POST /api/v1/hooks/{id}</c>; the server keeps
/// the raw request for the machine the hook names and hands it over as it came. The server holds no secret of the
/// sender: the machine checks the signature, and an event only selects an automation the machine holds as signed.
/// </summary>
public static class FleetHookMethods
{
    /// <summary>
    /// The event of a hook event for the machine (<see cref="FleetHookEvent"/>): sent when one arrives for a machine online,
    /// and to a machine that connects while some wait for it. The machine answers with <see cref="Ack"/>.
    /// </summary>
    public const string Event = "hooks.event";

    /// <summary>What a machine did with hook events (<see cref="FleetHookAcks"/>): the server then forgets their bodies.</summary>
    public const string Ack = "hooks.ack";

    /// <summary>The feature a server announces in its welcome when it receives hook events.</summary>
    public const string Feature = "hooks";

    /// <summary>The path of the ingress of a hook, under which its id comes.</summary>
    public const string IngressPath = "/api/v1/hooks";
}

/// <summary>An event a hook received, as it was pushed: the headers and the body its sender signed.</summary>
/// <param name="DeliveryId">The id the server gave this delivery, which the machine acknowledges.</param>
/// <param name="HookId">The hook.</param>
/// <param name="ReceivedAt">When the server received it.</param>
/// <param name="Headers">Its headers, names in lower case, without those of the connection (cookies, proxies, host).</param>
/// <param name="Body">Its body, byte for byte, in base64.</param>
public sealed record FleetHookEvent(string DeliveryId, string HookId, DateTimeOffset ReceivedAt, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>What a machine did with hook events.</summary>
/// <param name="Acks">One per event.</param>
public sealed record FleetHookAcks(IReadOnlyList<FleetHookAck> Acks);

/// <summary>What a machine did with one hook event.</summary>
/// <param name="DeliveryId">The delivery.</param>
/// <param name="Accepted">Whether an automation of the machine took it (its signature verified).</param>
/// <param name="Message">What happened, in a line, without the event's content.</param>
public sealed record FleetHookAck(string DeliveryId, bool Accepted, string? Message);

/// <summary>The source-generated contracts of the hook methods of the control connection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetHookEvent))]
[JsonSerializable(typeof(FleetHookAcks))]
public sealed partial class FleetHookServerJsonContext : JsonSerializerContext;
