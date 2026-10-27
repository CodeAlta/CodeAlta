namespace CodeAlta.Catalog;

/// <summary>
/// What the space of a session has: the assignments of the global configuration, read once, and the space they are
/// read for. Every kind of resource is filtered by it before its global and project enablement, so that a project
/// cannot bring into a space what the space does not have.
/// </summary>
/// <param name="Assignments">The spaces each resource is assigned to.</param>
/// <param name="SpaceId">The space of the session; <see cref="SpaceDescriptor.DefaultId"/> for the default space.</param>
public sealed record SpaceFilter(SpaceAssignments Assignments, string SpaceId)
{
    /// <summary>Tells whether the space has a resource.</summary>
    /// <param name="kind">The kind of the resource.</param>
    /// <param name="key">Its key, name or id.</param>
    /// <returns><see langword="true"/> when the resource is available in the space.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    public bool Allows(SpaceResourceKind kind, string key) => Assignments.IsAvailable(kind, key, SpaceId);

    /// <summary>Reads the assignments of the global configuration for a space.</summary>
    /// <param name="configStore">The configuration store; <see langword="null"/> when the host keeps none, where every resource is in every space.</param>
    /// <param name="spaceId">The space of the session; <see langword="null"/> or empty for the default space.</param>
    /// <returns>The filter of the space.</returns>
    /// <exception cref="InvalidDataException">The configuration file cannot be parsed.</exception>
    /// <exception cref="IOException">The configuration file cannot be read.</exception>
    public static SpaceFilter Load(CodeAltaConfigStore? configStore, string? spaceId)
        => new(configStore?.LoadGlobalSpaceAssignments() ?? SpaceAssignments.Empty,
            string.IsNullOrWhiteSpace(spaceId) ? SpaceDescriptor.DefaultId : spaceId.Trim());
}

/// <summary>
/// The answer to a request for a resource that the space of the session does not have: an agent cannot reach it,
/// and only the user assigns resources to spaces.
/// </summary>
public sealed class ResourceNotInSpaceException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="ResourceNotInSpaceException"/> class.</summary>
    /// <param name="kind">The kind of the resource.</param>
    /// <param name="key">Its key, name or id.</param>
    /// <param name="spaceId">The space of the session.</param>
    public ResourceNotInSpaceException(SpaceResourceKind kind, string key, string spaceId)
        : base(MessageOf(kind, key, spaceId))
    {
        Kind = kind;
        Key = key;
        SpaceId = spaceId;
    }

    /// <summary>Gets the kind of the resource.</summary>
    public SpaceResourceKind Kind { get; }

    /// <summary>Gets the key, name or id of the resource.</summary>
    public string Key { get; }

    /// <summary>Gets the space of the session.</summary>
    public string SpaceId { get; }

    /// <summary>Writes the message that tells an agent why a resource is refused.</summary>
    /// <param name="kind">The kind of the resource.</param>
    /// <param name="key">Its key, name or id.</param>
    /// <param name="spaceId">The space of the session.</param>
    /// <returns>The message.</returns>
    public static string MessageOf(SpaceResourceKind kind, string key, string spaceId)
    {
        var what = kind switch
        {
            SpaceResourceKind.Provider => "The provider",
            SpaceResourceKind.McpServer => "The MCP server",
            SpaceResourceKind.Plugin => "The plugin",
            SpaceResourceKind.Skill => "The skill",
            SpaceResourceKind.Prompt => "The agent prompt",
            SpaceResourceKind.Script => "The script",
            _ => "The resource",
        };
        return $"{what} '{key}' is not available in the space '{spaceId}' of this session. " +
               "Only the user assigns resources to spaces, in Settings of the CodeAlta window: tell the user if this session needs it.";
    }
}
