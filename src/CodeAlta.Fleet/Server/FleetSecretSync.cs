using System.Collections.Immutable;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>A project of this machine, as the values scoped to projects find it: its id here, and what recognizes it on every machine of the person.</summary>
/// <param name="ProjectId">The id of the project on this machine.</param>
/// <param name="Identity">What recognizes it on the person's machines (<see cref="FleetProjectKeys.IdentityAsync"/>).</param>
public sealed record FleetSecretProject(string ProjectId, string Identity);

/// <summary>What a refresh of the values of one server took.</summary>
/// <param name="Taken">How many values this machine holds now from that server.</param>
/// <param name="Refused">The values whose copy did not open, with why: a copy no controller device of the person gave.</param>
/// <param name="Revoked">Whether this machine was revoked: every value of that server was taken away.</param>
public sealed record FleetSecretSyncResult(int Taken, IReadOnlyList<string> Refused, bool Revoked);

/// <summary>
/// Takes the values one server holds for this machine (<c>doc/fleet/secrets.md</c>, how a run gets them): it reads the
/// copies sealed for it, opens each one whose key a controller device of its person gave by the device list the anchor
/// of the link verified (<see cref="FleetSecretSealing.TryOpen"/>), maps its scope to this machine (a space by the key of
/// that space, a project by the blind index of its repository, an environment by its signed definition) and keeps the
/// values in the protected store of the machine (<see cref="LocalSecretStore.ReplaceFleetCopies"/>). A rotation is taken
/// at the next refresh, which runs before each run; a value the server no longer gives is dropped. A machine the list
/// removed, or the server refuses as revoked, takes every value of that server away.
/// </summary>
/// <remarks>One caller of <see cref="SyncAsync"/> at a time.</remarks>
public sealed class FleetSecretSync
{
    private readonly string _source;
    private readonly FleetOrganizationDevices _devices;
    private readonly FleetMetadataKeyring? _keys;
    private readonly LocalSecretStore _store;
    private readonly Func<CancellationToken, Task<IReadOnlyList<FleetSecretProject>>>? _projects;
    private ImmutableDictionary<string, FleetEnvironmentDefinition> _environments = ImmutableDictionary<string, FleetEnvironmentDefinition>.Empty;

    /// <summary>Initializes a new instance of the <see cref="FleetSecretSync"/> class.</summary>
    /// <param name="source">The id of the server link, which the values are kept under.</param>
    /// <param name="devices">The anchored device list of the server, whose key and HTTP handler the calls use too.</param>
    /// <param name="keys">The metadata keys of the spaces of the person, which map spaces and projects; null maps none.</param>
    /// <param name="store">The store of the machine.</param>
    /// <param name="projects">Lists the projects of this machine with what recognizes them; null maps none.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> or <paramref name="store"/> is null.</exception>
    public FleetSecretSync(string source, FleetOrganizationDevices devices, FleetMetadataKeyring? keys, LocalSecretStore store,
        Func<CancellationToken, Task<IReadOnlyList<FleetSecretProject>>>? projects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(store);
        _source = source;
        _devices = devices;
        _keys = keys;
        _store = store;
        _projects = projects;
    }

    /// <summary>Gets the id of the server link.</summary>
    public string Source => _source;

    /// <summary>Gets the anchored device list of the server.</summary>
    public FleetOrganizationDevices Devices => _devices;

    /// <summary>Gets the device id of this machine.</summary>
    public string MachineId => _devices.Identity.DeviceId;

    /// <summary>Asks the server, for a run of this machine, the approval an environment requires.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="request">The run, the nonce this machine chose and the version of the definition it verified.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approval asked.</returns>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetApprovalDto> RequestApprovalAsync(string environmentId, RequestFleetApprovalRequest request, CancellationToken cancellationToken)
    {
        using var client = _devices.CreateClient();
        return await client.RequestApprovalAsync(environmentId, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads an approval this machine asked.</summary>
    /// <param name="approvalId">The approval.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approval, with the signed approval once given.</returns>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetApprovalDto> GetApprovalAsync(string approvalId, CancellationToken cancellationToken)
    {
        using var client = _devices.CreateClient();
        return await client.GetApprovalAsync(approvalId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the environments of the person as the last refresh verified them, by name; those taken away are not among them.</summary>
    public IReadOnlyDictionary<string, FleetEnvironmentDefinition> Environments => Volatile.Read(ref _environments);

    /// <summary>Reads the values of the server for this machine again, and keeps those that open.</summary>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>What it took.</returns>
    /// <exception cref="FleetDeviceListException">The device list does not verify: nothing is taken, what was held stays.</exception>
    /// <exception cref="FleetServerException">The server refused a call (but a revocation, which takes every value away).</exception>
    /// <exception cref="HttpRequestException">The server does not answer: what was held stays.</exception>
    public async Task<FleetSecretSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        FleetDeviceListState list;
        FleetSecretListDto held;
        FleetEnvironmentListDto environments;
        try
        {
            list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (list.Removed.Contains(_devices.Identity.DeviceId)) return Revoke();
            using var client = _devices.CreateClient();
            held = await client.ListHeldSecretsAsync(cancellationToken).ConfigureAwait(false);
            environments = await client.ListEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (FleetServerException exception) when (exception.Code == FleetErrorCodes.Revoked)
        {
            return Revoke();
        }

        // The environments whose definition a controller device of the person signed, the newest of each name.
        var verified = ImmutableDictionary.CreateBuilder<string, FleetEnvironmentDefinition>(StringComparer.Ordinal);
        var byId = new Dictionary<string, FleetEnvironmentDefinition>(StringComparer.Ordinal);
        foreach (var environment in environments.Environments)
        {
            if (!FleetEnvironmentDocuments.TryVerify(environment.Signed, list, _devices.Identity.DeviceId, out var definition, out _) || definition.EnvironmentId != environment.Id)
                continue;
            byId[definition.EnvironmentId] = definition;
            if (!definition.Removed && (!verified.TryGetValue(definition.Name, out var known) || known.Number < definition.Number)) verified[definition.Name] = definition;
        }

        Volatile.Write(ref _environments, verified.ToImmutable());
        var spaces = SpacesById();
        var projects = await ProjectsByKeyAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<FleetSecretValue>(held.Secrets.Count);
        var refused = new List<string>();
        foreach (var secret in held.Secrets)
        {
            if (!FleetSecretSealing.TryOpen(secret.Copy, FleetSecretBinding.Of(secret), _devices.Identity, list, out var value, out var reason))
            {
                refused.Add($"{secret.Name}: {reason}");
                continue;
            }

            // A value of a scope this machine does not know (a space it does not link, a project it does not have) is not kept.
            foreach (var scopeId in LocalScopes(secret.Scope, spaces, projects, byId))
                values.Add(new FleetSecretValue(_source, secret.Id, secret.Kind, secret.Name, LocalKind(secret.Scope.Kind), scopeId, secret.Version, secret.HostOnly, value));
        }

        _store.ReplaceFleetCopies(_source, values);
        return new FleetSecretSyncResult(values.Count, refused, false);
    }

    /// <summary>Publishes the uses this machine recorded of the values of the server: which run, which value, which version, never a value.</summary>
    /// <param name="uses">The uses.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once the server took them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uses"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused them.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task PublishUsesAsync(IReadOnlyList<FleetSecretUseDto> uses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uses);
        if (uses.Count == 0) return;
        using var client = _devices.CreateClient();
        await client.PublishSecretUsesAsync(uses, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes away every value of the server: the link is gone, or the machine was revoked.</summary>
    public void Forget()
    {
        Volatile.Write(ref _environments, ImmutableDictionary<string, FleetEnvironmentDefinition>.Empty);
        _store.RemoveFleetCopies(_source);
    }

    private FleetSecretSyncResult Revoke()
    {
        Forget();
        return new FleetSecretSyncResult(0, [], true);
    }

    private static string LocalKind(string kind) => kind switch
    {
        FleetSecretScopeKinds.Environment => FleetSecretScopes.Environment,
        FleetSecretScopeKinds.Project => FleetSecretScopes.Project,
        FleetSecretScopeKinds.Space => FleetSecretScopes.Space,
        _ => FleetSecretScopes.Person,
    };

    private static IEnumerable<string?> LocalScopes(FleetSecretScopeDto scope, IReadOnlyDictionary<string, string> spaces, ILookup<string, string> projects,
        IReadOnlyDictionary<string, FleetEnvironmentDefinition> environments)
    {
        switch (scope.Kind)
        {
            case FleetSecretScopeKinds.Person:
                yield return null;
                break;
            case FleetSecretScopeKinds.Space when scope.Id is { } id && spaces.TryGetValue(id, out var local):
                yield return local;
                break;
            case FleetSecretScopeKinds.Project when scope.Id is { } id:
                foreach (var project in projects[id]) yield return project;
                break;
            case FleetSecretScopeKinds.Environment when scope.Id is { } id && environments.TryGetValue(id, out var environment) && !environment.Removed:
                yield return environment.Name;
                break;
        }
    }

    // The spaces of this machine by the opaque id the server knows them by: the grant of the key of each space names it.
    private Dictionary<string, string> SpacesById()
    {
        var spaces = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_keys is null) return spaces;
        foreach (var (local, key) in _keys.Current)
        {
            if (key.Grant?.SpaceId is { } id) spaces.TryAdd(id, local);
        }

        return spaces;
    }

    // The projects of this machine by their blind index with every key of the person this machine holds: a project
    // scoped by another device with the key of its space is found here whatever epoch that key is of.
    private async Task<ILookup<string, string>> ProjectsByKeyAsync(CancellationToken cancellationToken)
    {
        if (_keys is not { Held: { Count: > 0 } keys } || _projects is null) return Array.Empty<(string, string)>().ToLookup(static pair => pair.Item1, static pair => pair.Item2);
        var pairs = new List<(string Key, string Project)>();
        foreach (var project in await _projects(cancellationToken).ConfigureAwait(false))
        {
            foreach (var key in keys) pairs.Add((FleetProjectKeys.Of(key, project.Identity), project.ProjectId));
        }

        return pairs.Distinct().ToLookup(static pair => pair.Key, static pair => pair.Project, StringComparer.Ordinal);
    }
}
