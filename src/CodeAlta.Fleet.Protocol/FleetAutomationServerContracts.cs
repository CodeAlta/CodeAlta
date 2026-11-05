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

    /// <summary>
    /// The event of a run asked by hand (<see cref="FleetSignedAutomationRunRequest"/>), sent to the machine when it is
    /// online; the machine verifies the request before it runs the automation, and reports the run as the others.
    /// </summary>
    public const string Run = "automations.run";

    /// <summary>
    /// The event of a take-over of a run in progress asked by a person (<see cref="FleetSignedAutomationRunRequest"/> whose
    /// action is <see cref="FleetAutomationRunRequest.TakeOverAction"/>), sent to the machine when it is online; the machine
    /// verifies it as a run asked by hand before the run waits for that person.
    /// </summary>
    public const string TakeOver = "automations.takeOver";

    /// <summary>
    /// What a machine did with a take-over (<see cref="FleetAutomationTakeOverAck"/>): the server tells the person who asked that
    /// the run is theirs only once its machine accepted it, and why it refused it otherwise.
    /// </summary>
    public const string TakeOverAck = "automations.takeOverAck";

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

/// <summary>What a machine did with a take-over of a run.</summary>
/// <param name="AutomationId">The automation the take-over named.</param>
/// <param name="RunId">The run it named.</param>
/// <param name="Accepted">Whether the run now waits for the person who asked, and no approval stops it.</param>
/// <param name="Message">Why it refused it, in a line; null when accepted.</param>
public sealed record FleetAutomationTakeOverAck(string AutomationId, string RunId, bool Accepted, string? Message);

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

/// <summary>Runs a machine reports, and when the schedules of its automations are next due.</summary>
/// <param name="Runs">The runs, the newest last; none when only the schedules changed.</param>
public sealed record FleetAutomationRunFacts(IReadOnlyList<FleetAutomationRunFact> Runs)
{
    /// <summary>Gets when the schedules of the automations of the server are next due on the machine; null when unchanged.</summary>
    public IReadOnlyList<FleetAutomationSchedule>? Schedules { get; init; }
}

/// <summary>When an automation is next due on its machine, as the machine reads its schedules.</summary>
/// <param name="AutomationId">The automation.</param>
/// <param name="NextDue">When a schedule of it is next due; null without one, while disabled or paused.</param>
public sealed record FleetAutomationSchedule(string AutomationId, DateTimeOffset? NextDue);

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

    /// <summary>Gets the input tokens of its session, the prompt cache included; null when unknown.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Gets the output tokens of its session; null when unknown.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Gets the cost the provider reported for its session, in <see cref="CostUnit"/>; null when none was.</summary>
    public double? Cost { get; init; }

    /// <summary>Gets the unit of <see cref="Cost"/>: <c>usd</c>, or the one the provider named.</summary>
    public string? CostUnit { get; init; }

    /// <summary>Gets the service identity it ran as, for an automation of a team or of the organization; null for a personal one.</summary>
    public string? ActingAs { get; init; }

    /// <summary>
    /// Gets who the automation says answers the approvals of a run that waits: <c>oncall</c> for the person on call of the team
    /// that owns it; null for its owner. Only a waiting run says it.
    /// </summary>
    public string? Approvers { get; init; }

    /// <summary>
    /// Gets who the on-call tool of the automation said was on call, by email and name, for a run that waits with
    /// <see cref="Approvers"/> <c>oncall</c>: the server asks them when they answer the approvals of the automation, else the
    /// rotation of its team. Null for none.
    /// </summary>
    public IReadOnlyList<string>? OnCall { get; init; }

    /// <summary>
    /// Gets the id of the approval a run that waits waits for, a new one each time it starts to wait: the server tells a person of
    /// each approval once, not of the run once. Null for a run that waits for none, or a machine that does not say it.
    /// </summary>
    public string? Approval { get; init; }
}

/// <summary>The source-generated contracts of the automation methods of the control connection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetAutomationDelivery))]
[JsonSerializable(typeof(FleetAutomationAcks))]
[JsonSerializable(typeof(FleetAutomationClaimRequest))]
[JsonSerializable(typeof(FleetAutomationClaimResult))]
[JsonSerializable(typeof(FleetAutomationRunFacts))]
[JsonSerializable(typeof(FleetAutomationTakeOverAck))]
public sealed partial class FleetAutomationServerJsonContext : JsonSerializerContext;
