using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    /// <summary>The status of a selection or a send whose provider is not available in the space of the session.</summary>
    public const string ProviderOutsideSpace = "provider_outside_space";

    /// <summary>
    /// Tells whether a provider is available in a space: a provider that the configuration assigns to no space is in
    /// every space. A configuration that cannot be read assigns none.
    /// </summary>
    /// <param name="providerKey">The key of the provider.</param>
    /// <param name="spaceId">The space; <see langword="null"/> for the default space.</param>
    /// <returns><see langword="true"/> when the provider can be used there.</returns>
    public bool IsProviderInSpace(string providerKey, string? spaceId)
        => string.IsNullOrWhiteSpace(providerKey) || LoadSpaceAssignments().IsAvailable(SpaceResourceKind.Provider, providerKey, spaceId);

    /// <summary>
    /// Tells whether the provider of a session is available in the space of the session. A session whose provider
    /// was taken out of its space cannot send until it switches to another provider.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when it is, or when the session is not known.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<bool> IsSessionProviderInSpaceAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var session = await ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return true;
        var space = await ResolveSpaceIdAsync(session.SpaceId, session.ProjectRef, cancellationToken).ConfigureAwait(false);
        return IsProviderInSpace(session.ResolvedProviderKey, space);
    }

    // The space a session is in: the one its journal records, else the only space of its project, else the default.
    private async Task<string> ResolveSpaceIdAsync(string? recordedSpaceId, string? projectId, CancellationToken cancellationToken)
    {
        try
        {
            return (await new SpaceCatalog(_projectCatalog).ResolveSessionSpaceAsync(recordedSpaceId, projectId, cancellationToken).ConfigureAwait(false)).Id;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return SpaceDescriptor.IsValidId(recordedSpaceId) ? recordedSpaceId! : SpaceDescriptor.DefaultId;
        }
    }

    private SpaceAssignments LoadSpaceAssignments()
    {
        try { return _configStore.LoadGlobalSpaceAssignments(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return SpaceAssignments.Empty;
        }
    }
}
