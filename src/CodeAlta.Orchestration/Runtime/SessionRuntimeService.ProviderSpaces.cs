using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>The providers a session can use in its space, and how its own provider is named.</summary>
/// <param name="ProviderInSpace">Whether the provider of the session is available in its space.</param>
/// <param name="ProviderName">The display name of the provider of the session; null when no provider has its key.</param>
/// <param name="Providers">The enabled providers of the space of the session, in the order of the registry.</param>
public sealed record SessionSpaceProviders(bool ProviderInSpace, string? ProviderName, IReadOnlyList<ModelProviderDescriptor> Providers);

public sealed partial class SessionRuntimeService
{
    /// <summary>The status of a selection or a send whose provider is not available in the space of the session.</summary>
    public const string ProviderOutsideSpace = "provider_outside_space";

    // Asked at every send and every read of the choices of a session: parsed again only when the file changes.
    private CachedSpaceAssignments? _providerSpaceAssignments;

    /// <summary>
    /// Tells whether a provider is available in a space: a provider that the configuration assigns to no space is in
    /// every space. A configuration that cannot be read keeps the assignments it gave last, or none.
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

    /// <summary>
    /// Reads the providers a session can use in its space, and how its own provider is named. A provider that is
    /// turned off, or assigned to other spaces only, is not among them.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the session can use; null when the session is not known.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<SessionSpaceProviders?> GetSessionSpaceProvidersAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var session = await ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        var space = await ResolveSpaceIdAsync(session.SpaceId, session.ProjectRef, cancellationToken).ConfigureAwait(false);
        var assignments = LoadSpaceAssignments();
        var providers = _agentHub.SelectionProviders;
        var own = providers.FirstOrDefault(provider => string.Equals(provider.ProviderId.Value, session.ResolvedProviderKey, StringComparison.Ordinal));
        return new(IsProviderInSpace(session.ResolvedProviderKey, space), own?.DisplayName,
            [.. providers.Where(provider => provider.IsEnabled && assignments.IsAvailable(SpaceResourceKind.Provider, provider.ProviderId.Value, space))]);
    }

    // The space a session is in: the one its journal records, else the only space of its project, else the default.
    private async Task<string> ResolveSpaceIdAsync(string? recordedSpaceId, string? projectId, CancellationToken cancellationToken)
        => (await ResolveSpaceAsync(recordedSpaceId, projectId, cancellationToken).ConfigureAwait(false)).Id;

    // The same, with the space as the catalog has it; none when the catalog cannot be read, and the id is then the one
    // the session records, else the default.
    private async Task<(string Id, SpaceDescriptor? Space)> ResolveSpaceAsync(string? recordedSpaceId, string? projectId, CancellationToken cancellationToken)
    {
        try
        {
            var space = await _spaceCatalog.ResolveSessionSpaceAsync(recordedSpaceId, projectId, cancellationToken).ConfigureAwait(false);
            return (space.Id, space);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (SpaceDescriptor.IsValidId(recordedSpaceId) ? recordedSpaceId! : SpaceDescriptor.DefaultId, null);
        }
    }

    private SpaceAssignments LoadSpaceAssignments()
        => LazyInitializer.EnsureInitialized(ref _providerSpaceAssignments, () => new CachedSpaceAssignments(_configStore)).Current();
}
