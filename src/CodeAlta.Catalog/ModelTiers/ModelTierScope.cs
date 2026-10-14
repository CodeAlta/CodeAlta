namespace CodeAlta.Catalog.ModelTiers;

/// <summary>
/// The tiers of models a session of a space uses: the space's own when its file sets some, the global ones
/// otherwise; a target whose provider is not available in the space is never taken.
/// </summary>
/// <param name="Tiers">The tiers.</param>
/// <param name="FromSpace">Whether the tiers are the space's own.</param>
/// <param name="SpaceId">The space; <see langword="null"/> for the default space.</param>
/// <param name="Assignments">The spaces of the providers.</param>
public sealed record ModelTierScope(ModelTierSettings Tiers, bool FromSpace, string? SpaceId, SpaceAssignments Assignments)
{
    /// <summary>Gets a scope with no tier.</summary>
    public static ModelTierScope None { get; } = new(ModelTierSettings.Empty, false, null, SpaceAssignments.Empty);

    /// <summary>Gets whether a provider is available in the space.</summary>
    /// <param name="providerKey">The key of the provider.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool IsProviderAvailable(string providerKey)
        => !string.IsNullOrWhiteSpace(providerKey) && Assignments.IsAvailable(SpaceResourceKind.Provider, providerKey, SpaceId);

    /// <summary>Works out the tiers of a space from the global ones and its settings.</summary>
    /// <param name="global">The global tiers.</param>
    /// <param name="space">The space, or <see langword="null"/> when the host keeps none.</param>
    /// <param name="assignments">The spaces of the providers.</param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="global"/> or <paramref name="assignments"/> is null.</exception>
    public static ModelTierScope Of(ModelTierSettings global, SpaceDescriptor? space, SpaceAssignments assignments)
    {
        ArgumentNullException.ThrowIfNull(global);
        ArgumentNullException.ThrowIfNull(assignments);
        return space?.Settings.NewSessions?.Tiers is { IsEmpty: false } own
            ? new ModelTierScope(own.ToTierSettings(), true, space.Id, assignments)
            : new ModelTierScope(global, false, space?.Id, assignments);
    }

    /// <summary>
    /// Reads the tiers of the space of a session: the space it records, else the one of its project, else the default
    /// space. A file that cannot be read gives the global tiers, or none.
    /// </summary>
    /// <param name="store">The configuration store.</param>
    /// <param name="spaces">The spaces, or <see langword="null"/> when the host keeps none.</param>
    /// <param name="recordedSpaceId">The space the session records, or <see langword="null"/>.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a chat.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public static async Task<ModelTierScope> LoadAsync(CodeAltaConfigStore store, SpaceCatalog? spaces, string? recordedSpaceId, string? projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ModelTierSettings global;
        SpaceAssignments assignments;
        try
        {
            global = store.LoadGlobalModelSettings().Tiers;
            assignments = store.LoadGlobalSpaceAssignments();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return None;
        }

        SpaceDescriptor? space = null;
        if (spaces is not null)
        {
            try { space = await spaces.ResolveSessionSpaceAsync(recordedSpaceId, projectId, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }

        return Of(global, space, assignments);
    }
}
