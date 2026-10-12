using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What a fork of a session is created with.</summary>
public sealed record SessionForkRequest
{
    /// <summary>Gets the session to fork.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the run of the turn the fork stops before; null to keep every turn that is complete.</summary>
    public string? BeforeRunId { get; init; }

    /// <summary>Gets the title of the fork; null for the title of the session followed by "(fork)".</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the session the fork reports to as its sub-agent; null for none, which keeps the instructions of the fork
    /// those of the session it comes from.
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>Gets who creates the fork when it is not the user.</summary>
    public AltaActorProvenance? CreatedBy { get; init; }

    /// <summary>Gets a git worktree of the project the fork works in; null for the folder the session works in.</summary>
    public string? WorktreeDirectory { get; init; }

    /// <summary>Gets a value indicating whether the fork works in the folder of its project, also when the session works in a worktree.</summary>
    public bool LeaveWorktree { get; init; }
}

/// <summary>What a rewind of a session removes.</summary>
public sealed record SessionRewindRequest
{
    /// <summary>Gets the session to rewind.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the run of the first turn to remove: the session is as it was before that turn.</summary>
    public required string BeforeRunId { get; init; }

    /// <summary>
    /// Gets a value indicating whether the session as it was before the rewind is kept as a session of its own.
    /// Nothing of what is removed can be found again otherwise.
    /// </summary>
    public bool KeepRemovedTurns { get; init; } = true;

    /// <summary>Gets who rewinds the session when it is not the user.</summary>
    public AltaActorProvenance? CreatedBy { get; init; }
}

/// <summary>The outcome of a fork or of a rewind of a session.</summary>
/// <param name="Status">
/// <c>ok</c>, or why nothing was done: <c>session_missing</c>, <c>session_archived</c>, <c>turn_not_found</c>,
/// <c>turn_running</c>, <c>session_in_use</c>, <c>interaction_pending</c>, <c>queue_pending</c>, <c>background_work</c>,
/// <c>session_changed</c>, <c>unsupported_format</c> or <c>io_failed</c>.
/// </param>
public sealed record SessionBranchOutcome(string Status)
{
    /// <summary>Gets the fork, or the session that was rewound; null when nothing was done.</summary>
    public SessionViewDescriptor? Session { get; init; }

    /// <summary>Gets the session that keeps what a rewind removed; null for a fork, and when it was not kept.</summary>
    public SessionViewDescriptor? RemovedTurns { get; init; }

    /// <summary>Gets the turns of the session before the operation.</summary>
    public IReadOnlyList<AgentSessionTurn> Turns { get; init; } = [];

    /// <summary>Gets the number of turns before the point: those of the fork, or those the rewound session keeps.</summary>
    public int KeptTurnCount { get; init; }

    /// <summary>Gets the first turn after the point; null when the point is after the last turn.</summary>
    public AgentSessionTurn? NextTurn => KeptTurnCount < Turns.Count ? Turns[KeptTurnCount] : null;
}

/// <summary>The turns of a session and the one that runs.</summary>
/// <param name="Turns">The turns, with the journal revision they were read from.</param>
/// <param name="ActiveRunId">The run in progress, which no fork includes and no rewind can remove while it runs; null when idle.</param>
public sealed record SessionTurnsSnapshot(AgentSessionTurns Turns, string? ActiveRunId);

public sealed partial class SessionRuntimeService
{
    /// <summary>Reads the turns of a session: the points it can be forked at or rewound to.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The turns, or null when the session does not exist or its journal cannot be read as turns.</returns>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    /// <exception cref="IOException">Journal access fails.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<SessionTurnsSnapshot?> ReadSessionTurnsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return await AdmitAsync(async () =>
        {
            var active = await ReadActiveRunIdAsync(sessionId).ConfigureAwait(false);
            try
            {
                var turns = await _sessionViewCatalog.JournalStore.CreateSessionStore().ReadTurnsAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return new SessionTurnsSnapshot(turns, active);
            }
            catch (AgentSessionHistoryException)
            {
                return null;
            }
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a session that starts with the turns of another one up to a point, and leaves that one as it is.</summary>
    /// <param name="request">The session, the point and what the fork is created with.</param>
    /// <param name="cancellationToken">Cancels before the fork exists.</param>
    /// <returns>The fork, or why it was not created.</returns>
    /// <remarks>
    /// The session may run: a turn in progress is not part of the fork. No provider is started; the fork is attached
    /// when it is first used, as a stored session is. Its first request starts with what the session sent, under the
    /// same prompt-cache key and with the same instructions, unless it is given a parent or another folder.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">The worktree is not an absolute folder.</exception>
    /// <exception cref="InvalidOperationException">The session or its lineage is being retired or has been retired.</exception>
    public async Task<SessionBranchOutcome> ForkSessionAsync(SessionForkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        if (request.WorktreeDirectory is not null && !Path.IsPathFullyQualified(request.WorktreeDirectory))
            throw new ArgumentException("A worktree is an absolute folder.", nameof(request));
        return await AdmitAsync(() => ForkSessionOwnedBodyAsync(request, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes from a session the turn of a run and every turn after it.</summary>
    /// <param name="request">The session, the first turn to remove, and whether what is removed is kept.</param>
    /// <param name="cancellationToken">Cancels before the session is changed.</param>
    /// <returns>The session as it is afterwards, or why it was not rewound.</returns>
    /// <remarks>
    /// The session must be idle: no run, no queued prompt, no question or permission waiting, no background work. Its
    /// attachment is retired, so that the next prompt starts from the shortened journal. Files that the removed turns
    /// changed are not restored.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="InvalidOperationException">The session or its lineage is being retired or has been retired.</exception>
    public async Task<SessionBranchOutcome> RewindSessionAsync(SessionRewindRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BeforeRunId);
        cancellationToken.ThrowIfCancellationRequested();
        // Once admitted the rewind runs to its end: a caller that stops waiting does not leave it half done.
        return await AdmitAsync(() => RewindSessionOwnedBodyAsync(request), cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionBranchOutcome> ForkSessionOwnedBodyAsync(SessionForkRequest request, CancellationToken cancellationToken)
    {
        using var lineage = ClaimRetentionWork(request.SessionId, request.ParentSessionId, request.CreatedBy?.SourceSessionId);
        var source = await ResolveOwnedSessionBodyAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (source is null) return new("session_missing");
        var active = await ReadActiveRunIdAsync(request.SessionId).ConfigureAwait(false);
        var target = await CreateBranchDescriptorAsync(source, request.Title, "fork", cancellationToken).ConfigureAwait(false);
        target.ParentSessionId = NormalizeOptionalText(request.ParentSessionId);
        target.CreatedBy = request.CreatedBy;
        target.WorktreeDirectory = request.LeaveWorktree ? null : NormalizeOptionalText(request.WorktreeDirectory) ?? source.WorktreeDirectory;
        SessionViewBranchResult result;
        try
        {
            result = await _sessionViewCatalog.JournalStore.ForkAsync(source, target, NormalizeOptionalText(request.BeforeRunId), active, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (BranchFailure(error) is { } status)
        {
            return new(status);
        }

        await _agentSessionCatalog.NotifySessionCreatedAsync(target.SessionId, CancellationToken.None).ConfigureAwait(false);
        PublishSessionCatalogEvent(target);
        PublishSessionLifecycleEvent(target.SessionId);
        return new("ok") { Session = target, Turns = result.Turns, KeptTurnCount = result.KeptTurnCount };
    }

    private async Task<SessionBranchOutcome> RewindSessionOwnedBodyAsync(SessionRewindRequest request)
    {
        var sessionId = request.SessionId;
        using var lineage = ClaimRetentionWork(sessionId, request.CreatedBy?.SourceSessionId);
        async Task<(string Status, SessionViewDescriptor? Session)> ValidateAsync(CancellationToken token)
        {
            var session = await ResolveOwnedSessionBodyAsync(sessionId, token).ConfigureAwait(false);
            if (session is null) return ("session_missing", null);
            if (session.Status == SessionViewStatus.Archived) return ("session_archived", null);
            if ((await Permissions.ListAsync().ConfigureAwait(false)).Any(permission => permission.Handle.SessionId == sessionId)
                || (await Permissions.ListOwnedUserInputsAsync(sessionId, token).ConfigureAwait(false)).Entries.Count != 0)
                return ("interaction_pending", null);
            var local = await _sessionViewCatalog.JournalStore.ReadLatestStateAsync(sessionId, session.CreatedAt, token).ConfigureAwait(false);
            if (local?.QueuedPrompts.Any(static prompt => prompt.State is "queued" or "submitting") == true) return ("queue_pending", null);
            // A command that runs for the session, or a task of its provider, reports to a conversation that would no longer be there.
            if (Jobs.CountRunning(sessionId) != 0) return ("background_work", null);
            return ("ok", session);
        }

        var validation = await ValidateAsync(CancellationToken.None).ConfigureAwait(false);
        if (validation.Session is null) return new(validation.Status);
        // The attachment holds the conversation in memory: it is retired before the journal changes, so that nothing
        // of it is written afterwards, and the next prompt reads the journal again.
        var actor = GetActorForWork(sessionId);
        var preparation = await actor.QueryAsync(_ =>
        {
            if (_transitions.ContainsKey(sessionId)) return ValueTask.FromResult((Status: "session_in_use", Work: (Task?)null));
            if (!_entries.TryGetValue(sessionId, out var entry)) return ValueTask.FromResult((Status: "ok", Work: (Task?)null));
            if (entry.HasActiveRun || entry.QueueDrainInProgress || entry.OwnedQueue is not null || entry.Attachment.IsRetiring)
                return ValueTask.FromResult((Status: "session_in_use", Work: (Task?)null));
            if (entry.BackgroundTasks.Any(static task => task.Outcome is null)) return ValueTask.FromResult((Status: "background_work", Work: (Task?)null));
            var retirement = _forwarding.RetireAsync(entry.Attachment);
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? ticket = null;
            var work = _forwarding.RunAsync(async () =>
            {
                await launch.Task.ConfigureAwait(false);
                try { await retirement.ConfigureAwait(false); }
                finally
                {
                    await actor.QueryAsync(token =>
                    {
                        if (_entries.TryGetValue(sessionId, out var current) && ReferenceEquals(current, entry))
                            _entries.TryRemove(sessionId, out var removedEntry);
                        if (_transitions.TryGetValue(sessionId, out var currentTicket) && ReferenceEquals(currentTicket, ticket))
                            _transitions.TryRemove(sessionId, out var removedTransition);
                        return ValueTask.FromResult(true);
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }, external: false);
            _transitions[sessionId] = work;
            ticket = work;
            launch.TrySetResult();
            return ValueTask.FromResult((Status: "ok", Work: (Task?)work));
        }, CancellationToken.None).ConfigureAwait(false);
        if (preparation.Status != "ok") return new(preparation.Status);
        if (preparation.Work is not null) await preparation.Work.ConfigureAwait(false);

        var outcome = await actor.QueryAsync(async token =>
        {
            if (_transitions.ContainsKey(sessionId) || _entries.ContainsKey(sessionId)) return new SessionBranchOutcome("session_in_use");
            var check = await ValidateAsync(token).ConfigureAwait(false);
            if (check.Session is not { } session) return new SessionBranchOutcome(check.Status);
            SessionViewDescriptor? removed = null;
            if (request.KeepRemovedTurns)
            {
                removed = await CreateBranchDescriptorAsync(session, null, "before rewind", token).ConfigureAwait(false);
                removed.ParentSessionId = null;
                removed.CreatedBy = request.CreatedBy;
            }

            try
            {
                var result = await _sessionViewCatalog.JournalStore.RewindAsync(session, request.BeforeRunId, removed, token).ConfigureAwait(false);
                return new SessionBranchOutcome("ok") { RemovedTurns = removed, Turns = result.Turns, KeptTurnCount = result.KeptTurnCount };
            }
            catch (Exception error) when (BranchFailure(error) is { } status)
            {
                return new SessionBranchOutcome(status);
            }
        }, CancellationToken.None).ConfigureAwait(false);
        if (outcome.Status != "ok") return outcome;

        // The live window of the session still holds the last texts of the turns that were removed.
        _events.Display.ForgetConversation(sessionId);
        await _agentSessionCatalog.NotifySessionUpdatedAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (outcome.RemovedTurns is { } kept)
        {
            await _agentSessionCatalog.NotifySessionCreatedAsync(kept.SessionId, CancellationToken.None).ConfigureAwait(false);
            PublishSessionCatalogEvent(kept);
            PublishSessionLifecycleEvent(kept.SessionId);
        }

        var rewound = await ResolveOwnedSessionBodyAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (rewound is not null) PublishSessionCatalogEvent(rewound);
        PublishSessionLifecycleEvent(sessionId);
        return outcome with { Session = rewound };
    }

    // The run of a session that is in progress, as its attachment knows it; null without one.
    private async Task<string?> ReadActiveRunIdAsync(string sessionId)
    {
        if (!_sessionActors.TryGet(sessionId, out var actor)) return null;
        return await actor.QueryAsync(_ => ValueTask.FromResult(
            _entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated ? entry.ActiveRunId?.Value : null), CancellationToken.None).ConfigureAwait(false);
    }

    // A session beside another one: same project, provider, model and folders, with an identifier and a time of its own.
    private async Task<SessionViewDescriptor> CreateBranchDescriptorAsync(SessionViewDescriptor source, string? title, string suffix,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var target = CloneSessionDescriptor(source);
        target.SessionId = Guid.CreateVersion7().ToString();
        target.Status = SessionViewStatus.Active;
        target.CreatedAt = now;
        target.UpdatedAt = now;
        target.LastActiveAt = now;
        target.StartedAt = now;
        target.MessageCount = null;
        target.ForkedFrom = null;
        target.RewindCount = 0;
        if (NormalizeOptionalText(title) is { } given)
        {
            target.Title = given;
            return target;
        }

        // The title the list shows for the source, also when it was never named.
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        var metadata = await store.GetSessionAsync(source.SessionId, cancellationToken).ConfigureAwait(false);
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var listed = metadata is null ? source.Title : ListedTitle(metadata, source.Kind, ProjectOf(source, projects));
        // Several forks of one session are told apart in a list: the second is "(fork 2)", as the fork of a fork is.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var other in store.ListSessionsAsync(filter: null, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (NormalizeOptionalText((other.Details as RawApiSessionMetadataDetails)?.Title) is { } saved) taken.Add(saved);
        }

        var name = BranchTitle(listed, suffix);
        while (taken.Contains(name)) name = BranchTitle(name, suffix);
        target.Title = name;
        return target;
    }

    /// <summary>Names a session after the one it comes from: "Title (fork)", then "Title (fork 2)" for a fork of that fork.</summary>
    /// <param name="title">The title of the source.</param>
    /// <param name="suffix">What the new session is to the source, as in "fork".</param>
    /// <returns>The title of the new session.</returns>
    internal static string BranchTitle(string title, string suffix)
    {
        var text = title.Trim();
        var open = text.LastIndexOf(" (" + suffix, StringComparison.Ordinal);
        if (open >= 0 && text.EndsWith(')'))
        {
            var number = text[(open + 2 + suffix.Length)..^1].Trim();
            if (number.Length == 0) return $"{text[..open]} ({suffix} 2)";
            if (int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count < int.MaxValue)
                return $"{text[..open]} ({suffix} {(count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)})";
        }

        return $"{text} ({suffix})";
    }

    // Why a fork or a rewind did nothing, for the failures a caller can do something about; null for the others.
    private static string? BranchFailure(Exception error) => error switch
    {
        AgentSessionHistoryException { Code: "missing_session" or "outside_root" } => "session_missing",
        AgentSessionHistoryException { Code: "turn_not_found" or "turn_running" or "unsupported_format" } history => history.Code,
        InvalidDataException => "unsupported_format",
        InvalidOperationException => "session_changed",
        IOException or UnauthorizedAccessException => "io_failed",
        _ => null,
    };
}
