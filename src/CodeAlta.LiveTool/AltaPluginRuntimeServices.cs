using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;

namespace CodeAlta.LiveTool;

/// <summary>
/// Registers plugin runtime-backed services used by in-process <c>alta</c> commands.
/// </summary>
public static class AltaPluginRuntimeServices
{
    /// <summary>
    /// Adds orchestration hook services backed by the active plugin runtime.
    /// </summary>
    /// <param name="services">The service collection to update.</param>
    /// <param name="runtime">The active plugin runtime.</param>
    /// <returns><paramref name="services" /> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null" />.</exception>
    public static AltaServiceCollection AddPluginRuntimeHooks(this AltaServiceCollection services, PluginRuntimeManager runtime)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runtime);

        return services.Add(new PluginOrchestrationBridge(runtime.Adapter, () => runtime.ActivePlugins));
    }

    /// <summary>
    /// Adds the orchestration hook services of a host: its bridge, which gives the plugins what the host knows of a run (the
    /// tools a session loaded, whether its provider defers tools).
    /// </summary>
    /// <param name="services">The service collection to update.</param>
    /// <param name="bridge">The bridge of the host.</param>
    /// <returns><paramref name="services" /> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null" />.</exception>
    public static AltaServiceCollection AddPluginRuntimeHooks(this AltaServiceCollection services, PluginOrchestrationBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(bridge);

        return services.Add(bridge);
    }
}
