namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Identifies the CodeAlta applications: the ones a plugin supports, or the one that hosts it.
/// </summary>
/// <remarks>
/// A plugin declares what it supports with <see cref="PluginAttribute.Frontends"/>; the default is the two
/// interactive applications, <see cref="All"/>. A host reports the application it is with
/// <see cref="PluginHostInfo.Frontend"/>, and does not start a plugin that does not support it. The
/// command-line application runs without a user interface: a plugin opts in to it with <see cref="Cli"/>.
/// </remarks>
[Flags]
public enum PluginFrontends
{
    /// <summary>No application: a host without a user interface.</summary>
    None = 0,

    /// <summary>The terminal application (<c>altatui</c>).</summary>
    Terminal = 1,

    /// <summary>The desktop application (<c>alta</c>).</summary>
    Desktop = 2,

    /// <summary>
    /// The command-line application (<c>altacli</c>), which runs sessions without a user interface. A plugin
    /// supports it only when it says so: its contributions that need no window (agent tools, <c>alta</c>
    /// commands, prompt parts, hooks, model providers) work there.
    /// </summary>
    Cli = 4,

    /// <summary>
    /// The two interactive applications, <see cref="Terminal"/> and <see cref="Desktop"/>. It does not include
    /// <see cref="Cli"/>: a plugin opts in to the command-line application explicitly.
    /// </summary>
    All = Terminal | Desktop,
}

/// <summary>
/// Helpers for <see cref="PluginFrontends"/>.
/// </summary>
public static class PluginFrontendsExtensions
{
    /// <summary>
    /// Determines whether a plugin that supports <paramref name="supported"/> runs in a host that is <paramref name="host"/>.
    /// </summary>
    /// <param name="supported">The applications the plugin supports.</param>
    /// <param name="host">The application of the host, or <see cref="PluginFrontends.None"/> for a host without a user interface.</param>
    /// <returns><see langword="true"/> when the host has no user interface, or when the plugin supports the host's application.</returns>
    public static bool Supports(this PluginFrontends supported, PluginFrontends host)
        => host == PluginFrontends.None || (supported & host) != 0;

    /// <summary>Gets the name shown to users for one application.</summary>
    /// <param name="frontend">The application.</param>
    /// <returns><c>desktop application</c>, <c>terminal application</c>, <c>command-line application</c>, or <c>host</c> for anything else.</returns>
    public static string ToDisplayName(this PluginFrontends frontend) => frontend switch
    {
        PluginFrontends.Desktop => "desktop application",
        PluginFrontends.Terminal => "terminal application",
        PluginFrontends.Cli => "command-line application",
        _ => "host",
    };
}
