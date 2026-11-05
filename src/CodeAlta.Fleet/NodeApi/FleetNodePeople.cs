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

    /// <summary>
    /// Gets or sets what gives the organization of the machine when it is linked to exactly one: a person named before their
    /// organization was kept is that organization's, and is kept as such from the first time the machine knows it, so that an
    /// organization the machine joins later never takes them for one of its people with the same id; on a machine of several, they
    /// are nobody's until named again. Null: none.
    /// </summary>
    public Func<string?>? SoleOrganization
    {
        get => _soleOrganization;
        set
        {
            _soleOrganization = value;
            lock (_gate) BindLegacy();
        }
    }

    private Func<string?>? _soleOrganization;

    /// <summary>Gets what a person of an organization may do on the machine, or null when the machine does not name them.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="organizationId">Their organization: a person's id is unique within one only; null for a device of no organization.</param>
    /// <returns>Their permissions, or null.</returns>
    public DevicePermissions? Named(string personId, string? organizationId)
    {
        lock (_gate)
        {
            BindLegacy();
            return _state.People.FirstOrDefault(person => person.PersonId == personId && Same(person.OrganizationId, organizationId)) is { } named
                && DevicePermissionNames.TryParse(named.Permissions, out var permissions, out _) ? permissions & Allowed : null;
        }
    }

    // Whether what the machine keeps for a person of an organization is for this one: the same organization. One kept before
    // organizations were is bound to the sole organization of the machine first (BindLegacy), and else is nobody's.
    private static bool Same(string? kept, string? organizationId) => string.Equals(kept, organizationId, StringComparison.Ordinal);

    // Binds the people and the requests kept before organizations were to the organization of the machine, once it is linked to
    // exactly one, and keeps them so: from then on they are of that organization only, whatever the machine joins later. A
    // binding is taken only once it is written: one held in memory alone would be made again with another organization after a
    // restart, so until the file takes it they stay nobody's, and get nothing.
    private void BindLegacy()
    {
        if (!_state.People.Any(static person => person.OrganizationId is null) && !_state.Asks.Any(static ask => ask.OrganizationId is null)) return;
        if (_soleOrganization?.Invoke() is not { } sole) return;
        var bound = _state with
        {
            People = [.. _state.People.Select(person => person.OrganizationId is null ? person with { OrganizationId = sole } : person)],
            Asks = [.. _state.Asks.Select(ask => ask.OrganizationId is null ? ask with { OrganizationId = sole } : ask)],
        };
        try
        {
            Save(bound);
            _state = bound;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not kept: tried again at the next read of the people.
        }
    }

    private static string RefusalKey(string personId, string? organizationId) => organizationId is null ? personId : organizationId + "/" + personId;

    /// <summary>Reads the people and the requests.</summary>
    /// <returns>What the node holds.</returns>
    public FleetNodePeopleInfo Read()
    {
        lock (_gate)
        {
            BindLegacy();
            return new FleetNodePeopleInfo([.. _state.People], [.. _state.Asks], [.. _state.Refused]);
        }
    }

    /// <summary>Records that a person the machine does not name tried to use it, once, unless its owner refused them.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="deviceId">The device they asked from.</param>
    /// <param name="organizationId">Their organization; null for a device of no organization.</param>
    /// <returns>Whether the request is new.</returns>
    public bool Ask(string personId, string deviceId, string? organizationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(personId);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        lock (_gate)
        {
            BindLegacy();
            if (_state.Refused.Contains(RefusalKey(personId, organizationId), StringComparer.Ordinal)
                || _state.Asks.Any(ask => ask.PersonId == personId && Same(ask.OrganizationId, organizationId))
                || _state.People.Any(person => person.PersonId == personId && Same(person.OrganizationId, organizationId))) return false;
            _state = _state with { Asks = [.. _state.Asks.TakeLast(MaxEntries - 1), new FleetNodeAsk(personId, deviceId, _time.GetUtcNow(), organizationId)] };
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
            BindLegacy();
            // The organization of the person: the one named, else the one of their request, else the only one of the machine.
            var asked = _state.Asks.Where(ask => ask.PersonId == change.PersonId).Select(static ask => ask.OrganizationId).Distinct(StringComparer.Ordinal).ToArray();
            if (change.OrganizationId is null && asked.Length > 1)
                throw new ArgumentException("People of several organizations have this id: name the organization of the person.", nameof(change));
            var organization = change.OrganizationId ?? (asked.Length == 1 ? asked[0] : SoleOrganization?.Invoke());
            var people = _state.People.Where(person => !(person.PersonId == change.PersonId && string.Equals(person.OrganizationId, organization, StringComparison.Ordinal))).ToList();
            var key = RefusalKey(change.PersonId, organization);
            var refused = _state.Refused.Where(id => id != key).ToList();
            if (permissions != DevicePermissions.None)
                people.Add(new FleetNodePerson(change.PersonId, DevicePermissionNames.Format(permissions), _time.GetUtcNow(), organization));
            else if (change.Refuse)
                refused.Add(key);
            if (people.Count > MaxEntries || refused.Count > MaxEntries) throw new ArgumentException($"A machine names at most {MaxEntries} people.", nameof(change));
            _state = new PeopleState(people, [.. _state.Asks.Where(ask => !(ask.PersonId == change.PersonId && string.Equals(ask.OrganizationId, organization, StringComparison.Ordinal)))],
                refused);
            Save();
            return new FleetNodePeopleInfo([.. _state.People], [.. _state.Asks], [.. _state.Refused]);
        }
    }

    /// <summary>Takes out every person the machine names, their requests and the refusals: what its former owner decided ends with a change of owner.</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public void Clear()
    {
        lock (_gate)
        {
            if (_state.People.Count == 0 && _state.Asks.Count == 0 && _state.Refused.Count == 0) return;
            _state = PeopleState.Empty;
            Save();
        }
    }

    private void Save() => Save(_state);

    private void Save(PeopleState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(state, FleetNodePeopleJson.Default.PeopleState));
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
        return device is { PersonId: { } person, Permissions: DevicePermissions.None, OwnSessionsOnly: true } && people.Named(person, device.OrganizationId) is { } named
            ? device with { Permissions = named }
            : device;
    }

    /// <inheritdoc />
    public bool IsRevoked(DeviceFingerprint fingerprint) => organization.IsRevoked(fingerprint);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(FleetNodePeople.PeopleState))]
internal sealed partial class FleetNodePeopleJson : JsonSerializerContext;
