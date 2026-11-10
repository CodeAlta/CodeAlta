using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugins.PrivateNetworks;

/// <summary>This computer on a private network, with the network that says so.</summary>
/// <param name="Network">The provider of the network.</param>
/// <param name="Presence">Where the other devices of the network reach this computer.</param>
public sealed record PrivateNetworkFound(IPrivateNetworkProvider Network, PrivateNetworkPresence Presence);

/// <summary>
/// The private networks of this computer: those of CodeAlta (Tailscale, Dev Tunnels), then those the active plugins bring
/// (<see cref="IPrivateNetworkSource"/>). One id names one network: the first one of the list keeps it.
/// </summary>
public sealed class PrivateNetworkRegistry : IPluginPrivateNetworkService
{
    private readonly IReadOnlyList<IPrivateNetworkProvider> _builtIns;
    private readonly Func<IEnumerable<object?>> _plugins;
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.PrivateNetworks");

    /// <summary>Creates the registry with the networks of CodeAlta and the instances of the active plugins.</summary>
    /// <param name="plugins">Gives the instances of the active plugins, read at each use.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plugins"/> is null.</exception>
    public PrivateNetworkRegistry(Func<IEnumerable<object?>> plugins)
        : this([new TailscaleNetwork(), new DevTunnelNetwork()], plugins)
    {
    }

    /// <summary>Finds the first publishing provider that recognizes a server address, including those of active plugins.</summary>
    /// <param name="address">The server address.</param>
    /// <returns>The provider, or null for an address the person manages themselves.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    public IPrivateNetworkProvider? FindForAddress(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        foreach (var provider in Providers.Where(static provider => provider.Capabilities.HasFlag(PrivateNetworkCapabilities.Publish)))
        {
            try
            {
                if (provider.OwnsAddress(address)) return provider;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (LogManager.IsInitialized) _logger.Warn(exception, $"The private network {provider.Id} could not recognize a server address.");
            }
        }

        return null;
    }

    /// <summary>Creates the registry with networks of its own, for tests.</summary>
    internal PrivateNetworkRegistry(IReadOnlyList<IPrivateNetworkProvider> builtIns, Func<IEnumerable<object?>> plugins)
    {
        ArgumentNullException.ThrowIfNull(builtIns);
        ArgumentNullException.ThrowIfNull(plugins);
        _builtIns = builtIns;
        _plugins = plugins;
    }

    /// <inheritdoc />
    public IReadOnlyList<IPrivateNetworkProvider> Providers
    {
        get
        {
            var providers = new List<IPrivateNetworkProvider>(_builtIns);
            var ids = new HashSet<string>(_builtIns.Select(static provider => provider.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var source in _plugins().OfType<IPrivateNetworkSource>())
            {
                IReadOnlyList<IPrivateNetworkProvider> brought;
                try
                {
                    brought = source.PrivateNetworks ?? [];
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A plugin that fails gives no network; the others stay.
                    if (LogManager.IsInitialized) _logger.Warn(exception, $"The private networks of {source.GetType().Name} could not be read.");
                    continue;
                }

                foreach (var provider in brought)
                {
                    if (provider is null || !IsId(provider.Id)) continue;
                    if (ids.Add(provider.Id)) providers.Add(provider);
                    else if (LogManager.IsInitialized) _logger.Warn($"The private network {provider.Id} of {source.GetType().Name} is left out: another one has its id.");
                }
            }

            return providers;
        }
    }

    /// <summary>
    /// Finds this computer on a private network that says where it is (<see cref="PrivateNetworkCapabilities.Presence"/>): the first one
    /// of the list that finds it. A network that fails is passed over.
    /// </summary>
    /// <param name="cancellationToken">Stops the search.</param>
    /// <returns>This computer on a network, or null when it is on none of them now.</returns>
    public async Task<PrivateNetworkFound?> FindThisComputerAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in Providers.Where(static provider => provider.Capabilities.HasFlag(PrivateNetworkCapabilities.Presence)))
        {
            try
            {
                if (await provider.FindThisComputerAsync(cancellationToken).ConfigureAwait(false) is { Addresses.Count: > 0 } presence)
                    return new PrivateNetworkFound(provider, presence);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (LogManager.IsInitialized) _logger.Warn(exception, $"The private network {provider.Id} could not say where this computer is.");
            }
        }

        return null;
    }

    /// <summary>Gets whether an id is one a network may have: 1 to 64 letters, digits and <c>-</c>.</summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether it is one.</returns>
    public static bool IsId(string? id)
        => id is { Length: > 0 and <= 64 } && id.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
