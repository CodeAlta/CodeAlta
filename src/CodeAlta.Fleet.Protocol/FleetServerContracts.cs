using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>The methods and events of the server API over the control connection (<c>wss://&lt;server&gt;/fleet/v1</c>).</summary>
public static class FleetServerMethods
{
    /// <summary>The path of the control connection on the server.</summary>
    public const string ControlPath = "/fleet/v1";

    /// <summary>The state of the device, sent with each heartbeat; kept in memory by the server.</summary>
    public const string PresenceUpdate = "presence.update";

    /// <summary>A snapshot of the machine: name, system, capabilities.</summary>
    public const string InventoryPublish = "inventory.publish";

    /// <summary>The facts of the sessions of the machine, versioned per session.</summary>
    public const string FactsPublish = "facts.publish";

    /// <summary>
    /// Takes back the facts of sessions of the machine that are no longer published (made Private, or their space
    /// unlinked from the server): the server deletes them, audits it, and the directory says they are gone.
    /// </summary>
    public const string FactsWithdraw = "facts.withdraw";

    /// <summary>The machines, sessions and presence the device may see, then their changes as <see cref="DirectoryChanged"/> events.</summary>
    public const string DirectoryWatch = "directory.watch";

    /// <summary>The event of a change of the directory after <see cref="DirectoryWatch"/>.</summary>
    public const string DirectoryChanged = "directory.changed";

    /// <summary>
    /// The event the server sends before it closes a connection, with a <see cref="FleetError"/> that says why and what to
    /// do: <c>revoked</c>, <c>server_busy</c> (a reader that stayed behind), <c>connection_closed</c> (replaced by a newer
    /// connection of the device, or the server stops).
    /// </summary>
    public const string ConnectionClosing = "connection.closing";

    /// <summary>
    /// Asks the server for a relayed session channel to another device of the organization (<see cref="FleetRelayOpen"/>);
    /// answers once that device accepted, with the ticket of the viewer's side (<see cref="FleetRelayTicket"/>).
    /// </summary>
    public const string RelayOpen = "relay.open";

    /// <summary>The event the server sends to the device a viewer asked a relay to (<see cref="FleetRelayIncoming"/>).</summary>
    public const string RelayIncoming = "relay.incoming";

    /// <summary>The answer of a device to <see cref="RelayIncoming"/> that takes the relay (<see cref="FleetRelayRef"/>); gives the ticket of its side.</summary>
    public const string RelayAccept = "relay.accept";

    /// <summary>Declines an incoming relay, or ends one, from either side (<see cref="FleetRelayClose"/>).</summary>
    public const string RelayClose = "relay.close";

    /// <summary>
    /// The path of the WebSocket of a relayed channel: <c>/fleet/v1/relay/{ticket}</c>, opened by each side with its
    /// one-time ticket and a request signed with its device key. Its binary frames are the bytes of the channel, which is
    /// TLS between the two devices: the server pipes them without reading them.
    /// </summary>
    public const string RelayPath = "/fleet/v1/relay";

    /// <summary>
    /// The event of the telemetry setting of the fleet (<see cref="FleetSignedTelemetrySettings"/>): sent once a device
    /// connects, when the organization has one, and to every device online when an owner changes it. A device applies it
    /// only once it verified the signature of a controller device of its organization.
    /// </summary>
    public const string TelemetrySettings = "telemetry.settings";

    /// <summary>
    /// The event of an update the fleet asks of a node (<see cref="FleetUpdateInstruction"/>): sent when a campaign reaches
    /// it, and again when it connects while the campaign waits for it. The node checks the signed manifest (or the signed
    /// automatic policy) against the device list it anchored, keeps the instruction, and waits for its time.
    /// </summary>
    public const string UpdateInstruction = "update.instruction";

    /// <summary>The event of a campaign cancelled before the node switched (<see cref="FleetUpdateCancel"/>).</summary>
    public const string UpdateCancel = "update.cancel";

    /// <summary>Where the node stands in a campaign (<see cref="FleetUpdateReport"/>); the server records it for the campaign.</summary>
    public const string UpdateReport = "update.report";

    /// <summary>
    /// The event of the update policy of the fleet (<see cref="FleetSignedUpdateDocument"/>, the policy as its controller
    /// device signed it, whatever its mode): sent to the nodes online when an owner changes it, and to each node that
    /// connects. A node keeps the newest it accepted, by sequence, and refuses what a policy it superseded would allow.
    /// </summary>
    public const string UpdatePolicy = "update.policy";

    /// <summary>The kinds of <see cref="FleetDirectoryChange"/>.</summary>
    public static class ChangeKinds
    {
        /// <summary>A machine and its inventory.</summary>
        public const string Machine = "machine";

        /// <summary>The facts of a session.</summary>
        public const string Session = "session";

        /// <summary>
        /// A session whose facts its machine withdrew: <see cref="FleetDirectoryChange.Id"/> names it, and
        /// <see cref="FleetDirectoryChange.Session"/> the facts removed (their machine, epoch and version), so that a
        /// watcher keeps newer facts it already holds.
        /// </summary>
        public const string SessionRemoved = "session_removed";

        /// <summary>A device that came online, went offline or changed state.</summary>
        public const string Presence = "presence";

        /// <summary>An update campaign of the organization changed; <see cref="FleetDirectoryChange.Id"/> names it. Read it from the REST API.</summary>
        public const string Update = "update";
    }
}

/// <summary>The cursors of the publications of a device, given back in the welcome so that it resumes from them.</summary>
/// <param name="Facts">The last acknowledged sequence of <c>facts.publish</c>.</param>
/// <param name="Inventory">The last acknowledged sequence of <c>inventory.publish</c>.</param>
/// <param name="Statistics">The last acknowledged sequence of <c>statistics.publish</c>; 0 from a server that keeps none.</param>
public sealed record FleetPublicationCursors(long Facts, long Inventory, long Statistics = 0);

/// <summary>The parameters of <c>presence.update</c>.</summary>
/// <param name="State">The state of the device, such as <c>active</c>, <c>idle</c> or <c>busy</c>.</param>
/// <param name="ActiveSessions">How many sessions run on the device now, when it runs some.</param>
/// <param name="Update">Where the device stands in the update campaign it was last asked, or null.</param>
public sealed record FleetPresenceUpdate(string State, int? ActiveSessions = null, FleetUpdateReport? Update = null);

/// <summary>A device online, with its state.</summary>
/// <param name="DeviceId">The device.</param>
/// <param name="Online">Whether it holds a control connection now.</param>
/// <param name="State">Its last state (<see cref="FleetPresenceUpdate.State"/>), or <see langword="null"/>.</param>
/// <param name="ActiveSessions">How many sessions it runs, when it said so.</param>
/// <param name="Since">When it connected, or went offline.</param>
/// <param name="Update">Where it stands in the update campaign it was last asked, when it said so.</param>
public sealed record FleetPresence(string DeviceId, bool Online, string? State, int? ActiveSessions, DateTimeOffset Since, FleetUpdateReport? Update = null);

/// <summary>The snapshot of a machine (<c>doc/fleet/server.md</c>, machines): what it is and can do, never its content.</summary>
/// <param name="Name">The name of the machine.</param>
/// <param name="Os">Its system: <c>windows</c>, <c>macos</c>, <c>linux</c>.</param>
/// <param name="Architecture">Its processor architecture, such as <c>x64</c> or <c>arm64</c>.</param>
/// <param name="AppVersion">The version of CodeAlta on it.</param>
/// <param name="Capabilities">What it offers, such as <c>sandbox</c> or <c>moves</c>.</param>
/// <param name="Labels">The labels its owner gave it.</param>
/// <param name="Providers">The types of the providers ready on it, such as <c>anthropic</c>.</param>
public sealed record FleetMachineInventory(
    string Name,
    string Os,
    string? Architecture = null,
    string? AppVersion = null,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<string>? Providers = null);

/// <summary>The parameters of <c>inventory.publish</c>.</summary>
/// <param name="Sequence">The sequence of the publication on this device; an older or equal one is a no-op.</param>
/// <param name="Inventory">The snapshot.</param>
public sealed record FleetInventoryPublish(long Sequence, FleetMachineInventory Inventory);

/// <summary>The answer of a publication: the highest sequence the server holds for that stream of the device.</summary>
/// <param name="Ack">The acknowledged sequence.</param>
public sealed record FleetPublishAck(long Ack);

/// <summary>The counts of a session.</summary>
/// <param name="Turns">Its turns.</param>
/// <param name="ToolCalls">Its tool calls.</param>
/// <param name="InputTokens">Its input tokens.</param>
/// <param name="OutputTokens">Its output tokens.</param>
public sealed record FleetSessionCounts(long Turns = 0, long ToolCalls = 0, long InputTokens = 0, long OutputTokens = 0);

/// <summary>
/// The facts of one session, the allow-list a server may read (<c>doc/fleet/security-and-privacy.md</c>): no title, no
/// project, no path; those travel only inside <see cref="SealedMetadata"/>, which the server keeps without reading.
/// </summary>
/// <param name="SessionId">The id of the session.</param>
/// <param name="Epoch">The ownership epoch of the session; a fact of an older epoch is refused.</param>
/// <param name="Version">The version of the facts in their epoch; the server keeps the highest.</param>
/// <param name="State">The state, such as <c>running</c>, <c>requires_action</c> or <c>idle</c>.</param>
/// <param name="CreatedAt">When the session was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="ProviderType">The type of its provider, such as <c>anthropic</c>.</param>
/// <param name="Model">Its model.</param>
/// <param name="Counts">Its counts.</param>
/// <param name="SealedMetadata">Its descriptive metadata sealed for the person's devices, as opaque text, or <see langword="null"/>.</param>
public sealed record FleetSessionFact(
    string SessionId,
    long Epoch,
    long Version,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ProviderType = null,
    string? Model = null,
    FleetSessionCounts? Counts = null,
    string? SealedMetadata = null);

/// <summary>The parameters of <c>facts.publish</c>.</summary>
/// <param name="Sequence">The sequence of the batch on this device.</param>
/// <param name="Facts">The facts.</param>
public sealed record FleetFactsPublish(long Sequence, IReadOnlyList<FleetSessionFact> Facts);

/// <summary>The answer of <c>facts.publish</c>.</summary>
/// <param name="Ack">The acknowledged sequence.</param>
/// <param name="Applied">How many facts changed what the server holds.</param>
/// <param name="Unchanged">How many facts the server already held (an equal or older version).</param>
/// <param name="Stale">The sessions whose fact was refused: an older epoch, or another machine at the same epoch.</param>
public sealed record FleetFactsPublishResult(long Ack, int Applied, int Unchanged, IReadOnlyList<string> Stale);

/// <summary>The parameters of <c>facts.withdraw</c>.</summary>
/// <param name="SessionIds">The sessions of the machine whose facts the server deletes.</param>
public sealed record FleetFactsWithdraw(IReadOnlyList<string> SessionIds);

/// <summary>The answer of <c>facts.withdraw</c>.</summary>
/// <param name="Removed">The sessions whose facts the server held for this machine and deleted; the others it did not hold.</param>
public sealed record FleetFactsWithdrawResult(IReadOnlyList<string> Removed);

/// <summary>A machine as the directory shows it.</summary>
/// <param name="DeviceId">The device of the machine.</param>
/// <param name="Kind">The class of the device (<see cref="FleetDeviceKinds"/>).</param>
/// <param name="Inventory">Its last inventory.</param>
/// <param name="UpdatedAt">When the inventory was received.</param>
public sealed record FleetDirectoryMachine(string DeviceId, string Kind, FleetMachineInventory Inventory, DateTimeOffset UpdatedAt);

/// <summary>A published session as the directory shows it.</summary>
/// <param name="MachineId">The device of the machine that owns it.</param>
/// <param name="Fact">Its facts.</param>
public sealed record FleetDirectorySession(string MachineId, FleetSessionFact Fact);

/// <summary>The answer of <c>directory.watch</c>: what the device may see now; changes follow as events.</summary>
/// <param name="Machines">The machines.</param>
/// <param name="Sessions">The published sessions.</param>
/// <param name="Presence">The devices online.</param>
public sealed record FleetDirectorySnapshot(
    IReadOnlyList<FleetDirectoryMachine> Machines,
    IReadOnlyList<FleetDirectorySession> Sessions,
    IReadOnlyList<FleetPresence> Presence);

/// <summary>The payload of a <c>directory.changed</c> event: one of its members is set, as <see cref="Kind"/> says.</summary>
/// <param name="Kind">What changed (<see cref="FleetServerMethods.ChangeKinds"/>).</param>
/// <param name="Id">The id of what changed: a device or a session.</param>
/// <param name="Machine">The machine, for a machine change.</param>
/// <param name="Session">The session, for a session change.</param>
/// <param name="Presence">The presence, for a presence change.</param>
public sealed record FleetDirectoryChange(
    string Kind,
    string Id,
    FleetDirectoryMachine? Machine = null,
    FleetDirectorySession? Session = null,
    FleetPresence? Presence = null);

/// <summary>The codes of the refusals of a relay, beside those of <see cref="FleetErrorCodes"/>.</summary>
public static class FleetRelayErrorCodes
{
    /// <summary>The device asked for is not online: it holds no control connection.</summary>
    public const string TargetOffline = "relay.target_offline";

    /// <summary>The device asked for declined the relay, or did not answer in time.</summary>
    public const string Declined = "relay.declined";

    /// <summary>The device has as many relays open as the server allows.</summary>
    public const string TooMany = "relay.too_many";

    /// <summary>The ticket is unknown, used, expired, or not this device's.</summary>
    public const string TicketInvalid = "relay.invalid_ticket";

    /// <summary>The relay is not known, or not this device's.</summary>
    public const string NotFound = "relay.not_found";
}

/// <summary>The parameters of <c>relay.open</c>.</summary>
/// <param name="Target">The device to reach.</param>
public sealed record FleetRelayOpen(string Target);

/// <summary>A side of a relay: its ticket, to open <see cref="FleetServerMethods.RelayPath"/> once, before it expires.</summary>
/// <param name="RelayId">The relay.</param>
/// <param name="Ticket">The one-time ticket of this side.</param>
/// <param name="ExpiresAt">When the ticket stops working.</param>
/// <param name="Peer">The device at the other end.</param>
public sealed record FleetRelayTicket(string RelayId, string Ticket, DateTimeOffset ExpiresAt, string Peer);

/// <summary>The payload of <c>relay.incoming</c>: a device of the organization asks for a channel to this one.</summary>
/// <param name="RelayId">The relay.</param>
/// <param name="From">The device that asks; the channel inside the relay authenticates it again, with its key.</param>
/// <param name="ExpiresAt">When the offer lapses.</param>
public sealed record FleetRelayIncoming(string RelayId, string From, DateTimeOffset ExpiresAt);

/// <summary>The parameters of <c>relay.accept</c>.</summary>
/// <param name="RelayId">The relay.</param>
public sealed record FleetRelayRef(string RelayId);

/// <summary>The parameters of <c>relay.close</c>.</summary>
/// <param name="RelayId">The relay.</param>
/// <param name="Reason">Why, in a word, such as <c>declined</c>; or <see langword="null"/>.</param>
public sealed record FleetRelayClose(string RelayId, string? Reason = null);

/// <summary>The source-generated contracts of the server API over the control connection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetPublicationCursors))]
[JsonSerializable(typeof(FleetPresenceUpdate))]
[JsonSerializable(typeof(FleetPresence))]
[JsonSerializable(typeof(FleetMachineInventory))]
[JsonSerializable(typeof(FleetInventoryPublish))]
[JsonSerializable(typeof(FleetPublishAck))]
[JsonSerializable(typeof(FleetSessionFact))]
[JsonSerializable(typeof(FleetFactsPublish))]
[JsonSerializable(typeof(FleetFactsPublishResult))]
[JsonSerializable(typeof(FleetFactsWithdraw))]
[JsonSerializable(typeof(FleetFactsWithdrawResult))]
[JsonSerializable(typeof(FleetDirectoryMachine))]
[JsonSerializable(typeof(FleetDirectorySession))]
[JsonSerializable(typeof(FleetDirectorySnapshot))]
[JsonSerializable(typeof(FleetDirectoryChange))]
[JsonSerializable(typeof(FleetError))]
[JsonSerializable(typeof(FleetRelayOpen))]
[JsonSerializable(typeof(FleetRelayTicket))]
[JsonSerializable(typeof(FleetRelayIncoming))]
[JsonSerializable(typeof(FleetRelayRef))]
[JsonSerializable(typeof(FleetRelayClose))]
[JsonSerializable(typeof(FleetSignedTelemetrySettings))]
[JsonSerializable(typeof(FleetUpdateInstruction))]
[JsonSerializable(typeof(FleetUpdateCancel))]
[JsonSerializable(typeof(FleetSignedUpdateDocument))]
[JsonSerializable(typeof(FleetUpdateReport))]
public sealed partial class FleetServerJsonContext : JsonSerializerContext;
