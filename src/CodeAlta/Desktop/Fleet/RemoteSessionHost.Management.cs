using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Desktop.Fleet;

internal sealed partial class RemoteSessionHost
{
    /// <summary>
    /// Calls the machine to manage it (<c>control.*</c>, which it answers only for a device that manages it) on its
    /// current channel, then, when the call changed something there, reads what the machine is again (what this computer
    /// may do there, its projects) and asks it for its list: the hosts and the automations placed there see it at once.
    /// </summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="call">The call.</param>
    /// <param name="changes">Whether the call changes the machine (its devices, projects or version).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What the machine answered.</returns>
    /// <exception cref="ObjectDisposedException">The machine is not online.</exception>
    internal async Task<T> ManageAsync<T>(Func<FleetNodeClient, CancellationToken, Task<T>> call, bool changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var client = RequireClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var result = await call(client, linked.Token).ConfigureAwait(false);
        if (!changes) return result;
        try
        {
            // The loop, the one writer of the listing, takes it at its next read of the list, while that connection lasts.
            Volatile.Write(ref _readInfo, new FleetNodeInfoRead(client, await client.GetInfoAsync(linked.Token).ConfigureAwait(false)));
        }
        catch (Exception exception) when (exception is FleetException or IOException or ObjectDisposedException or FleetConnectionClosedException)
        {
            // The change was made: the next connection reads the machine again.
        }

        RequestRefresh();
        return result;
    }
}
