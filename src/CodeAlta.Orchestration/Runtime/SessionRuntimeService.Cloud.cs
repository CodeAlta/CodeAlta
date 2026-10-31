using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Images;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Thrown when something would start a local conversation for a session that runs in the cloud: a prompt, a queue, a
/// compaction, Remote Control. The cloud session is written to from its tab, or brought back first.
/// </summary>
public sealed class SessionInCloudException : InvalidOperationException
{
    /// <summary>Initializes the exception for a session.</summary>
    /// <param name="sessionId">The session.</param>
    public SessionInCloudException(string sessionId)
        : base(SessionRuntimeService.InCloudMessage)
    {
        SessionId = sessionId;
    }

    /// <summary>Gets the session.</summary>
    public string SessionId { get; }
}

/// <summary>How a cloud session was started.</summary>
/// <param name="Status">
/// <c>created</c> (<paramref name="Session"/> holds it); <c>unrecorded</c> when the cloud session was created but CodeAlta
/// could not record it (its id and address are given, so that it is not started twice); <c>failed</c>;
/// <c>canceled</c>; <c>terminal_unavailable</c>; <c>needs_claude_ai</c>; <c>unavailable</c> when the provider cannot
/// run cloud sessions; <c>provider_outside_space</c>.
/// </param>
/// <param name="Session">The session, when created.</param>
/// <param name="CloudSessionId">The cloud session, when Claude Code created it.</param>
/// <param name="Url">Its address, when created.</param>
/// <param name="Message">What Claude Code said of a failure, or null.</param>
/// <param name="TerminalId">The terminal kept to be looked at after a failure, or null.</param>
public sealed record CloudSessionStartResult(string Status, SessionViewDescriptor? Session = null, string? CloudSessionId = null, string? Url = null,
    string? Message = null, string? TerminalId = null);

/// <summary>How a message was sent to a cloud session, or why it was not.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>not_cloud</c> when the session does not run in the cloud; <c>busy</c> while it is being brought back;
/// <c>archived</c>, <c>not_found</c>, <c>needs_claude_ai</c>, <c>policy</c>, <c>failed</c> or <c>unavailable</c> as
/// Claude Code answered.
/// </param>
/// <param name="Message">What Claude Code said, or null.</param>
public sealed record CloudSessionSendResult(string Status, string? Message = null);

/// <summary>How a cloud session was brought back to this computer, or why it was not.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>terminal</c> when the user brings it back in a terminal: <paramref name="Command"/> is to be typed there;
/// <c>not_cloud</c>; <c>busy</c> (another bring-back, a send); <c>uncommitted_changes</c> when the folder has
/// changes of tracked files (<paramref name="ChangedFiles"/>); <c>not_found</c> when the conversation brought back in a
/// terminal is not there; <c>failed</c>; <c>unavailable</c>.
/// </param>
/// <param name="Message">Why it failed, in the words of Claude Code when it said something, or null.</param>
/// <param name="ChangedFiles">The number of tracked files changed in the folder, for <c>uncommitted_changes</c>.</param>
/// <param name="Command">For a bring-back in a terminal: the command to type there.</param>
/// <param name="ConfigDirectory">For a bring-back in a terminal: the account folder the command is run with, or null.</param>
/// <param name="Folder">The folder the session is brought into.</param>
public sealed record CloudSessionContinueResult(string Status, string? Message = null, int ChangedFiles = 0, string? Command = null, string? ConfigDirectory = null,
    string? Folder = null);

/// <summary>What a session holds of the cloud session it runs as, for its tab.</summary>
/// <param name="Id">The cloud session.</param>
/// <param name="Url">Its address.</param>
/// <param name="Title">Its title.</param>
/// <param name="Task">What it was started to do.</param>
/// <param name="Branch">The branch of the project folder when it started.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="Note">What Claude Code said of where it started.</param>
/// <param name="Sent">The messages sent to it from CodeAlta.</param>
/// <param name="Continuing">Whether it is being brought back, or a bring-back was interrupted.</param>
/// <param name="ContinuedAt">When it was brought back; null while it runs in the cloud.</param>
/// <param name="ProjectId">The project of the session, or null.</param>
public sealed record CloudSessionDetails(string Id, string? Url, string? Title, string? Task, string? Branch, DateTimeOffset? StartedAt, string? Note,
    IReadOnlyList<SessionCloudMessage> Sent, bool Continuing, DateTimeOffset? ContinuedAt, string? ProjectId = null);

public sealed partial class SessionRuntimeService
{
    /// <summary>What a session in the cloud answers to what would start a local conversation of it.</summary>
    public const string InCloudMessage = "This session runs in the cloud: write to it from its tab in CodeAlta Desktop, or continue it here first.";

    /// <summary>The code of a command refused because its session runs in the cloud.</summary>
    public const string InCloudCode = "in_cloud";

    /// <summary>Finds the refusal of a session that runs in the cloud in an exception or the exceptions it holds.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The refusal, or null.</returns>
    public static SessionInCloudException? FindInCloud(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 8; depth++)
        {
            if (exception is SessionInCloudException inCloud) return inCloud;
            if (exception is AggregateException { InnerExceptions.Count: 1 } aggregate) exception = aggregate.InnerExceptions[0];
            else exception = exception.InnerException;
        }

        return null;
    }

    // A cloud session is started, sent to or brought back one request at a time per session.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _cloudWork = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Starts a cloud session of a project with its provider (Claude Code: <c>claude --cloud</c> in a terminal of the host),
    /// then records it as a session of the project that runs in the cloud: a session with no turn, whose cloud state names
    /// the cloud session. The session is written whole before it is listed, and never attached to its provider here.
    /// </summary>
    /// <param name="project">The project: the cloud session starts from its repository.</param>
    /// <param name="provider">The provider, which must support cloud sessions.</param>
    /// <param name="spaceId">The space of the session, or null.</param>
    /// <param name="title">The title given, or null for the one of the cloud session.</param>
    /// <param name="task">What the cloud session is to do.</param>
    /// <param name="terminal">The terminal of the host, or null when it has none.</param>
    /// <param name="onShown">Called when the terminal is shown to the user, or null.</param>
    /// <param name="cancellationToken">Ends the start; a cloud session Claude Code created before is still recorded.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The task is empty.</exception>
    public async Task<CloudSessionStartResult> CreateCloudSessionAsync(ProjectDescriptor project, ModelProviderDescriptor provider, string? spaceId, string? title,
        string task, IAgentInteractiveTerminal? terminal, Action? onShown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        var space = NormalizeSpaceId(spaceId);
        return await AdmitAsync(async () =>
        {
            if (!IsProviderInSpace(provider.ProviderId.Value, await ResolveSpaceIdAsync(space, project.Id, CancellationToken.None).ConfigureAwait(false)))
                return new CloudSessionStartResult(ProviderOutsideSpace);
            if (await _agentHub.GetCloudSessionsAsync(provider.ProviderId, CancellationToken.None).ConfigureAwait(false) is not { } found)
                return new CloudSessionStartResult("unavailable", Message: "This provider cannot run sessions in the cloud.");
            var (cloud, runtime) = found;
            var branch = await ReadBranchAsync(project.ProjectPath).ConfigureAwait(false);
            var started = await cloud.StartCloudSessionAsync(new AgentCloudStartRequest(project.ProjectPath, task.Trim(), project.DisplayName), terminal, onShown,
                cancellationToken).ConfigureAwait(false);
            if (started.Status != "created" || started.SessionId is not { } cloudSessionId)
            {
                var status = started.Status is "failed" && NeedsClaudeAi(started.Message) ? "needs_claude_ai" : started.Status;
                return new CloudSessionStartResult(status, Message: started.Message, TerminalId: started.TerminalId);
            }

            try
            {
                var session = await RecordCloudSessionAsync(project, provider, runtime, space, title, task.Trim(), branch, started).ConfigureAwait(false);
                return new CloudSessionStartResult("created", session, cloudSessionId, started.Url);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The cloud session runs: it is given, so that it is opened or brought here rather than started again.
                return new CloudSessionStartResult("unrecorded", null, cloudSessionId, started.Url, exception.Message);
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a cloud session started elsewhere (on claude.ai, in the Claude app) as a session of a project that runs in
    /// the cloud, from the link or the identifier the user pasted, so that it is brought back with
    /// <see cref="ContinueCloudSessionHereAsync"/>. Nothing is read from the cloud session: its title is the one the user
    /// gives, else the name of the project, until it is brought back.
    /// </summary>
    /// <param name="project">The project it is to be brought into.</param>
    /// <param name="provider">The provider, signed in to the account of the cloud session.</param>
    /// <param name="spaceId">The space of the session, or null.</param>
    /// <param name="title">The title given, or null.</param>
    /// <param name="link">The link or the identifier the user pasted.</param>
    /// <returns>
    /// <c>created</c> with the session; <c>existing</c> with the session that holds it already; <c>invalid_link</c>;
    /// <c>unavailable</c>; <c>provider_outside_space</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public async Task<CloudSessionStartResult> AdoptCloudSessionAsync(ProjectDescriptor project, ModelProviderDescriptor provider, string? spaceId, string? title, string link)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(link);
        var space = NormalizeSpaceId(spaceId);
        return await AdmitAsync(async () =>
        {
            if (!IsProviderInSpace(provider.ProviderId.Value, await ResolveSpaceIdAsync(space, project.Id, CancellationToken.None).ConfigureAwait(false)))
                return new CloudSessionStartResult(ProviderOutsideSpace);
            if (await _agentHub.GetCloudSessionsAsync(provider.ProviderId, CancellationToken.None).ConfigureAwait(false) is not { } found)
                return new CloudSessionStartResult("unavailable", Message: "This provider cannot run sessions in the cloud.");
            if (found.Cloud.FindCloudSession(link) is not { } cloud) return new CloudSessionStartResult("invalid_link");
            await foreach (var listed in ListRecoverableSessionsAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (listed.Cloud is { } known && string.Equals(known.Id, cloud.Id, StringComparison.Ordinal))
                    return new CloudSessionStartResult("existing", listed, cloud.Id, cloud.Url);
            }

            var session = await RecordCloudSessionAsync(project, provider, found.Runtime, space, title, task: null, await ReadBranchAsync(project.ProjectPath).ConfigureAwait(false),
                new AgentCloudStart("created", cloud.Id, cloud.Url)).ConfigureAwait(false);
            return new CloudSessionStartResult("created", session, cloud.Id, cloud.Url);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    // The session of a cloud session, written whole with its cloud state, then listed.
    private async Task<SessionViewDescriptor> RecordCloudSessionAsync(ProjectDescriptor project, ModelProviderDescriptor provider,
        ModelProviderRuntimeDescriptor runtime, string? spaceId, string? title, string? task, string? branch, AgentCloudStart started)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.CreateVersion7().ToString();
        var named = !string.IsNullOrWhiteSpace(title) && SessionTitleError(title.Trim()) is null ? title.Trim() : null;
        var sessionTitle = named ?? ImportedTitle(started.Title ?? task ?? project.DisplayName, project);
        var target = new SessionViewDescriptor
        {
            SessionId = sessionId,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = provider.ProviderId.Value,
            ProviderKey = provider.ProviderId.Value,
            ProjectRef = project.Id,
            SpaceId = spaceId,
            WorkingDirectory = project.ProjectPath,
            Title = sessionTitle,
            TitleSource = AgentSessionTitleSource.User,
            Status = SessionViewStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            LastActiveAt = now,
            StartedAt = now,
            ModelId = provider.DefaultModelId,
            ReasoningEffort = provider.DefaultReasoningEffort,
            LatestSummary = "Runs in the cloud.",
        };
        var state = new AgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = runtime.ProtocolFamily,
            ProviderKey = runtime.ProviderKey,
            UpdatedAt = now,
        };
        var summary = new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = provider.ProviderId,
            ProtocolFamily = runtime.ProtocolFamily,
            ProviderKey = runtime.ProviderKey,
            ModelId = provider.DefaultModelId,
            ReasoningEffort = provider.DefaultReasoningEffort,
            WorkingDirectory = project.ProjectPath,
            Title = sessionTitle,
            TitleSource = AgentSessionTitleSource.User,
            Summary = target.LatestSummary,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var local = new SessionViewLocalState
        {
            ProviderKey = provider.ProviderId.Value,
            ModelId = provider.DefaultModelId,
            ReasoningEffort = provider.DefaultReasoningEffort,
            PermissionMode = NormalizeOptionalText(provider.DefaultPermissionMode),
            Cloud = new SessionCloudState
            {
                Id = started.SessionId!,
                Url = started.Url,
                Title = started.Title,
                Task = task,
                Branch = branch,
                StartedAt = now,
                Note = started.Note,
            },
        };
        await _sessionViewCatalog.JournalStore.ImportAsync(target, summary, state, [], local, CancellationToken.None).ConfigureAwait(false);
        await _agentSessionCatalog.NotifySessionCreatedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        var listed = await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false) ?? target;
        PublishSessionCatalogEvent(listed);
        PublishSessionLifecycleEvent(sessionId);
        return listed;
    }

    /// <summary>Reads what a session holds of the cloud session it runs as, or ran as.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    /// <returns>The details, or null for a session that never ran in the cloud.</returns>
    public async Task<CloudSessionDetails?> GetCloudSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var session = await ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        var local = await ReadLatestLocalStateAsync(sessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false);
        return local?.Cloud is { Id.Length: > 0 } cloud
            ? new CloudSessionDetails(cloud.Id, cloud.Url, cloud.Title, cloud.Task, cloud.Branch, cloud.StartedAt, cloud.Note, cloud.Sent ?? [],
                cloud.Continuing is not null, cloud.ContinuedAt, session.ProjectRef)
            : null;
    }

    /// <summary>
    /// Sends a message to the cloud session a session runs as (Claude Code: <c>claude -p --cloud &lt;id&gt;</c>), and
    /// records it as sent. It is not a turn of the session: the turns of the cloud session are added when it is brought back.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="message">The message.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    public Task<CloudSessionSendResult> SendToCloudSessionAsync(string sessionId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return AdmitAsync(async () =>
        {
            if (!_cloudWork.TryAdd(sessionId, 0)) return new CloudSessionSendResult("busy");
            try
            {
                var session = await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                if (session is null) return new CloudSessionSendResult("not_cloud");
                var local = await ReadLatestLocalStateAsync(sessionId, session.CreatedAt, CancellationToken.None).ConfigureAwait(false);
                if (local?.Cloud is not { Id.Length: > 0, ContinuedAt: null } cloud) return new CloudSessionSendResult("not_cloud");
                if (cloud.Continuing is not null) return new CloudSessionSendResult("busy");
                if (await _agentHub.GetCloudSessionsAsync(new ModelProviderId(session.ProviderKey ?? session.ProviderId), CancellationToken.None).ConfigureAwait(false) is not { } found)
                    return new CloudSessionSendResult("unavailable", "The provider of this session cannot reach its cloud session.");
                var sent = await found.Cloud.SendToCloudSessionAsync(cloud.Id, message.Trim(), session.WorkingDirectory ?? Environment.CurrentDirectory,
                    CancellationToken.None).ConfigureAwait(false);
                if (sent.Status != "ok") return new CloudSessionSendResult(sent.Status, sent.Message);
                await UpdateCloudStateAsync(session, current =>
                {
                    var messages = current.Sent ?? [];
                    messages.Add(new SessionCloudMessage(message.Trim(), DateTimeOffset.UtcNow));
                    if (messages.Count > SessionCloudState.MaxSentMessages) messages.RemoveRange(0, messages.Count - SessionCloudState.MaxSentMessages);
                    current.Sent = messages;
                }).ConfigureAwait(false);
                return new CloudSessionSendResult("ok");
            }
            finally
            {
                _cloudWork.TryRemove(sessionId, out _);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Brings the cloud session a session runs as back to this computer, into the session itself: Claude Code writes its
    /// conversation in a new conversation of its own in the folder (and checks out its branch there), the turns made in the
    /// cloud are added to the session, marked as made in the cloud, and the next prompt continues that conversation here.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="worktree">
    /// A worktree of the project made for it, to bring it into instead of the folder of the project, or null. The session
    /// works there from then on.
    /// </param>
    /// <param name="fromTerminal">
    /// Whether the user brings it back in a terminal: without it, a command is run that makes no model call. With it, the
    /// first call answers the command to type, and a second one, once it ran, reads what it wrote.
    /// </param>
    /// <returns>The outcome.</returns>
    /// <remarks>
    /// The return is reserved first (its conversation and folder are journaled), then Claude Code runs outside the actor
    /// of the session, then its turns are read and written with the state that follows them in one append. A failure
    /// clears the reservation; a return that was interrupted is taken up by the next one, which reads the conversation when
    /// it was written already rather than running Claude Code again.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public Task<CloudSessionContinueResult> ContinueCloudSessionHereAsync(string sessionId, string? worktree, bool fromTerminal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return AdmitAsync(async () =>
        {
            if (!_cloudWork.TryAdd(sessionId, 0)) return new CloudSessionContinueResult("busy");
            try
            {
                return await ContinueCloudSessionCoreAsync(sessionId, NormalizeOptionalText(worktree), fromTerminal).ConfigureAwait(false);
            }
            finally
            {
                _cloudWork.TryRemove(sessionId, out _);
            }
        }, CancellationToken.None);
    }

    private async Task<CloudSessionContinueResult> ContinueCloudSessionCoreAsync(string sessionId, string? worktree, bool fromTerminal)
    {
        if (!TryHoldForWrite(sessionId, out var lease)) return new CloudSessionContinueResult("busy");
        using var held = lease;
        var session = await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (session is null || session.Status == SessionViewStatus.Archived) return new CloudSessionContinueResult("not_cloud");
        var local = await ReadLatestLocalStateAsync(sessionId, session.CreatedAt, CancellationToken.None).ConfigureAwait(false);
        if (local?.Cloud is not { Id.Length: > 0, ContinuedAt: null } cloud) return new CloudSessionContinueResult("not_cloud");
        var providerId = new ModelProviderId(session.ProviderKey ?? session.ProviderId);
        if (await _agentHub.GetCloudSessionsAsync(providerId, CancellationToken.None).ConfigureAwait(false) is not { } found)
            return new CloudSessionContinueResult("unavailable", "The provider of this session cannot reach its cloud session.");
        var (provider, runtime) = found;

        // The return in progress is taken up as it was reserved; a new one is reserved with its folder and conversation.
        var reserved = cloud.Continuing;
        if (reserved is null || (worktree is not null && !string.Equals(reserved.Folder, worktree, StringComparison.OrdinalIgnoreCase)) || reserved.FromTerminal != fromTerminal)
        {
            var folder = worktree ?? session.WorktreeDirectory ?? session.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new CloudSessionContinueResult("failed", "The folder of the session is not there.");
            reserved = new SessionCloudContinuing(Guid.NewGuid().ToString(), folder, worktree is not null, fromTerminal);
        }

        if (!fromTerminal || !TranscriptWritten(reserved))
        {
            // Teleport fails without a word on a change of a tracked file: it is said here first.
            var changed = await CountTrackedChangesAsync(reserved.Folder).ConfigureAwait(false);
            if (changed > 0) return new CloudSessionContinueResult("uncommitted_changes", ChangedFiles: changed, Folder: reserved.Folder);
        }

        await UpdateCloudStateAsync(session, current => current.Continuing = reserved).ConfigureAwait(false);
        var succeeded = false;
        try
        {
            if (reserved.Worktree && !string.Equals(session.WorktreeDirectory, reserved.Folder, StringComparison.OrdinalIgnoreCase))
            {
                // The session works in the worktree made for it from now on: Claude Code keeps the conversation where it runs.
                await _sessionViewCatalog.JournalStore.MoveSessionAsync(session,
                    new SessionViewMove(session.ProjectRef ?? string.Empty, session.WorkingDirectory ?? reserved.Folder, reserved.Folder, null, null)
                    { ProviderFamily = runtime.ProtocolFamily }, CancellationToken.None).ConfigureAwait(false);
                session = await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false) ?? session;
            }

            if (fromTerminal && !TranscriptWritten(reserved))
            {
                var (command, configDirectory) = provider.BringBackCommand(cloud.Id, reserved.ConversationId);
                succeeded = true; // The reservation stays: the user runs the command, then asks again.
                return new CloudSessionContinueResult("terminal", Command: command, ConfigDirectory: configDirectory, Folder: reserved.Folder);
            }

            if (!fromTerminal && !TranscriptWritten(reserved))
            {
                var returned = await provider.BringCloudSessionBackAsync(cloud.Id, reserved.ConversationId, reserved.Folder, CancellationToken.None).ConfigureAwait(false);
                if (returned.Status != "ok") return new CloudSessionContinueResult(returned.Status, returned.Message, Folder: reserved.Folder);
            }

            AgentExternalTurns read;
            try
            {
                read = provider.ReadBroughtBackTurns(reserved.ConversationId, reserved.Folder, 0, 0, reserved.FromTerminal, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return new CloudSessionContinueResult("failed", exception.Message, Folder: reserved.Folder);
            }

            if (read.Status != AgentExternalTurnsStatus.Read)
                return new CloudSessionContinueResult(fromTerminal ? "not_found" : "failed", "The conversation of the cloud session was not written on this computer.",
                    Folder: reserved.Folder);
            await WriteBroughtBackTurnsAsync(session, providerId, runtime, reserved, read).ConfigureAwait(false);
            succeeded = true;
            return new CloudSessionContinueResult("ok", Folder: reserved.Folder);
        }
        finally
        {
            if (!succeeded)
            {
                try { await UpdateCloudStateAsync(session, current => current.Continuing = null).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or InvalidOperationException) { }
            }
        }

        bool TranscriptWritten(SessionCloudContinuing continuing)
            => provider.ReadBroughtBackTurns(continuing.ConversationId, continuing.Folder, 0, 0, continuing.FromTerminal, CancellationToken.None).Status
               == AgentExternalTurnsStatus.Read;
    }

    // The turns brought back and the state after them, then the cloud state that says it is back, on the actor.
    private async Task WriteBroughtBackTurnsAsync(SessionViewDescriptor session, ModelProviderId providerId, ModelProviderRuntimeDescriptor runtime,
        SessionCloudContinuing reserved, AgentExternalTurns read)
    {
        var sessionId = session.SessionId;
        await GetActorForWork(sessionId).QueryAsync(async token =>
        {
            var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
            var current = await store.GetStateAsync(sessionId, token).ConfigureAwait(false)
                          ?? new AgentSessionState { SessionId = sessionId, ProtocolFamily = runtime.ProtocolFamily, ProviderKey = runtime.ProviderKey, UpdatedAt = DateTimeOffset.UtcNow };
            var summary = await store.GetSessionSummaryAsync(sessionId, token).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("The session has no summary.");
            var basis = current with { ProviderSessionId = reserved.ConversationId, ProtocolFamily = runtime.ProtocolFamily, ProviderKey = runtime.ProviderKey };
            var saved = new List<string>();
            var images = new PromptImageAttachmentStore(_catalogOptions);
            var toolImages = new AgentSessionToolImages(store, sessionId);
            try
            {
                var prepared = await AgentExternalTurnJournal.PrepareAsync(
                    providerId,
                    basis,
                    read,
                    reserved.Folder,
                    async (data, imageToken) =>
                    {
                        byte[] bytes;
                        try { bytes = Convert.FromBase64String(data.Base64Data); }
                        catch (FormatException) { return new AgentInputItem.Text("[An image whose data is not valid.]"); }
                        var stored = await images.SaveAsync(session,
                            [new PromptImageAttachment(Guid.NewGuid().ToString("N"), "image", bytes, data.MediaType, ImageExtension(data.MediaType))],
                            imageToken).ConfigureAwait(false);
                        saved.AddRange(stored.Select(static image => image.Path));
                        return new AgentInputItem.LocalImage(stored[0].Path, stored[0].Title, stored[0].MediaType);
                    },
                    async (toolResult, resultToken) =>
                    {
                        var withImages = await toolImages.PrepareAsync(toolResult, null, resultToken).ConfigureAwait(false);
                        saved.AddRange(withImages.Items.OfType<AgentToolResultItem.LocalImage>().Select(static image => image.Path));
                        return withImages;
                    },
                    DateTimeOffset.UtcNow,
                    token).ConfigureAwait(false);
                IReadOnlyList<AgentEvent> events = [.. prepared.Events, FileSystemAgentSessionStore.CreateSummarySnapshotEvent(
                    summary with { Summary = prepared.Preview ?? summary.Summary, UpdatedAt = DateTimeOffset.UtcNow })];
                await store.AppendEventsAsync(summary.ProtocolFamily, summary.ProviderKey, sessionId, events, token).ConfigureAwait(false);
            }
            catch
            {
                DeleteSavedImages(saved);
                throw;
            }

            var local = await ReadLatestLocalStateAsync(sessionId, session.CreatedAt, token).ConfigureAwait(false) ?? new SessionViewLocalState();
            if (local.Cloud is { } cloud)
            {
                cloud = cloud.Copy();
                cloud.Continuing = null;
                cloud.ContinuedAt = DateTimeOffset.UtcNow;
                local.Cloud = cloud;
                await _sessionViewCatalog.JournalStore.AppendStateAsync(session, local, token).ConfigureAwait(false);
            }

            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        await _agentSessionCatalog.NotifySessionUpdatedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } updated) PublishSessionCatalogEvent(updated);
        PublishSessionLifecycleEvent(sessionId);
    }

    // Changes the cloud state of a session on its actor, from its last state.
    private async Task UpdateCloudStateAsync(SessionViewDescriptor session, Action<SessionCloudState> change)
    {
        await GetActorForWork(session.SessionId).QueryAsync(async token =>
        {
            var local = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, token).ConfigureAwait(false);
            if (local?.Cloud is not { } cloud) return false;
            cloud = cloud.Copy();
            change(cloud);
            local.Cloud = cloud;
            await _sessionViewCatalog.JournalStore.AppendStateAsync(session, local, token).ConfigureAwait(false);
            session.Cloud = cloud.ToListing();
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        await _agentSessionCatalog.NotifySessionUpdatedAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
        if (await ResolveOwnedSessionBodyAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false) is { } updated) PublishSessionCatalogEvent(updated);
    }

    /// <summary>Gets whether a session runs in the cloud now: nothing starts a local conversation of it.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>True while it runs in the cloud, being brought back included.</returns>
    internal async Task<bool> IsSessionInCloudAsync(string sessionId)
    {
        // An attached session has a conversation here: a session in the cloud is never attached.
        if (string.IsNullOrWhiteSpace(sessionId) || _entries.ContainsKey(sessionId)) return false;
        return await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } session
               && await RunsInCloudAsync(session, CancellationToken.None).ConfigureAwait(false);
    }

    // Whether a session runs in the cloud now, so that nothing attaches it to its provider: its descriptor says so when it
    // was resolved, and its last state decides otherwise. Read on the actor of the session.
    private async Task<bool> RunsInCloudAsync(SessionViewDescriptor session, CancellationToken token)
    {
        if (session.Cloud is { Continued: false }) return true;
        if (string.IsNullOrWhiteSpace(session.SessionId)) return false;
        lock (_identityGate)
        {
            if (_newSessionIds.Contains(session.SessionId)) return false;
        }

        var local = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, token).ConfigureAwait(false);
        return local?.Cloud is { Id.Length: > 0, ContinuedAt: null };
    }

    // The tracked files changed in a folder (staged or not); untracked files are left out: they do not stop Claude Code.
    private static async Task<int> CountTrackedChangesAsync(string folder)
    {
        try
        {
            var run = await GitWorktreeService.RunGitAsync(folder, ["status", "--porcelain", "--untracked-files=no"], TimeSpan.FromSeconds(30), CancellationToken.None)
                .ConfigureAwait(false);
            return run.ExitCode == 0 ? run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length : 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    // The branch the folder of the project is on, or null when it is not known.
    private static async Task<string?> ReadBranchAsync(string folder)
    {
        try
        {
            var run = await GitWorktreeService.RunGitAsync(folder, ["rev-parse", "--abbrev-ref", "HEAD"], TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            var branch = run.Output.Trim();
            return run.ExitCode == 0 && branch.Length is > 0 and <= 200 && branch != "HEAD" ? branch : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool NeedsClaudeAi(string? message)
        => message is not null && (message.Contains("claude.ai", StringComparison.OrdinalIgnoreCase) || message.Contains("Anthropic account", StringComparison.OrdinalIgnoreCase)
                                   || message.Contains("organization UUID", StringComparison.OrdinalIgnoreCase));
}
