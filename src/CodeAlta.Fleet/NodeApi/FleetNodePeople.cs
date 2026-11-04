using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// The people of the organization a machine names, and the requests of the people it does not (<c>doc/fleet/access-control.md</c>,
/// what the node decides: "its owner, people it names, or the members of a team"; "the first start by a new person asks the
/// node's owner"). Kept in the fleet folder of the node: its owner sets it on the machine or from a device that manages it, and
/// the server can neither add a person to it nor answer a request.
/// </summary>
public sealed class FleetNodePeople
{
    /// <summary>The most people a machine names, and the most requests it keeps.</summary>
    public const int MaxEntries = 200;

    /// <summary>The permissions a named person may hold: their own sessions only.</summary>
    public const DevicePermissions Allowed = DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Start;

    private const string FileName = "people.json";

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly TimeProvider _time;
    private PeopleState _state;

    /// <summary>Opens the people of a node.</summary>
    /// <param name="fleetRoot">The fleet folder of the node.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentException"><paramref name="fleetRoot"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is null.</exception>
    public FleetNodePeople(string fleetRoot, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrEmpty(fleetRoot);
        ArgumentNullException.ThrowIfNull(time);
        _path = Path.Combine(fleetRoot, FileName);
        _time = time;
        _state = Load(_path);
    }

    /// <summary>Raised after a person asked to be let in for the first time.</summary>
    public event Action? Asked;

    /// <summary>Gets what a person may do on the machine, or null when the machine does not name them.</summary>
    /// <param name="personId">The person.</param>
    /// <returns>Their permissions, or null.</returns>
    public DevicePermissions? Named(string personId)
    {
        lock (_gate)
        {
            return _state.People.FirstOrDefault(person => person.PersonId == personId) is { } named
                && DevicePermissionNames.TryParse(named.Permissions, out var permissions, out _) ? permissions & Allowed : null;
        }
    }

    /// <summary>Reads the people and the requests.</summary>
    /// <returns>What the node holds.</returns>
    public FleetNodePeopleInfo Read()
    {
        lock (_gate) return new FleetNodePeopleInfo([.. _state.People], [.. _state.Asks], [.. _state.Refused]);
    }

    /// <summary>Records that a person the machine does not name tried to use it, once, unless its owner refused them.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="deviceId">The device they asked from.</param>
    /// <returns>Whether the request is new.</returns>
    public bool Ask(string personId, string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(personId);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        lock (_gate)
        {
            if (_state.Refused.Contains(personId, StringComparer.Ordinal) || _state.Asks.Any(ask => ask.PersonId == personId)
                || _state.People.Any(person => person.PersonId == personId)) return false;
            _state = _state with { Asks = [.. _state.Asks.TakeLast(MaxEntries - 1), new FleetNodeAsk(personId, deviceId, _time.GetUtcNow())] };
            Save();
        }

        Asked?.Invoke();
        return true;
    }

    /// <summary>Lets a person in with permissions, takes them out, or refuses their request.</summary>
    /// <param name="change">The change.</param>
    /// <returns>What the node holds now.</returns>
    /// <exception cref="ArgumentException">The person or a permission does not hold.</exception>
    public FleetNodePeopleInfo Set(FleetNodePeopleChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (string.IsNullOrWhiteSpace(change.PersonId) || change.PersonId.Length > 128 || change.PersonId.Any(char.IsControl))
            throw new ArgumentException("A person is named by their id.", nameof(change));
        var permissions = DevicePermissions.None;
        if (change.Permissions is { Count: > 0 } names && (!DevicePermissionNames.TryParse(names, out permissions, out var unknown) || (permissions & ~Allowed) != 0))
            throw new ArgumentException($"A person may view, drive and start their own sessions: '{unknown ?? string.Join(",", names)}' is not one of those.", nameof(change));
        lock (_gate)
        {
            var people = _state.People.Where(person => person.PersonId != change.PersonId).ToList();
            var refused = _state.Refused.Where(id => id != change.PersonId).ToList();
            if (permissions != DevicePermissions.None)
                people.Add(new FleetNodePerson(change.PersonId, DevicePermissionNames.Format(permissions), _time.GetUtcNow()));
            else if (change.Refuse)
                refused.Add(change.PersonId);
            if (people.Count > MaxEntries || refused.Count > MaxEntries) throw new ArgumentException($"A machine names at most {MaxEntries} people.", nameof(change));
            _state = new PeopleState(people, [.. _state.Asks.Where(ask => ask.PersonId != change.PersonId)], refused);
            Save();
            return new FleetNodePeopleInfo([.. _state.People], [.. _state.Asks], [.. _state.Refused]);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(_state, FleetNodePeopleJson.Default.PeopleState));
    }

    private static PeopleState Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), FleetNodePeopleJson.Default.PeopleState) ?? PeopleState.Empty;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return PeopleState.Empty;
        }
    }

    internal sealed record PeopleState(IReadOnlyList<FleetNodePerson> People, IReadOnlyList<FleetNodeAsk> Asks, IReadOnlyList<string> Refused)
    {
        public static PeopleState Empty { get; } = new([], [], []);
    }
}

/// <summary>
/// The trust of an organization with the people a machine names: a person's controller device that the organization alone lets
/// do nothing here gets what the machine's owner gave that person, on their own sessions.
/// </summary>
/// <param name="organization">The trust of the organization.</param>
/// <param name="people">The people the machine names.</param>
public sealed class FleetNamedPeopleTrust(IDeviceTrustPolicy organization, FleetNodePeople people) : IDeviceTrustPolicy
{
    /// <inheritdoc />
    public TrustedDevice? FindTrusted(DeviceFingerprint fingerprint)
    {
        var device = organization.FindTrusted(fingerprint);
        return device is { PersonId: { } person, Permissions: DevicePermissions.None, OwnSessionsOnly: true } && people.Named(person) is { } named
            ? device with { Permissions = named }
            : device;
    }

    /// <inheritdoc />
    public bool IsRevoked(DeviceFingerprint fingerprint) => organization.IsRevoked(fingerprint);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(FleetNodePeople.PeopleState))]
internal sealed partial class FleetNodePeopleJson : JsonSerializerContext;
