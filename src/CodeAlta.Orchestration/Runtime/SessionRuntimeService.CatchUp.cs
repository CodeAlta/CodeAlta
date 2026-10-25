using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Images;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    // The outcomes of a catch-up, as CatchUpExternalTurnsAsync returns them.
    internal const string CatchUpAppended = "appended";
    internal const string CatchUpCannotFollow = "cannot_follow";
    internal const string CatchUpUnchanged = "unchanged";
    internal const string CatchUpNone = "none";
    internal const string CatchUpBusy = "busy";
    internal const string CatchUpHeld = "held";
    internal const string CatchUpStale = "stale";

    // The catch-up of each session in progress: a second one, a send's included, joins it rather than reading the
    // conversation again, so that a send never starts before turns it follows are written.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _catchUps = new(StringComparer.OrdinalIgnoreCase);

    // Told the session each time a catch-up, a send's included, waits for the one in progress: what a test orders
    // itself by. Null in the application.
    internal Action<string>? CatchUpJoined { get; set; }

    /// <summary>
    /// Adds to the journal of an idle session the turns made outside CodeAlta on the conversation its provider keeps
    /// (Claude Code: in VS Code or by the CLI in a terminal), so that the timeline shows them and the next turn resumes
    /// that conversation as it is, without sending again what it already holds.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>
    /// <c>appended</c> when turns were written; <c>cannot_follow</c> when the conversation was changed in a way that
    /// cannot be followed (a notice was written instead); <c>unchanged</c> when nothing new was found; <c>none</c> when
    /// the provider keeps no such conversation, or it cannot be read; <c>busy</c> when the session is not idle (a run, a
    /// send or a compaction that took the session, a queue, a question, Remote Control, a fork or a rewind not taken up
    /// yet): nothing is read then; <c>held</c> when another program is working on the conversation now (VS Code in the
    /// middle of a turn): nothing is read either; <c>stale</c> when the session changed while the conversation was read.
    /// </returns>
    /// <remarks>
    /// The transcript is read outside the actor of the session, from the state the session recorded; what was read is
    /// written on the actor, only when the session is still idle and its state is the one read from. Each turn is
    /// written as the session writes its own turns, under a run of its own; the state recorded after them counts
    /// exactly the messages written. The turns, their states and the summary of the session are appended together.
    /// Once that append started, an idle attachment is retired, as a provider selection does, whether it succeeded or
    /// not: its process of the provider holds the conversation as it was before those turns, and its next summary
    /// would write over what was written; the next send attaches the session again, which resumes the conversation
    /// from the journal. When nothing was found, nothing is written and the attachment stays.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="SessionLeaseUnavailableException">The runtime takes leases and the lease of the session cannot be taken.</exception>
    internal Task<string> CatchUpExternalTurnsAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return AdmitAsync(() => CatchUpExternalTurnsCoreAsync(sessionId), CancellationToken.None);
    }

    // The catch-up of a work already admitted (a send).
    private async Task<string> CatchUpExternalTurnsCoreAsync(string sessionId)
    {
        // A session another process runs is written by that process alone. The lease is held from the observation of the
        // session to the end of the append and of the retirement it leads to: a probe alone would let another process take
        // the session while the conversation is read, and write after it.
        if (!TryHoldForWrite(sessionId, out var lease)) return CatchUpBusy;
        using var held = lease;
        var joined = false;
        while (true)
        {
            if (_catchUps.TryGetValue(sessionId, out var running))
            {
                CatchUpJoined?.Invoke(sessionId);
                await ((Task)running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                // What it found, it wrote. One that found the session busy or changed, or that failed, wrote nothing this
                // one can rely on: it runs once more after it, itself or joining the next one.
                var conclusive = running.IsCompletedSuccessfully && running.Result is not (CatchUpBusy or CatchUpStale);
                if (conclusive || joined) return running.IsCompletedSuccessfully ? running.Result : CatchUpUnchanged;
                joined = true;
                continue;
            }

            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_catchUps.TryAdd(sessionId, started.Task)) continue;
            // Removed before it completes: one that joined it and runs once more after it never finds it again, which
            // it would take for the next one and end with, reading nothing.
            var registration = new KeyValuePair<string, Task<string>>(sessionId, started.Task);
            string result;
            try
            {
                result = await CatchUpOnceAsync(sessionId).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _catchUps.TryRemove(registration);
                started.TrySetException(exception);
                // Observed here: who joined it does not rethrow it.
                _ = started.Task.Exception;
                throw;
            }

            _catchUps.TryRemove(registration);
            started.TrySetResult(result);
            return result;
        }
    }

    private async Task<string> CatchUpOnceAsync(string sessionId)
    {
        var actor = GetActorForWork(sessionId);
        var observed = await actor.QueryAsync<(string Status, AgentSessionState? State, string? Directory, long? Length)>(async token =>
        {
            if (await CatchUpRefusalAsync(sessionId, token).ConfigureAwait(false) is { } refusal) return (refusal, null, null, null);
            var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
            var state = await store.GetStateAsync(sessionId, token).ConfigureAwait(false);
            // A fork or a rewind is taken up by the next turn of the provider: until then the conversation it resumes is
            // not the one the state names.
            if (state is not { ProviderState: not null, ProviderBranch: null }) return (CatchUpNone, null, null, null);
            var summary = await store.GetSessionSummaryAsync(sessionId, token).ConfigureAwait(false);
            return summary?.ExecutionDirectory is { Length: > 0 } directory
                ? (CatchUpUnchanged, state, directory, await store.GetJournalLengthAsync(sessionId, token).ConfigureAwait(false))
                : (CatchUpNone, null, null, null);
        }, CancellationToken.None).ConfigureAwait(false);
        if (observed.State is not { } recorded) return observed.Status;

        AgentExternalTurns read;
        try
        {
            read = await _agentHub.ReadExternalTurnsAsync(recorded, observed.Directory!, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A conversation that cannot be read is left as it is: the next turn resumes it all the same.
            return CatchUpNone;
        }

        switch (read.Status)
        {
            case AgentExternalTurnsStatus.Unchanged:
                return CatchUpUnchanged;
            case AgentExternalTurnsStatus.Held:
                return CatchUpHeld;
            case AgentExternalTurnsStatus.None or AgentExternalTurnsStatus.Missing:
                return CatchUpNone;
        }

        Task? retirement = null;
        SessionViewDescriptor? changed = null;
        string result;
        try
        {
            result = await actor.QueryAsync(async token =>
            {
                if (await CatchUpRefusalAsync(sessionId, token).ConfigureAwait(false) is { } refusal) return refusal;
                var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
                var current = await store.GetStateAsync(sessionId, token).ConfigureAwait(false);
                // Anything written meanwhile (the prompt of a run that starts, a state) makes what was read stale.
                if (current is null || current.UpdatedAt != recorded.UpdatedAt || current.ProviderBranch is not null
                    || !string.Equals(current.ProviderState?.GetRawText(), recorded.ProviderState?.GetRawText(), StringComparison.Ordinal)
                    || await store.GetJournalLengthAsync(sessionId, token).ConfigureAwait(false) != observed.Length) return CatchUpStale;
                _entries.TryGetValue(sessionId, out var entry);
                // Only lines the session does not show were written (metadata of the CLI): the process of an attached
                // session is up to date, and the provider keeps where the read stopped for its next answer. The
                // position read up to is recorded when no attachment would write its own state over it.
                var appends = read.Status == AgentExternalTurnsStatus.CannotFollow || read.Turns.Count > 0;
                if (!appends && entry is not null) return CatchUpUnchanged;

                var session = await ResolveOwnedSessionBodyAsync(sessionId, token).ConfigureAwait(false);
                if (session is null || session.Status == SessionViewStatus.Archived) return CatchUpNone;
                var summary = await store.GetSessionSummaryAsync(sessionId, token).ConfigureAwait(false);
                if (summary is null) return CatchUpNone;

                // The images saved for the turns: removed again when the turns are not written.
                var saved = new List<string>();
                var images = new PromptImageAttachmentStore(_catalogOptions);
                var toolImages = new AgentSessionToolImages(store, sessionId);
                var appending = false;
                try
                {
                    var prepared = await AgentExternalTurnJournal.PrepareAsync(
                        summary.ProviderId,
                        current,
                        read,
                        observed.Directory,
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

                    // The summary is written with the turns: what is written of them is written whole, with the preview
                    // they give the session.
                    IReadOnlyList<AgentEvent> events = prepared.Turns == 0
                        ? prepared.Events
                        : [.. prepared.Events, FileSystemAgentSessionStore.CreateSummarySnapshotEvent(
                            summary with { Summary = prepared.Preview ?? summary.Summary, UpdatedAt = DateTimeOffset.UtcNow })];
                    appending = true;
                    await store.AppendEventsAsync(summary.ProtocolFamily, summary.ProviderKey, sessionId, events, token).ConfigureAwait(false);
                }
                catch
                {
                    DeleteSavedImages(saved);
                    throw;
                }
                finally
                {
                    // From the moment the journal may hold turns the process of the attachment does not have, that
                    // process is not used again, whether the append ended or not.
                    if (appending && appends && entry is not null)
                    {
                        retirement = _forwarding.RetireAsync(entry.Attachment);
                        if (_entries.TryRemove(sessionId, out _)) ReleaseEntryLeaseAfter(sessionId, retirement);
                        _transitions[sessionId] = retirement;
                    }
                }

                changed = session;
                return read.Status == AgentExternalTurnsStatus.CannotFollow ? CatchUpCannotFollow : appends ? CatchUpAppended : CatchUpUnchanged;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            if (retirement is not null) await EndCatchUpRetirementAsync(actor, sessionId, retirement).ConfigureAwait(false);
            throw;
        }

        if (changed is not null)
        {
            await _agentSessionCatalog.NotifySessionUpdatedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } updated) PublishSessionCatalogEvent(updated);
        }

        if (retirement is not null) await EndCatchUpRetirementAsync(actor, sessionId, retirement).ConfigureAwait(false);
        return result;
    }

    private async Task EndCatchUpRetirementAsync(Actors.SessionActor actor, string sessionId, Task retirement)
    {
        PublishSessionLifecycleEvent(sessionId);
        try
        {
            await retirement.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // What was written stays written; the attachment that did not end cleanly is not used again.
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
    }

    // Best effort: an image that cannot be removed is only a file too many.
    private static void DeleteSavedImages(List<string> paths)
    {
        foreach (var path in paths)
        {
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    // Before a send: a catch-up that fails never stops the prompt, which resumes the conversation all the same (the turns
    // made elsewhere are then only missing from the timeline). A runtime that closes stops the send as it would anyway.
    private async Task CatchUpBeforeSendAsync(SessionViewDescriptor session)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId) || session.Status == SessionViewStatus.Archived) return;
        try
        {
            await CatchUpExternalTurnsCoreAsync(session.SessionId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (ObjectDisposedException or OperationCanceledException))
        {
        }
    }

    // Why the turns made outside CodeAlta cannot be written now, or null. Runs on the actor of the session: the guards of
    // a provider selection; a send or a compaction that took the session, whose run may not be observed yet; Remote
    // Control, which the retirement of the attachment would end; and the work of the provider in the background, which
    // ends with its process.
    private async Task<string?> CatchUpRefusalAsync(string sessionId, CancellationToken token)
    {
        if (_transitions.ContainsKey(sessionId)) return CatchUpBusy;
        if (_entries.TryGetValue(sessionId, out var entry) && (entry.HasActiveRun || entry.QueueDrainInProgress || entry.OwnedQueue is not null
            || entry.Attachment.IsRetiring || entry.Attachment.HasWorkInProgress
            || entry.RemoteControl.Status is AgentRemoteControlStatus.Connecting or AgentRemoteControlStatus.Connected
            || entry.BackgroundTasks.Any(static task => task.Outcome is null))) return CatchUpBusy;
        if ((await Permissions.ListAsync().ConfigureAwait(false)).Any(permission => permission.Handle.SessionId == sessionId)
            || (await Permissions.ListOwnedUserInputsAsync(sessionId, token).ConfigureAwait(false)).Entries.Count != 0
            || (await ConversationHolds.ListAsync(sessionId, token).ConfigureAwait(false)).Count != 0) return CatchUpBusy;
        return null;
    }

    private static string ImageExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".png",
        };
}
