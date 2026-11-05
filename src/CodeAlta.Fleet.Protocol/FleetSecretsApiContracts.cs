using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol.Api;

/// <summary>The events of the control connection about the named values of the fleet.</summary>
public static class FleetSecretMethods
{
    /// <summary>
    /// Sent to a machine when a value it holds or may hold changed (set, rotated, given, taken away, deleted), an
    /// environment of its person changed, or an approval of one of its runs was given: it reads its copies again. No payload.
    /// </summary>
    public const string Changed = "secrets.changed";
}

/// <summary>The kinds of named values of the fleet (<c>doc/fleet/secrets.md</c>, the model).</summary>
public static class FleetSecretKinds
{
    /// <summary>A value written and used, never read back by a person or the API.</summary>
    public const string Secret = "secret";

    /// <summary>A value that is not secret: the person's devices read it back.</summary>
    public const string Variable = "variable";

    /// <summary>Gets whether a text is a known kind.</summary>
    /// <param name="kind">The text.</param>
    /// <returns>Whether it is <see cref="Secret"/> or <see cref="Variable"/>.</returns>
    public static bool IsKnown(string? kind) => kind is Secret or Variable;
}

/// <summary>
/// The scopes of the named values of the fleet, the narrowest first when a run resolves a name (<c>doc/fleet/secrets.md</c>,
/// scopes): the environment of the run, the project, the space, the person; teams and the organization come with their roles.
/// </summary>
public static class FleetSecretScopeKinds
{
    /// <summary>An environment of the person (its id at the server).</summary>
    public const string Environment = "environment";

    /// <summary>A project of the person: the blind index of its repository with the key of its space, which the server cannot reverse.</summary>
    public const string Project = "project";

    /// <summary>A space of the person: the opaque id the server knows it by.</summary>
    public const string Space = "space";

    /// <summary>Every work of the person, on the machines it gives the value to.</summary>
    public const string Person = "person";

    /// <summary>A team: reserved, it comes with teams and their roles.</summary>
    public const string Team = "team";

    /// <summary>The organization: reserved, it comes with its roles and policies.</summary>
    public const string Organization = "organization";

    /// <summary>Gets whether a scope can hold values in this version: the scopes of one person.</summary>
    /// <param name="kind">The kind of the scope.</param>
    /// <returns>Whether it is <see cref="Person"/>, <see cref="Space"/>, <see cref="Project"/> or <see cref="Environment"/>.</returns>
    public static bool IsAvailable(string? kind) => kind is Person or Space or Project or Environment;

    /// <summary>Gets whether a scope is known, available or reserved.</summary>
    /// <param name="kind">The kind of the scope.</param>
    /// <returns>Whether it is one of the kinds of this class.</returns>
    public static bool IsKnown(string? kind) => IsAvailable(kind) || kind is Team or Organization;

    /// <summary>Gets whether a scope names one thing by an id (every scope but the person).</summary>
    /// <param name="kind">The kind of the scope.</param>
    /// <returns>Whether its id is required.</returns>
    public static bool HasId(string? kind) => kind is not Person;
}

/// <summary>Where a named value applies.</summary>
/// <param name="Kind">The kind of the scope (<see cref="FleetSecretScopeKinds"/>).</param>
/// <param name="Id">The id of what it names; null for the person.</param>
public sealed record FleetSecretScopeDto(string Kind, string? Id = null);

/// <summary>The copy of a value sealed for one device: only that device opens it.</summary>
/// <param name="DeviceId">The device.</param>
/// <param name="Sealed">The sealed copy, as opaque text.</param>
public sealed record FleetSecretCopyDto(string DeviceId, string Sealed);

/// <summary>
/// The body of <c>PUT /api/v1/secrets/{id}</c>: a value a controller device of the person sealed for the devices allowed
/// to use it and for its person's controller devices; version 1 sets it, the next version rotates it.
/// </summary>
/// <param name="Kind">The kind of the value (<see cref="FleetSecretKinds"/>).</param>
/// <param name="Name">Its name, the name of an environment variable.</param>
/// <param name="Scope">Where it applies.</param>
/// <param name="Version">Its version: 1 for a new value, one more than the current version for a rotation.</param>
/// <param name="HostOnly">Whether it goes only to host-side tools (MCP servers, plugin tools, providers), never to the agent's shell.</param>
/// <param name="Copies">The sealed copies, one per device; the device that sets it holds one.</param>
public sealed record SetFleetSecretRequest(string Kind, string Name, FleetSecretScopeDto Scope, int Version, bool HostOnly, IReadOnlyList<FleetSecretCopyDto> Copies);

/// <summary>The body of <c>POST /api/v1/secrets/{id}/copies</c>: copies of the current version for devices that hold none (a machine allowed later).</summary>
/// <param name="Version">The version the copies are of, which must be the current one.</param>
/// <param name="Copies">The copies.</param>
public sealed record AddFleetSecretCopiesRequest(int Version, IReadOnlyList<FleetSecretCopyDto> Copies);

/// <summary>
/// A named value as the server holds it: what is known of it, never its value; the copy sealed for the calling device,
/// which only that device opens.
/// </summary>
/// <param name="Id">The id of the value, which the sealed copies are bound to.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Name">Its name.</param>
/// <param name="Scope">Where it applies.</param>
/// <param name="Version">Its current version.</param>
/// <param name="HostOnly">Whether it goes only to host-side tools.</param>
/// <param name="SetByDeviceId">The device that set its current version.</param>
/// <param name="SetByPersonId">The person of that device.</param>
/// <param name="SetAt">When its current version was set.</param>
/// <param name="CreatedAt">When it was first set.</param>
/// <param name="Holders">The devices that hold a copy of its current version.</param>
/// <param name="Flagged">The devices revoked while they held a copy of its current version: it needs a rotation.</param>
/// <param name="Copy">The copy of the calling device; null when it holds none.</param>
/// <param name="HolderCopies">
/// The copies of the current version the holders have, for a controller device of the person: each carries the grant a
/// controller device signed, which tells which machines a controller gave the value to (a rotation seals for those only,
/// never for a holder the server lists by itself); null for a machine, and from a server of an older version.
/// </param>
public sealed record FleetSecretDto(string Id, string Kind, string Name, FleetSecretScopeDto Scope, int Version, bool HostOnly, string SetByDeviceId, string? SetByPersonId,
    DateTimeOffset SetAt, DateTimeOffset CreatedAt, IReadOnlyList<string> Holders, IReadOnlyList<string> Flagged, string? Copy,
    IReadOnlyList<FleetSecretCopyDto>? HolderCopies = null);

/// <summary>Named values.</summary>
/// <param name="Secrets">The values.</param>
public sealed record FleetSecretListDto(IReadOnlyList<FleetSecretDto> Secrets);

/// <summary>A use of a named value by a run of a machine: which run, which value, which version, never the value.</summary>
/// <param name="SecretId">The value.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">The version used.</param>
/// <param name="RunId">The run (a session, a run of an automation).</param>
/// <param name="UsedAt">When it was used.</param>
/// <param name="MachineId">The machine; set by the server, which takes it from the signed request.</param>
public sealed record FleetSecretUseDto(string SecretId, string Name, int Version, string RunId, DateTimeOffset UsedAt, string? MachineId = null);

/// <summary>The body of <c>POST /api/v1/secret-uses</c>: the uses a machine recorded since it last published.</summary>
/// <param name="Uses">The uses.</param>
public sealed record PublishFleetSecretUsesRequest(IReadOnlyList<FleetSecretUseDto> Uses);

/// <summary>Uses of named values, the newest first.</summary>
/// <param name="Uses">The uses.</param>
public sealed record FleetSecretUseListDto(IReadOnlyList<FleetSecretUseDto> Uses);

/// <summary>The body of <c>PUT /api/v1/environments/{id}</c>: an environment a controller device of its person signed.</summary>
/// <param name="Environment">The signed definition.</param>
public sealed record PutFleetEnvironmentRequest(FleetSignedEnvironment Environment);

/// <summary>An environment as the server holds it: the facts it reads, and the definition as signed.</summary>
/// <param name="Id">The environment.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">The version of its definition.</param>
/// <param name="SignerDeviceId">The device that signed it.</param>
/// <param name="UpdatedAt">When the server stored it.</param>
/// <param name="Signed">The definition as signed.</param>
public sealed record FleetEnvironmentDto(string Id, string Name, long Version, string SignerDeviceId, DateTimeOffset UpdatedAt, FleetSignedEnvironment Signed);

/// <summary>Environments.</summary>
/// <param name="Environments">The environments.</param>
public sealed record FleetEnvironmentListDto(IReadOnlyList<FleetEnvironmentDto> Environments);

/// <summary>The body of <c>POST /api/v1/environments/{id}/approvals</c>: a machine asks the approval of a run that uses an environment.</summary>
/// <param name="RunId">The run, which the approval is bound to.</param>
/// <param name="Nonce">A random value of the machine, which the approval is bound to too.</param>
/// <param name="EnvironmentVersion">The version of the definition the machine verified.</param>
/// <param name="AutomationId">The automation of the run; null for a session.</param>
/// <param name="Title">What the run is, as the machine names it for the approver.</param>
public sealed record RequestFleetApprovalRequest(string RunId, string Nonce, long EnvironmentVersion, string? AutomationId, string? Title);

/// <summary>The body of <c>POST /api/v1/approvals/{id}/approve</c>: the approval an approver's controller device signed.</summary>
/// <param name="Approval">The signed approval.</param>
public sealed record ApproveFleetApprovalRequest(FleetSignedApproval Approval);

/// <summary>An approval a run asked, as the server holds it.</summary>
/// <param name="Id">The approval.</param>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="EnvironmentName">Its name.</param>
/// <param name="EnvironmentVersion">The version of its definition the machine verified.</param>
/// <param name="MachineId">The machine of the run.</param>
/// <param name="RunId">The run.</param>
/// <param name="Nonce">The nonce of the machine.</param>
/// <param name="AutomationId">The automation of the run; null for a session.</param>
/// <param name="Title">What the run is.</param>
/// <param name="Status"><c>pending</c> or <c>approved</c>.</param>
/// <param name="RequestedAt">When the machine asked.</param>
/// <param name="Approval">The signed approval, once given.</param>
public sealed record FleetApprovalDto(string Id, string EnvironmentId, string EnvironmentName, long EnvironmentVersion, string MachineId, string RunId, string Nonce,
    string? AutomationId, string? Title, string Status, DateTimeOffset RequestedAt, FleetSignedApproval? Approval)
{
    /// <summary>
    /// Gets the signed definition of the environment as the server holds it, which an approver of another person verifies to know
    /// what they approve (its name, its approvers), without its values or the right to manage it; null from a server that gives none.
    /// </summary>
    public FleetSignedEnvironment? Environment { get; init; }
}

/// <summary>Approvals.</summary>
/// <param name="Approvals">The approvals.</param>
public sealed record FleetApprovalListDto(IReadOnlyList<FleetApprovalDto> Approvals);

/// <summary>The states of an approval.</summary>
public static class FleetApprovalStates
{
    /// <summary>Asked by the machine, not given yet.</summary>
    public const string Pending = "pending";

    /// <summary>Given: the signed approval is held.</summary>
    public const string Approved = "approved";
}

/// <summary>The source-generated contracts of the secrets, environments and approvals of the REST API.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SetFleetSecretRequest))]
[JsonSerializable(typeof(AddFleetSecretCopiesRequest))]
[JsonSerializable(typeof(FleetSecretDto))]
[JsonSerializable(typeof(FleetSecretListDto))]
[JsonSerializable(typeof(PublishFleetSecretUsesRequest))]
[JsonSerializable(typeof(FleetSecretUseListDto))]
[JsonSerializable(typeof(PutFleetEnvironmentRequest))]
[JsonSerializable(typeof(FleetEnvironmentDto))]
[JsonSerializable(typeof(FleetEnvironmentListDto))]
[JsonSerializable(typeof(RequestFleetApprovalRequest))]
[JsonSerializable(typeof(ApproveFleetApprovalRequest))]
[JsonSerializable(typeof(FleetApprovalDto))]
[JsonSerializable(typeof(FleetApprovalListDto))]
[JsonSerializable(typeof(ApiProblemDto))]
public sealed partial class FleetSecretsApiJsonContext : JsonSerializerContext;
