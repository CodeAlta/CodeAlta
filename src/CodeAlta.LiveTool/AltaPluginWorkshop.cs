using CodeAlta.Plugins;

namespace CodeAlta.LiveTool;

/// <summary>
/// What a host lets the <c>alta plugin</c> commands do with its source plugins: say what it did with each
/// package, create one, build one, load one again while the host runs, and show one in its code editor.
/// </summary>
/// <remarks>
/// Building and loading a plugin runs code written by the caller. A host registers this service only where its
/// sessions run their commands without a review by the user, and where it follows a plugin that changes while
/// it runs. A host without it has the commands that list its active plugins and look the API up.
/// </remarks>
public sealed class AltaPluginWorkshop
{
    /// <summary>Creates the service over the plugin runtime of a host.</summary>
    /// <param name="runtime">The plugin runtime of the host.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    public AltaPluginWorkshop(PluginRuntimeManager runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Runtime = runtime;
    }

    /// <summary>Gets the plugin runtime of the host.</summary>
    public PluginRuntimeManager Runtime { get; }

    /// <summary>
    /// Gets what shows a file of a package to the user in the code editor of the host: the package, the file
    /// relative to its folder (null for the folder alone), then the 1-based line and column. It returns false
    /// when no window is there to show it. Null in a host without a code editor.
    /// </summary>
    public Func<SourcePluginPackage, string?, int?, int?, bool>? OpenEditor { get; init; }
}
