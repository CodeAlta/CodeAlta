using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    // The outcomes of a description of the conversation of a session, as DescribeSharedConversationAsync returns them.
    internal const string SharedConversationOk = "ok";
    internal const string SharedConversationUnavailable = "unavailable";
    internal const string SharedConversationUnsupported = "unsupported";
    internal const string SharedConversationNone = "none";

    // The outcomes of a release, as ReleaseSharedConversationAsync returns them.
    internal const string ReleaseReleased = "released";
    internal const string ReleaseRunning = "running";
    internal const string ReleaseBackgroundTasks = "background_tasks";
    internal const string ReleaseRemoteControl = "remote_control";
    internal const string ReleaseInteractionPending = "interaction_pending";
    internal const string ReleaseUnconfirmed = "release_unconfirmed";

    /// <summary>
    /// Describes the conversation the provider of a session keeps of it where other programs continue it (Claude Code:
    /// the conversation of the CLI, which VS Code and a terminal resume), from the state the session recorded.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>
    /// <c>ok</c> with the conversation; <c>unavailable</c> when the session is unknown or archived; <c>unsupported</c> when
    /// its provider keeps no such conversation; <c>none</c> when the session has none yet (no turn of the provider, or a
    /// fork or a rewind that its next turn takes up).
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    internal Task<(string Status, AgentSharedConversation? Conversation)> DescribeSharedConversationAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return AdmitAsync(async () =>
        {
            var observed = await GetActorForWork(sessionId).QueryAsync<(string Status, AgentSessionState? State, string? Directory)>(async token =>
            {
                var session = await ResolveOwnedSessionBodyAsync(sessionId, token).ConfigureAwait(false);
                if (session is null || session.Status == SessionViewStatus.Archived) return (SharedConversationUnavailable, null, null);
                var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
                var state = await store.GetStateAsync(sessionId, token).ConfigureAwait(false);
                var summary = await store.GetSessionSummaryAsync(sessionId, token).ConfigureAwait(false);
                var directory = summary?.ExecutionDirectory is { Length: > 0 } execution ? execution : session.WorkingDirectory;
                return state is { ProviderState: not null } && directory is { Length: > 0 }
                    ? (SharedConversationOk, state, directory)
                    : (_agentHub.SelectionProviders.Any(provider => provider.ProviderId.Value == session.ResolvedProviderKey && provider.SharesConversations)
                        ? SharedConversationNone : SharedConversationUnsupported, null, null);
            }, CancellationToken.None).ConfigureAwait(false);
            if (observed.State is not { } state) return (observed.Status, (AgentSharedConversation?)null);

            var (shares, conversation) = await _agentHub.DescribeSharedConversationAsync(state, observed.Directory!, CancellationToken.None).ConfigureAwait(false);
            return !shares ? (SharedConversationUnsupported, null)
                : conversation is null ? (SharedConversationNone, null)
                : (SharedConversationOk, conversation);
        }, CancellationToken.None);
    }

    /// <summary>
    /// Releases the conversation of an idle session for another program (VS Code, the CLI in a terminal): the process of
    /// the provider its attachment keeps after a turn holds the conversation, and another program would see it held for
    /// as long as that process waits for the next turn (ten minutes for Claude Code). The attachment is retired, as a
    /// provider selection retires it, which closes that process; the next send attaches the session again and resumes
    /// the conversation as the other program left it.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>
    /// <c>released</c> when no process of CodeAlta keeps the conversation any more (also when none did); <c>running</c>
    /// while a run, a send, a compaction or a queue of the session works; <c>background_tasks</c> while tasks of the
    /// provider run in the background (they end with its process); <c>remote_control</c> while Remote Control connects
    /// or is connected (it ends with the process too); <c>interaction_pending</c> while a question of the session waits
    /// for an answer; <c>release_unconfirmed</c> when the attachment did not end cleanly (it is not used again).
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    internal Task<string> ReleaseSharedConversationAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return AdmitAsync(async () =>
        {
            var actor = GetActorForWork(sessionId);
            Task? retirement = null;
            var result = await actor.QueryAsync(async token =>
            {
                if (await ReleaseRefusalAsync(sessionId, token).ConfigureAwait(false) is { } refusal) return refusal;
                if (!_entries.TryGetValue(sessionId, out var entry)) return ReleaseReleased;
                retirement = _forwarding.RetireAsync(entry.Attachment);
                if (_entries.TryRemove(sessionId, out _)) ReleaseEntryLeaseAfter(sessionId, retirement);
                _transitions[sessionId] = retirement;
                return ReleaseReleased;
            }, CancellationToken.None).ConfigureAwait(false);
            if (retirement is null) return result;

            PublishSessionLifecycleEvent(sessionId);
            try
            {
                await retirement.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The attachment that did not end cleanly is not used again; its process may still be there.
                result = ReleaseUnconfirmed;
            }
            finally
            {
                await actor.QueryAsync(_ =>
                {
                    if (_transitions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, retirement))
                        _transitions.TryRemove(sessionId, out var removed);
                    return ValueTask.FromResult(true);
                }, CancellationToken.None).ConfigureAwait(false);
                PublishSessionLifecycleEvent(sessionId);
            }

            return result;
        }, CancellationToken.None);
    }

    // Why the conversation of a session cannot be released now, or null. Runs on the actor of the session: the guards of
    // a catch-up, each with the reason the user is told.
    private async Task<string?> ReleaseRefusalAsync(string sessionId, CancellationToken token)
    {
        if (_transitions.ContainsKey(sessionId)) return ReleaseRunning;
        if (_entries.TryGetValue(sessionId, out var entry))
        {
            if (entry.HasActiveRun || entry.QueueDrainInProgress || entry.OwnedQueue is not null
                || entry.Attachment.IsRetiring || entry.Attachment.HasWorkInProgress) return ReleaseRunning;
            if (entry.RemoteControl.Status is AgentRemoteControlStatus.Connecting or AgentRemoteControlStatus.Connected) return ReleaseRemoteControl;
            if (entry.BackgroundTasks.Any(static task => task.Outcome is null)) return ReleaseBackgroundTasks;
        }

        if ((await Permissions.ListAsync().ConfigureAwait(false)).Any(permission => permission.Handle.SessionId == sessionId)
            || (await Permissions.ListOwnedUserInputsAsync(sessionId, token).ConfigureAwait(false)).Entries.Count != 0
            || (await ConversationHolds.ListAsync(sessionId, token).ConfigureAwait(false)).Count != 0) return ReleaseInteractionPending;
        return null;
    }
}
