using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// An environment of a person (<c>doc/fleet/secrets.md</c>, environments): a named set of secrets and variables with its
/// protection rules, signed by a controller device of that person, so that a server can neither drop a rule nor give a
/// machine an environment of its own making. The machine that runs checks the signature against the device list it
/// anchored before it applies any rule.
/// </summary>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization of the server.</param>
/// <param name="PersonId">The person whose environment it is.</param>
/// <param name="EnvironmentId">The id of the environment, which its secrets are scoped to.</param>
/// <param name="Name">Its name, such as <c>production</c>.</param>
/// <param name="Number">The version of the definition: a machine never goes back to an older one.</param>
/// <param name="Approvers">The people (their ids at the server) one of whom approves each run that uses it; empty for none.</param>
/// <param name="MachineLabels">The labels a machine must have, one of them, to use it; empty for every machine of the person.</param>
/// <param name="Automations">The automations that may use it; empty for every run.</param>
/// <param name="Removed">Whether the environment is taken away.</param>
/// <param name="SignerDeviceId">The controller device that signs it.</param>
/// <param name="Time">When it was signed.</param>
public sealed record FleetEnvironmentDefinition(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string OrganizationId,
    [property: JsonPropertyName("person")] string PersonId,
    [property: JsonPropertyName("id")] string EnvironmentId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("approvers")] IReadOnlyList<string> Approvers,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> MachineLabels,
    [property: JsonPropertyName("automations")] IReadOnlyList<string> Automations,
    [property: JsonPropertyName("removed")] bool Removed,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("time")] DateTimeOffset Time)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>An environment as it travels: the exact JSON text the signer signed, and the signature.</summary>
/// <param name="Payload">The JSON of a <see cref="FleetEnvironmentDefinition"/>, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of <see cref="FleetEnvironmentDocuments.GetSignedBytes"/>, in base64url.</param>
public sealed record FleetSignedEnvironment(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>
/// The approval of one run that uses an environment, signed by a controller device of one of its approvers: bound to the
/// environment and the version of its definition, the machine, the run and the nonce the machine chose, and valid for a
/// while. The machine takes it once.
/// </summary>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="ApprovalId">The approval the machine asked, at the server.</param>
/// <param name="EnvironmentId">The environment.</param>
/// <param name="EnvironmentNumber">The version of its definition.</param>
/// <param name="MachineId">The machine of the run.</param>
/// <param name="RunId">The run.</param>
/// <param name="Nonce">The nonce of the machine.</param>
/// <param name="ApproverPersonId">The person who approves.</param>
/// <param name="SignerDeviceId">The controller device of that person that signs.</param>
/// <param name="Time">When it was signed.</param>
/// <param name="Expires">Until when the run may start with it.</param>
public sealed record FleetEnvironmentApproval(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string OrganizationId,
    [property: JsonPropertyName("approval")] string ApprovalId,
    [property: JsonPropertyName("environment")] string EnvironmentId,
    [property: JsonPropertyName("number")] long EnvironmentNumber,
    [property: JsonPropertyName("machine")] string MachineId,
    [property: JsonPropertyName("run")] string RunId,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("person")] string ApproverPersonId,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("expires")] DateTimeOffset Expires)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>An approval as it travels: the exact JSON text the signer signed, and the signature.</summary>
/// <param name="Payload">The JSON of a <see cref="FleetEnvironmentApproval"/>, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of <see cref="FleetEnvironmentDocuments.GetApprovalSignedBytes"/>, in base64url.</param>
public sealed record FleetSignedApproval(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>Writes, reads and verifies the signed environments and their approvals.</summary>
public static class FleetEnvironmentDocuments
{
    /// <summary>The longest an approval stays valid.</summary>
    public static readonly TimeSpan MaxApprovalLifetime = TimeSpan.FromDays(1);

    /// <summary>How far in the future of the machine an approval may be signed.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(10);

    /// <summary>The most approvers, labels or automations of an environment.</summary>
    public const int MaxListItems = 32;

    private const string EnvironmentDomain = "codealta-environment/1\n";
    private const string ApprovalDomain = "codealta-environment-approval/1\n";
    private const int MaxPayloadLength = 16 * 1024;
    private const int MaxIdLength = 128;

    /// <summary>Gets whether a text can be the name of an environment: letters, digits, <c>-</c> and <c>_</c>, at most 64.</summary>
    /// <param name="name">The text.</param>
    /// <returns>Whether it can.</returns>
    public static bool IsName([NotNullWhen(true)] string? name)
        => name is { Length: > 0 and <= 64 } && name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    /// <summary>Writes the JSON text of a definition, the text a signer signs.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    public static string Serialize(FleetEnvironmentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, FleetEnvironmentJsonContext.Default.FleetEnvironmentDefinition);
    }

    /// <summary>Writes the JSON text of an approval, the text a signer signs.</summary>
    /// <param name="approval">The approval.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="approval"/> is null.</exception>
    public static string Serialize(FleetEnvironmentApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return JsonSerializer.Serialize(approval, FleetEnvironmentJsonContext.Default.FleetEnvironmentApproval);
    }

    /// <summary>Gets the bytes a signer signs for a definition.</summary>
    /// <param name="payload">The JSON text of the definition.</param>
    /// <returns>The UTF-8 of a domain line and the text.</returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is null or empty.</exception>
    public static byte[] GetSignedBytes(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Encoding.UTF8.GetBytes(EnvironmentDomain + payload);
    }

    /// <summary>Gets the bytes a signer signs for an approval: under a domain of its own, so that a definition never reads as an approval.</summary>
    /// <param name="payload">The JSON text of the approval.</param>
    /// <returns>The UTF-8 of a domain line and the text.</returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is null or empty.</exception>
    public static byte[] GetApprovalSignedBytes(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Encoding.UTF8.GetBytes(ApprovalDomain + payload);
    }

    /// <summary>Reads the JSON text of a definition and checks what it says, apart from its signature.</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="definition">The definition read, or null.</param>
    /// <param name="reason">Why it does not hold, or null.</param>
    /// <returns>Whether it reads and holds.</returns>
    public static bool TryRead(string? payload, [NotNullWhen(true)] out FleetEnvironmentDefinition? definition, [NotNullWhen(false)] out string? reason)
    {
        definition = null;
        reason = "the environment does not read";
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength) return false;
        FleetEnvironmentDefinition? read;
        try { read = JsonSerializer.Deserialize(payload, FleetEnvironmentJsonContext.Default.FleetEnvironmentDefinition); }
        catch (JsonException) { return false; }
        if (read is null) return false;
        if (read.Version != FleetEnvironmentDefinition.CurrentVersion)
            return Refuse($"version {read.Version.ToString(CultureInfo.InvariantCulture)} of the environment is not known", out reason);
        if (!IsId(read.OrganizationId) || !IsId(read.PersonId) || !IsId(read.EnvironmentId) || !IsId(read.SignerDeviceId) || !IsName(read.Name) || read.Number < 1)
            return Refuse("an environment names its organization, person, id, name and signer, and a version from 1", out reason);
        if (!IsList(read.Approvers, IsId) || !IsList(read.MachineLabels, IsLabel) || !IsList(read.Automations, IsId))
            return Refuse($"an environment has at most {MaxListItems} approvers, labels and automations, each a name", out reason);
        (definition, reason) = (read, null);
        return true;
    }

    /// <summary>Reads the JSON text of an approval and checks what it says, apart from its signature.</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="approval">The approval read, or null.</param>
    /// <param name="reason">Why it does not hold, or null.</param>
    /// <returns>Whether it reads and holds.</returns>
    public static bool TryReadApproval(string? payload, [NotNullWhen(true)] out FleetEnvironmentApproval? approval, [NotNullWhen(false)] out string? reason)
    {
        approval = null;
        reason = "the approval does not read";
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength) return false;
        FleetEnvironmentApproval? read;
        try { read = JsonSerializer.Deserialize(payload, FleetEnvironmentJsonContext.Default.FleetEnvironmentApproval); }
        catch (JsonException) { return false; }
        if (read is null) return false;
        if (read.Version != FleetEnvironmentApproval.CurrentVersion)
            return Refuse($"version {read.Version.ToString(CultureInfo.InvariantCulture)} of the approval is not known", out reason);
        if (!IsId(read.OrganizationId) || !IsId(read.ApprovalId) || !IsId(read.EnvironmentId) || !IsId(read.MachineId) || !IsId(read.RunId) || !IsId(read.Nonce)
            || !IsId(read.ApproverPersonId) || !IsId(read.SignerDeviceId) || read.EnvironmentNumber < 1)
            return Refuse("an approval names its organization, environment, machine, run, nonce, approver and signer", out reason);
        if (read.Expires <= read.Time || read.Expires - read.Time > MaxApprovalLifetime)
            return Refuse("an approval is valid for at most a day after it is signed", out reason);
        (approval, reason) = (read, null);
        return true;
    }

    /// <summary>
    /// Verifies a definition for a machine: it reads and holds, is of the organization of the machine's server and of the
    /// person who owns the machine, and is signed by a controller device of that person in the device list the machine
    /// verified, with the key the list holds for it.
    /// </summary>
    /// <param name="signed">The signed definition.</param>
    /// <param name="devices">The device list as the machine verified it.</param>
    /// <param name="machineId">The device of the machine that verifies.</param>
    /// <param name="definition">The definition, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public static bool TryVerify(FleetSignedEnvironment? signed, FleetDeviceListState devices, string? machineId,
        [NotNullWhen(true)] out FleetEnvironmentDefinition? definition, [NotNullWhen(false)] out string? reason)
        => TryVerify(signed, devices, null, machineId, out definition, out reason);

    /// <summary>
    /// Verifies a definition for a machine, as
    /// <see cref="TryVerify(FleetSignedEnvironment?, FleetDeviceListState, string?, out FleetEnvironmentDefinition?, out string?)"/> does,
    /// with the owner of the machine the signed access document names once it was given to another person
    /// (<see cref="FleetAccessState.OwnerOfMachine"/>): the environments of its new owner hold there, those of the person the
    /// device list names no more. On a machine of a team, those of its maintainers hold; on a machine of the organization, those of
    /// its owners and admins who sign (<see cref="FleetAccessState.HoldsValuesOf"/>).
    /// </summary>
    /// <param name="signed">The signed definition.</param>
    /// <param name="devices">The device list as the machine verified it.</param>
    /// <param name="access">The access document as the machine verified it; null for none.</param>
    /// <param name="machineId">The device of the machine that verifies.</param>
    /// <param name="definition">The definition, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public static bool TryVerify(FleetSignedEnvironment? signed, FleetDeviceListState devices, FleetAccessState? access, string? machineId,
        [NotNullWhen(true)] out FleetEnvironmentDefinition? definition, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(devices);
        definition = null;
        if (signed is null || string.IsNullOrEmpty(signed.Signature)) return Refuse("the environment does not read", out reason);
        if (!TryRead(signed.Payload, out var read, out reason)) return false;
        if (devices.OrganizationId is { } organization && organization != read.OrganizationId) return Refuse("the environment is of another organization", out reason);
        // A machine of a team takes the environments of its maintainers (FleetAccessState.HoldsValuesOf), never those of the person the device list names.
        if (string.IsNullOrEmpty(machineId) || !devices.Members.TryGetValue(machineId, out var self) || !FleetAccessState.HoldsValuesOf(access, self, read.PersonId))
            return Refuse("the environment is of another person than the owner of this machine", out reason);
        if (!IsSigner(devices, read.SignerDeviceId, read.PersonId, out var signer))
            return Refuse("the environment is not signed by a controller device of its person", out reason);
        if (!signer.Key.Verify(GetSignedBytes(signed.Payload), signed.Signature))
            return Refuse("the signature of the environment does not verify with the key the device list holds for its signer", out reason);
        definition = read;
        return true;
    }

    /// <summary>
    /// Verifies the approval of a run for a machine: it reads and holds, names the environment and the version of the
    /// definition the machine verified, this machine, this run and this nonce; it is valid at <paramref name="now"/>; and it
    /// is signed by a controller device of one of the approvers of the environment, with the key the device list holds for it.
    /// </summary>
    /// <param name="signed">The signed approval.</param>
    /// <param name="environment">The definition the machine verified (<see cref="TryVerify(FleetSignedEnvironment?, FleetDeviceListState, FleetAccessState?, string?, out FleetEnvironmentDefinition?, out string?)"/>).</param>
    /// <param name="devices">The device list as the machine verified it.</param>
    /// <param name="machineId">The device of the machine.</param>
    /// <param name="runId">The run the machine asked the approval of.</param>
    /// <param name="nonce">The nonce the machine chose for it.</param>
    /// <param name="now">The time of the machine.</param>
    /// <param name="approval">The approval, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> or <paramref name="devices"/> is null.</exception>
    public static bool TryVerifyApproval(FleetSignedApproval? signed, FleetEnvironmentDefinition environment, FleetDeviceListState devices, string machineId, string runId,
        string nonce, DateTimeOffset now, [NotNullWhen(true)] out FleetEnvironmentApproval? approval, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(devices);
        approval = null;
        if (signed is null || string.IsNullOrEmpty(signed.Signature)) return Refuse("the approval does not read", out reason);
        if (!TryReadApproval(signed.Payload, out var read, out reason)) return false;
        if (read.OrganizationId != environment.OrganizationId || read.EnvironmentId != environment.EnvironmentId || read.EnvironmentNumber != environment.Number)
            return Refuse("the approval is for another environment, or another version of its rules", out reason);
        if (read.MachineId != machineId || read.RunId != runId || read.Nonce != nonce)
            return Refuse("the approval is for another machine or another run", out reason);
        if (read.Time - now > MaxSkew) return Refuse("the approval is signed in the future", out reason);
        if (read.Expires <= now) return Refuse("the approval expired", out reason);
        if (!environment.Approvers.Contains(read.ApproverPersonId, StringComparer.Ordinal))
            return Refuse("the approval is not given by an approver of the environment", out reason);
        if (!IsSigner(devices, read.SignerDeviceId, read.ApproverPersonId, out var signer))
            return Refuse("the approval is not signed by a controller device of its approver", out reason);
        if (!signer.Key.Verify(GetApprovalSignedBytes(signed.Payload), signed.Signature))
            return Refuse("the signature of the approval does not verify with the key the device list holds for its signer", out reason);
        approval = read;
        return true;
    }

    // A controller device of the person, still in the list.
    private static bool IsSigner(FleetDeviceListState devices, string deviceId, string personId, [NotNullWhen(true)] out FleetDeviceListMember? signer)
        => devices.Members.TryGetValue(deviceId, out signer) && signer.Kind == FleetDeviceKinds.Controller && !devices.Removed.Contains(deviceId)
            && signer.OwnerId is not null && signer.OwnerId == personId;

    private static bool IsList(IReadOnlyList<string>? items, Func<string?, bool> valid)
        => items is not null && items.Count <= MaxListItems && items.All(valid) && items.Distinct(StringComparer.Ordinal).Count() == items.Count;

    private static bool IsLabel(string? text) => text is { Length: > 0 and <= 64 } && !text.Any(char.IsControl);

    private static bool IsId(string? text) => !string.IsNullOrEmpty(text) && text.Length <= MaxIdLength && !text.Any(char.IsControl);

    private static bool Refuse(string why, out string reason)
    {
        reason = why;
        return false;
    }
}

/// <summary>The source-generated contracts of the signed environments and approvals.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never, WriteIndented = false)]
[JsonSerializable(typeof(FleetEnvironmentDefinition))]
[JsonSerializable(typeof(FleetSignedEnvironment))]
[JsonSerializable(typeof(FleetEnvironmentApproval))]
[JsonSerializable(typeof(FleetSignedApproval))]
public sealed partial class FleetEnvironmentJsonContext : JsonSerializerContext;
