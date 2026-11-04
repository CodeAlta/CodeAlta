using System.Buffers.Text;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>A team as the signed access document names it: ids only, which machines need to decide.</summary>
/// <param name="Id">The team.</param>
/// <param name="Maintainers">The people who maintain it.</param>
/// <param name="Members">Its other members.</param>
/// <param name="RunsMemberAutomations">Whether its machines run the personal automations of its members.</param>
/// <param name="Services">
/// The service identities of the team that are turned on (<c>doc/fleet/orchestration.md</c>, who a run acts as): its
/// automations run as one of them, and not at all without one; null for none, which writes the format as before.
/// </param>
public sealed record FleetAccessTeam(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("maintainers")] IReadOnlyList<string> Maintainers,
    [property: JsonPropertyName("members")] IReadOnlyList<string> Members,
    [property: JsonPropertyName("runsMemberAutomations")] bool RunsMemberAutomations,
    [property: JsonPropertyName("services"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Services = null);

/// <summary>
/// The people who answer the approvals of the runs of an automation, and take its runs over, besides the person who placed
/// it on a machine: the members of the team that owns it and those granted to approve it (<c>automation.approve</c>).
/// </summary>
/// <param name="AutomationId">The automation.</param>
/// <param name="People">The people.</param>
public sealed record FleetAccessApprovers(
    [property: JsonPropertyName("automation")] string AutomationId,
    [property: JsonPropertyName("people")] IReadOnlyList<string> People)
{
    /// <summary>
    /// Gets when the approval of some of its people ends, by person: a grant with an end date. A machine refuses them from
    /// that time. Null, or a person missing here: no end.
    /// </summary>
    [JsonPropertyName("until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, DateTimeOffset>? Until { get; init; }
}

/// <summary>A team machine: the device of the machine and the team that owns it.</summary>
/// <param name="DeviceId">The device of the machine.</param>
/// <param name="TeamId">The team.</param>
public sealed record FleetAccessMachine(
    [property: JsonPropertyName("device")] string DeviceId,
    [property: JsonPropertyName("team")] string TeamId);

/// <summary>
/// One version of the signed access document of an organization (<c>doc/fleet/access-control.md</c>, what the node decides):
/// who owns and signs for the organization, its teams, its team machines and its suspended people, signed by the controller
/// device of a signer. Versions form a chain, as the device list does: each names the hash of the one before and the next
/// number, so that a machine that anchored the chain can neither be given a team the signers did not sign nor have a
/// suspension taken back by the server.
/// </summary>
/// <remarks>
/// Version 1 is signed by a controller device of the organization's owner, the person of the first entry of the device list.
/// Each later version is signed by a controller device of a signer of the version before (an owner or an admin it lists) who
/// is not suspended there; a version that changes who the owners or signers are, or takes a person off the suspended list,
/// is signed by an owner's device. <see cref="DeviceListSequence"/> is the entry of the device list the signer read: the
/// signing device was a member at that entry, and it never goes back.
/// </remarks>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Number">The number of this version, from 1.</param>
/// <param name="Previous">The hash of the version before; null for the first one.</param>
/// <param name="DeviceListSequence">The sequence of the device list the signer read when signing.</param>
/// <param name="Owners">The people who own the organization.</param>
/// <param name="Signers">The admins whose devices also sign the next versions.</param>
/// <param name="Teams">The teams.</param>
/// <param name="Machines">The team machines.</param>
/// <param name="Suspended">The people who left or were suspended: machines refuse their devices and stop their work.</param>
/// <param name="SignerDeviceId">The controller device that signs it.</param>
/// <param name="Time">When the signer made it; for display, never for ordering.</param>
public sealed record FleetAccessDocument(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string OrganizationId,
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("prev")] string? Previous,
    [property: JsonPropertyName("list")] long DeviceListSequence,
    [property: JsonPropertyName("owners")] IReadOnlyList<string> Owners,
    [property: JsonPropertyName("signers")] IReadOnlyList<string> Signers,
    [property: JsonPropertyName("teams")] IReadOnlyList<FleetAccessTeam> Teams,
    [property: JsonPropertyName("machines")] IReadOnlyList<FleetAccessMachine> Machines,
    [property: JsonPropertyName("suspended")] IReadOnlyList<string> Suspended,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("time")] DateTimeOffset Time)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Gets the people who answer the approvals of automations and take their runs over, besides who placed each; null for
    /// none, which a version without them says by leaving the field out.
    /// </summary>
    [JsonPropertyName("approvers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FleetAccessApprovers>? Approvers { get; init; }
}

/// <summary>A version of the access document as it travels and is stored: the exact JSON text signed, and the signature.</summary>
/// <param name="Payload">The JSON of a <see cref="FleetAccessDocument"/>, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of <see cref="FleetAccessDocuments.GetSignedBytes"/>, r and s in base64url.</param>
public sealed record FleetSignedAccessDocument(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>
/// The access document as a verifier replayed it: who owns and signs, the teams, the team machines and the suspended people,
/// and where the next version goes.
/// </summary>
/// <param name="OrganizationId">The organization; null before the first version.</param>
/// <param name="Number">The number of the last version, 0 when none.</param>
/// <param name="LastHash">The hash of the last version, which the next names; null when none.</param>
/// <param name="DeviceListSequence">The entry of the device list the last version was signed at.</param>
/// <param name="Owners">The owners.</param>
/// <param name="Signers">The admins who sign.</param>
/// <param name="Teams">The teams by id.</param>
/// <param name="Machines">The team of each team machine, by device.</param>
/// <param name="Suspended">The suspended people.</param>
public sealed record FleetAccessState(
    string? OrganizationId,
    long Number,
    string? LastHash,
    long DeviceListSequence,
    ImmutableHashSet<string> Owners,
    ImmutableHashSet<string> Signers,
    ImmutableDictionary<string, FleetAccessTeam> Teams,
    ImmutableDictionary<string, string> Machines,
    ImmutableHashSet<string> Suspended)
{
    /// <summary>Gets the state before any version: nobody is suspended, no machine is a team's.</summary>
    public static FleetAccessState Empty { get; } = new(null, 0, null, 0, ImmutableHashSet.Create<string>(StringComparer.Ordinal), ImmutableHashSet.Create<string>(StringComparer.Ordinal),
        ImmutableDictionary.Create<string, FleetAccessTeam>(StringComparer.Ordinal), ImmutableDictionary.Create<string, string>(StringComparer.Ordinal),
        ImmutableHashSet.Create<string>(StringComparer.Ordinal));

    /// <summary>Gets the hash of the first version, the root a device pins per server.</summary>
    public string? FirstHash { get; init; }

    /// <summary>Gets the people who answer the approvals of each automation and take its runs over, besides who placed it, by automation.</summary>
    public ImmutableDictionary<string, ImmutableHashSet<string>> Approvers { get; init; } = ImmutableDictionary.Create<string, ImmutableHashSet<string>>(StringComparer.Ordinal);

    /// <summary>Gets when the approval of a person for an automation ends, by automation then person; a person missing has no end.</summary>
    public ImmutableDictionary<string, ImmutableDictionary<string, DateTimeOffset>> ApproversUntil { get; init; } =
        ImmutableDictionary.Create<string, ImmutableDictionary<string, DateTimeOffset>>(StringComparer.Ordinal);

    /// <summary>Gets whether the document names a person among those who answer the approvals of an automation and take its runs over, at a time.</summary>
    /// <param name="automationId">The automation.</param>
    /// <param name="personId">The person.</param>
    /// <param name="now">The time of the machine: an approval with an end date holds before it only.</param>
    /// <returns>Whether it names them, they are not suspended, and their approval has not ended.</returns>
    public bool Approves(string? automationId, string? personId, DateTimeOffset now)
        => automationId is not null && personId is not null && !IsSuspended(personId) && Approvers.TryGetValue(automationId, out var people) && people.Contains(personId)
            && !(ApproversUntil.TryGetValue(automationId, out var ends) && ends.TryGetValue(personId, out var end) && now >= end);

    /// <summary>Lists when the approvals of a version end, by automation then person.</summary>
    /// <param name="approvers">The approvers of a version; null for none.</param>
    /// <returns>The ends.</returns>
    public static ImmutableDictionary<string, ImmutableDictionary<string, DateTimeOffset>> UntilOf(IReadOnlyList<FleetAccessApprovers>? approvers)
        => (approvers ?? []).Where(static item => item.Until is { Count: > 0 })
            .ToImmutableDictionary(static item => item.AutomationId, static item => item.Until!.ToImmutableDictionary(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Lists the approvers of a version, by automation.</summary>
    /// <param name="approvers">The approvers of a version; null for none.</param>
    /// <returns>The people by automation.</returns>
    public static ImmutableDictionary<string, ImmutableHashSet<string>> ApproversOf(IReadOnlyList<FleetAccessApprovers>? approvers)
        => (approvers ?? []).ToImmutableDictionary(static item => item.AutomationId, static item => item.People.ToImmutableHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Gets the team that owns a machine, or null for a personal machine.</summary>
    /// <param name="deviceId">The device of the machine.</param>
    /// <returns>The team, or null.</returns>
    public FleetAccessTeam? TeamOf(string deviceId) => Machines.TryGetValue(deviceId, out var team) && Teams.TryGetValue(team, out var held) ? held : null;

    /// <summary>Gets a person's role in a team: <see cref="FleetTeamRoles.Maintainer"/>, <see cref="FleetTeamRoles.Member"/>, or null.</summary>
    /// <param name="team">The team.</param>
    /// <param name="personId">The person.</param>
    /// <returns>Their role, or null; always null for a suspended person.</returns>
    public static string? RoleIn(FleetAccessTeam team, string? personId)
    {
        ArgumentNullException.ThrowIfNull(team);
        if (personId is null) return null;
        if (team.Maintainers.Contains(personId, StringComparer.Ordinal)) return FleetTeamRoles.Maintainer;
        return team.Members.Contains(personId, StringComparer.Ordinal) ? FleetTeamRoles.Member : null;
    }

    /// <summary>Gets whether a person is suspended.</summary>
    /// <param name="personId">The person.</param>
    /// <returns>Whether the document lists them as suspended.</returns>
    public bool IsSuspended(string? personId) => personId is not null && Suspended.Contains(personId);

    /// <summary>Gets whether a person's devices sign the next version: an owner or a signer who is not suspended.</summary>
    /// <param name="personId">The person.</param>
    /// <returns>Whether they sign.</returns>
    public bool IsSigner(string? personId) => personId is not null && !IsSuspended(personId) && (Owners.Contains(personId) || Signers.Contains(personId));
}

/// <summary>The result of checking an access document chain.</summary>
/// <param name="IsValid">Whether every version holds.</param>
/// <param name="BrokenAt">The number of the first version that does not hold, or null.</param>
/// <param name="Reason">Why it does not hold, for people, or null.</param>
/// <param name="State">The document after the last version that holds.</param>
public sealed record FleetAccessVerification(bool IsValid, long? BrokenAt, string? Reason, FleetAccessState State);

/// <summary>An access document chain that does not verify: a machine keeps the last state it verified.</summary>
public sealed class FleetAccessDocumentException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="FleetAccessDocumentException"/> class.</summary>
    /// <param name="brokenAt">The number of the version that does not hold.</param>
    /// <param name="reason">Why it does not hold.</param>
    public FleetAccessDocumentException(long brokenAt, string reason)
        : base($"The access document of the server does not verify at version {brokenAt}: {reason}. Nothing after it was trusted.")
    {
        BrokenAt = brokenAt;
        Reason = reason;
    }

    /// <summary>Gets the number of the version that does not hold.</summary>
    public long BrokenAt { get; }

    /// <summary>Gets why it does not hold.</summary>
    public string Reason { get; }
}

/// <summary>Writes, reads, signs, chains and verifies the versions of the access document, and says what changes between two.</summary>
public static class FleetAccessDocuments
{
    /// <summary>The largest text of a version.</summary>
    public const int MaxPayloadLength = 512 * 1024;

    /// <summary>The most teams, team machines or people a list of a version holds.</summary>
    public const int MaxItems = 10_000;

    private const string SignDomain = "codealta-access-document/1\n";
    private const string HashDomain = "codealta-access-document-hash/1\n";
    private const int MaxIdLength = 128;

    /// <summary>Writes the JSON text of a version, the text a signer signs.</summary>
    /// <param name="document">The version.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static string Serialize(FleetAccessDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, FleetAccessDocumentJsonContext.Default.FleetAccessDocument);
    }

    /// <summary>Reads the JSON text of a version.</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="document">The version read, or null.</param>
    /// <returns>Whether the text is a version with its required fields.</returns>
    public static bool TryParse(string? payload, [NotNullWhen(true)] out FleetAccessDocument? document)
    {
        document = null;
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength) return false;
        try
        {
            document = JsonSerializer.Deserialize(payload, FleetAccessDocumentJsonContext.Default.FleetAccessDocument);
        }
        catch (JsonException)
        {
            return false;
        }

        if (document is null || string.IsNullOrEmpty(document.OrganizationId) || string.IsNullOrEmpty(document.SignerDeviceId) || document.Owners is null
            || document.Signers is null || document.Teams is null || document.Machines is null || document.Suspended is null)
        {
            document = null;
            return false;
        }

        return true;
    }

    /// <summary>Gets the bytes a signer signs for a version.</summary>
    /// <param name="payload">The JSON text of the version.</param>
    /// <returns>The UTF-8 of a domain line and the text.</returns>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is null or empty.</exception>
    public static byte[] GetSignedBytes(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Encoding.UTF8.GetBytes(SignDomain + payload);
    }

    /// <summary>Gets the hash of a signed version, which the next version names.</summary>
    /// <param name="document">The signed version.</param>
    /// <returns>The base64url of the SHA-256 of its text and signature.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static string Hash(FleetSignedAccessDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes($"{HashDomain}{document.Payload}\n{document.Signature}")));
    }

    /// <summary>Replays a chain of versions from its first, against the device list the verifier anchored.</summary>
    /// <param name="documents">The signed versions, in order.</param>
    /// <param name="devices">The device list, as the verifier verified it.</param>
    /// <param name="organizationId">The organization the versions must name.</param>
    /// <returns>Whether the chain holds, where it breaks, and the document up to there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="documents"/> or <paramref name="devices"/> is null.</exception>
    public static FleetAccessVerification Verify(IEnumerable<FleetSignedAccessDocument> documents, FleetDeviceListState devices, string organizationId)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(devices);
        var state = FleetAccessState.Empty;
        foreach (var signed in documents)
        {
            if (!TryAppend(state, signed, devices, organizationId, out var next, out var reason)) return new(false, state.Number + 1, reason, state);
            state = next;
        }

        return new(true, null, null, state);
    }

    /// <summary>Checks one version after a state and gives the state with it.</summary>
    /// <param name="state">The document before the version.</param>
    /// <param name="signed">The signed version.</param>
    /// <param name="devices">The device list, as the verifier verified it.</param>
    /// <param name="organizationId">The organization the version must name.</param>
    /// <param name="next">The document with the version, or <paramref name="state"/>.</param>
    /// <param name="reason">Why it does not hold, or null.</param>
    /// <returns>Whether it holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> or <paramref name="devices"/> is null.</exception>
    public static bool TryAppend(FleetAccessState state, FleetSignedAccessDocument? signed, FleetDeviceListState devices, string organizationId,
        out FleetAccessState next, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(devices);
        next = state;
        reason = Check(state, signed, devices, organizationId, out var document);
        if (reason is not null) return false;
        var hash = Hash(signed!);
        next = new FleetAccessState(document!.OrganizationId, document.Number, hash, document.DeviceListSequence,
            [.. document.Owners], [.. document.Signers],
            document.Teams.ToImmutableDictionary(static team => team.Id, StringComparer.Ordinal),
            document.Machines.ToImmutableDictionary(static machine => machine.DeviceId, static machine => machine.TeamId, StringComparer.Ordinal),
            [.. document.Suspended])
        {
            FirstHash = state.FirstHash ?? hash,
            Approvers = FleetAccessState.ApproversOf(document.Approvers),
            ApproversUntil = FleetAccessState.UntilOf(document.Approvers),
        };
        return true;
    }

    /// <summary>Reads a signed version and checks it was signed by a given device, apart from the chain: what a server checks before it keeps one.</summary>
    /// <param name="signed">The signed version.</param>
    /// <param name="key">The key of the device that sends it.</param>
    /// <param name="document">The version read.</param>
    /// <param name="reason">Why it does not hold.</param>
    /// <returns>Whether it reads, holds and is signed by that key.</returns>
    public static bool TryReadSigned(FleetSignedAccessDocument? signed, FleetDevicePublicKey key, [NotNullWhen(true)] out FleetAccessDocument? document,
        [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(key);
        document = null;
        if (signed is null || string.IsNullOrEmpty(signed.Signature) || !TryParse(signed.Payload, out var parsed))
        {
            reason = "the version does not read";
            return false;
        }

        if (CheckContent(parsed) is { } problem)
        {
            reason = problem;
            return false;
        }

        if (parsed.SignerDeviceId != key.DeviceId || !key.Verify(GetSignedBytes(signed.Payload), signed.Signature))
        {
            reason = "it is not signed by the device that sends it";
            return false;
        }

        document = parsed;
        reason = null;
        return true;
    }

    /// <summary>
    /// Says, for people, what a version changes from the state before it: who becomes owner or signer, who joins or leaves
    /// which team, which machine becomes a team's, who is suspended. Names come from <paramref name="nameOf"/>, for display
    /// only; the ids are what was verified.
    /// </summary>
    /// <param name="before">The document before.</param>
    /// <param name="after">The new version.</param>
    /// <param name="nameOf">Gives the name of a person, a team or a machine by its id, or null to show the id.</param>
    /// <returns>One line per change, in a stable order.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> Describe(FleetAccessState before, FleetAccessDocument after, Func<string, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(nameOf);
        string Name(string id) => nameOf(id) is { Length: > 0 } name ? name : id;
        var lines = new List<string>();
        foreach (var id in after.Owners.Where(id => !before.Owners.Contains(id))) lines.Add($"{Name(id)} becomes an owner");
        foreach (var id in before.Owners.Where(id => !after.Owners.Contains(id, StringComparer.Ordinal))) lines.Add($"{Name(id)} is no longer an owner");
        foreach (var id in after.Signers.Where(id => !before.Signers.Contains(id))) lines.Add($"{Name(id)} becomes an admin who signs");
        foreach (var id in before.Signers.Where(id => !after.Signers.Contains(id, StringComparer.Ordinal))) lines.Add($"{Name(id)} no longer signs as an admin");
        var teams = after.Teams.ToDictionary(static team => team.Id, StringComparer.Ordinal);
        foreach (var team in after.Teams)
        {
            before.Teams.TryGetValue(team.Id, out var held);
            if (held is null) lines.Add($"Team {Name(team.Id)} is created");
            foreach (var id in team.Maintainers.Where(id => held is null || FleetAccessState.RoleIn(held, id) != FleetTeamRoles.Maintainer))
                lines.Add($"{Name(id)} maintains {Name(team.Id)}");
            foreach (var id in team.Members.Where(id => held is null || FleetAccessState.RoleIn(held, id) != FleetTeamRoles.Member))
                lines.Add($"{Name(id)} is a member of {Name(team.Id)}");
            if (held is not null)
            {
                foreach (var id in held.Maintainers.Concat(held.Members).Where(id => FleetAccessState.RoleIn(team, id) is null)) lines.Add($"{Name(id)} leaves {Name(team.Id)}");
            }

            if ((held?.RunsMemberAutomations ?? false) != team.RunsMemberAutomations)
                lines.Add(team.RunsMemberAutomations ? $"Machines of {Name(team.Id)} run the automations of its members" : $"Machines of {Name(team.Id)} stop running the automations of its members");
            foreach (var id in (team.Services ?? []).Where(id => held?.Services?.Contains(id, StringComparer.Ordinal) != true))
                lines.Add($"{Name(team.Id)} runs its automations as {Name(id)}");
            foreach (var id in (held?.Services ?? []).Where(id => team.Services?.Contains(id, StringComparer.Ordinal) != true))
                lines.Add($"{Name(team.Id)} no longer runs its automations as {Name(id)}");
        }

        foreach (var id in before.Teams.Keys.Where(id => !teams.ContainsKey(id))) lines.Add($"Team {Name(id)} is dissolved");
        foreach (var machine in after.Machines.Where(machine => !before.Machines.TryGetValue(machine.DeviceId, out var team) || team != machine.TeamId))
            lines.Add($"{Name(machine.DeviceId)} becomes a machine of {Name(machine.TeamId)}");
        foreach (var device in before.Machines.Keys.Where(device => after.Machines.All(machine => machine.DeviceId != device)))
            lines.Add($"{Name(device)} is no longer a team machine");
        foreach (var id in after.Suspended.Where(id => !before.Suspended.Contains(id))) lines.Add($"{Name(id)} is suspended: their work stops on team machines");
        foreach (var id in before.Suspended.Where(id => !after.Suspended.Contains(id, StringComparer.Ordinal))) lines.Add($"{Name(id)} is no longer suspended");
        var approvers = FleetAccessState.ApproversOf(after.Approvers);
        foreach (var (automation, people) in approvers.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var ends = FleetAccessState.UntilOf(after.Approvers).GetValueOrDefault(automation);
            DateTimeOffset? EndAfter(string id) => ends is not null && ends.TryGetValue(id, out var end) ? end : null;
            foreach (var id in people.Where(id => !(before.Approvers.TryGetValue(automation, out var held) && held.Contains(id))
                    || End(before, automation, id) != EndAfter(id)).Order(StringComparer.Ordinal))
                lines.Add(ends?.TryGetValue(id, out var end) == true
                    ? $"{Name(id)} answers the approvals of {Name(automation)} and takes its runs over until {end.ToString("u", System.Globalization.CultureInfo.InvariantCulture)}"
                    : $"{Name(id)} answers the approvals of {Name(automation)} and takes its runs over");
        }

        foreach (var (automation, people) in before.Approvers.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var id in people.Where(id => !(approvers.TryGetValue(automation, out var now) && now.Contains(id))).Order(StringComparer.Ordinal))
                lines.Add($"{Name(id)} no longer answers the approvals of {Name(automation)}");
        }

        return lines;
    }

    /// <summary>Gets whether two versions say the same: owners, signers, teams, team machines and suspended people, whatever their order.</summary>
    /// <param name="left">A version.</param>
    /// <param name="right">Another version.</param>
    /// <returns>Whether they say the same.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool SaySame(FleetAccessDocument left, FleetAccessDocument right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return SameSet(left.Owners, right.Owners) && SameSet(left.Signers, right.Signers) && SameSet(left.Suspended, right.Suspended)
            && SameSet(left.Machines.Select(static machine => machine.DeviceId + "\n" + machine.TeamId), right.Machines.Select(static machine => machine.DeviceId + "\n" + machine.TeamId))
            && SameSet(left.Teams.Select(TeamKey), right.Teams.Select(TeamKey))
            && SameSet(Approvals(left), Approvals(right));

        static IEnumerable<string> Approvals(FleetAccessDocument document)
            => (document.Approvers ?? []).SelectMany(static item => item.People.Select(person => item.AutomationId + "\n" + person + "\n"
                + (item.Until?.TryGetValue(person, out var end) == true ? end.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)));

        static string TeamKey(FleetAccessTeam team)
            => $"{team.Id}\n{team.RunsMemberAutomations}\n{string.Join(',', team.Maintainers.Order(StringComparer.Ordinal))}\n{string.Join(',', team.Members.Order(StringComparer.Ordinal))}"
                + $"\n{string.Join(',', (team.Services ?? []).Order(StringComparer.Ordinal))}";
    }

    /// <summary>Gets whether a state and a version say the same.</summary>
    /// <param name="state">The state.</param>
    /// <param name="document">The version.</param>
    /// <returns>Whether they say the same.</returns>
    public static bool SaySame(FleetAccessState state, FleetAccessDocument document)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(document);
        return SaySame(new FleetAccessDocument(1, document.OrganizationId, 0, null, 0, [.. state.Owners], [.. state.Signers], [.. state.Teams.Values],
            [.. state.Machines.Select(static pair => new FleetAccessMachine(pair.Key, pair.Value))], [.. state.Suspended], document.SignerDeviceId, document.Time)
        {
            Approvers = [.. state.Approvers.Select(pair => new FleetAccessApprovers(pair.Key, [.. pair.Value])
            {
                Until = state.ApproversUntil.TryGetValue(pair.Key, out var ends) && ends.Count > 0 ? ends : null,
            })],
        }, document);
    }

    // When the approval of a person for an automation ends in a state; null for no end.
    private static DateTimeOffset? End(FleetAccessState state, string automation, string person)
        => state.ApproversUntil.TryGetValue(automation, out var ends) && ends.TryGetValue(person, out var end) ? end : null;

    private static bool SameSet(IEnumerable<string> left, IEnumerable<string> right)
        => new HashSet<string>(left, StringComparer.Ordinal).SetEquals(right);

    private static string? Check(FleetAccessState state, FleetSignedAccessDocument? signed, FleetDeviceListState devices, string organizationId,
        out FleetAccessDocument? document)
    {
        document = null;
        if (signed is null || string.IsNullOrEmpty(signed.Signature) || !TryParse(signed.Payload, out var parsed)) return "the version does not read";
        if (CheckContent(parsed) is { } problem) return problem;
        if (string.IsNullOrEmpty(organizationId) || parsed.OrganizationId != organizationId || devices.OrganizationId is { } listed && listed != organizationId)
            return "it names another organization";
        if (parsed.Number != state.Number + 1) return $"its number is {parsed.Number}, not {state.Number + 1}";
        if (!string.Equals(parsed.Previous, state.LastHash, StringComparison.Ordinal)) return "it does not name the hash of the version before it";
        if (parsed.DeviceListSequence < state.DeviceListSequence || parsed.DeviceListSequence > devices.Sequence || parsed.DeviceListSequence < 1)
            return "it names an entry of the device list before the one of the version before, or one this device does not hold";
        if (devices.MemberFrom(parsed.SignerDeviceId, parsed.DeviceListSequence) is not { Kind: FleetDeviceKinds.Controller } signer
            || devices.Tenures.TryGetValue(parsed.SignerDeviceId, out var tenure) && tenure.AddedAt > parsed.DeviceListSequence)
            return "its signer was not a controller device of the device list at the entry it names";
        if (!signer.Key.Verify(GetSignedBytes(signed.Payload), signed.Signature)) return "its signature does not verify with the key of its signer";

        var person = signer.OwnerId;
        if (state.Number == 0)
        {
            var founder = devices.FirstDeviceId is { } first && devices.Tenures.TryGetValue(first, out var held) ? held.Member.OwnerId
                : devices.FirstDeviceId is { } id && devices.Members.TryGetValue(id, out var member) ? member.OwnerId : null;
            if (founder is null || person != founder) return "the first version is signed by a device of the person who set the server up";
            if (!parsed.Owners.Contains(founder, StringComparer.Ordinal)) return "the first version names the person who set the server up as an owner";
        }
        else
        {
            if (!state.IsSigner(person)) return "its signer is not an owner or an admin who signs, or is suspended";
            var ownersChange = !SameSet(state.Owners, parsed.Owners) || !SameSet(state.Signers, parsed.Signers)
                || state.Suspended.Any(id => !parsed.Suspended.Contains(id, StringComparer.Ordinal));
            if (ownersChange && !state.Owners.Contains(person!)) return "only an owner changes who owns and signs, or takes a suspension back";
        }

        document = parsed;
        return null;
    }

    // What a version says, whatever the chain: known format, short ids, no one twice, no suspended person with a role, an owner.
    private static string? CheckContent(FleetAccessDocument document)
    {
        if (document.Version != FleetAccessDocument.CurrentVersion) return $"version {document.Version} of the format is not known";
        if (document.Number < 1) return "its number is not positive";
        if (document.Owners.Count == 0) return "it names no owner";
        foreach (var list in new[] { document.Owners, document.Signers, document.Suspended })
        {
            if (list.Count > MaxItems || list.Any(static id => !IsId(id)) || list.Distinct(StringComparer.Ordinal).Count() != list.Count) return "a list of people does not hold";
        }

        if (document.Teams.Count > MaxItems || document.Machines.Count > MaxItems) return "it holds too many teams or machines";
        var suspended = new HashSet<string>(document.Suspended, StringComparer.Ordinal);
        if (document.Owners.Concat(document.Signers).Any(suspended.Contains)) return "a suspended person owns or signs";
        var teams = new HashSet<string>(StringComparer.Ordinal);
        foreach (var team in document.Teams)
        {
            if (team is null || !IsId(team.Id) || !teams.Add(team.Id) || team.Maintainers is null || team.Members is null) return "a team does not hold";
            var people = team.Maintainers.Concat(team.Members).ToList();
            if (people.Count > MaxItems || people.Any(static id => !IsId(id)) || people.Distinct(StringComparer.Ordinal).Count() != people.Count) return "a team names a person twice";
            if (people.Any(suspended.Contains)) return "a suspended person is in a team";
            if (team.Services is { } services && (services.Count > MaxItems || services.Any(static id => !IsId(id)) || services.Distinct(StringComparer.Ordinal).Count() != services.Count))
                return "a team names its service identities once each";
        }

        var machines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var machine in document.Machines)
        {
            if (machine is null || !IsId(machine.DeviceId) || !machines.Add(machine.DeviceId) || !teams.Contains(machine.TeamId ?? string.Empty))
                return "a team machine does not name a team of the version, or is named twice";
        }

        if (document.Approvers is { } approvers)
        {
            if (approvers.Count > MaxItems) return "it holds too many approvers";
            var automations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in approvers)
            {
                if (item is null || !IsId(item.AutomationId) || !automations.Add(item.AutomationId) || item.People is null || item.People.Count == 0 || item.People.Count > MaxItems
                    || item.People.Any(static id => !IsId(id)) || item.People.Distinct(StringComparer.Ordinal).Count() != item.People.Count)
                    return "the approvers of an automation do not hold";
                if (item.People.Any(suspended.Contains)) return "a suspended person answers approvals";
                if (item.Until is { } until && until.Keys.Any(person => !item.People.Contains(person, StringComparer.Ordinal))) return "the approvers of an automation do not hold";
            }
        }

        return null;
    }

    private static bool IsId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength && !id.Any(char.IsControl);
}

/// <summary>A version of the access document as the server lists it.</summary>
/// <param name="Number">Its number.</param>
/// <param name="Hash">Its hash.</param>
/// <param name="Document">The signed version.</param>
/// <param name="RecordedAt">When the server recorded it.</param>
public sealed record AccessDocumentDto(long Number, string Hash, FleetSignedAccessDocument Document, DateTimeOffset RecordedAt);

/// <summary>Versions of the access document.</summary>
/// <param name="Items">The versions, in order.</param>
public sealed record AccessDocumentListDto(IReadOnlyList<AccessDocumentDto> Items);

/// <summary>What the next version of the access document would say, from the server's model; a signer's console checks it, says what changes, and signs it.</summary>
/// <param name="Document">The next version, without its signer and time: the console fills them, then signs.</param>
/// <param name="Changes">Whether it says something else than the last version.</param>
/// <param name="Names">The names of the people, teams and machines it names, for display only.</param>
public sealed record AccessDocumentDraftDto(FleetAccessDocument Document, bool Changes, IReadOnlyDictionary<string, string> Names);

/// <summary>The body of <c>POST /api/v1/access/documents</c>.</summary>
/// <param name="Document">The version, signed by the calling controller device.</param>
public sealed record AppendAccessDocumentRequest(FleetSignedAccessDocument Document);

/// <summary>The source-generated contracts of the access document.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(FleetAccessDocument))]
[JsonSerializable(typeof(FleetSignedAccessDocument))]
[JsonSerializable(typeof(AccessDocumentDto))]
[JsonSerializable(typeof(AccessDocumentListDto))]
[JsonSerializable(typeof(AccessDocumentDraftDto))]
[JsonSerializable(typeof(AppendAccessDocumentRequest))]
[JsonSerializable(typeof(Api.ApiProblemDto))]
public sealed partial class FleetAccessDocumentJsonContext : JsonSerializerContext;
