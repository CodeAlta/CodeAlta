using System.Collections.Concurrent;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Services;

/// <summary>
/// What each caller may see listed (<c>doc/fleet/access-control.md</c>, where access matters: "search and the change feed:
/// the server filters per caller"): the machines, the published sessions, the devices and the presence of an organization,
/// for the REST API and for the directory pushed on the control connection.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Owners, admins and auditors see every machine and session: that they exist, where they run, what they cost; never their
/// content, which the server does not hold.</item>
/// <item>A person sees their own devices and machines, the sessions of their personal machines, the machines of their teams, the
/// sessions they started on a team machine (all of them for its maintainers), and what a grant names.</item>
/// <item>A node sees itself and its own sessions; a suspended person or a guest without grants sees nothing of others.</item>
/// </list>
/// The access of an organization is read once and kept a few seconds, and read again at once when access changes, so that a
/// person taken out of a team stops seeing its machines.
/// </remarks>
internal sealed class DirectoryVisibility
{
    /// <summary>How long a read of the access of an organization is kept when nothing invalidates it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(2);

    private readonly ICoordinatorStore _store;
    private readonly IAccessStore _access;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _views = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="DirectoryVisibility"/> class.</summary>
    /// <param name="store">The store of the organization.</param>
    /// <param name="access">The store of access control.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public DirectoryVisibility(ICoordinatorStore store, IAccessStore access, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _access = access;
        _time = time;
    }

    /// <summary>Forgets what was read of an organization: the next decision reads it again.</summary>
    /// <param name="organizationId">The organization.</param>
    public void Invalidate(string organizationId) => _views.TryRemove(organizationId, out _);

    /// <summary>Reads what a device may see in its organization.</summary>
    /// <param name="viewer">The device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The scope of the viewer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="viewer"/> is null.</exception>
    public async ValueTask<DirectoryScope> ScopeAsync(DeviceRecord viewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var (snapshot, devices) = await ViewAsync(viewer.OrganizationId, cancellationToken).ConfigureAwait(false);
        return new DirectoryScope(viewer, snapshot, devices, _time.GetUtcNow());
    }

    /// <summary>Gets whether a device may see a change of the directory.</summary>
    /// <param name="viewer">The device that watches.</param>
    /// <param name="change">The change.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether it is sent to that device.</returns>
    public async ValueTask<bool> SeesAsync(DeviceRecord viewer, FleetDirectoryChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var scope = await ScopeAsync(viewer, cancellationToken).ConfigureAwait(false);
        if (change.Session is { } session) return scope.SeesSession(session.MachineId, session.Fact.SessionId, session.Fact.PersonId);
        if (change.Machine is { } machine) return scope.SeesMachine(machine.DeviceId);
        if (change.Presence is { } presence) return scope.SeesDevice(presence.DeviceId);
        return scope.SeesMachine(change.Id) || scope.SeesDevice(change.Id);
    }

    /// <summary>Keeps of a snapshot of the directory what a device may see.</summary>
    /// <param name="viewer">The device that watches.</param>
    /// <param name="snapshot">The whole snapshot.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The snapshot of that device.</returns>
    public async ValueTask<FleetDirectorySnapshot> FilterAsync(DeviceRecord viewer, FleetDirectorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var scope = await ScopeAsync(viewer, cancellationToken).ConfigureAwait(false);
        return new FleetDirectorySnapshot([.. snapshot.Machines.Where(machine => scope.SeesMachine(machine.DeviceId))],
            [.. snapshot.Sessions.Where(session => scope.SeesSession(session.MachineId, session.Fact.SessionId, session.Fact.PersonId))],
            [.. snapshot.Presence.Where(presence => scope.SeesDevice(presence.DeviceId))]);
    }

    private async ValueTask<(AccessSnapshot Snapshot, IReadOnlyDictionary<string, DeviceRecord> Devices)> ViewAsync(string organizationId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_views.TryGetValue(organizationId, out var held) && now - held.ReadAt < Lifetime) return (held.Snapshot, held.Devices);
        var snapshot = await _access.ReadAccessAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var devices = (await _store.ListDevicesAsync(organizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(static device => device.Id, StringComparer.Ordinal);
        _views[organizationId] = new Entry(snapshot, devices, now);
        return (snapshot, devices);
    }

    private sealed record Entry(AccessSnapshot Snapshot, IReadOnlyDictionary<string, DeviceRecord> Devices, DateTimeOffset ReadAt);
}

/// <summary>What one device may see of its organization, decided by the rules of <see cref="DirectoryVisibility"/>.</summary>
internal sealed class DirectoryScope
{
    private readonly DeviceRecord _viewer;
    private readonly AccessSnapshot _snapshot;
    private readonly IReadOnlyDictionary<string, DeviceRecord> _devices;
    private readonly DateTimeOffset _now;
    private readonly PersonRecord? _person;

    internal DirectoryScope(DeviceRecord viewer, AccessSnapshot snapshot, IReadOnlyDictionary<string, DeviceRecord> devices, DateTimeOffset now)
    {
        _viewer = viewer;
        _snapshot = snapshot;
        _devices = devices;
        _now = now;
        // A node acts only as itself; a controller device for its active person.
        _person = viewer.Kind == FleetDeviceKinds.Controller ? snapshot.Person(viewer.OwnerPersonId) is { IsActive: true } person ? person : null : null;
        Broad = _person is { Role: FleetOrganizationRoles.Owner or FleetOrganizationRoles.Admin or FleetOrganizationRoles.Auditor };
    }

    /// <summary>Gets whether the viewer sees every machine and session of the organization: an owner, an admin or an auditor.</summary>
    public bool Broad { get; }

    /// <summary>Gets whether the viewer sees a machine.</summary>
    /// <param name="machineId">The device of the machine.</param>
    /// <returns>Whether it is listed to the viewer.</returns>
    public bool SeesMachine(string machineId)
    {
        if (machineId == _viewer.Id || Broad) return true;
        if (_person is null) return false;
        var (kind, owner) = OwnerOf(machineId);
        if (kind == FleetPrincipalKinds.Person && owner == _person.Id) return true;
        if (kind == FleetPrincipalKinds.Team && owner is not null && _snapshot.TeamRole(owner, _person.Id) is not null) return true;
        return owner is not null && AccessPolicy.Allows(_snapshot, _person.Id, "machine.view", AccessTarget.Resource(FleetResourceKinds.Machine, machineId, kind, owner), _now);
    }

    /// <summary>Gets whether the viewer sees a published session.</summary>
    /// <param name="machineId">The machine that owns it.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="personId">The person who started it on a team machine, as the machine said.</param>
    /// <returns>Whether it is listed to the viewer.</returns>
    public bool SeesSession(string machineId, string sessionId, string? personId)
    {
        if (machineId == _viewer.Id || Broad) return true;
        if (_person is null) return false;
        // A session a person started (on a team machine, or one whose owner named them) is listed to them.
        if (personId == _person.Id) return true;
        var (kind, owner) = OwnerOf(machineId);
        if (kind == FleetPrincipalKinds.Person && owner == _person.Id) return true;
        // On a team machine, its maintainers see every session's facts, a member the sessions they started there.
        if (kind == FleetPrincipalKinds.Team && owner is not null && _snapshot.TeamRole(owner, _person.Id) is { } role)
        {
            if (role == FleetTeamRoles.Maintainer || personId == _person.Id) return true;
        }

        // A session a person started on a team machine is theirs; one without a person (a run of the team) is the team's.
        var (sessionKind, sessionOwner) = kind == FleetPrincipalKinds.Team && personId is not null ? (FleetPrincipalKinds.Person, personId) : (kind, owner);
        if (sessionOwner is null) return false;
        // A grant of its content lists the session too: its sealed title opens with the keys of that grant.
        var target = AccessTarget.Resource(FleetResourceKinds.Session, sessionId, sessionKind, sessionOwner);
        return AccessPolicy.Allows(_snapshot, _person.Id, "session.view-facts", target, _now) || AccessPolicy.Allows(_snapshot, _person.Id, "session.view-content", target, _now);
    }

    /// <summary>Gets whether the viewer sees a device (its name, its presence): its own devices, the machines it sees, everything for the broad roles.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>Whether it is listed to the viewer.</returns>
    public bool SeesDevice(string deviceId)
    {
        if (deviceId == _viewer.Id || Broad) return true;
        if (_person is null) return false;
        if (_devices.TryGetValue(deviceId, out var device) && device.OwnerPersonId == _person.Id) return true;
        return SeesMachine(deviceId);
    }

    /// <summary>Gets whether the viewer sees a person of the organization: everyone who may view access, themselves otherwise.</summary>
    /// <param name="personId">The person.</param>
    /// <returns>Whether they are listed.</returns>
    public bool SeesPerson(string personId)
        => _person is not null && (_person.Id == personId || AccessPolicy.Allows(_snapshot, _person.Id, "access.view", AccessTarget.Organization, _now));

    // The owner of a machine: its record of its own, or the person its device belongs to.
    private (string Kind, string? Id) OwnerOf(string machineId)
    {
        if (_snapshot.Owner(FleetResourceKinds.Machine, machineId) is { } own) return (own.OwnerKind, own.OwnerId);
        return (FleetPrincipalKinds.Person, _devices.TryGetValue(machineId, out var device) ? device.OwnerPersonId : null);
    }
}
