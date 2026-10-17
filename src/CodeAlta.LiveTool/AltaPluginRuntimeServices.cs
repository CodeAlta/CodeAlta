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
    /// Adds orchestration hook services backed by the active plugin runtime, whose runs give the plugins the tools each
    /// session loaded.
    /// </summary>
    /// <param name="services">The service collection to update.</param>
    /// <param name="runtime">The active plugin runtime.</param>
    /// <param name="loadedToolsOf">What reads the references of the tools a session loaded (<see cref="PluginOrchestrationBridge.LoadedToolsOf"/>).</param>
    /// <returns><paramref name="services" /> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null" />.</exception>
    public static AltaServiceCollection AddPluginRuntimeHooks(this AltaServiceCollection services, PluginRuntimeManager runtime,
        Func<string, CancellationToken, ValueTask<IReadOnlyList<string>>> loadedToolsOf)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(loadedToolsOf);

        return services.Add(new PluginOrchestrationBridge(runtime.Adapter, () => runtime.ActivePlugins) { LoadedToolsOf = loadedToolsOf });
    }
}
