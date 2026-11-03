using CodeAlta.Fleet.Client;

namespace CodeAlta.Desktop.Fleet;

internal sealed partial class RemoteSessionHost
{
    /// <summary>
    /// Calls the machine to manage it (<c>control.*</c>, which it answers only for a device that manages it) on its
    /// current channel, then asks it for its list again when the call changed something there.
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
        if (changes) RequestRefresh();
        return result;
    }
}
