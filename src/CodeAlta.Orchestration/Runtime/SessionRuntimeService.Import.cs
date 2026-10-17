using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Images;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>A conversation another program started, which a session can import.</summary>
/// <param name="ConversationId">The identifier the provider gives it (Claude Code: the session of the CLI).</param>
/// <param name="Title">Its title, as the program that started it shows it.</param>
/// <param name="LastChangedAt">When it was last written.</param>
/// <param name="Origin">Where it was started: <c>vscode</c>, <c>terminal</c>, <c>other</c> or <c>unknown</c>.</param>
/// <param name="Entrypoint">The name the provider gives that program, as written; null when it names none.</param>
/// <param name="IsHeld">Whether another live program holds it now (open in VS Code, a CLI in a terminal).</param>
/// <param name="HolderStatus">
/// What the program that holds it says it does (Claude Code: <c>busy</c>, <c>idle</c>, <c>waiting</c>, ...), when it is held
/// and says it: a conversation merely open in a VS Code panel is <c>idle</c>.
/// </param>
/// <param name="ProjectId">The project that lists it; null when no project does.</param>
/// <param name="Tag">
/// Where it started when that is not the folder of its project: a subfolder or a worktree of the project, another
/// checkout of its repository, or the start folder of a conversation of no project.
/// </param>
/// <param name="StartDirectory">The folder it started in, as written when it is a folder of another system.</param>
public sealed record ExternalConversationSummary(
    string ConversationId,
    string Title,
    DateTimeOffset LastChangedAt,
    string Origin,
    string? Entrypoint,
    bool IsHeld,
    string? HolderStatus = null,
    string? ProjectId = null,
    string? Tag = null,
    string? StartDirectory = null);

/// <summary>The conversations other programs started that one project lists, or that no project lists.</summary>
/// <param name="Conversations">The conversations of the page asked for, the most recently changed first.</param>
/// <param name="Total">How many there are, also those outside the page.</param>
public sealed record ExternalConversationListing(IReadOnlyList<ExternalConversationSummary> Conversations, int Total);

/// <summary>The outcome of the import of a conversation another program started.</summary>
/// <param name="Status">
/// <c>imported</c> (a new session holds it), <c>existing</c> (a session continues it already: that one is given),
/// <c>not_found</c> (the provider has no such conversation a listing would give), or
/// <c>provider_outside_space</c> (the provider is not available in the space of the session: nothing is written).
/// </param>
/// <param name="Session">The session that holds the conversation; null for <c>not_found</c>.</param>
public sealed record ExternalConversationImportResult(string Status, SessionViewDescriptor? Session);

public sealed partial class SessionRuntimeService
{
    internal const string ImportImported = "imported";
    internal const string ImportExisting = "existing";
    internal const string ImportNotFound = "not_found";
    // A conversation another program started, described to open it there (DescribeExternalConversationAsync).
    internal const string ExternalConversationOk = "ok";

    // One import at a time: a second import of one conversation finds the session of the first.
    private readonly SemaphoreSlim _externalImportGate = new(1, 1);

    // The placement of every conversation a provider lists, while it is being made: the listings of the groups of all
    // the projects asked together share it. An import, or any change of the catalog of sessions, makes it stale, so that
    // a listing asked after it waits for a new one.
    private readonly object _externalListingGate = new();
    private (ModelProviderId Provider, int Generation, Task<List<(AgentExternalConversation Conversation, ExternalConversationPlace Place)>> Work)? _externalListing;
    private int _externalListingGeneration;

    /// <summary>
    /// Lists the conversations a provider keeps that other programs started (Claude Code: in VS Code or by the CLI in a
    /// terminal), that no session of CodeAlta continues, and that one project lists, or no project. Nothing of the
    /// provider is started.
    /// </summary>
    /// <param name="project">The project; null for the conversations no project lists.</param>
    /// <param name="provider">The provider, which shares its conversations with other programs; another one lists nothing.</param>
    /// <param name="offset">How many conversations are skipped before the page.</param>
    /// <param name="count">The most conversations returned.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The conversations of the page, newest first, and how many there are in all.</returns>
    /// <remarks>
    /// <para>
    /// Every conversation is listed under one project at most, chosen from the folder it started in by
    /// <see cref="ExternalConversationPlacement"/>, over every project of the catalog and the worktrees their sessions
    /// work in. Whether another program holds one is looked up for the page only.
    /// </para>
    /// <para>
    /// A conversation is known when a session of CodeAlta names it as the conversation of its provider, in any project,
    /// archived or not: only the last conversation of a session is known, so one it left (after a broken resume, or a
    /// change of provider) is listed again.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative, or <paramref name="count"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException">The listing was stopped.</exception>
    public async Task<ExternalConversationListing> ListExternalConversationsAsync(ProjectDescriptor? project, ModelProviderDescriptor provider, int offset, int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return await AdmitAsync(async () =>
        {
            var placed = await PlaceExternalConversationsAsync(provider, cancellationToken).ConfigureAwait(false);
            var group = placed.Where(item => string.Equals(item.Place.ProjectId, project?.Id, StringComparison.Ordinal)).ToList();
            var page = group.Skip(offset).Take(count).ToList();
            var held = await _agentHub.FindExternalConversationHoldersAsync(provider.ProviderId, [.. page.Select(static item => item.Conversation)],
                cancellationToken).ConfigureAwait(false);
            return new ExternalConversationListing([.. page.Select((item, index) => ToSummary(held[index], item.Place))], group.Count);
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists every conversation a provider keeps that other programs started and that no session of CodeAlta continues,
    /// whichever project lists it, with that project: what a search looks through. Nothing of the provider is started,
    /// and whether another program holds one is not looked up.
    /// </summary>
    /// <param name="provider">The provider, which shares its conversations with other programs; another one lists nothing.</param>
    /// <param name="count">The most conversations returned, the most recently changed first.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The conversations, newest first, and how many there are in all.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException">The listing was stopped.</exception>
    public async Task<ExternalConversationListing> ListAllExternalConversationsAsync(ModelProviderDescriptor provider, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return await AdmitAsync(async () =>
        {
            var placed = await PlaceExternalConversationsAsync(provider, cancellationToken).ConfigureAwait(false);
            return new ExternalConversationListing([.. placed.Take(count).Select(static item => ToSummary(item.Conversation, item.Place))], placed.Count);
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Describes a conversation another program started, which no session of CodeAlta continues, to open it in that
    /// program or a terminal (Claude Code: VS Code, <c>claude --resume</c>): the command and the address that open it,
    /// built by the provider, and the program that holds it now. Nothing of the provider is started, and nothing is
    /// released: CodeAlta does not hold such a conversation.
    /// </summary>
    /// <param name="project">
    /// The project it is opened from; null for a conversation of no project. The terminal command is run in the worktree
    /// of the project the conversation ended in, else in the folder of the project; without a project, in the folder the
    /// conversation ended or started in, when it exists.
    /// </param>
    /// <param name="provider">The provider, which shares its conversations with other programs.</param>
    /// <param name="conversationId">The conversation, as the listing gave it.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>
    /// <c>ok</c> with the conversation; <c>existing</c> when a session of CodeAlta continues it (it is opened from that
    /// session); <c>not_found</c> when no listing gives it.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was stopped.</exception>
    internal async Task<(string Status, AgentSharedConversation? Conversation)> DescribeExternalConversationAsync(ProjectDescriptor? project,
        ModelProviderDescriptor provider, string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return await AdmitAsync(async () =>
        {
            var (known, _) = await ReadKnownConversationsAsync(project, cancellationToken).ConfigureAwait(false);
            if (known.ContainsKey(conversationId)) return (ImportExisting, (AgentSharedConversation?)null);
            var listed = (await _agentHub.ListExternalConversationsAsync(provider.ProviderId, known.ContainsKey, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(conversation => string.Equals(conversation.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase));
            if (listed is null) return (ImportNotFound, null);
            var folder = project is null
                ? ExistingFolder(listed.EndDirectory) ?? ExistingFolder(listed.StartDirectory) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : ImportWorktree(project, listed.EndDirectory) ?? project.ProjectPath;
            var conversation = await _agentHub.DescribeExternalConversationAsync(provider.ProviderId, conversationId, folder, cancellationToken)
                .ConfigureAwait(false);
            return conversation is null ? (ImportNotFound, null) : (ExternalConversationOk, conversation);
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports a conversation another program started as a new session of a project, which continues it: its turns are
    /// written as turns made elsewhere, and the first prompt resumes the conversation of the provider. A conversation a
    /// session continues already is not imported again: that session is given.
    /// </summary>
    /// <param name="project">
    /// The project of the new session: the one that lists the conversation, or any other one (a conversation dropped on
    /// a project). The session works in the worktree of the project the conversation ended in when it still exists, else
    /// in the folder of the project. The files of the provider are not moved: it resumes the conversation from any folder.
    /// </param>
    /// <param name="provider">The provider of the new session: its default model and reasoning effort are those of the session.</param>
    /// <param name="conversationId">The conversation, as the listing gave it.</param>
    /// <param name="cancellationToken">Stops waiting before the session is written.</param>
    /// <returns>The session that holds the conversation, or why there is none.</returns>
    /// <remarks>
    /// The conversation is only read, also when another program holds it now: the first prompt of the session then
    /// asks what to do, as for any conversation held elsewhere. Nothing of the provider is started. The turns carry no
    /// usage: they are not counted as usage of CodeAlta. The journal is written whole, then listed.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty.</exception>
    /// <exception cref="IOException">The journal or an attachment cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The import was stopped before the session was written.</exception>
    public async Task<ExternalConversationImportResult> ImportExternalConversationAsync(ProjectDescriptor project, ModelProviderDescriptor provider,
        string conversationId, CancellationToken cancellationToken = default)
        => await ImportExternalConversationAsync(project, provider, conversationId, spaceId: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Imports a conversation another program started in the folder of a project as a new session of that project, in a
    /// space: as <see cref="ImportExternalConversationAsync(ProjectDescriptor, ModelProviderDescriptor, string, CancellationToken)"/>,
    /// and the session belongs to that space for its whole life, as a new session does.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="provider">The provider of the new session, which must be available in the space.</param>
    /// <param name="conversationId">The conversation, as the listing gave it.</param>
    /// <param name="spaceId">
    /// The space of the session, recorded in its journal; null records none, and the session is then read as in the
    /// only space of its project, else the default one.
    /// </param>
    /// <param name="cancellationToken">Stops waiting before the session is written.</param>
    /// <returns>
    /// The session that holds the conversation, or why there is none: <see cref="ProviderOutsideSpace"/> when the provider
    /// is not available in the space, and nothing is written.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="conversationId"/> is empty, or <paramref name="spaceId"/> is not the identifier of a space.</exception>
    /// <exception cref="IOException">The journal or an attachment cannot be written.</exception>
    /// <exception cref="OperationCanceledException">The import was stopped before the session was written.</exception>
    public async Task<ExternalConversationImportResult> ImportExternalConversationAsync(ProjectDescriptor project, ModelProviderDescriptor provider,
        string conversationId, string? spaceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        var space = NormalizeSpaceId(spaceId);
        return await AdmitAsync(() => ImportExternalConversationOwnedAsync(project, provider, conversationId, space, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ExternalConversationImportResult> ImportExternalConversationOwnedAsync(ProjectDescriptor project, ModelProviderDescriptor provider,
        string conversationId, string? spaceId, CancellationToken cancellationToken)
    {
        await _externalImportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (known, _) = await ReadKnownConversationsAsync(project, cancellationToken).ConfigureAwait(false);
            if (known.TryGetValue(conversationId, out var existingId))
            {
                var existing = await ResolveOwnedSessionBodyAsync(existingId, cancellationToken).ConfigureAwait(false);
                return existing is null ? new(ImportNotFound, null) : new(ImportExisting, existing);
            }

            // The providers of a session are those of its space, as for a new session: its next send would be refused.
            if (!IsProviderInSpace(provider.ProviderId.Value, await ResolveSpaceIdAsync(spaceId, project.Id, cancellationToken).ConfigureAwait(false)))
                return new(ProviderOutsideSpace, null);

            var read = await _agentHub.ReadExternalConversationAsync(provider.ProviderId, conversationId, cancellationToken).ConfigureAwait(false);
            if (read is not { } found) return new(ImportNotFound, null);
            var (conversation, runtime) = found;
            var worktree = ImportWorktree(project, conversation.Conversation.EndDirectory);

            var now = DateTimeOffset.UtcNow;
            var sessionId = Guid.CreateVersion7().ToString();
            var title = ImportedTitle(conversation.Conversation.Title, project);
            var target = new SessionViewDescriptor
            {
                SessionId = sessionId,
                Kind = SessionViewKind.ProjectSession,
                ProviderId = provider.ProviderId.Value,
                ProviderKey = provider.ProviderId.Value,
                ProjectRef = project.Id,
                SpaceId = spaceId,
                WorkingDirectory = project.ProjectPath,
                WorktreeDirectory = worktree,
                Title = title,
                // The title the conversation has where it was started was chosen there: CodeAlta does not replace it.
                TitleSource = AgentSessionTitleSource.User,
                Status = SessionViewStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
                LastActiveAt = now,
                StartedAt = now,
                ModelId = provider.DefaultModelId,
                ReasoningEffort = provider.DefaultReasoningEffort,
            };
            var initial = new AgentSessionState
            {
                SessionId = sessionId,
                ProtocolFamily = runtime.ProtocolFamily,
                ProviderKey = runtime.ProviderKey,
                ProviderSessionId = conversationId,
                UpdatedAt = now,
            };

            // The images of the turns are saved beside the journal before it is written; removed again when it is not.
            var attachments = _sessionViewCatalog.JournalStore.GetNewSessionAttachmentDirectory(target);
            var attachmentsExisted = Directory.Exists(attachments);
            var images = new PromptImageAttachmentStore(_catalogOptions);
            var toolImages = new AgentSessionToolImages(new NewSessionAttachments(attachments), sessionId);
            try
            {
                var prepared = await AgentExternalTurnJournal.PrepareAsync(
                    provider.ProviderId,
                    initial,
                    conversation.Turns,
                    worktree ?? project.ProjectPath,
                    async (data, imageToken) =>
                    {
                        byte[] bytes;
                        try { bytes = Convert.FromBase64String(data.Base64Data); }
                        catch (FormatException) { return new AgentInputItem.Text("[An image whose data is not valid.]"); }
                        var stored = await images.SaveAsync(target,
                            [new PromptImageAttachment(Guid.NewGuid().ToString("N"), "image", bytes, data.MediaType, ImageExtension(data.MediaType))],
                            imageToken).ConfigureAwait(false);
                        return new AgentInputItem.LocalImage(stored[0].Path, stored[0].Title, stored[0].MediaType);
                    },
                    (toolResult, resultToken) => toolImages.PrepareAsync(toolResult, null, resultToken),
                    now,
                    cancellationToken).ConfigureAwait(false);
                target.LatestSummary = prepared.Preview;
                var summary = new AgentSessionSummary
                {
                    SessionId = sessionId,
                    ProviderId = provider.ProviderId,
                    ProtocolFamily = runtime.ProtocolFamily,
                    ProviderKey = runtime.ProviderKey,
                    ModelId = provider.DefaultModelId,
                    ReasoningEffort = provider.DefaultReasoningEffort,
                    WorkingDirectory = project.ProjectPath,
                    WorktreeDirectory = worktree,
                    Title = title,
                    TitleSource = AgentSessionTitleSource.User,
                    Summary = prepared.Preview,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                var local = new SessionViewLocalState
                {
                    ProviderKey = provider.ProviderId.Value,
                    ModelId = provider.DefaultModelId,
                    ReasoningEffort = provider.DefaultReasoningEffort,
                    PermissionMode = NormalizeOptionalText(provider.DefaultPermissionMode),
                };
                await _sessionViewCatalog.JournalStore.ImportAsync(target, summary, prepared.State, prepared.Events, local, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Nothing names what was saved: the session does not exist.
                if (!attachmentsExisted && Directory.Exists(attachments))
                {
                    try { Directory.Delete(attachments, recursive: true); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }

                throw;
            }

            await _agentSessionCatalog.NotifySessionCreatedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            InvalidateExternalListing();
            var listed = await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false) ?? target;
            PublishSessionCatalogEvent(listed);
            PublishSessionLifecycleEvent(sessionId);
            return new(ImportImported, listed);
        }
        finally
        {
            _externalImportGate.Release();
        }
    }

    // The conversations sessions of CodeAlta continue, with the session that does (one of the project first), and the
    // worktrees the sessions work in, with the folder of their project.
    private async Task<(Dictionary<string, string> Known, List<(string Checkout, string ProjectPath)> Checkouts)> ReadKnownConversationsAsync(
        ProjectDescriptor? project, CancellationToken cancellationToken)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var checkouts = new List<(string Checkout, string ProjectPath)>();
        await foreach (var session in _agentSessionCatalog.ListSessionsAsync(filter: null, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (session.WorktreePath is { Length: > 0 } worktree && session.WorkspacePath is { Length: > 0 } workspace) checkouts.Add((worktree, workspace));
            if ((session.Details as RawApiSessionMetadataDetails)?.ProviderSessionId is not { Length: > 0 } conversation) continue;
            if (!known.ContainsKey(conversation) || (project is not null && string.Equals(session.WorkspacePath, project.ProjectPath, StringComparison.OrdinalIgnoreCase)))
                known[conversation] = session.SessionId;
        }

        return (known, checkouts);
    }

    // The placement being made for the provider when there is one since the last import, else a new one.
    private Task<List<(AgentExternalConversation Conversation, ExternalConversationPlace Place)>> PlaceExternalConversationsAsync(
        ModelProviderDescriptor provider, CancellationToken cancellationToken)
    {
        Task<List<(AgentExternalConversation Conversation, ExternalConversationPlace Place)>> work;
        lock (_externalListingGate)
        {
            if (_externalListing is { } shared && shared.Provider == provider.ProviderId && shared.Generation == _externalListingGeneration && !shared.Work.IsCompleted)
            {
                work = shared.Work;
            }
            else
            {
                // Not stopped by the caller that starts it: the others wait for it too. A failure no caller waits for
                // any more is observed here.
                work = Task.Run(() => PlaceAllExternalConversationsAsync(provider, CancellationToken.None), CancellationToken.None);
                _ = work.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _externalListing = (provider.ProviderId, _externalListingGeneration, work);
            }
        }

        return work.WaitAsync(cancellationToken);
    }

    private void InvalidateExternalListing()
    {
        lock (_externalListingGate) _externalListingGeneration++;
    }

    // Every conversation not yet continued in CodeAlta, newest first, with the project that lists it: an archived project
    // lists none, since it is not shown, nor a project whose folder is gone, whose listing is refused.
    private async Task<List<(AgentExternalConversation Conversation, ExternalConversationPlace Place)>> PlaceAllExternalConversationsAsync(
        ModelProviderDescriptor provider, CancellationToken cancellationToken)
    {
        var (known, checkouts) = await ReadKnownConversationsAsync(null, cancellationToken).ConfigureAwait(false);
        var listed = await _agentHub.ListExternalConversationsAsync(provider.ProviderId, known.ContainsKey, cancellationToken).ConfigureAwait(false);
        if (listed.Count == 0) return [];
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var placement = new ExternalConversationPlacement(projects.Where(static item => !item.Archived && Directory.Exists(item.ProjectPath)).Select(static item => (item.Id, item.ProjectPath)), checkouts);
        return [.. listed.Select(conversation => (conversation, placement.Place(conversation.StartDirectory)))];
    }

    private static ExternalConversationSummary ToSummary(AgentExternalConversation conversation, ExternalConversationPlace place)
        => new(
            conversation.ConversationId,
            conversation.Title,
            conversation.LastChangedAt,
            OriginName(conversation.Origin),
            conversation.Entrypoint,
            conversation.IsHeld,
            conversation.HolderStatus,
            place.ProjectId,
            place.Tag,
            conversation.StartDirectory);

    // The worktree of the project a conversation ended in, when it still exists and is not the checkout of the project.
    private static string? ImportWorktree(ProjectDescriptor project, string? endDirectory)
        => ExistingFolder(endDirectory) is { } folder && GitWorktreeService.FindCheckoutRoot(folder) is { } checkout
            ? KeptWorktree(checkout, project.ProjectPath)
            : null;

    private static string? ExistingFolder(string? folder)
        => !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder) && Directory.Exists(folder) ? folder : null;

    // The title the conversation has elsewhere, made a title of a session: one line, at most 256 characters.
    private static string ImportedTitle(string title, ProjectDescriptor project)
    {
        var line = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        line = new string([.. line.Where(static character => !char.IsControl(character))]).Trim();
        if (line.Length > 256)
        {
            var cut = char.IsHighSurrogate(line[255]) ? 255 : 256;
            line = line[..cut].TrimEnd();
        }

        return SessionTitleError(line) is null ? line : project.DisplayName;
    }

    private static string OriginName(AgentExternalTurnOrigin origin)
        => origin switch
        {
            AgentExternalTurnOrigin.VsCode => "vscode",
            AgentExternalTurnOrigin.Terminal => "terminal",
            AgentExternalTurnOrigin.OtherApplication => "other",
            _ => "unknown",
        };

    // The attachment folder of a session whose journal is written last.
    private sealed class NewSessionAttachments(string folder) : IAgentSessionAttachmentStore
    {
        public ValueTask<string?> GetAttachmentDirectoryAsync(string sessionId, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(folder);
    }
}
