using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

// What the node decides from the signed access document (doc/fleet/access-control.md, what the node decides): on a team
// machine each person sees and drives only the sessions they started, and the work of a person the document suspends stops.
public sealed partial class FleetNodeService
{
    private FleetSessionPrincipals? _principals;
    private FleetNodePeople? _people;
    // One reconciliation of the owner at a time: the links of several servers apply their documents beside each other.
    private readonly Lock _ownerGate = new();

    /// <summary>Gets the people of the organization this machine names, and who asks to be let in.</summary>
    public FleetNodePeople People => LazyInitializer.EnsureInitialized(ref _people, () => new FleetNodePeople(_host.CatalogOptions.FleetRoot, _options.TimeProvider));

    // Who the machine lets in: read and changed by its owner, on the machine or from a device paired to manage it.
    private void RegisterPeople(FleetNodeCaller caller, Access access)
    {
        On(caller, FleetNodeMethods.ControlPeopleGet, access, FleetNodeJsonContext.Default.FleetEmpty, FleetNodeJsonContext.Default.FleetNodePeopleInfo,
            (_, _, _) => Task.FromResult(People.Read()), optionalParams: true);
        On(caller, FleetNodeMethods.ControlPeopleSet, access, FleetNodeJsonContext.Default.FleetNodePeopleChange, FleetNodeJsonContext.Default.FleetNodePeopleInfo,
            (_, change, _) =>
            {
                try
                {
                    var info = People.Set(change);
                    LogInfo($"The people of the machine changed: {change.PersonId} {(change.Permissions is { Count: > 0 } names ? string.Join(",", names) : change.Refuse ? "refused" : "removed")}.");
                    return Task.FromResult(info);
                }
                catch (ArgumentException exception)
                {
                    throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message);
                }
            });
    }

    /// <summary>Gets who started each session here through a team membership, kept in the fleet folder of the node.</summary>
    internal FleetSessionPrincipals Principals => LazyInitializer.EnsureInitialized(ref _principals, () => new FleetSessionPrincipals(_host.CatalogOptions.FleetRoot));

    /// <summary>Gets the person who started a session here through a team membership, which the directory of the server shows it to; null otherwise.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The person, or null.</returns>
    public string? PersonOfSession(string sessionId) => Principals.PersonOf(sessionId);

    /// <summary>Gets the organization of the person who started a session here through a team membership; null otherwise.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The organization, or null.</returns>
    public string? OrganizationOfSession(string sessionId) => Principals.OrganizationOf(sessionId);

    /// <summary>
    /// Applies a newer access document of an organization: the sessions people it suspends started here are stopped, and their
    /// open channels closed. A node calls it once its server link verified the document.
    /// </summary>
    /// <param name="access">The document, as the link verified it.</param>
    /// <param name="cancellationToken">Cancels before the next session is stopped.</param>
    /// <returns>The sessions stopped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="access"/> is null.</exception>
    public Task<IReadOnlyList<string>> ApplyAccessAsync(FleetAccessState access, CancellationToken cancellationToken) => ApplyAccessAsync(access, null, cancellationToken);

    /// <summary>
    /// Applies a newer access document of an organization as <see cref="ApplyAccessAsync(FleetAccessState, CancellationToken)"/>
    /// does, and, with the device list it was verified against, ends what the former owner of this machine gave once the document
    /// says the machine has another owner (<see cref="ReconcileOwner"/>).
    /// </summary>
    /// <param name="access">The document, as the link verified it.</param>
    /// <param name="devices">The device list of that link, as it verified it; null leaves the pairings as they are.</param>
    /// <param name="cancellationToken">Cancels before the next session is stopped.</param>
    /// <returns>The sessions stopped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="access"/> is null.</exception>
    public async Task<IReadOnlyList<string>> ApplyAccessAsync(FleetAccessState access, FleetDeviceListState? devices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (devices is not null) ReconcileOwner(access, devices);
        var stopped = new List<string>();
        foreach (var (sessionId, personId, organizationId) in Principals.List())
        {
            // A person of another organization with the same id is not the one suspended; one kept before organizations were may be.
            if (!access.IsSuspended(personId) || organizationId is not null && organizationId != access.OrganizationId) continue;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _host.RuntimeService.AbortTreeAsync(sessionId, "Its person was suspended in the organization.", cancellationToken).ConfigureAwait(false);
                stopped.Add(sessionId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or IOException)
            {
                LogWarn($"The session {sessionId} of a suspended person could not be stopped: {exception.Message}");
            }
        }

        foreach (var caller in _callers.Values)
        {
            if (caller.Trust.IsRevoked(caller.Fingerprint)) await caller.Connection.CloseAsync(FleetErrorCodes.Revoked).ConfigureAwait(false);
        }

        if (stopped.Count > 0) LogInfo($"The sessions of suspended people were stopped: {string.Join(", ", stopped)}.");
        return stopped;
    }

    /// <summary>
    /// Ends what the former owner of this machine gave, once a signed access document says it has another owner than the one its
    /// pairings were made under (<c>doc/fleet/access-control.md</c>, ownership over time): a machine given to another person, to a
    /// team or to the organization. The devices paired with it are revoked, but the controller devices of a person who owns it now,
    /// who manage it through the device list anyway, and the people it named are taken out: its new owner pairs and names again.
    /// The owner they were made under is kept in the fleet folder, the person the device list names until a document says another,
    /// so that this happens once per change of owner, never for what the new owner pairs afterwards.
    /// </summary>
    /// <param name="access">The access document, as a server link verified it.</param>
    /// <param name="devices">The device list it was verified against.</param>
    /// <returns>The paired devices revoked.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="IOException">The pairings or the owner they were made under cannot be written.</exception>
    public IReadOnlyList<string> ReconcileOwner(FleetAccessState access, FleetDeviceListState devices)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(devices);
        var self = _options.Identity.DeviceId;
        if (access.OrganizationId is not { } organization || !devices.Members.TryGetValue(self, out var listed)) return [];
        var person = access.TeamOf(self) is null && !access.IsOrganizationMachine(self) ? access.MachineOwner(self, devices) : null;
        var owner = access.TeamOf(self) is { } team ? "team:" + team.Id : access.IsOrganizationMachine(self) ? "organization" : person is null ? null : "person:" + person;
        if (owner is null) return [];
        lock (_ownerGate) return EndWhatTheFormerOwnerGave(organization, listed, person, owner, devices);
    }

    private List<string> EndWhatTheFormerOwnerGave(string organization, FleetDeviceListMember listed, string? person, string owner, FleetDeviceListState devices)
    {
        var pairedUnder = new FleetPairedOwner(_host.CatalogOptions.FleetRoot);
        var before = pairedUnder.Read(organization) ?? (listed.OwnerId is { } named ? "person:" + named : null);
        if (string.Equals(before, owner, StringComparison.Ordinal)) return [];
        var revoked = new List<string>();
        foreach (var device in _trusted.List())
        {
            // The new owner's own controller devices manage the machine through the device list: their pairing stays.
            if (person is not null && devices.Members.TryGetValue(device.DeviceId, out var member) && member.Kind == FleetDeviceKinds.Controller && member.OwnerId == person
                && !devices.Removed.Contains(device.DeviceId) && string.Equals(member.Key.Fingerprint, device.Fingerprint.ToString(), StringComparison.Ordinal))
                continue;
            if (_trusted.Revoke(device.DeviceId)) revoked.Add(device.DeviceId);
        }

        People.Clear();
        pairedUnder.Write(organization, owner);
        LogInfo($"The machine has another owner ({owner}): the devices paired before were revoked ({(revoked.Count == 0 ? "none" : string.Join(", ", revoked))}), and the people it named taken out.");
        return revoked;
    }

    /// <summary>
    /// Gets or sets what says whether the signed access document of an organization grants a person of it the content of a
    /// session of this machine (<c>session.view-content</c>, by organization, person and session): their devices read that
    /// session live here, never drive it. Null grants nothing.
    /// </summary>
    public Func<string?, string, string, bool>? ContentGrants { get; set; }

    // A device of a person of an organization granted the content of the session a read names, but whose rights here do not
    // reach it (not their session, not a device of the owner): it reads that session only, for this call.
    private (TrustedDevice Device, string SessionId)? ContentReader(FleetNodeCaller caller, JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } read
            || !read.TryGetProperty("sessionId", out var named) || named.ValueKind != JsonValueKind.String || named.GetString() is not { Length: > 0 } sessionId)
            return null;
        return ContentReader(caller, sessionId) is { } device ? (device, sessionId) : null;
    }

    private TrustedDevice? ContentReader(FleetNodeCaller caller, string sessionId)
    {
        if (caller.IsOwner || ContentGrants is not { } grants) return null;
        if (caller.Trust.IsRevoked(caller.Fingerprint) || _trusted.FindTrusted(caller.Fingerprint) is not null) return null;
        if (caller.Trust.FindTrusted(caller.Fingerprint) is not { PersonId: { } person, OwnSessionsOnly: true } device) return null;
        if (Principals.Owns(sessionId, person, device.OrganizationId) || !grants(device.OrganizationId, person, sessionId)) return null;
        return device with { Permissions = DevicePermissions.View };
    }

    // Whether the caller of a live read may still read the session, as at its start: a grant that expired or that a newer
    // signed document took away, a pairing revoked or narrowed, ends what it reads live.
    private bool MayStillRead(FleetNodeCaller caller, string sessionId)
    {
        if (ContentReader(caller, sessionId) is not null) return true;
        try
        {
            RequireOwnSession(Authorize(caller, Access.View), new FleetSessionRef(sessionId));
            return true;
        }
        catch (FleetException)
        {
            return false;
        }
    }

    // A device that sees its own sessions only reaches a session its person started here; others are not shown to exist.
    private void RequireOwnSession(TrustedDevice? device, object parameters)
    {
        if (device is not { OwnSessionsOnly: true }) return;
        if (SessionIdOf(parameters) is not { } sessionId) return;
        if (!Principals.Owns(sessionId, device.PersonId, device.OrganizationId))
            throw new FleetException(FleetNodeErrorCodes.SessionNotFound, null, "No session of yours has this id on this machine.");
    }

    // Records that a person started a session here through a team membership: they alone see and drive it.
    private void RecordPrincipal(TrustedDevice? device, string sessionId)
    {
        if (device is { OwnSessionsOnly: true, PersonId: { } person }) Principals.Record(sessionId, person, device.OrganizationId);
    }

    private static string? SessionIdOf(object parameters) => parameters switch
    {
        FleetSessionRef value => value.SessionId,
        FleetHistoryParams value => value.SessionId,
        FleetWatchParams value => value.SessionId,
        FleetSendParams value => value.SessionId,
        FleetQueueParams value => value.SessionId,
        FleetSteerParams value => value.SessionId,
        FleetAbortParams value => value.SessionId,
        FleetPermissionAnswerParams value => value.SessionId,
        FleetAskAnswerParams value => value.SessionId,
        FleetMoveStartParams value => value.SessionId,
        _ => null,
    };
}

/// <summary>
/// Who started each session of a node through a team membership (<c>doc/fleet/security-and-privacy.md</c>, shared machines):
/// on a team machine each person sees only their own sessions, and the work of a person who leaves is stopped. Kept in the
/// fleet folder of the node, bounded, one writer.
/// </summary>
internal sealed class FleetSessionPrincipals
{
    /// <summary>The most sessions kept; the oldest go first.</summary>
    public const int MaxSessions = 5000;

    private const string FileName = "session-principals.json";

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly List<FleetSessionPrincipal> _items;

    public FleetSessionPrincipals(string fleetRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(fleetRoot);
        _path = Path.Combine(fleetRoot, FileName);
        _items = Load(_path);
    }

    // A person's id is unique within one organization only: a session is theirs for the same person of the same organization.
    public bool Owns(string sessionId, string? personId, string? organizationId)
    {
        if (personId is null) return false;
        lock (_gate)
            return _items.Any(item => item.SessionId == sessionId && item.PersonId == personId && string.Equals(item.OrganizationId, organizationId, StringComparison.Ordinal));
    }

    public string? PersonOf(string sessionId)
    {
        lock (_gate) return _items.FirstOrDefault(item => item.SessionId == sessionId)?.PersonId;
    }

    public string? OrganizationOf(string sessionId)
    {
        lock (_gate) return _items.FirstOrDefault(item => item.SessionId == sessionId)?.OrganizationId;
    }

    public IReadOnlyList<(string SessionId, string PersonId, string? OrganizationId)> List()
    {
        lock (_gate) return [.. _items.Select(static item => (item.SessionId, item.PersonId, item.OrganizationId))];
    }

    public void Record(string sessionId, string personId, string? organizationId)
    {
        lock (_gate)
        {
            if (_items.Any(item => item.SessionId == sessionId)) return;
            _items.Add(new FleetSessionPrincipal(sessionId, personId, organizationId));
            if (_items.Count > MaxSessions) _items.RemoveRange(0, _items.Count - MaxSessions);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(_items, FleetSessionPrincipalJson.Default.ListFleetSessionPrincipal));
        }
    }

    private static List<FleetSessionPrincipal> Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), FleetSessionPrincipalJson.Default.ListFleetSessionPrincipal) ?? [];
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// The owner the pairings and the people of a node were made under, by organization: a person, a team or the organization, as
/// <see cref="FleetNodeService.ReconcileOwner"/> last read it. Kept in the fleet folder of the node.
/// </summary>
internal sealed class FleetPairedOwner(string fleetRoot)
{
    private const string FileName = "paired-owner.json";

    private readonly string _path = Path.Combine(fleetRoot, FileName);

    public string? Read(string organizationId)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(_path), FleetSessionPrincipalJson.Default.DictionaryStringString)?.GetValueOrDefault(organizationId);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    public void Write(string organizationId, string owner)
    {
        Dictionary<string, string> owners;
        try
        {
            owners = JsonSerializer.Deserialize(File.ReadAllBytes(_path), FleetSessionPrincipalJson.Default.DictionaryStringString) ?? new(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            owners = new(StringComparer.Ordinal);
        }

        owners[organizationId] = owner;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(owners, FleetSessionPrincipalJson.Default.DictionaryStringString));
    }
}

/// <summary>A session and the person who started it.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="PersonId">The person.</param>
/// <param name="OrganizationId">The organization of the person; null for a session recorded before it was kept.</param>
internal sealed record FleetSessionPrincipal(string SessionId, string PersonId, string? OrganizationId = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<FleetSessionPrincipal>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class FleetSessionPrincipalJson : JsonSerializerContext;
