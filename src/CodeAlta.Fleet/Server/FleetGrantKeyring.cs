using System.Collections.Immutable;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The metadata of the sessions that grants of sealed content give this device (<c>doc/fleet/access-control.md</c>, access and keys):
/// read from the server, each copy verified against the device list this device anchored and opened with its key; a copy that does
/// not hold is left out. Read again when access changes; a grant taken back is gone at the next read.
/// </summary>
public sealed class FleetGrantKeyring
{
    private readonly FleetOrganizationDevices _devices;
    private ImmutableDictionary<string, FleetSessionMetadata> _sessions = ImmutableDictionary.Create<string, FleetSessionMetadata>(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="FleetGrantKeyring"/> class.</summary>
    /// <param name="devices">The anchored device list of the server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public FleetGrantKeyring(FleetOrganizationDevices devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
    }

    /// <summary>
    /// Gets the signed access document of the server as this device verified it, read at each refresh: the copies of a session of a
    /// machine given to another person are signed by a device of its new owner, never of the person the device list names. Null
    /// reads the device list alone.
    /// </summary>
    public Func<FleetAccessState?>? Access { get; init; }

    /// <summary>Gets the metadata a grant gives this device for a session, or null.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>Its metadata, or null.</returns>
    public FleetSessionMetadata? Find(string sessionId) => Volatile.Read(ref _sessions).GetValueOrDefault(sessionId);

    /// <summary>Reads the keys of the grants given to this device again, and opens them.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many sessions they open.</returns>
    /// <exception cref="FleetServerException">The server refused the read.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    public async Task<int> RefreshAsync(CancellationToken cancellationToken)
    {
        using var client = _devices.CreateClient();
        var keys = await client.ListGrantKeysAsync(cancellationToken).ConfigureAwait(false);
        if (keys.Items.Count == 0)
        {
            Volatile.Write(ref _sessions, _sessions.Clear());
            return 0;
        }

        var list = _devices.Current ?? await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (keys.Items.Any(key => !list.Members.ContainsKey(key.MachineId))) list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var opened = ImmutableDictionary.CreateBuilder<string, FleetSessionMetadata>(StringComparer.Ordinal);
        var access = Access?.Invoke();
        foreach (var key in keys.Items)
        {
            try
            {
                // On a team machine, the copy is given by the person who started the session there.
                opened[key.SessionId] = FleetGrantKeys.Open(key.Sealed, key.Wrapped, list.OrganizationId ?? string.Empty, key.GrantId, key.SessionId, key.MachineId, _devices.Identity, list,
                    access, key.PersonId);
            }
            catch (InvalidDataException)
            {
                // A copy that does not hold (another signer, another session) opens nothing.
            }
        }

        Volatile.Write(ref _sessions, opened.ToImmutable());
        return opened.Count;
    }
}
