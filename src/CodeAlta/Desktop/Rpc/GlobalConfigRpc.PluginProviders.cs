using CodeAlta.Hosting;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

// The providers whose type a plugin runs: they are registered with the others, and again when the plugins change.
internal sealed partial class GlobalConfigService
{
    /// <summary>Gets the provider types of the running plugins, or <see langword="null"/> for a host without plugins.</summary>
    internal PluginModelProviderTypes? PluginProviderTypes { get; init; }

    /// <summary>
    /// Registers the providers again when the provider types the running plugins contribute changed: a plugin that
    /// started brings its providers, one that stopped takes them away, one built again replaces their runtimes.
    /// A change of another plugin changes nothing.
    /// </summary>
    internal void ApplyPluginProviderChanges()
    {
        if (_store is null || _registry is null || PluginProviderTypes is not { } types)
        {
            return;
        }

        lock (_gate)
        {
            if (string.Equals(types.Signature, types.RegisteredSignature, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                ApplyProviders(_store, _registry, _stateRoot!, types);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                LogManager.GetLogger("CodeAlta.Desktop").Warn($"The providers of the plugins could not be registered again: {exception.Message}");
            }
        }
    }
}
