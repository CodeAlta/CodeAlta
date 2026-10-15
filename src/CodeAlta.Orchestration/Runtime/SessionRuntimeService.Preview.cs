using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>A conversation another program started, read to be shown before it is imported.</summary>
/// <param name="Title">Its title, as the program that started it shows it.</param>
/// <param name="Length">The length of its transcript when it was read, in bytes.</param>
/// <param name="LastWriteUtcTicks">When its transcript was last written then, in UTC ticks.</param>
/// <param name="Events">
/// The records an import would write for its turns, in order, without the records that only hold data (the messages
/// as the provider is given them, the state snapshots); the images are named, not attached.
/// </param>
public sealed record ExternalConversationPreview(string Title, long Length, long LastWriteUtcTicks, IReadOnlyList<AgentEvent> Events);

/// <summary>The outcome of a read of a conversation another program started, to show it.</summary>
/// <param name="Status">
/// <c>ok</c> (read), <c>existing</c> (a session of CodeAlta continues it: <paramref name="SessionId"/> is that session),
/// <c>not_found</c> (the provider has no such conversation the listing would give for the project), or <c>too_large</c>
/// (its transcript is larger than a preview reads).
/// </param>
/// <param name="SessionId">The session that continues the conversation, with <c>existing</c>.</param>
/// <param name="Preview">The conversation, with <c>ok</c>.</param>
public sealed record ExternalConversationPreviewResult(string Status, string? SessionId, ExternalConversationPreview? Preview);

public sealed partial class SessionRuntimeService
{
    /// <summary>The largest transcript a preview reads, in bytes.</summary>
    public const long ExternalConversationPreviewMaxBytes = 64L * 1024 * 1024;

    /// <summary>What the session identity of the records of a preview starts with: they belong to no session.</summary>
    public const string ExternalConversationPreviewPrefix = "preview:";

    internal const string PreviewTooLarge = "too_large";

    // The records prepared for a conversation read: kept as long as the provider keeps that read (its transcript unchanged).
    private readonly ConditionalWeakTable<AgentExternalConversationImport, ExternalConversationPreview> _previews = new();

    /// <summary>
    /// Reads a conversation another program started in the folder of a project, which no session of CodeAlta continues,
    /// to show it as an import would write it, without writing anything. A conversation a session continues already gives
    /// that session (<c>existing</c>). Nothing of the provider is started.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="provider">The provider, which shares its conversations with other programs.</param>
    /// <param name="conversationId">The conversation, as the listing gave it.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The conversation, or why there is none to show.</returns>
    /// <remarks>
    /// A transcript larger than <see cref="ExternalConversationPreviewMaxBytes"/> is not read. The read and its records
    /// are kept while the transcript does not change, so that the pages of a preview cost one read.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The read was stopped.</exception>
    public async Task<ExternalConversationPreviewResult> PreviewExternalConversationAsync(ProjectDescriptor project, ModelProviderDescriptor provider,
        string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return await AdmitAsync(async () =>
        {
            var known = await ReadKnownConversationsAsync(project, cancellationToken).ConfigureAwait(false);
            if (known.TryGetValue(conversationId, out var existingId)) return new ExternalConversationPreviewResult(ImportExisting, existingId, null);
            var read = await _agentHub.PreviewExternalConversationAsync(provider.ProviderId, project.ProjectPath, conversationId,
                ExternalConversationPreviewMaxBytes, cancellationToken).ConfigureAwait(false);
            if (read is not { } found) return new ExternalConversationPreviewResult(ImportNotFound, null, null);
            var (preview, _) = found;
            if (preview.Status == AgentExternalConversationPreviewStatus.TooLarge) return new ExternalConversationPreviewResult(PreviewTooLarge, null, null);
            if (preview.Status != AgentExternalConversationPreviewStatus.Read || preview.Conversation is not { } conversation)
                return new ExternalConversationPreviewResult(ImportNotFound, null, null);
            if (_previews.TryGetValue(conversation, out var prepared)) return new ExternalConversationPreviewResult(ExternalConversationOk, null, prepared);
            prepared = await PrepareExternalPreviewAsync(provider, project, conversationId, preview, conversation, cancellationToken).ConfigureAwait(false);
            _previews.AddOrUpdate(conversation, prepared);
            return new ExternalConversationPreviewResult(ExternalConversationOk, null, prepared);
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // The records an import writes, made in memory: no image is saved, nothing is written.
    private static async Task<ExternalConversationPreview> PrepareExternalPreviewAsync(ModelProviderDescriptor provider, ProjectDescriptor project,
        string conversationId, AgentExternalConversationPreview preview, AgentExternalConversationImport conversation, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AgentSessionState { SessionId = ExternalConversationPreviewPrefix + conversationId, ProviderSessionId = conversationId, UpdatedAt = now };
        var prepared = await AgentExternalTurnJournal.PrepareAsync(
            provider.ProviderId,
            state,
            conversation.Turns,
            project.ProjectPath,
            static (data, _) => Task.FromResult<AgentInputItem>(new AgentInputItem.Text("[Image]")),
            static (result, _) => Task.FromResult(result.Items.Any(static item => item is AgentToolResultItem.Image)
                ? result with
                {
                    Items = [.. result.Items.Select(static item => item is AgentToolResultItem.Image image
                        ? new AgentToolResultItem.Text($"[Image: {(string.IsNullOrWhiteSpace(image.DisplayName) ? "image" : image.DisplayName.Trim())}]")
                        : item)],
                }
                : result),
            now,
            cancellationToken).ConfigureAwait(false);
        return new ExternalConversationPreview(
            conversation.Conversation.Title,
            preview.Length,
            preview.LastWriteTimeUtc.Ticks,
            [.. prepared.Events.Where(static record => record is not AgentRawEvent)]);
    }
}
