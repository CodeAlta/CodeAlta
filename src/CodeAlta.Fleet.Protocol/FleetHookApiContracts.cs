using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol.Api;

/// <summary>Creates a hook for one machine of the caller's person.</summary>
/// <param name="MachineId">The machine its events go to.</param>
/// <param name="Source">What pushes to it: <c>incident_io</c> or <c>webhook</c>.</param>
/// <param name="Label">A name for the person, such as the automation it is for; null for none.</param>
public sealed record CreateHookRequest(string MachineId, string Source, string? Label);

/// <summary>A hook of the server: an address a tool pushes its events to, for one machine.</summary>
/// <param name="Id">Its id, which is part of its address and hard to guess.</param>
/// <param name="Path">Its path on the server: <c>/api/v1/hooks/{id}</c>, the address a tool is given with the server's.</param>
/// <param name="MachineId">The machine its events go to.</param>
/// <param name="Source">What pushes to it.</param>
/// <param name="Label">Its name.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="LastReceivedAt">When it last received an event; null before.</param>
/// <param name="Pending">How many events wait for the machine.</param>
public sealed record HookDto(string Id, string Path, string MachineId, string Source, string Label, DateTimeOffset CreatedAt, DateTimeOffset? LastReceivedAt, int Pending);

/// <summary>The hooks of the caller's person.</summary>
/// <param name="Hooks">The hooks.</param>
public sealed record HookListDto(IReadOnlyList<HookDto> Hooks);

/// <summary>One event a hook received, as facts: never its content.</summary>
/// <param name="DeliveryId">The id of the delivery.</param>
/// <param name="ReceivedAt">When it came.</param>
/// <param name="State"><c>pending</c>, <c>accepted</c>, <c>refused</c> or <c>expired</c>.</param>
/// <param name="Message">What the machine said of it.</param>
/// <param name="AnsweredAt">When the machine answered; null before.</param>
public sealed record HookEventDto(string DeliveryId, DateTimeOffset ReceivedAt, string State, string? Message, DateTimeOffset? AnsweredAt);

/// <summary>The latest events of a hook, the newest first.</summary>
/// <param name="Events">The events.</param>
public sealed record HookEventListDto(IReadOnlyList<HookEventDto> Events);

/// <summary>What the ingress answers a tool that pushed an event.</summary>
/// <param name="DeliveryId">The id of the delivery.</param>
/// <param name="Duplicate">Whether the same event was received before: it is not delivered twice.</param>
public sealed record HookReceiptDto(string DeliveryId, bool Duplicate);

/// <summary>The states of a hook event.</summary>
public static class HookEventStates
{
    /// <summary>Waiting for its machine.</summary>
    public const string Pending = "pending";

    /// <summary>An automation of the machine took it.</summary>
    public const string Accepted = "accepted";

    /// <summary>The machine took it for no automation: its signature did not verify, or no enabled automation names the hook.</summary>
    public const string Refused = "refused";

    /// <summary>Its machine did not take it in time.</summary>
    public const string Expired = "expired";
}

/// <summary>The sources of hooks.</summary>
public static class HookSources
{
    /// <summary>incident.io.</summary>
    public const string IncidentIo = "incident_io";

    /// <summary>Any tool that signs its webhooks.</summary>
    public const string Webhook = "webhook";
}

/// <summary>The source-generated contracts of the hooks of the REST API.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(CreateHookRequest))]
[JsonSerializable(typeof(HookDto))]
[JsonSerializable(typeof(HookListDto))]
[JsonSerializable(typeof(HookEventListDto))]
[JsonSerializable(typeof(HookReceiptDto))]
public sealed partial class FleetHookApiJsonContext : JsonSerializerContext;
