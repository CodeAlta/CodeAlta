using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol.Api;

/// <summary>The body of <c>PUT /api/v1/automations/{id}</c>: a version the calling controller device signed, or a removal.</summary>
/// <param name="Version">The signed version, sealed for its machine.</param>
public sealed record PutAutomationRequest(FleetSignedAutomationVersion Version);

/// <summary>
/// An automation version a server holds for a machine: the facts it reads (id, machine, number, enabled, trigger kinds,
/// signer), where its delivery stands, and the version as signed, whose content the server does not read.
/// </summary>
/// <param name="Id">The automation.</param>
/// <param name="MachineId">The machine it is for.</param>
/// <param name="Number">The number of the version.</param>
/// <param name="Removed">Whether it takes the automation away from the machine.</param>
/// <param name="Enabled">Whether its triggers start it.</param>
/// <param name="Triggers">The kinds of its triggers.</param>
/// <param name="SignerDeviceId">The controller device that signed it.</param>
/// <param name="UpdatedAt">When the server stored it.</param>
/// <param name="State"><c>pending</c> (not taken by the machine yet), <c>applied</c> or <c>refused</c>.</param>
/// <param name="Message">Why the machine refused it; null otherwise.</param>
/// <param name="Version">The version as signed.</param>
public sealed record AutomationDto(string Id, string MachineId, long Number, bool Removed, bool Enabled, IReadOnlyList<string> Triggers, string SignerDeviceId,
    DateTimeOffset UpdatedAt, string State, string? Message, FleetSignedAutomationVersion Version);

/// <summary>The automations of the caller.</summary>
/// <param name="Automations">One per automation and machine.</param>
public sealed record AutomationListDto(IReadOnlyList<AutomationDto> Automations);

/// <summary>A run of an automation, as its machine reported it.</summary>
/// <param name="RunId">The run.</param>
/// <param name="AutomationId">The automation.</param>
/// <param name="MachineId">The machine that ran it.</param>
/// <param name="Number">The version that ran.</param>
/// <param name="Trigger">What started it.</param>
/// <param name="Status">Its status; <c>claimed</c> for a due time claimed and not reported yet.</param>
/// <param name="StartedAt">When it started or was claimed.</param>
/// <param name="EndedAt">When it ended; null otherwise.</param>
/// <param name="Occurrence">The due time it took; null for a run by hand or by an event.</param>
/// <param name="LeaseUntil">Until when its machine is known to be at work on it.</param>
/// <param name="SessionId">The session it started on its machine; null when none.</param>
/// <param name="Message">Why it failed or was skipped; null otherwise.</param>
public sealed record AutomationRunDto(string RunId, string AutomationId, string MachineId, long Number, string Trigger, string Status, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string? Occurrence, DateTimeOffset? LeaseUntil, string? SessionId, string? Message)
{
    /// <summary>Gets the input tokens of its session, the prompt cache included; null when unknown.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Gets the output tokens of its session; null when unknown.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Gets the cost the provider reported for its session, in <see cref="CostUnit"/>; null when none was.</summary>
    public double? Cost { get; init; }

    /// <summary>Gets the unit of <see cref="Cost"/>: <c>usd</c>, or the one the provider named.</summary>
    public string? CostUnit { get; init; }
}

/// <summary>Runs of an automation, the newest first.</summary>
/// <param name="Runs">The runs.</param>
public sealed record AutomationRunListDto(IReadOnlyList<AutomationRunDto> Runs);

/// <summary>The source-generated contracts of the automation resources of the REST API.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PutAutomationRequest))]
[JsonSerializable(typeof(AutomationDto))]
[JsonSerializable(typeof(AutomationListDto))]
[JsonSerializable(typeof(AutomationRunListDto))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(ApiProblemDto))]
public sealed partial class FleetAutomationApiJsonContext : JsonSerializerContext;
