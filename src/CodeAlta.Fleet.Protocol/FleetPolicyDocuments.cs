using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol;

/// <summary>
/// The rules of an organization's policy (<c>doc/fleet/security-and-privacy.md</c>, policies and the kill switch): each one
/// narrows what the work of the fleet may do on its machines, in the spaces linked to its server; a rule left out says
/// nothing. A policy never widens what a machine's own policy allows.
/// </summary>
/// <param name="Providers">The kinds of providers the work may use (the <c>type</c> of a provider, such as <c>anthropic</c>); null for any.</param>
/// <param name="Models">The models it may use, by id, a trailing <c>*</c> naming a prefix; null for any.</param>
/// <param name="Sandbox">The sandbox profile the work runs in at least; null for no floor.</param>
/// <param name="ToolsOff">The tools turned off, as patterns of tool names; null for none.</param>
/// <param name="Network">The widest network of its sandbox: <c>off</c>, <c>hosts</c> or <c>on</c>; null for what the sandbox says.</param>
/// <param name="RemoteStarts">Whether a person may start sessions on a machine from another device; null or true for yes.</param>
/// <param name="AgentStarts">Whether agents of other machines may start sessions or send prompts; null or true for what the machine allows.</param>
/// <param name="MaxSessionMinutes">How long a session of the work of the fleet may run before its machine stops it; null for no limit.</param>
/// <param name="PermissionMode">The widest permission mode of the work: <c>default</c> (ask), <c>acceptEdits</c> or <c>bypassPermissions</c>; null for no limit.</param>
/// <param name="PublishFacts">Whether the sessions of the organization's spaces publish their facts (never their content) and cannot be Private towards it.</param>
/// <param name="AccountCredentials">Whether every automation runs with account credentials only, personal ones included.</param>
public sealed record FleetPolicyRules(
    [property: JsonPropertyName("providers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Providers = null,
    [property: JsonPropertyName("models"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Models = null,
    [property: JsonPropertyName("sandbox"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sandbox = null,
    [property: JsonPropertyName("toolsOff"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ToolsOff = null,
    [property: JsonPropertyName("network"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Network = null,
    [property: JsonPropertyName("remoteStarts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RemoteStarts = null,
    [property: JsonPropertyName("agentStarts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AgentStarts = null,
    [property: JsonPropertyName("maxSessionMinutes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxSessionMinutes = null,
    [property: JsonPropertyName("permissionMode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PermissionMode = null,
    [property: JsonPropertyName("publishFacts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PublishFacts = null,
    [property: JsonPropertyName("accountCredentials"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AccountCredentials = null)
{
    /// <summary>Gets the rules that say nothing.</summary>
    public static FleetPolicyRules None { get; } = new();

    /// <summary>Gets whether a provider of a kind may be used.</summary>
    /// <param name="providerType">The kind of the provider.</param>
    /// <returns>Whether it may.</returns>
    public bool AllowsProvider(string? providerType)
        => Providers is null || providerType is not null && Providers.Contains(providerType, StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets whether a model may be used; a model not named yet (the provider's default) is decided when it is known.</summary>
    /// <param name="modelId">The id of the model, or null when not known yet.</param>
    /// <returns>Whether it may.</returns>
    public bool AllowsModel(string? modelId)
        => Models is null || modelId is null || Models.Any(pattern => pattern.EndsWith('*')
            ? modelId.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, modelId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The kill switch, part of a version of the policy (<c>doc/fleet/security-and-privacy.md</c>, policies and the kill switch):
/// the machines it names stop the work of the fleet (sessions started from other devices, the sessions of agents of other
/// machines, automation runs) and take none until a version without it, while what a person does at the machine goes on.
/// </summary>
/// <param name="Scope"><see cref="FleetPolicyStopScopes.Fleet"/> (every machine), <see cref="FleetPolicyStopScopes.Machines"/> or <see cref="FleetPolicyStopScopes.Teams"/>.</param>
/// <param name="Machines">The devices of the machines it stops, for <see cref="FleetPolicyStopScopes.Machines"/>.</param>
/// <param name="Teams">The teams whose machines it stops, for <see cref="FleetPolicyStopScopes.Teams"/>.</param>
/// <param name="Reason">Why, in a sentence people read on the machines; null for none.</param>
public sealed record FleetPolicyStop(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("machines")] IReadOnlyList<string> Machines,
    [property: JsonPropertyName("teams")] IReadOnlyList<string> Teams,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

/// <summary>What a stop of the kill switch reaches.</summary>
public static class FleetPolicyStopScopes
{
    /// <summary>Every machine of the organization.</summary>
    public const string Fleet = "fleet";

    /// <summary>The machines it names.</summary>
    public const string Machines = "machines";

    /// <summary>The machines of the teams it names, as the signed access document says.</summary>
    public const string Teams = "teams";
}

/// <summary>
/// One version of the signed policy of an organization: its rules and its kill switch, signed by the controller device of
/// a signer. Versions form a chain, as the access document does: each names the hash of the one before, the entry of the
/// device list and the version of the access document its signer read, so that a machine that anchored the chain applies
/// only what a signer signed, and a server can neither forge a version, nor answer an older one, nor take a stop back.
/// </summary>
/// <remarks>
/// A version is signed by a controller device of a signer of the access document version it names (an owner, or an admin
/// who signs, not suspended there); while the organization signed no access document (<see cref="AccessNumber"/> 0), by a
/// controller device of the person who set the server up. <see cref="AccessNumber"/> never goes back.
/// </remarks>
/// <param name="Version">The version of the format: 1.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Number">The number of this version, from 1.</param>
/// <param name="Previous">The hash of the version before; null for the first one.</param>
/// <param name="DeviceListSequence">The entry of the device list the signer read.</param>
/// <param name="AccessNumber">The version of the access document the signer read; 0 when none was signed.</param>
/// <param name="Rules">The rules.</param>
/// <param name="Stop">The kill switch, or null when the work of the fleet goes on.</param>
/// <param name="SignerDeviceId">The controller device that signs it.</param>
/// <param name="Time">When the signer made it; for display, never for ordering.</param>
public sealed record FleetPolicyDocument(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("org")] string OrganizationId,
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("prev")] string? Previous,
    [property: JsonPropertyName("list")] long DeviceListSequence,
    [property: JsonPropertyName("access")] long AccessNumber,
    [property: JsonPropertyName("rules")] FleetPolicyRules Rules,
    [property: JsonPropertyName("stop"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FleetPolicyStop? Stop,
    [property: JsonPropertyName("signer")] string SignerDeviceId,
    [property: JsonPropertyName("time")] DateTimeOffset Time)
{
    /// <summary>The version of the format this library writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>A version of the policy as it travels and is stored: the exact JSON text signed, and the signature.</summary>
/// <param name="Payload">The JSON of a <see cref="FleetPolicyDocument"/>, byte for byte as signed.</param>
/// <param name="Signature">The ECDSA P-256 signature of <see cref="FleetPolicyDocuments.GetSignedBytes"/>, r and s in base64url.</param>
public sealed record FleetSignedPolicyDocument(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Signature);

/// <summary>The policy as a verifier replayed it.</summary>
/// <param name="OrganizationId">The organization; null before the first version.</param>
/// <param name="Number">The number of the last version, 0 when none.</param>
/// <param name="LastHash">The hash of the last version; null when none.</param>
/// <param name="DeviceListSequence">The entry of the device list the last version was signed at.</param>
/// <param name="AccessNumber">The version of the access document the last version was signed at.</param>
/// <param name="Rules">The rules in force.</param>
/// <param name="Stop">The kill switch in force, or null.</param>
/// <param name="SignerDeviceId">The device that signed the last version; null when none.</param>
/// <param name="Time">When its signer made the last version; null when none.</param>
public sealed record FleetPolicyState(
    string? OrganizationId,
    long Number,
    string? LastHash,
    long DeviceListSequence,
    long AccessNumber,
    FleetPolicyRules Rules,
    FleetPolicyStop? Stop,
    string? SignerDeviceId,
    DateTimeOffset? Time)
{
    /// <summary>Gets the state before any version: no rule, no stop.</summary>
    public static FleetPolicyState Empty { get; } = new(null, 0, null, 0, 0, FleetPolicyRules.None, null, null, null);

    /// <summary>Gets the hash of the first version, the root a device pins per server.</summary>
    public string? FirstHash { get; init; }

    /// <summary>Gets whether the kill switch stops the work of the fleet on a machine.</summary>
    /// <param name="deviceId">The device of the machine.</param>
    /// <param name="access">
    /// The access document the machine verified, which says the team of a team machine; null when it is not known, which a stop
    /// of teams reads strictly: it reaches the machine.
    /// </param>
    /// <returns>Whether its work stops.</returns>
    public bool Stops(string deviceId, FleetAccessState? access)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return Stop switch
        {
            null => false,
            { Scope: FleetPolicyStopScopes.Fleet } => true,
            { Scope: FleetPolicyStopScopes.Machines } stop => stop.Machines.Contains(deviceId, StringComparer.Ordinal),
            // An access document not known is no personal machine: the stop of teams reaches it, the strictest reading.
            { Scope: FleetPolicyStopScopes.Teams } stop => access is null || access.Machines.TryGetValue(deviceId, out var team) && stop.Teams.Contains(team, StringComparer.Ordinal),
            // A scope no version knows reaches every machine: the strictest reading.
            _ => true,
        };
    }
}

/// <summary>The result of checking a policy chain.</summary>
/// <param name="IsValid">Whether every version holds.</param>
/// <param name="BrokenAt">The number of the first version that does not hold, or null.</param>
/// <param name="Reason">Why it does not hold, for people, or null.</param>
/// <param name="State">The policy after the last version that holds.</param>
public sealed record FleetPolicyVerification(bool IsValid, long? BrokenAt, string? Reason, FleetPolicyState State);

/// <summary>A policy chain that does not verify: a machine keeps the last version it verified, and applies nothing of it.</summary>
public sealed class FleetPolicyDocumentException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="FleetPolicyDocumentException"/> class.</summary>
    /// <param name="brokenAt">The number of the version that does not hold.</param>
    /// <param name="reason">Why it does not hold.</param>
    public FleetPolicyDocumentException(long brokenAt, string reason)
        : base($"The policy of the server does not verify at version {brokenAt}: {reason}. Nothing after it was applied.")
    {
        BrokenAt = brokenAt;
        Reason = reason;
    }

    /// <summary>Gets the number of the version that does not hold.</summary>
    public long BrokenAt { get; }

    /// <summary>Gets why it does not hold.</summary>
    public string Reason { get; }
}

/// <summary>Writes, reads, signs, chains and verifies the versions of the policy, and says what changes between two.</summary>
public static class FleetPolicyDocuments
{
    /// <summary>The largest text of a version.</summary>
    public const int MaxPayloadLength = 128 * 1024;

    /// <summary>The most items of a list of a version.</summary>
    public const int MaxItems = 1_000;

    /// <summary>The longest reason of a stop.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>The permission modes a policy names, from the narrowest.</summary>
    public static IReadOnlyList<string> PermissionModes { get; } = ["default", "acceptEdits", "bypassPermissions"];

    /// <summary>The networks a policy names, from the narrowest.</summary>
    public static IReadOnlyList<string> Networks { get; } = ["off", "hosts", "on"];

    private const string SignDomain = "codealta-policy/1\n";
    private const string HashDomain = "codealta-policy-hash/1\n";
    private const int MaxIdLength = 128;
    private const int MaxNameLength = 200;

    /// <summary>Writes the JSON text of a version, the text a signer signs.</summary>
    /// <param name="document">The version.</param>
    /// <returns>The JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static string Serialize(FleetPolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, FleetPolicyDocumentJsonContext.Default.FleetPolicyDocument);
    }

    /// <summary>Reads the JSON text of a version.</summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="document">The version read, or null.</param>
    /// <returns>Whether the text is a version with its required fields.</returns>
    public static bool TryParse(string? payload, [NotNullWhen(true)] out FleetPolicyDocument? document)
    {
        document = null;
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength) return false;
        try
        {
            document = JsonSerializer.Deserialize(payload, FleetPolicyDocumentJsonContext.Default.FleetPolicyDocument);
        }
        catch (JsonException)
        {
            return false;
        }

        if (document is null || string.IsNullOrEmpty(document.OrganizationId) || string.IsNullOrEmpty(document.SignerDeviceId) || document.Rules is null
            || document.Stop is { } stop && (stop.Scope is null || stop.Machines is null || stop.Teams is null))
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
    public static string Hash(FleetSignedPolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes($"{HashDomain}{document.Payload}\n{document.Signature}")));
    }

    /// <summary>Replays a chain of versions from its first, against the device list and the access document the verifier anchored.</summary>
    /// <param name="documents">The signed versions, in order.</param>
    /// <param name="devices">The device list, as the verifier verified it.</param>
    /// <param name="accessAt">Gives the access document at a version the verifier verified, or null when it holds none of that number.</param>
    /// <param name="organizationId">The organization the versions must name.</param>
    /// <returns>Whether the chain holds, where it breaks, and the policy up to there.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static FleetPolicyVerification Verify(IEnumerable<FleetSignedPolicyDocument> documents, FleetDeviceListState devices, Func<long, FleetAccessState?> accessAt,
        string organizationId)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var state = FleetPolicyState.Empty;
        foreach (var signed in documents)
        {
            if (!TryAppend(state, signed, devices, accessAt, organizationId, out var next, out var reason)) return new(false, state.Number + 1, reason, state);
            state = next;
        }

        return new(true, null, null, state);
    }

    /// <summary>Checks one version after a state and gives the state with it.</summary>
    /// <param name="state">The policy before the version.</param>
    /// <param name="signed">The signed version.</param>
    /// <param name="devices">The device list, as the verifier verified it.</param>
    /// <param name="accessAt">Gives the access document at a version the verifier verified, or null.</param>
    /// <param name="organizationId">The organization the version must name.</param>
    /// <param name="next">The policy with the version, or <paramref name="state"/>.</param>
    /// <param name="reason">Why it does not hold, or null.</param>
    /// <returns>Whether it holds.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool TryAppend(FleetPolicyState state, FleetSignedPolicyDocument? signed, FleetDeviceListState devices, Func<long, FleetAccessState?> accessAt,
        string organizationId, out FleetPolicyState next, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(accessAt);
        next = state;
        reason = Check(state, signed, devices, accessAt, organizationId, out var document);
        if (reason is not null) return false;
        var hash = Hash(signed!);
        next = new FleetPolicyState(document!.OrganizationId, document.Number, hash, document.DeviceListSequence, document.AccessNumber, document.Rules, document.Stop,
            document.SignerDeviceId, document.Time)
        {
            FirstHash = state.FirstHash ?? hash,
        };
        return true;
    }

    /// <summary>
    /// Says why the signer of a version a verifier takes after its checkpoint no longer holds authority now, or null when it
    /// does: its device is a controller device of the device list as the verifier holds it now, not removed, and its person
    /// signs in the access document the verifier holds now (the founder while there is none). A chain only checks that the
    /// signer held authority at the versions the version names, which a signer removed since still did; a version taken for the
    /// first time is held to the authority of now, so that a revoked signer cannot sign a new one against their past authority.
    /// </summary>
    /// <param name="signed">The signed version, which verified in its chain.</param>
    /// <param name="devices">The device list as the verifier holds it now.</param>
    /// <param name="access">The access document as the verifier holds it now; null or empty when the organization signed none.</param>
    /// <returns>Why its signer does not hold authority now, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signed"/> or <paramref name="devices"/> is null.</exception>
    public static string? CheckCurrentSigner(FleetSignedPolicyDocument signed, FleetDeviceListState devices, FleetAccessState? access)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(devices);
        if (!TryParse(signed.Payload, out var parsed)) return "the version does not read";
        if (!devices.Members.TryGetValue(parsed.SignerDeviceId, out var member) || member.Kind != FleetDeviceKinds.Controller || devices.Removed.Contains(parsed.SignerDeviceId))
            return "its signer signs no more: its device is no longer a controller device of the device list";
        if (access is { Number: > 0 })
            return access.IsSigner(member.OwnerId) ? null : "its signer signs no more: not an owner or an admin who signs in the access document as it is now, or suspended there";
        var founder = devices.FirstDeviceId is { } first && devices.Tenures.TryGetValue(first, out var held) ? held.Member.OwnerId
            : devices.FirstDeviceId is { } id && devices.Members.TryGetValue(id, out var initial) ? initial.OwnerId : null;
        return founder is not null && member.OwnerId == founder ? null : "its signer signs no more: before any access document, the person who set the server up signs";
    }

    /// <summary>Reads a signed version and checks it was signed by a given device, apart from the chain: what a server checks before it keeps one.</summary>
    /// <param name="signed">The signed version.</param>
    /// <param name="key">The key of the device that sends it.</param>
    /// <param name="document">The version read.</param>
    /// <param name="reason">Why it does not hold.</param>
    /// <returns>Whether it reads, holds and is signed by that key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public static bool TryReadSigned(FleetSignedPolicyDocument? signed, FleetDevicePublicKey key, [NotNullWhen(true)] out FleetPolicyDocument? document,
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

    /// <summary>Checks what a version says, whatever the chain: what a console checks before it signs one.</summary>
    /// <param name="document">The version.</param>
    /// <returns>Why it does not hold, for people, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static string? Validate(FleetPolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return CheckContent(document);
    }

    /// <summary>
    /// Says, for people, what a version changes from the policy before it: each rule set, changed or taken off, and the kill
    /// switch set or lifted. Names come from <paramref name="nameOf"/>, for display only; the ids are what was verified.
    /// </summary>
    /// <param name="before">The policy before.</param>
    /// <param name="after">The new version.</param>
    /// <param name="nameOf">Gives the name of a machine or a team by its id, or null to show the id.</param>
    /// <returns>One line per change, in a stable order.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<string> Describe(FleetPolicyState before, FleetPolicyDocument after, Func<string, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(nameOf);
        string Name(string id) => nameOf(id) is { Length: > 0 } name ? name : id;
        string List(IReadOnlyList<string> values) => string.Join(", ", values);
        var lines = new List<string>();
        var (old, rules) = (before.Rules, after.Rules);
        void Line(string? was, string? now, Func<string, string> set, string off)
        {
            if (was == now) return;
            lines.Add(now is null ? off : set(now));
        }

        void Flag(bool? was, bool? now, Func<bool, string> set, string off)
        {
            if (was == now) return;
            lines.Add(now is { } value ? set(value) : off);
        }

        void Number(int? was, int? now, Func<int, string> set, string off)
        {
            if (was == now) return;
            lines.Add(now is { } value ? set(value) : off);
        }

        void Items(IReadOnlyList<string>? was, IReadOnlyList<string>? now, Func<IReadOnlyList<string>, string> set, string off)
        {
            if (was is null && now is null || was is not null && now is not null && SameSet(was, now)) return;
            lines.Add(now is null ? off : set(now));
        }

        Items(old.Providers, rules.Providers, values => $"Only these kinds of providers: {List(values)}", "Any kind of provider");
        Items(old.Models, rules.Models, values => $"Only these models: {List(values)}", "Any model");
        Line(old.Sandbox, rules.Sandbox, value => $"The work of the fleet runs in the sandbox '{value}' at least", "No sandbox floor");
        Items(old.ToolsOff, rules.ToolsOff, values => $"Tools turned off: {List(values)}", "No tool turned off");
        Line(old.Network, rules.Network, value => $"Network of the sandbox at most: {value}", "No limit on the network of the sandbox");
        Flag(old.RemoteStarts, rules.RemoteStarts, value => value ? "People may start sessions on other machines" : "Nobody starts sessions on another machine",
            "People may start sessions on other machines");
        Flag(old.AgentStarts, rules.AgentStarts, value => value ? "Agents may start work on other machines, where the machine allows it" : "Agents start no work on other machines",
            "Agents may start work on other machines, where the machine allows it");
        Number(old.MaxSessionMinutes, rules.MaxSessionMinutes, value => $"A session of the fleet stops after {value} minutes", "No limit on the length of sessions");
        Line(old.PermissionMode, rules.PermissionMode, value => $"Permission mode at most: {value}", "No limit on the permission mode");
        Flag(old.PublishFacts, rules.PublishFacts, value => value ? "The sessions of the organization's spaces publish their facts" : "Sessions may be Private towards the organization",
            "Sessions may be Private towards the organization");
        Flag(old.AccountCredentials, rules.AccountCredentials, value => value ? "Every automation runs with account credentials" : "Personal automations may use a subscription",
            "Personal automations may use a subscription");

        var (was, stop) = (before.Stop, after.Stop);
        if (stop is not null && (was is null || !SameStop(was, stop)))
        {
            var where = stop.Scope switch
            {
                FleetPolicyStopScopes.Fleet => "every machine",
                FleetPolicyStopScopes.Machines => List([.. stop.Machines.Select(Name)]),
                _ => "the machines of " + List([.. stop.Teams.Select(Name)]),
            };
            lines.Add($"STOP: the work of the fleet stops on {where}" + (stop.Reason is { Length: > 0 } reason ? $" ({reason})" : string.Empty));
        }
        else if (stop is null && was is not null)
        {
            lines.Add("The work of the fleet goes on: the stop is lifted");
        }

        return lines;
    }

    private static bool SameStop(FleetPolicyStop left, FleetPolicyStop right)
        => left.Scope == right.Scope && SameSet(left.Machines, right.Machines) && SameSet(left.Teams, right.Teams) && left.Reason == right.Reason;

    private static bool SameSet(IEnumerable<string> left, IEnumerable<string> right) => new HashSet<string>(left, StringComparer.Ordinal).SetEquals(right);

    private static string? Check(FleetPolicyState state, FleetSignedPolicyDocument? signed, FleetDeviceListState devices, Func<long, FleetAccessState?> accessAt,
        string organizationId, out FleetPolicyDocument? document)
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
        if (parsed.AccessNumber < state.AccessNumber) return "it names a version of the access document older than the one of the version before";
        if (devices.MemberFrom(parsed.SignerDeviceId, parsed.DeviceListSequence) is not { Kind: FleetDeviceKinds.Controller } signer
            || devices.Tenures.TryGetValue(parsed.SignerDeviceId, out var tenure) && tenure.AddedAt > parsed.DeviceListSequence)
            return "its signer was not a controller device of the device list at the entry it names";
        if (!signer.Key.Verify(GetSignedBytes(signed.Payload), signed.Signature)) return "its signature does not verify with the key of its signer";

        var person = signer.OwnerId;
        if (parsed.AccessNumber == 0)
        {
            var founder = devices.FirstDeviceId is { } first && devices.Tenures.TryGetValue(first, out var held) ? held.Member.OwnerId
                : devices.FirstDeviceId is { } id && devices.Members.TryGetValue(id, out var member) ? member.OwnerId : null;
            if (founder is null || person != founder) return "before any access document, a version is signed by a device of the person who set the server up";
        }
        else if (accessAt(parsed.AccessNumber) is not { } access || access.OrganizationId != organizationId)
        {
            return $"it names version {parsed.AccessNumber} of the access document, which this device has not verified";
        }
        else if (!access.IsSigner(person))
        {
            return "its signer is not an owner or an admin who signs in the access document it names, or is suspended there";
        }

        document = parsed;
        return null;
    }

    // What a version says, whatever the chain: known format and values, short ids, no item twice.
    private static string? CheckContent(FleetPolicyDocument document)
    {
        if (document.Version != FleetPolicyDocument.CurrentVersion) return $"version {document.Version} of the format is not known";
        if (document.Number < 1) return "its number is not positive";
        if (document.AccessNumber < 0) return "it names a negative version of the access document";
        var rules = document.Rules;
        foreach (var list in new[] { rules.Providers, rules.Models, rules.ToolsOff })
        {
            if (list is null) continue;
            if (list.Count > MaxItems || list.Any(static item => !IsName(item)) || list.Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count)
                return "a list of its rules does not hold";
        }

        if (rules.Sandbox is { } sandbox && !IsName(sandbox)) return "its sandbox does not read";
        if (rules.Network is { } network && !Networks.Contains(network, StringComparer.Ordinal)) return $"its network is '{network}', not off, hosts or on";
        if (rules.PermissionMode is { } mode && !PermissionModes.Contains(mode, StringComparer.Ordinal)) return $"its permission mode '{mode}' is not known";
        if (rules.MaxSessionMinutes is { } minutes && (minutes < 1 || minutes > 7 * 24 * 60)) return "its longest session is 1 minute to 7 days";
        if (document.Stop is { } stop)
        {
            if (stop.Scope is not (FleetPolicyStopScopes.Fleet or FleetPolicyStopScopes.Machines or FleetPolicyStopScopes.Teams)) return $"its stop reaches '{stop.Scope}', which is not known";
            foreach (var list in new[] { stop.Machines, stop.Teams })
            {
                if (list.Count > MaxItems || list.Any(static id => !IsId(id)) || list.Distinct(StringComparer.Ordinal).Count() != list.Count) return "the list of its stop does not hold";
            }

            if (stop.Scope == FleetPolicyStopScopes.Machines && (stop.Machines.Count == 0 || stop.Teams.Count > 0)) return "a stop of machines names machines, and only them";
            if (stop.Scope == FleetPolicyStopScopes.Teams && (stop.Teams.Count == 0 || stop.Machines.Count > 0)) return "a stop of teams names teams, and only them";
            if (stop.Scope == FleetPolicyStopScopes.Fleet && (stop.Machines.Count > 0 || stop.Teams.Count > 0)) return "a stop of the fleet names no machine and no team";
            if (stop.Reason is { } reason && (reason.Length > MaxReasonLength || reason.Any(static c => char.IsControl(c) && c is not '\n'))) return "the reason of its stop does not hold";
        }

        return null;
    }

    private static bool IsId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength && !id.Any(char.IsControl);

    private static bool IsName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength && !name.Any(char.IsControl);
}

/// <summary>A version of the policy as the server lists it.</summary>
/// <param name="Number">Its number.</param>
/// <param name="Hash">Its hash.</param>
/// <param name="Document">The signed version.</param>
/// <param name="RecordedAt">When the server recorded it.</param>
public sealed record PolicyDocumentDto(long Number, string Hash, FleetSignedPolicyDocument Document, DateTimeOffset RecordedAt);

/// <summary>Versions of the policy.</summary>
/// <param name="Items">The versions, in order.</param>
public sealed record PolicyDocumentListDto(IReadOnlyList<PolicyDocumentDto> Items);

/// <summary>The body of <c>POST /api/v1/policies</c>.</summary>
/// <param name="Document">The version, signed by the calling controller device.</param>
public sealed record AppendPolicyDocumentRequest(FleetSignedPolicyDocument Document);

/// <summary>The methods of the control connection about the policy.</summary>
public static class FleetPolicyMethods
{
    /// <summary>
    /// The event a server sends every device of the organization when a new version of the policy was recorded: devices read
    /// the chain again and verify it themselves.
    /// </summary>
    public const string Changed = "policy.changed";
}

/// <summary>The source-generated contracts of the policy.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(FleetPolicyDocument))]
[JsonSerializable(typeof(FleetSignedPolicyDocument))]
[JsonSerializable(typeof(PolicyDocumentDto))]
[JsonSerializable(typeof(PolicyDocumentListDto))]
[JsonSerializable(typeof(AppendPolicyDocumentRequest))]
[JsonSerializable(typeof(Api.ApiProblemDto))]
public sealed partial class FleetPolicyDocumentJsonContext : JsonSerializerContext;
