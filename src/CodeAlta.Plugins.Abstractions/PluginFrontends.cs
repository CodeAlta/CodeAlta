namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Identifies the CodeAlta applications: the ones a plugin supports, or the one that hosts it.
/// </summary>
/// <remarks>
/// A plugin declares what it supports with <see cref="PluginAttribute.Frontends"/>; the default is both
/// applications. A host reports the application it is with <see cref="PluginHostInfo.Frontend"/>, and does
/// not start a plugin that does not support it.
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

    /// <summary>Both applications.</summary>
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
    /// <returns><c>desktop application</c>, <c>terminal application</c>, or <c>host</c> for anything else.</returns>
    public static string ToDisplayName(this PluginFrontends frontend) => frontend switch
    {
        PluginFrontends.Desktop => "desktop application",
        PluginFrontends.Terminal => "terminal application",
        _ => "host",
    };
}
