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
    /// <param name="offset">How many conversations are skipped before the page.</param>
    /// <param name="count">The most conversations returned.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The conversations of the page, newest first, and how many there are in all.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative, or <paramref name="count"/> is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The host command owner is closing.</exception>
    /// <exception cref="OperationCanceledException">The listing was stopped.</exception>
    public Task<ExternalConversationListing> ListExternalConversationsAsync(ProjectDescriptor project, ModelProviderDescriptor provider, int offset, int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate) { if (_closed || _retained) throw new ObjectDisposedException(nameof(OwnedSessionCommandService)); }
        return _runtime.ListExternalConversationsAsync(project, provider, offset, count, cancellationToken);
    }

    /// <summary>
    /// Describes a conversation another program started in the folder of a project, which no session of CodeAlta
    /// continues, to open it in that program or a terminal: the command and the address the provider builds, and the
    /// program that holds it now. Only reads; nothing is released.
    /// </summary>
    /// <param name="project">An already resolved, trusted catalog project.</param>
    /// <param name="provider">A provider of the host registry that shares its conversations.</param>
    /// <param name="conversationId">The conversation, as the listing gave it.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>
    /// <c>ok</c> with the conversation; <c>existing</c> when a session continues it; <c>not_found</c> when the listing does
    /// not give it for the project; <c>closed</c> when the owner is closing.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The conversation identity is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was stopped.</exception>
    public async Task<OwnedSharedConversationResult> DescribeExternalConversationAsync(ProjectDescriptor project, ModelProviderDescriptor provider,
        string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        lock (_gate) { if (_closed || _retained) return new("closed", null); }
        try
        {
            var (status, conversation) = await _runtime.DescribeExternalConversationAsync(project, provider, conversationId, cancellationToken).ConfigureAwait(false);
            return new(status, conversation is null ? null
                : new(conversation.ConversationId, conversation.ResumeCommand, conversation.WorkingDirectory, conversation.EditorUri, conversation.Holder));
        }
        catch (ObjectDisposedException) { return new("closed", null); }
    }

    /// <summary>
    /// Reads a conversation another program started in the folder of a project, which no session of CodeAlta continues,
    /// to show it as an import would write it. Only reads; nothing is written.
    /// </summary>
    /// <param name="project">An already resolved, trusted catalog project.</param>
    /// <param name="provider">A provider of the host registry that shares its conversations.</param>
    /// <param name="conversationId">The conversation, as the listing gave it.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>
    /// <c>ok</c> with the conversation; <c>existing</c> with the session that continues it; <c>not_found</c>;
    /// <c>too_large</c>; <c>closed</c> when the owner is closing.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The conversation identity is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    public async Task<ExternalConversationPreviewResult> PreviewExternalConversationAsync(ProjectDescriptor project, ModelProviderDescriptor provider,
        string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        lock (_gate) { if (_closed || _retained) return new("closed", null, null); }
        try { return await _runtime.PreviewExternalConversationAsync(project, provider, conversationId, cancellationToken).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return new("closed", null, null); }
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
        => ImportExternalConversationAsync(project, provider, conversationId, spaceId: null);

    /// <summary>
    /// Imports a conversation another program started in the folder of a project as a new session of that project, which
    /// belongs to a space for its whole life; a conversation a session continues already gives that session.
    /// </summary>
    /// <remarks>The caller owns admission and must drain the returned task before disposing the host.</remarks>
    /// <param name="project">An already resolved, trusted catalog project.</param>
    /// <param name="provider">An enabled provider of the host registry that shares its conversations: the provider of the session.</param>
    /// <param name="conversationId">The conversation, as the listing gave it.</param>
    /// <param name="spaceId">
    /// The space of the session, recorded in its journal; null records none, and the session is then read as in the
    /// only space of its project, else the default one.
    /// </param>
    /// <returns>
    /// The session that holds the conversation, or why there is none: <c>provider_outside_space</c> when the provider is
    /// not available in that space.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The provider is disabled, the conversation identity is empty, or the space is not a valid identifier.</exception>
    /// <exception cref="ObjectDisposedException">The host command owner is closing.</exception>
    /// <exception cref="IOException">The session cannot be written.</exception>
    public Task<ExternalConversationImportResult> ImportExternalConversationAsync(ProjectDescriptor project, ModelProviderDescriptor provider, string conversationId,
        string? spaceId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (!provider.IsEnabled) throw new ArgumentException("An enabled provider is required.", nameof(provider));
        lock (_gate) { if (_closed || _retained) throw new ObjectDisposedException(nameof(OwnedSessionCommandService)); }
        return _runtime.ImportExternalConversationAsync(project, provider, conversationId, spaceId, CancellationToken.None);
    }
}
