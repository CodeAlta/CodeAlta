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

    /// <summary>
    /// Applies a newer access document of an organization: the sessions people it suspends started here are stopped, and their
    /// open channels closed. A node calls it once its server link verified the document.
    /// </summary>
    /// <param name="access">The document, as the link verified it.</param>
    /// <param name="cancellationToken">Cancels before the next session is stopped.</param>
    /// <returns>The sessions stopped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="access"/> is null.</exception>
    public async Task<IReadOnlyList<string>> ApplyAccessAsync(FleetAccessState access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var stopped = new List<string>();
        foreach (var (sessionId, personId) in Principals.List())
        {
            if (!access.IsSuspended(personId)) continue;
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

    // A device that sees its own sessions only reaches a session its person started here; others are not shown to exist.
    private void RequireOwnSession(TrustedDevice? device, object parameters)
    {
        if (device is not { OwnSessionsOnly: true }) return;
        if (SessionIdOf(parameters) is not { } sessionId) return;
        if (!Principals.Owns(sessionId, device.PersonId))
            throw new FleetException(FleetNodeErrorCodes.SessionNotFound, null, "No session of yours has this id on this machine.");
    }

    // Records that a person started a session here through a team membership: they alone see and drive it.
    private void RecordPrincipal(TrustedDevice? device, string sessionId)
    {
        if (device is { OwnSessionsOnly: true, PersonId: { } person }) Principals.Record(sessionId, person);
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

    public bool Owns(string sessionId, string? personId)
    {
        if (personId is null) return false;
        lock (_gate) return _items.Any(item => item.SessionId == sessionId && item.PersonId == personId);
    }

    public string? PersonOf(string sessionId)
    {
        lock (_gate) return _items.FirstOrDefault(item => item.SessionId == sessionId)?.PersonId;
    }

    public IReadOnlyList<(string SessionId, string PersonId)> List()
    {
        lock (_gate) return [.. _items.Select(static item => (item.SessionId, item.PersonId))];
    }

    public void Record(string sessionId, string personId)
    {
        lock (_gate)
        {
            if (_items.Any(item => item.SessionId == sessionId)) return;
            _items.Add(new FleetSessionPrincipal(sessionId, personId));
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

/// <summary>A session and the person who started it.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="PersonId">The person.</param>
internal sealed record FleetSessionPrincipal(string SessionId, string PersonId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<FleetSessionPrincipal>))]
internal sealed partial class FleetSessionPrincipalJson : JsonSerializerContext;
