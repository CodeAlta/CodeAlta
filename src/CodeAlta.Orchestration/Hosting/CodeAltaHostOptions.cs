using CodeAlta.Agent;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Hosting;

/// <summary>
/// Configures shared CodeAlta runtime composition for interactive and headless hosts.
/// </summary>
public sealed class CodeAltaHostOptions
{
    /// <summary>
    /// Gets the global CodeAlta data root. When unset, the host uses the user's <c>.alta</c> directory.
    /// </summary>
    public string? GlobalRoot { get; init; }

    /// <summary>
    /// Gets the project path used to seed the project catalog. When unset, the current directory is used.
    /// </summary>
    public string? CurrentProjectPath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the host is running without a frontend UI.
    /// </summary>
    public bool IsHeadless { get; init; }

    /// <summary>
    /// Gets a value indicating whether interactive UI services are available to plugins and adapters.
    /// </summary>
    public bool HasInteractiveUi { get; init; }

    /// <summary>
    /// Gets a value indicating whether plugin discovery should use safe mode.
    /// </summary>
    public bool PluginSafeMode { get; init; }

    /// <summary>
    /// Gets the raw command-line arguments forwarded to plugin bootstrap.
    /// </summary>
    public IReadOnlyList<string> RawArguments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets a value indicating whether interactive plugin build output should wait for Enter after live builds complete.
    /// </summary>
    public bool WaitForEnterAfterPluginLiveOutput { get; init; }

    /// <summary>Gets borrowed plugin startup presentation; defaults to silent feedback for every host.</summary>
    /// <remarks>Validated even when plugins are prestarted or disabled; never disposed by the host.</remarks>
    public IPluginStartupFeedback PluginStartupFeedback { get; init; } = new SilentPluginStartupFeedback();

    /// <summary>
    /// Gets built-in plugins to activate as part of shared host composition.
    /// </summary>
    public IReadOnlyList<BuiltInPluginDefinition> PluginBuiltIns { get; init; } = Array.Empty<BuiltInPluginDefinition>();

    /// <summary>
    /// Gets host services exposed to plugins started by this host.
    /// </summary>
    public IPluginServices? PluginServices { get; init; }

    /// <summary>
    /// Gets an optional callback that registers host-specific model providers before the agent hub is created.
    /// </summary>
    public Action<ModelProviderRegistry>? ConfigureModelProviders { get; init; }

    /// <summary>
    /// Gets a prestarted plugin runtime supplied by the caller. When set, the host will not dispose it.
    /// </summary>
    public PluginRuntimeManager? PrestartedPluginRuntime { get; init; }

    /// <summary>
    /// Gets a value indicating whether the host should start a plugin runtime when one is not supplied.
    /// </summary>
    public bool StartPlugins { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the host should own process-wide logging initialization.
    /// </summary>
    public bool OwnsLogging { get; init; }
}
