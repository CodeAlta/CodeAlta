using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// The methods of the control connection for the automations a person gives the machines of a server
/// (<c>doc/fleet/orchestration.md</c>): the server stores each version a controller device signed and delivers it to its
/// machine, which verifies it before it takes it; the machine claims each due time before it runs it and reports its
/// runs as facts, without the prompt, the answer or what the run was about.
/// </summary>
public static class FleetAutomationMethods
{
    /// <summary>
    /// The event of the versions a machine has not taken yet (<see cref="FleetAutomationDelivery"/>): sent when a version is
    /// stored for a machine online, and to a machine that connects while one waits for it. The machine answers with
    /// <see cref="Ack"/>.
    /// </summary>
    public const string Assigned = "automations.assigned";

    /// <summary>What a machine did with delivered versions (<see cref="FleetAutomationAcks"/>).</summary>
    public const string Ack = "automations.ack";

    /// <summary>
    /// A machine claims a due time of an automation before it runs it (<see cref="FleetAutomationClaimRequest"/>,
    /// answered by <see cref="FleetAutomationClaimResult"/>): one run per (automation, due time), whatever the machine.
    /// </summary>
    public const string Claim = "automations.claim";

    /// <summary>A machine reports runs (<see cref="FleetAutomationRunFacts"/>); the server keeps them for the owner.</summary>
    public const string Runs = "automations.runs";

    /// <summary>The feature a server announces in its welcome when it holds automations.</summary>
    public const string Feature = "automations";
}

/// <summary>The versions a server delivers to a machine, as their controller devices signed them; removals included.</summary>
/// <param name="Versions">The versions, at most one per automation.</param>
public sealed record FleetAutomationDelivery(IReadOnlyList<FleetSignedAutomationVersion> Versions);

/// <summary>What a machine did with the versions delivered to it.</summary>
/// <param name="Acks">One per version.</param>
public sealed record FleetAutomationAcks(IReadOnlyList<FleetAutomationAck> Acks);

/// <summary>What a machine did with one version.</summary>
/// <param name="AutomationId">The automation.</param>
/// <param name="Number">The number of the version.</param>
/// <param name="Applied">Whether the machine holds that version now (or a newer one).</param>
/// <param name="Message">Why it refused it; null when applied.</param>
public sealed record FleetAutomationAck(string AutomationId, long Number, bool Applied, string? Message);

/// <summary>A machine claims a due time of an automation.</summary>
/// <param name="AutomationId">The automation.</param>
/// <param name="Number">The version the machine holds.</param>
/// <param name="Occurrence">What is due: the trigger and the due time, as the engine names it.</param>
/// <param name="RunId">The run that would take it.</param>
/// <param name="LeaseUntil">Until when the machine is at work on it unless it reports the run.</param>
public sealed record FleetAutomationClaimRequest(string AutomationId, long Number, string Occurrence, string RunId, DateTimeOffset LeaseUntil);

/// <summary>The answer to a claim.</summary>
/// <param name="Granted">Whether the run may start.</param>
/// <param name="Message">Why not; null when granted.</param>
public sealed record FleetAutomationClaimResult(bool Granted, string? Message);

/// <summary>Runs a machine reports.</summary>
/// <param name="Runs">The runs, the newest last.</param>
public sealed record FleetAutomationRunFacts(IReadOnlyList<FleetAutomationRunFact> Runs);

/// <summary>The facts of one run: never the prompt, the answer or what the trigger was about.</summary>
/// <param name="RunId">The run.</param>
/// <param name="AutomationId">The automation.</param>
/// <param name="Number">The version that ran.</param>
/// <param name="Trigger">What started it: <c>manual</c>, or the kind of the trigger.</param>
/// <param name="Status">Its status: running, completed, failed, cancelled, interrupted or skipped.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="EndedAt">When it ended; null while it runs.</param>
public sealed record FleetAutomationRunFact(string RunId, string AutomationId, long Number, string Trigger, string Status, DateTimeOffset StartedAt, DateTimeOffset? EndedAt)
{
    /// <summary>Gets the session it started on its machine; null when none.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets why it failed or was skipped, in a line; null otherwise.</summary>
    public string? Message { get; init; }
}

/// <summary>The source-generated contracts of the automation methods of the control connection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetAutomationDelivery))]
[JsonSerializable(typeof(FleetAutomationAcks))]
[JsonSerializable(typeof(FleetAutomationClaimRequest))]
[JsonSerializable(typeof(FleetAutomationClaimResult))]
[JsonSerializable(typeof(FleetAutomationRunFacts))]
public sealed partial class FleetAutomationServerJsonContext : JsonSerializerContext;
