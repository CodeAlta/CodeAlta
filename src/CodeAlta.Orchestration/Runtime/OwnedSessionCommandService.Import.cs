using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>
    /// Lists the conversations a provider keeps that other programs started in the folder of a project (Claude Code: in
    /// VS Code or by the CLI in a terminal) and that no session of CodeAlta continues. Only reads.
    /// </summary>
    /// <param name="project">An already resolved, trusted catalog project.</param>
    /// <param name="provider">A provider of the host registry that shares its conversations.</param>
    /// <param name="limit">The most conversations returned.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The conversations, newest first, and how many there are.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The host command owner is closing.</exception>
    /// <exception cref="OperationCanceledException">The listing was stopped.</exception>
    public Task<ExternalConversationListing> ListExternalConversationsAsync(ProjectDescriptor project, ModelProviderDescriptor provider, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate) { if (_closed || _retained) throw new ObjectDisposedException(nameof(OwnedSessionCommandService)); }
        return _runtime.ListExternalConversationsAsync(project, provider, limit, cancellationToken);
    }

    /// <summary>
    /// Imports a conversation another program started in the folder of a project as a new session of that project; a
    /// conversation a session continues already gives that session.
    /// </summary>
    /// <remarks>The caller owns admission and must drain the returned task before disposing the host.</remarks>
    /// <param name="project">An already resolved, trusted catalog project.</param>
    /// <param name="provider">An enabled provider of the host registry that shares its conversations: the provider of the session.</param>
    /// <param name="conversationId">The conversation, as the listing gave it.</param>
    /// <returns>The session that holds the conversation, or why there is none.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The provider is disabled, or the conversation identity is empty.</exception>
    /// <exception cref="ObjectDisposedException">The host command owner is closing.</exception>
    /// <exception cref="IOException">The session cannot be written.</exception>
    public Task<ExternalConversationImportResult> ImportExternalConversationAsync(ProjectDescriptor project, ModelProviderDescriptor provider, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (!provider.IsEnabled) throw new ArgumentException("An enabled provider is required.", nameof(provider));
        lock (_gate) { if (_closed || _retained) throw new ObjectDisposedException(nameof(OwnedSessionCommandService)); }
        return _runtime.ImportExternalConversationAsync(project, provider, conversationId, CancellationToken.None);
    }
}
