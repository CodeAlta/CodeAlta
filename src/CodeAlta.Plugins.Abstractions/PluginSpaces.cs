namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Tells a plugin the spaces it is assigned to, as Settings &gt; Plugins assigns them. The host already leaves a plugin out
/// of the operations of the spaces it is not assigned to; a plugin that serves something of its own beyond the operations
/// of the window, such as a page another device opens, reads them to keep to those spaces.
/// </summary>
public interface IPluginSpaceService
{
    /// <summary>Gets the ids of the spaces the plugin is assigned to, as the configuration says them now.</summary>
    /// <returns>
    /// The ids; empty when the plugin is in every space. A plugin of a project follows its project, which is assigned to
    /// spaces of its own, and a host without spaces has none: both get an empty list.
    /// </returns>
    IReadOnlyList<string> GetAssignedSpaces();
}

/// <summary>The space service of a host without spaces: every plugin is in every space.</summary>
public sealed class NoopPluginSpaceService : IPluginSpaceService
{
    private NoopPluginSpaceService()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static NoopPluginSpaceService Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<string> GetAssignedSpaces() => [];
}
