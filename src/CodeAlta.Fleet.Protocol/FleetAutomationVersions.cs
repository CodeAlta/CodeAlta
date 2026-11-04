using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// One version of an automation a person gives a machine (<c>doc/fleet/orchestration.md</c>, "The parts of an automation":
/// each version is signed by the device of the editor who saved it). A controller device signs it for one machine; the
/// machine runs it only once it verified the signature, against the device it is paired with or the device list of its
/// server, and refuses a version that is not newer than the one it holds, so that neither a channel nor a server can make
/// it run something its owner did not sign, nor bring back an older version.
/// </summary>
/// <remarks>
/// What the automation does travels in clear only on a direct channel (<see cref="Definition"/>); through a server it is
/// <see cref="Sealed"/> for the machine and the person's controller devices, and the server reads only the facts around it:
/// the id, the number, the machine, whether it is enabled and the kinds of its triggers.
/// </remarks>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization of the server it goes through; null on a direct channel.</param>
/// <param name="AutomationId">The automation.</param>
/// <param name="Number">The number of this version, from 1, always higher than the one before for this automation, whatever the machine.</param>
/// <param name="MachineId">The device of the machine that runs it: no other machine takes it.</param>
/// <param name="Removed">Whether this version takes the automation away from the machine: it holds nothing else, and older versions stay refused.</param>
/// <param name="Enabled">Whether its triggers start it.</param>
/// <param name="Triggers">The kinds of its triggers, such as <c>hourly</c> or <c>cron</c>: facts a server may read.</param>
/// <param name="Definition">The automation as one table of a configuration file, in clear; null when sealed or removed.</param>
/// <param name="Repository">The repository key of its project (<c>host/owner/repo</c>), in clear with <see cref="Definition"/>; null for a chat.</param>
/// <param name="Sealed">The definition and the repository sealed for the machine and the person's controller devices; null in clear or removed.</param>
/// <param name="SignerDeviceId">The controller device that signs it.</param>
/// <param name="SignerKey">The public key of the signer (the base64 of its DER SubjectPublicKeyInfo), which a paired machine checks against the fingerprint it paired with.</param>
/// <param name="Time">When the signer made it; for display, never for ordering.</param>
public sealed record FleetAutomationVersion(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string? OrganizationId,
    [property: JsonPropertyName("id")] string AutomationId,
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("machine")] string MachineId,
    [property: JsonPropertyName("removed")] bool Removed,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("triggers")] IReadOnlyList<string> Triggers,
    [property: JsonPropertyName("definition")] string? Definition,
    [property: JsonPropertyName("repository")] string? Repository,
    [property: JsonPropertyName("sealed")] string? Sealed,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("key")] string SignerKey,
    [property: JsonPropertyName("time")] DateTimeOffset Time)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The prefix of the scope of an automation of a team.</summary>
    public const string TeamScopePrefix = "team:";

    /// <summary>The scope of an automation of the organization.</summary>
    public const string OrganizationScope = "organization";

    /// <summary>Gets whether the automation is the organization's.</summary>
    [JsonIgnore]
    public bool IsOrganizationScope => Scope == OrganizationScope;

    /// <summary>
    /// Gets the scope of the automation (<c>doc/fleet/orchestration.md</c>, scopes): <c>team:&lt;id&gt;</c> for an automation of a
    /// team, which its maintainers sign for its machines and which runs as the team's service identity; null for a personal one,
    /// which writes the format as before.
    /// </summary>
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }

    /// <summary>
    /// Gets the provider the automation runs with (its id on the machine), said in clear so that the server checks that it
    /// runs with account credentials there for an automation of a team or of the organization; null for a personal one that
    /// says none.
    /// </summary>
    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }

    /// <summary>Gets the team of an automation of a team, or null.</summary>
    [JsonIgnore]
    public string? TeamId => Scope is { } scope && scope.StartsWith(TeamScopePrefix, StringComparison.Ordinal) ? scope[TeamScopePrefix.Length..] : null;
}

/// <summary>An automation version as it travels and is stored: the exact JSON text the signer signed, and the signature.</summary>
/// <param name="Payload">The JSON of a <see cref="FleetAutomationVersion"/>, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of <see cref="FleetAutomationVersionDocuments.GetSignedBytes"/>, r and s in base64url.</param>
public sealed record FleetSignedAutomationVersion(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>Writes, reads, checks and verifies the signed versions of automations.</summary>
public static class FleetAutomationVersionDocuments
{
    /// <summary>The most characters of a definition in clear.</summary>
    public const int MaxDefinitionLength = 256 * 1024;

    /// <summary>The most characters of a sealed definition.</summary>
    public const int MaxSealedLength = 512 * 1024;

    /// <summary>The most characters of a signed payload, all fields included.</summary>
    public const int MaxPayloadLength = 640 * 1024;

    /// <summary>The most trigger kinds a version names.</summary>
    public const int MaxTriggers = 32;

    private const string SignDomain = "codealta-automation-version/1\n";
    private const int MaxIdLength = 128;
    private const int MaxRepositoryLength = 512;
    private const int MaxTriggerLength = 32;

    /// <summary>Writes the JSON text of a version, the text a signer signs.</summary>
    /// <param name="version">The version.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is null.</exception>
    public static string Serialize(FleetAutomationVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return JsonSerializer.Serialize(version, FleetAutomationJsonContext.Default.FleetAutomationVersion);
    }

    /// <summary>Reads the JSON text of a version.</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="version">The version read, or null.</param>
    /// <returns>Whether the text is a version with its required fields.</returns>
    public static bool TryParse(string? payload, [NotNullWhen(true)] out FleetAutomationVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength) return false;
        try
        {
            version = JsonSerializer.Deserialize(payload, FleetAutomationJsonContext.Default.FleetAutomationVersion);
        }
        catch (JsonException)
        {
            return false;
        }

        if (version is null || string.IsNullOrEmpty(version.AutomationId) || string.IsNullOrEmpty(version.MachineId) || string.IsNullOrEmpty(version.SignerDeviceId)
            || string.IsNullOrEmpty(version.SignerKey) || version.Triggers is null)
        {
            version = null;
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

    /// <summary>
    /// Checks what a version says, apart from its signature: a known format, an id and a number, the machine, trigger kinds
    /// in short words, and its content: none once removed, otherwise either in clear or sealed, within their sizes.
    /// </summary>
    /// <param name="version">The version.</param>
    /// <returns>Why it does not hold, or null when it holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is null.</exception>
    public static string? Check(FleetAutomationVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Version != FleetAutomationVersion.CurrentVersion) return $"version {version.Version.ToString(CultureInfo.InvariantCulture)} of the format is not known";
        if (!IsName(version.AutomationId, MaxIdLength)) return "the id of the automation is 1 to 128 characters, without control characters";
        if (version.Number <= 0) return "the number of a version starts at 1";
        if (!IsName(version.MachineId, MaxIdLength)) return "a version names the machine that runs it";
        if (version.OrganizationId is { } organization && !IsName(organization, MaxIdLength)) return "the organization is 1 to 128 characters";
        if (version.Triggers.Count > MaxTriggers || version.Triggers.Any(static trigger => !IsWord(trigger)))
            return $"a version names at most {MaxTriggers} kinds of triggers, each a short word";
        if (version.Scope is not null && !version.IsOrganizationScope && (version.TeamId is not { } team || !IsName(team, MaxIdLength)))
            return "the scope of a version is a team (team:<id>), the organization, or none for a personal one";
        if (version.Provider is { } provider && !IsName(provider, MaxIdLength)) return "the provider of a version is its id, 1 to 128 characters";
        if (version.Scope is not null && !version.Removed && version.Provider is null) return "an automation of a team or of the organization names the provider it runs with";
        if (version.Removed)
        {
            return version.Definition is null && version.Repository is null && version.Sealed is null ? null : "a version that removes the automation holds nothing else";
        }

        if (version.Definition is null == version.Sealed is null) return "a version holds its definition either in clear or sealed";
        if (version.Definition is { Length: 0 or > MaxDefinitionLength }) return $"a definition is 1 to {MaxDefinitionLength} characters";
        if (version.Sealed is { Length: 0 or > MaxSealedLength }) return $"a sealed definition is 1 to {MaxSealedLength} characters";
        if (version.Repository is { } repository && (version.Sealed is not null || !IsName(repository, MaxRepositoryLength)))
            return "the repository is in clear beside a definition in clear, 1 to 512 characters";
        return null;
    }

    /// <summary>
    /// Verifies a version signed by the key it names, for a machine that checks that key against the device it is paired
    /// with: it reads, holds (<see cref="Check"/>), is for this machine, names the device of that key as its signer, and
    /// the signature verifies with it.
    /// </summary>
    /// <param name="signed">The signed version.</param>
    /// <param name="machineId">The device of the machine that verifies.</param>
    /// <param name="version">The version, when it verifies.</param>
    /// <param name="key">The key of its signer, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    public static bool TryVerify(FleetSignedAutomationVersion? signed, string? machineId, [NotNullWhen(true)] out FleetAutomationVersion? version,
        [NotNullWhen(true)] out FleetDevicePublicKey? key, [NotNullWhen(false)] out string? reason)
    {
        version = null;
        key = null;
        if (!TryRead(signed, machineId, out var parsed, out reason)) return false;
        if (!FleetDevicePublicKey.TryParse(parsed.SignerKey, out var signer) || !string.Equals(signer.DeviceId, parsed.SignerDeviceId, StringComparison.Ordinal))
        {
            reason = "the key of the version is not the key of its signer";
            return false;
        }

        if (!signer.Verify(GetSignedBytes(signed!.Payload), signed.Signature))
        {
            reason = "the signature does not verify with the key of its signer";
            return false;
        }

        (version, key) = (parsed, signer);
        return true;
    }

    /// <summary>
    /// Verifies a version for a machine of an organization: it reads, holds (<see cref="Check"/>), is for this machine and
    /// this organization, and is signed by a controller device of the verified device list, of the same person as the
    /// machine, with the key the list holds for it; a device the list removed signs nothing.
    /// </summary>
    /// <param name="signed">The signed version.</param>
    /// <param name="devices">The device list as the machine verified and anchored it.</param>
    /// <param name="organizationId">The organization of the server the machine is linked to.</param>
    /// <param name="machineId">The device of the machine that verifies.</param>
    /// <param name="version">The version, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public static bool TryVerify(FleetSignedAutomationVersion? signed, FleetDeviceListState devices, string? organizationId, string? machineId,
        [NotNullWhen(true)] out FleetAutomationVersion? version, [NotNullWhen(false)] out string? reason)
        => TryVerify(signed, devices, null, organizationId, machineId, out version, out reason);

    /// <summary>
    /// Verifies a version a server delivered, with the signed access document of the organization
    /// (<c>doc/fleet/access-control.md</c>, what the node decides): a version signed by a device of a suspended person is
    /// refused; a team machine takes the versions of its team's maintainers, and of its members when the team allows it,
    /// instead of those of the person the device list names.
    /// </summary>
    /// <param name="signed">The signed version.</param>
    /// <param name="devices">The device list of the server, as the machine verified it.</param>
    /// <param name="access">The access document of the server, as the machine verified it; null reads the machine as personal.</param>
    /// <param name="organizationId">The organization of the server.</param>
    /// <param name="machineId">The device of the machine that verifies.</param>
    /// <param name="version">The version, when it verifies.</param>
    /// <param name="reason">Why it does not verify, or null.</param>
    /// <returns>Whether it verifies.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public static bool TryVerify(FleetSignedAutomationVersion? signed, FleetDeviceListState devices, FleetAccessState? access, string? organizationId, string? machineId,
        [NotNullWhen(true)] out FleetAutomationVersion? version, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(devices);
        version = null;
        if (!TryRead(signed, machineId, out var parsed, out reason)) return false;
        if (string.IsNullOrEmpty(organizationId) || !string.Equals(parsed.OrganizationId, organizationId, StringComparison.Ordinal)
            || devices.OrganizationId is { } listed && !string.Equals(listed, organizationId, StringComparison.Ordinal))
        {
            reason = "the version is for another organization";
            return false;
        }

        if (!devices.Members.TryGetValue(parsed.SignerDeviceId, out var signer) || signer.Kind != FleetDeviceKinds.Controller || devices.Removed.Contains(parsed.SignerDeviceId))
        {
            reason = "the version is not signed by a controller device of the organization";
            return false;
        }

        if (CheckAccess(signer.OwnerId, machineId!, devices, access) is { } refused)
        {
            reason = refused;
            return false;
        }

        // An automation of the organization: signed by an owner or an admin who signs, for a machine of the organization; such a
        // machine runs no other.
        if ((parsed.IsOrganizationScope || access?.IsOrganizationMachine(machineId!) == true)
            && (!parsed.IsOrganizationScope || access?.IsOrganizationMachine(machineId!) != true || !access.IsSigner(signer.OwnerId)))
        {
            reason = "an automation of the organization is signed by an owner or an admin for a machine of the organization, which runs no other";
            return false;
        }

        // An automation of a team: signed by one of its maintainers, for a machine of that team, as the access document says.
        if (parsed.TeamId is { } teamId && (access?.TeamOf(machineId!) is not { } owning || owning.Id != teamId
            || FleetAccessState.RoleIn(owning, signer.OwnerId) != FleetTeamRoles.Maintainer))
        {
            reason = "an automation of a team is signed by one of its maintainers for a machine of that team";
            return false;
        }

        if (!string.Equals(parsed.SignerKey, signer.Key.Text, StringComparison.Ordinal) || !signer.Key.Verify(GetSignedBytes(signed!.Payload), signed.Signature))
        {
            reason = "the signature does not verify with the key the device list holds for its signer";
            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary>
    /// Says whether the person of a signing device may give a machine automations: not suspended; on a team machine a
    /// maintainer of its team, or a member when the team runs the automations of its members; on a personal machine its owner.
    /// </summary>
    /// <param name="signerPersonId">The person of the device that signed.</param>
    /// <param name="machineId">The device of the machine.</param>
    /// <param name="devices">The device list of the server, as the machine verified it.</param>
    /// <param name="access">The access document of the server, as the machine verified it; null reads the machine as personal.</param>
    /// <returns>Why not, or null when they may.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public static string? CheckAccess(string? signerPersonId, string machineId, FleetDeviceListState devices, FleetAccessState? access)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (access is not null && access.IsSuspended(signerPersonId)) return "the version is signed by a device of a person the organization suspended";
        // A machine of the organization runs what its owners and the admins who sign give it.
        if (access?.IsOrganizationMachine(machineId) == true)
            return access.IsSigner(signerPersonId) ? null : "a machine of the organization runs the automations its owners and admins give it";
        if (access?.TeamOf(machineId) is { } team)
        {
            var role = FleetAccessState.RoleIn(team, signerPersonId);
            if (role == FleetTeamRoles.Maintainer || role == FleetTeamRoles.Member && team.RunsMemberAutomations) return null;
            return "a team machine runs the automations of its team's maintainers, and of its members when the team allows it";
        }

        // Personal automations: only the person who owns the machine gives it automations.
        if (!devices.Members.TryGetValue(machineId, out var self) || self.OwnerId is null || !string.Equals(self.OwnerId, signerPersonId, StringComparison.Ordinal))
            return "the version is signed by a device of another person than the owner of this machine";
        return null;
    }

    private static bool TryRead(FleetSignedAutomationVersion? signed, string? machineId, [NotNullWhen(true)] out FleetAutomationVersion? version, [NotNullWhen(false)] out string? reason)
    {
        version = null;
        if (signed is null || string.IsNullOrEmpty(signed.Signature) || !TryParse(signed.Payload, out var parsed))
        {
            reason = "the version does not read";
            return false;
        }

        if (Check(parsed) is { } problem)
        {
            reason = problem;
            return false;
        }

        if (string.IsNullOrEmpty(machineId) || !string.Equals(parsed.MachineId, machineId, StringComparison.Ordinal))
        {
            reason = "the version is for another machine";
            return false;
        }

        version = parsed;
        reason = null;
        return true;
    }

    private static bool IsName(string? text, int maxLength) => !string.IsNullOrEmpty(text) && text.Length <= maxLength && !text.Any(char.IsControl);

    private static bool IsWord(string? text) => !string.IsNullOrEmpty(text) && text.Length <= MaxTriggerLength && text.All(static c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}

/// <summary>The source-generated contracts of the automation versions.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never, WriteIndented = false)]
[JsonSerializable(typeof(FleetAutomationVersion))]
[JsonSerializable(typeof(FleetSignedAutomationVersion))]
public sealed partial class FleetAutomationJsonContext : JsonSerializerContext;
