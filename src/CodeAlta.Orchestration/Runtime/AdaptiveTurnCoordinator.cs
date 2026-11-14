using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Runtime.Plugins;
using XenoAtom.Logging;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>What a critic of a level of Adaptive is asked: the session whose change it reviews, its model and its prompt.</summary>
/// <param name="SessionId">The session whose change is reviewed; the critic runs in a read-only child session of it.</param>
/// <param name="Critic">The model of the critic.</param>
/// <param name="Prompt">The prompt of the review: the request, the change, and the verdict to end with.</param>
internal sealed record AdaptiveCriticRequest(string SessionId, ModelTierTarget Critic, string Prompt);

/// <summary>What a critic answered.</summary>
/// <param name="Answer">The final answer of the review, or <see langword="null"/> when it gave none.</param>
/// <param name="Failure">Why the review did not complete, or <see langword="null"/>.</param>
internal sealed record AdaptiveCriticAnswer(string? Answer, string? Failure);

/// <summary>
/// Runs the turns of the sessions on Adaptive (<c>[models.adaptive]</c>), as the evaluation of November 2026 found best: a
/// turn the user starts runs on the model of the level of the session, and the critics of the level, models of another
/// family, review the code it changes, read-only, each in a session of its own given the request and the diff; the
/// session revises its change once with what they found.
/// </summary>
/// <remarks>
/// <para>
/// The session starts the review, not the host: it knows when its change is complete and what comes next (a push, a
/// merge), and is told so with its prompt (<see cref="SessionRuntimeService.AdaptiveNoteText"/>). The host gives it the
/// verdict as a prompt, and reminds it once when a run ends with code nobody reviewed. A level without critics has the
/// model of the turn recheck its change against the request at that point instead.
/// </para>
/// <para>
/// A request is followed over all its runs, from the prompt of the user to the next one: the answer of a sub-agent or
/// the end of a job continues it. It follows the events the runtime publishes, never in their way: the end of a run is
/// decided on its own, once the send of the run has released the session. What code is not reviewed yet is read from
/// git, a tree of the working folder taken at the start of the request, at each review and now, so that a run that
/// commits what was there changes nothing; a folder without git falls back on the files the tool calls say they changed.
/// The session keeps its own cache: the critics get only the request and the diff, as Copilot Fusion's critics do.
/// </para>
/// </remarks>
internal sealed partial class AdaptiveTurnCoordinator
{
    private const string LoggerName = "CodeAlta.Adaptive";
    private const int MaximumSessions = 1024;
    // What a review shows of the diff and of the findings it passes on; the rest is in the files.
    private const int MaximumDiff = 60_000;
    private const int MaximumFindings = 8_000;
    private const int MaximumRequest = 20_000;
    private const int MaximumNotes = 4_000;
    // The files a receipt or a status lists, and the ones a prompt names.
    private const int MaximumFiles = 50;
    private const int ListedFiles = 10;
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReleasePoll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
    // A tree taken for a send whose run never started is not the tree of a later turn.
    private static readonly TimeSpan SnapshotLife = TimeSpan.FromMinutes(10);

    // The logger of Adaptive, once the host initialized the logging. Before, a message written with an interpolated string
    // throws, even at a level that is off (XenoAtom.Logging 1.2.0), and a host that leaves the logging to its caller, such
    // as a test, may never initialize it.
    private static Logger? Log => LogManager.IsInitialized ? LogManager.GetLogger(LoggerName) : null;

    internal const string RecheckPrompt = """
        [CodeAlta Adaptive]
        Before you report, check your change against each requirement of the request above, one by one, with a test or a run for each rule it states. Fix what is missing or wrong, keep the tests passing, and answer as you would have.
        This message comes from CodeAlta, not from the user.
        """;

    private readonly OwnedSessionCommandService _commands;
    private readonly SessionRuntimeService _runtime;
    private readonly object _gate = new();
    private readonly Dictionary<string, Turn> _turns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _running = [];
    private readonly Dictionary<string, int> _runningSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _closing = new();

    internal AdaptiveTurnCoordinator(OwnedSessionCommandService commands, SessionRuntimeService runtime)
    {
        _commands = commands;
        _runtime = runtime;
        Snapshot = GitSnapshotAsync;
        Changes = GitChangesAsync;
        Diff = GitDiffAsync;
        TreeOf = GitTreeOfAsync;
        OwnWorkPending = runtime.HasOwnWorkPending;
        runtime.AdaptiveReviewHandler = RequestReviewAsync;
        runtime.AdaptiveStatusHandler = GetStatusAsync;
    }

    /// <summary>Gets or sets the clock; the system clock unless a test sets another.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Gets or sets what takes the tree of a working folder: its hash, or null when the folder has no git.</summary>
    internal Func<string, CancellationToken, Task<string?>> Snapshot { get; set; }

    /// <summary>Gets or sets what lists the paths that differ between two trees of a folder; null when it cannot tell.</summary>
    internal Func<string, string, string, CancellationToken, Task<IReadOnlyList<string>?>> Changes { get; set; }

    /// <summary>Gets or sets what writes the diff between two trees of a folder; null when it cannot.</summary>
    internal Func<string, string, string, CancellationToken, Task<string?>> Diff { get; set; }

    /// <summary>
    /// Gets or sets what tells whether work a session started itself still goes on (a background job, a sub-agent): the
    /// runtime, unless a test sets another.
    /// </summary>
    internal Func<string, bool> OwnWorkPending { get; set; }

    /// <summary>Gets or sets what gives the tree of a commit of a folder (a commit, a branch, a tag); null when it names none.</summary>
    internal Func<string, string, CancellationToken, Task<string?>> TreeOf { get; set; }

    /// <summary>
    /// Gets or sets what runs a review by a critic, in a read-only child session of the reviewed one; the host sets it.
    /// Without it, a level with critics has its change rechecked instead.
    /// </summary>
    internal Func<AdaptiveCriticRequest, CancellationToken, Task<AdaptiveCriticAnswer>>? Critic { get; set; }

    /// <summary>
    /// Gets or sets what is told of the decision at the end of each run, with its session: what it did, or why it did
    /// nothing. The log at Debug level says the same.
    /// </summary>
    internal Action<string, string>? Decided { get; set; }

    /// <summary>Waits for the decisions that run and stops new ones.</summary>
    public async Task CloseAsync()
    {
        Task[] running;
        lock (_gate)
        {
            if (!_closing.IsCancellationRequested) _closing.Cancel();
            running = [.. _running];
        }

        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception) { /* Each decision logged what failed. */ }
    }

    /// <summary>Gets whether a decision of a session is under way: the settlement of the session waits for it.</summary>
    /// <param name="sessionId">The session.</param>
    internal bool IsRunning(string sessionId)
    {
        lock (_gate) return _runningSessions.ContainsKey(sessionId);
    }

    /// <summary>Follows a published event of a session. It returns at once and never throws.</summary>
    /// <param name="envelope">The event and its session.</param>
    public void Observe(RuntimePluginAgentEventEnvelope envelope)
    {
        try { Observe(envelope.SessionId, envelope.Event); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log?.Error(exception, "Adaptive could not follow an event of session " + envelope.SessionId + ".");
        }
    }

    internal void Observe(string sessionId, AgentEvent agentEvent)
    {
        // The streamed parts of a text, and what else a run publishes often, say nothing of how it ends.
        if (agentEvent is not (AgentSessionUpdateEvent or AgentContentCompletedEvent or AgentActivityEvent or AgentErrorEvent)) return;
        (Turn Turn, string RunId)? ended = null;
        lock (_gate)
        {
            if (_closing.IsCancellationRequested) return;
            switch (agentEvent)
            {
                case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Shutdown }:
                    _turns.Remove(sessionId);
                    _snapshots.Remove(sessionId);
                    return;
                case AgentContentCompletedEvent { Kind: AgentContentKind.User } user:
                    StartRun(sessionId, user.RunId?.Value, user.Provenance, text: user.Content);
                    return;
                case AgentActivityEvent activity when IsToolActivity(activity.Kind) && !string.IsNullOrEmpty(activity.ActivityId)
                    && CurrentTurn(sessionId, activity.RunId) is { } turn:
                    turn.Note(activity.Details);
                    return;
                case AgentContentCompletedEvent { Kind: AgentContentKind.ToolOutput } output when CurrentTurn(sessionId, output.RunId) is { } turn:
                    turn.Note(output.Details);
                    return;
                case AgentErrorEvent error when CurrentTurn(sessionId, error.RunId) is { } turn:
                    // A run that failed is the fallback's: Adaptive does not continue it.
                    turn.Failed = true;
                    return;
                case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle, RunId: { } runId }
                    when CurrentTurn(sessionId, runId) is { Active: true } turn && turn.DecidedRun != runId.Value:
                    // An idle event is published once per run; a run already decided is not decided twice.
                    turn.Active = false;
                    turn.DecidedRun = runId.Value;
                    ended = (turn, runId.Value);
                    break;
                default:
                    return;
            }
        }

        if (ended is { } end) Track(sessionId, () => DecideAsync(sessionId, end.Turn, end.RunId));
    }

    /// <summary>
    /// Starts the run of a prompt the host sends to a session, just before it reaches the provider: a provider that runs
    /// its own loop does not tell the prompt of its run.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="provenance">The provenance of the prompt; null for a prompt of the user.</param>
    /// <param name="text">The text of the prompt: the request the critics check a change against.</param>
    internal void BeginRun(string sessionId, string? provenance, string? text = null)
    {
        lock (_gate)
        {
            if (!_closing.IsCancellationRequested) StartRun(sessionId, runId: null, provenance, text, sent: true);
        }
    }

    // Under _gate. A prompt starts a run: a new turn, unless it steers the run that goes on, or continues the turn
    // (a recheck, a revision, the continuation of a fallback). A provider may name the run only in its later events.
    // A prompt the host sends always starts a run; the user message the run then publishes is the same run.
    private void StartRun(string sessionId, string? runId, string? provenance, string? text, bool sent = false)
    {
        var turn = _turns.GetValueOrDefault(sessionId);
        if (!sent && turn is { Active: true } && (runId is null || turn.RunId is null || turn.RunId == runId))
        {
            turn.Request ??= Bounded(text, MaximumRequest);
            return;
        }

        // A prompt that does not come from the user continues the request: the answer of a sub-agent, the end of a job, a
        // reminder, a verdict of the critics. An automation starts a request of its own, as the user does.
        if (turn is not null && provenance is not (null or AgentPromptProvenance.Automation))
        {
            turn.StartRun(runId);
            return;
        }

        var fresh = new Turn(runId, Time.GetUtcNow()) { Request = Bounded(text, MaximumRequest) };
        if (_snapshots.Remove(sessionId, out var snapshot) && Time.GetUtcNow() - snapshot.At < SnapshotLife) fresh.Before = snapshot;
        Keep(sessionId, fresh);
    }

    // Under _gate. Bounded: the oldest request is forgotten, which only ends its follow-up.
    private void Keep(string sessionId, Turn turn)
    {
        if (!_turns.ContainsKey(sessionId) && _turns.Count >= MaximumSessions) _turns.Remove(_turns.MinBy(static pair => pair.Value.StartedAt).Key);
        _turns[sessionId] = turn;
    }

    // Under _gate: the request of a session while its current run is the one of an event (any run when the event or the
    // request names none; the first event that names it gives the request its run). A run that starts once the last one
    // ended continues the request: a provider that runs its own loop does not tell the prompt of such a run.
    private Turn? CurrentTurn(string sessionId, AgentRunId? runId)
    {
        if (!_turns.TryGetValue(sessionId, out var turn)) return null;
        if (runId is not { } named || turn.RunId == named.Value) return turn;
        if (turn.RunId is not null && turn.Active) return null;
        turn.StartRun(named.Value);
        return turn;
    }

    /// <summary>
    /// Chooses the model of a turn the user starts in a session: the session on Adaptive takes the model of its level,
    /// when the provider of the session is the one of that model (choosing the level moved the session there), and the
    /// tree of its working folder is taken to tell, at the end of the turn, whether it changed code. Never throws: what
    /// cannot be read keeps the selection sent.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="selection">The selection the prompt was sent with, or null.</param>
    /// <param name="workFolder">The folder the session works in, or null for a chat.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The selection of the turn: the one sent for a session that is not on Adaptive.</returns>
    internal async Task<OwnedSessionSelection?> ChooseTurnSelectionAsync(SessionViewDescriptor session, OwnedSessionSelection? selection, string? workFolder,
        CancellationToken cancellationToken)
    {
        try
        {
            var (_, adaptive) = await _runtime.GetSessionModelChoiceAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            if (!adaptive) return selection;
            var notice = await _runtime.ReadAdaptiveNoticeAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            if (notice is not { Adaptive: true, Scope.Adaptive: { } settings }) return selection;
            if (workFolder is not null && await Snapshot(workFolder, cancellationToken).ConfigureAwait(false) is { } tree)
            {
                lock (_gate) _snapshots[session.SessionId] = new TreeSnapshot(workFolder, tree, Time.GetUtcNow());
            }

            // A send keeps the provider of the session: the model of the level is taken on its own provider only.
            if (settings.Resolve(notice.Level) is not { } level
                || !string.Equals(level.Model.ProviderKey, session.ResolvedProviderKey, StringComparison.OrdinalIgnoreCase))
                return selection;
            return (selection ?? new OwnedSessionSelection(session.ResolvedProviderKey, notice.AgentPromptId ?? "default", null, null))
                with { ModelId = level.Model.ModelId, ReasoningEffort = level.Model.PreferredReasoning ?? selection?.ReasoningEffort };
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            Log?.Error(exception, "Adaptive could not choose the model of a turn of session " + session.SessionId + ".");
            return selection;
        }
    }

    private async Task DecideAsync(string sessionId, Turn turn, string runId)
    {
        try
        {
            var (_, adaptive) = await _runtime.GetSessionModelChoiceAsync(sessionId, _closing.Token).ConfigureAwait(false);
            if (!adaptive)
            {
                Forget(sessionId, turn);
                Tell(sessionId, runId, "the session is not on Adaptive");
                return;
            }

            var notice = await _runtime.ReadAdaptiveNoticeAsync(sessionId, _closing.Token).ConfigureAwait(false);
            if (notice is not { Adaptive: true, Scope.Adaptive: { } settings })
            {
                Tell(sessionId, runId, "Adaptive is not set up for the session");
                return;
            }

            Facts facts;
            lock (_gate) facts = turn.Read();
            // A failed run is the fallback's. While a review runs, a prompt waits or work of its own goes on (a job, a
            // sub-agent), the session is not done: what comes back to it starts the run that is decided next.
            var waits = facts.Failed ? "the run failed" : facts.ReviewRunning ? "a review runs" : facts.VerdictWaits ? "a verdict waits"
                : notice.HasQueuedPrompts ? "a prompt waits" : null;
            if (waits is not null)
            {
                Tell(sessionId, runId, waits);
                return;
            }

            if (OwnWorkPending(sessionId))
            {
                Tell(sessionId, runId, "work of the session goes on");
                return;
            }

            // The run that took a verdict or a recheck revised the change, by itself or through a sub-agent it waited for:
            // the files as they are now are the reviewed ones.
            if (facts.Revising)
            {
                var now = facts.Folder is null ? null : await Snapshot(facts.Folder, _closing.Token).ConfigureAwait(false);
                lock (_gate) turn.Accept(now);
                Tell(sessionId, runId, "the change was revised");
                return;
            }

            var (change, unchanged) = await ChangeOfTurnAsync(facts).ConfigureAwait(false);
            if (change is null)
            {
                Tell(sessionId, runId, unchanged!);
                return;
            }

            if (change.After is { } tree && string.Equals(tree, facts.Told, StringComparison.Ordinal))
            {
                Tell(sessionId, runId, "the session was already reminded");
                return;
            }

            // The session starts the reviews: the host only reminds it, once for these files. A level without critics has
            // the model of the turn recheck its change instead, and what it changes then is the recheck.
            var reviewed = settings.Resolve(notice.Level) is { Critics.Count: > 0 } && Critic is not null;
            lock (_gate) turn.Remind(change.After, revising: !reviewed);
            if (reviewed)
            {
                await RemindAsync(notice, change, runId).ConfigureAwait(false);
                Tell(sessionId, runId, "the session was reminded of the review");
                return;
            }

            await RecheckAsync(notice, runId).ConfigureAwait(false);
            Tell(sessionId, runId, "the change was rechecked");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (exception is OperationCanceledException or ObjectDisposedException && _closing.IsCancellationRequested) return;
            Log?.Error(exception, "Adaptive could not decide the end of a run of session " + sessionId + ".");
            Decided?.Invoke(sessionId, "the decision failed: " + exception.Message);
        }
    }

    private void Forget(string sessionId, Turn turn)
    {
        lock (_gate)
        {
            if (_turns.TryGetValue(sessionId, out var current) && ReferenceEquals(current, turn)) _turns.Remove(sessionId);
        }
    }

    // The code of a request that is not reviewed yet: the paths, the diff when git gives it, and the tree of the files now;
    // else why there is none (documentation only, nothing, or a folder whose trees were not read).
    private async Task<(TurnChange? Change, string? Unchanged)> ChangeOfTurnAsync(Facts facts)
    {
        var unread = "the tree of the folder before the turn was not read";
        if (facts is { Folder: { } folder, From: { } from })
        {
            var after = await Snapshot(folder, _closing.Token).ConfigureAwait(false);
            if (after is not null)
            {
                if (string.Equals(after, from, StringComparison.Ordinal)) return (null, "the files did not change");
                var changed = await Changes(folder, from, after, _closing.Token).ConfigureAwait(false);
                if (changed is not null && !changed.Any(IsCode)) return (null, "the turn changed no code");
                var diff = await Diff(folder, from, after, _closing.Token).ConfigureAwait(false);
                return (new TurnChange(changed ?? facts.ChangedFiles, diff, folder, after), null);
            }

            unread = "the tree of the folder after the turn was not read";
        }

        // Without the trees, what the tools of the turn say they changed.
        if (facts.ChangedFiles.Any(IsCode)) return (new TurnChange(facts.ChangedFiles, null, facts.Folder, null), null);
        return (null, facts.ChangedFiles.Count > 0 ? "the turn changed no code" : $"its tools changed no file, and {unread}");
    }

    private void Tell(string sessionId, string runId, string outcome)
    {
        Log?.Debug($"Adaptive, at the end of run {runId} of session {sessionId}: {outcome}.");
        Decided?.Invoke(sessionId, outcome);
    }

    /// <summary>Gets whether a path changed by a turn is code: not documentation, not what CodeAlta keeps under <c>.alta</c>.</summary>
    internal static bool IsCode(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith(".alta/", StringComparison.Ordinal) || normalized.Contains("/.alta/", StringComparison.Ordinal)) return false;
        return Path.GetExtension(normalized).ToLowerInvariant() is not (".md" or ".mdx" or ".markdown" or ".txt" or ".rst" or ".adoc");
    }

    private async Task RecheckAsync(AdaptiveSessionNotice notice, string runId)
    {
        if (!await WaitForReleaseAsync(notice.SessionId).ConfigureAwait(false)) return;
        // The prompt is the card: the timeline titles it Adaptive recheck.
        await AdmitAsync(new OwnedTextSendRequest($"adaptive-recheck:{notice.SessionId}:{runId}", notice.SessionId, RecheckPrompt)
        {
            Provenance = AgentPromptProvenance.AdaptiveRecheck,
        }).ConfigureAwait(false);
    }

    private async Task RemindAsync(AdaptiveSessionNotice notice, TurnChange change, string runId)
    {
        if (!await WaitForReleaseAsync(notice.SessionId).ConfigureAwait(false)) return;
        // The prompt is the card: the timeline titles it Adaptive reminder.
        await AdmitAsync(new OwnedTextSendRequest($"adaptive-reminder:{notice.SessionId}:{runId}", notice.SessionId, ReminderPrompt(change.Paths))
        {
            Provenance = AgentPromptProvenance.AdaptiveReminder,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the review a session asks for: the critics of its level review, each in a read-only session of its own, what
    /// changed since the last review of its request (or since the commit it names), and their verdict comes back to the
    /// session as a prompt. It returns once the review started, or with why it did not.
    /// </summary>
    /// <param name="request">The session, and what it says of its change.</param>
    /// <param name="cancellationToken">Stops the request, not a review that started.</param>
    /// <returns>How the request was taken, with what the session is told.</returns>
    internal async Task<AdaptiveReviewReceipt> RequestReviewAsync(AdaptiveReviewRequest request, CancellationToken cancellationToken)
    {
        var sessionId = request.SessionId;
        var notice = await _runtime.ReadAdaptiveNoticeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (notice is not { Adaptive: true, Scope.Adaptive: { } settings } || settings.Resolve(notice.Level) is not { } level)
            return new("not_adaptive", "This session is not on Adaptive: no critics review its changes. If a parent session asked you to have your change reviewed, report the change to it: the session on Adaptive starts the review. Otherwise go on without it.", null, [], []);
        var name = AdaptiveLevels.NameOf(level.Level);
        if (level.Critics.Count == 0 || Critic is not { } critic)
            return new("no_critics", $"Level `{name}` of Adaptive has no critics. Check your change yourself against each requirement of the request, one by one, with a test or a run for each rule it states, and fix what is missing or wrong.",
                name, [], []);
        var critics = level.Critics.Select(static model => model.Format()).ToArray();

        Turn turn;
        Facts facts;
        lock (_gate)
        {
            if (_closing.IsCancellationRequested) return new("unavailable", "CodeAlta is closing.", name, critics, []);
            // A session CodeAlta did not see the request of (it started again since) is reviewed from its last commit.
            if (!_turns.TryGetValue(sessionId, out turn!)) Keep(sessionId, turn = new Turn(null, Time.GetUtcNow()) { Active = false });
            if (turn.Review is not null) return Running(name, critics);
            facts = turn.Read();
        }

        var folder = facts.Folder ?? await _commands.WorkFolderOfAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (folder is null) return new("unavailable", "This session has no project folder: there is no change to give the critics.", name, critics, []);
        var after = await Snapshot(folder, cancellationToken).ConfigureAwait(false);
        TurnChange change;
        string since;
        if (after is null)
        {
            // Without git, the files the tools of the request say they changed.
            if (!string.IsNullOrWhiteSpace(request.Base)) return new("invalid_base", $"`--base` needs a git folder, and `{folder}` is not one.", name, critics, []);
            if (!facts.ChangedFiles.Any(IsCode))
                return new("no_change", $"`{folder}` is not a git folder and the tools of this request changed no code file CodeAlta saw: there is nothing to give the critics.", name, critics, []);
            change = new TurnChange(facts.ChangedFiles, null, folder, null);
            since = "the start of the request";
        }
        else
        {
            string? from;
            if (request.Base?.Trim() is { Length: > 0 } named)
            {
                from = await TreeOf(folder, named, cancellationToken).ConfigureAwait(false);
                if (from is null) return new("invalid_base", $"`{named}` is not a commit of `{folder}`.", name, critics, []);
                since = $"`{named}`";
            }
            else if (facts.From is not null)
            {
                from = facts.From;
                since = facts.Reviewed ? "the last review" : "the start of the request";
            }
            else
            {
                from = await TreeOf(folder, "HEAD", cancellationToken).ConfigureAwait(false);
                if (from is null) return new("unavailable", $"`{folder}` has no commit to compare with: give one with `--base <commit>`.", name, critics, []);
                since = "the last commit";
            }

            var changed = string.Equals(from, after, StringComparison.Ordinal) ? [] : await Changes(folder, from, after, cancellationToken).ConfigureAwait(false);
            if (changed is null) return new("unavailable", "git could not list what changed.", name, critics, []);
            if (!changed.Any(IsCode))
                return new("no_change", $"No code changed since {since}: there is nothing to review. If your change is committed on another base, name the commit it starts from with `--base <commit>`.", name, critics, []);
            change = new TurnChange(changed, await Diff(folder, from, after, cancellationToken).ConfigureAwait(false), folder, after);
        }

        var prompt = CriticPrompt(facts.Request, change, request.Notes);
        lock (_gate)
        {
            if (turn.Review is not null) return Running(name, critics);
            if (_closing.IsCancellationRequested) return new("unavailable", "CodeAlta is closing.", name, critics, []);
            turn.StartReview(Time.GetUtcNow(), folder, after);
        }

        Track(sessionId, () => RunReviewAsync(sessionId, turn, level.Critics, critic, prompt, change));
        var files = change.Paths.Where(IsCode).ToArray();
        return new(AdaptiveReviewReceipt.Started,
            $"The critics ({string.Join(", ", critics)}) review the {files.Length} code file(s) that changed since {since}: {Listed(files)}. "
            + "Their verdict comes back to you as a prompt that starts with `[CodeAlta Adaptive]`. Until then, do not push, merge or publish this change and do not edit these files; "
            + "when nothing else is left to do, end your turn saying that you wait for the review.",
            name, critics, [.. files.Take(MaximumFiles)]);

        static AdaptiveReviewReceipt Running(string name, string[] critics)
            => new("running", "A review of this session already runs: wait for its verdict, which comes back to you as a prompt, before you ask for another one.", name, critics, []);
    }

    /// <summary>Reads where a session stands with Adaptive: its level, its critics, the review that runs and the code not reviewed yet.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    internal async Task<AdaptiveSessionStatus> GetStatusAsync(string sessionId, CancellationToken cancellationToken)
    {
        var notice = await _runtime.ReadAdaptiveNoticeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (notice is not { Adaptive: true, Scope.Adaptive: { } settings } || settings.Resolve(notice.Level) is not { } level) return new(false, null, [], null, []);
        Facts? facts = null;
        DateTimeOffset? started = null;
        lock (_gate)
        {
            if (_turns.TryGetValue(sessionId, out var turn))
            {
                facts = turn.Read();
                started = turn.Review;
            }
        }

        var change = facts is null ? null : (await ChangeOfTurnAsync(facts).ConfigureAwait(false)).Change;
        return new(true, AdaptiveLevels.NameOf(level.Level), [.. level.Critics.Select(static model => model.Format())], started,
            [.. (change?.Paths ?? []).Where(IsCode).Take(MaximumFiles)]);
    }

    // The critics review the change at the same time; their verdict is one prompt of the host, which a running turn of the
    // session takes or which starts its next one.
    private async Task RunReviewAsync(string sessionId, Turn turn, IReadOnlyList<ModelTierTarget> critics,
        Func<AdaptiveCriticRequest, CancellationToken, Task<AdaptiveCriticAnswer>> critic, string prompt, TurnChange change)
    {
        string verdict;
        string outcome;
        var revising = true;
        try
        {
            var reviews = await Task.WhenAll(critics.Select(async model =>
            {
                try { return (Model: model, Answer: await critic(new AdaptiveCriticRequest(sessionId, model, prompt), _closing.Token).ConfigureAwait(false)); }
                catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
                {
                    Log?.Error(exception, "Adaptive could not run the review by " + model.Format() + " of session " + sessionId + ".");
                    return (Model: model, Answer: new AdaptiveCriticAnswer(null, exception.Message));
                }
            })).ConfigureAwait(false);

            var findings = new List<(ModelTierTarget Model, string Text)>();
            var approved = new List<ModelTierTarget>();
            var silent = new List<(ModelTierTarget Model, string Why)>();
            foreach (var (model, answer) in reviews)
            {
                var (line, text) = ParseVerdict(answer.Answer);
                switch (line)
                {
                    case "REVISE" when text.Length > 0: findings.Add((model, text)); break;
                    case "APPROVE": approved.Add(model); break;
                    default: silent.Add((model, answer.Failure ?? (answer.Answer is null ? "no answer" : "no verdict"))); break;
                }
            }

            verdict = VerdictPrompt(findings, approved, silent);
            outcome = findings.Count > 0 ? "the review asked for a revision" : approved.Count > 0 ? "the review approved the change" : "the review gave no verdict";
            revising = findings.Count > 0 || approved.Count == 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (exception is OperationCanceledException or ObjectDisposedException && _closing.IsCancellationRequested) return;
            Log?.Error(exception, "Adaptive could not review the change of session " + sessionId + ".");
            verdict = VerdictPrompt([], [], [.. critics.Select(model => (model, exception.Message))]);
            outcome = "the review failed: " + exception.Message;
        }

        try
        {
            lock (_gate) turn.EndReview(revising);
            var given = await _runtime.DeliverHostPromptAsync(sessionId, verdict, AgentPromptProvenance.AdaptiveReview, _closing.Token).ConfigureAwait(false);
            if (given == "missing_session") outcome = "the session of the review is gone";
            // A turn that runs took the verdict; one it starts takes it when it starts.
            else if (given == "steered") lock (_gate) turn.TakeVerdict();
            Decided?.Invoke(sessionId, outcome);
            Log?.Debug("Adaptive, for session " + sessionId + ": " + outcome + ".");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (exception is OperationCanceledException or ObjectDisposedException && _closing.IsCancellationRequested) return;
            Log?.Error(exception, "Adaptive could not give session " + sessionId + " the verdict of its review.");
            Decided?.Invoke(sessionId, "the verdict could not be given: " + exception.Message);
        }
    }

    /// <summary>The prompt of a critic: the request, the change, what to check and the verdict to end with.</summary>
    internal static string CriticPrompt(string? request, TurnChange change, string? notes = null)
    {
        var text = new StringBuilder();
        text.Append("[CodeAlta Adaptive]\nYou review a change that another engineer, another AI model, made to answer the request below. ");
        text.Append("You are read-only: do not modify, create or delete any file of the project; a scratch copy outside it is fine. ");
        text.Append("Read the code, and run the tests or the commands you need to check the change. ");
        // The session asks for the review before it finishes: a critic told nothing reports the rest of the request as missing.
        text.Append("The author asked for this review before finishing: what the request asks to do with the change afterwards (commit it, push it, merge it, report it) is still to come, and is not a problem to report.");
        text.Append("\n\n## The request\n\n");
        text.Append(string.IsNullOrWhiteSpace(request) ? "(The request is in the session that made the change; judge the change on its own.)" : request.Trim());
        if (!string.IsNullOrWhiteSpace(notes))
            text.Append("\n\n## What the author says of the change\n\n").Append(Bounded(notes.Trim(), MaximumNotes));
        text.Append("\n\n## The change\n\n");
        if (change.Diff is { Length: > 0 } diff)
        {
            text.Append("It is in the working tree").Append(change.Folder is null ? "" : $" of {change.Folder}").Append(". Its diff:\n\n```diff\n");
            text.Append(diff.Length <= MaximumDiff ? diff : diff[..MaximumDiff] + "\n[... the diff goes on: read the files ...]");
            text.Append("\n```\n");
        }
        else
        {
            text.Append("It is in the working tree; the files it changed: ").Append(string.Join(", ", change.Paths.Take(50))).Append(".\n");
        }

        text.Append("""

            ## What to check

            - Does it do every part of the request, with the names, signatures, messages and behaviours it states?
            - Is it correct on the edge cases and the error paths? Does it fix the cause rather than a symptom? Does it break existing behaviour or tests?

            Report only problems you verified in the code or by a run, each with the file, what is wrong and why it matters, most important first. Do not report style or optional improvements.

            End your answer with exactly one line: `VERDICT: APPROVE` if the change is right as it is, or `VERDICT: REVISE` if at least one problem must be fixed.
            """);
        return text.ToString();
    }

    /// <summary>
    /// The prompt that gives a session the verdict of the review it asked for: what the critics found, for it to check and
    /// fix; their approval; or that none could review, for it to recheck its change itself.
    /// </summary>
    internal static string VerdictPrompt(IReadOnlyList<(ModelTierTarget Model, string Text)> findings, IReadOnlyList<ModelTierTarget> approved,
        IReadOnlyList<(ModelTierTarget Model, string Why)> silent)
    {
        const string Then = "Then do what waited for this review, and answer as you would have.";
        var text = new StringBuilder("[CodeAlta Adaptive]\n");
        var others = new StringBuilder();
        if (approved.Count > 0) others.Append("Approved by ").Append(string.Join(", ", approved.Select(static model => model.Format()))).Append(". ");
        if (silent.Count > 0) others.Append("No verdict from ").Append(string.Join(", ", silent.Select(static item => $"{item.Model.Format()} ({item.Why})"))).Append(". ");
        if (findings.Count > 0)
        {
            text.Append("The review you asked for is done. The critics checked your change against the request, read-only. What they found:\n");
            foreach (var (model, found) in findings) text.Append("\n### ").Append(model.Format()).Append("\n\n").Append(Bounded(found, MaximumFindings)).Append('\n');
            text.Append('\n').Append(others);
            text.Append("Check each finding yourself. Fix the ones that are real and keep the tests passing; leave the ones that are wrong. These fixes need no second review. ").Append(Then);
        }
        else if (approved.Count > 0)
        {
            text.Append("The review you asked for is done: the critics found nothing to fix. ").Append(others).Append("Do what waited for this review, and answer as you would have.");
        }
        else
        {
            text.Append("The review you asked for could not be done. ").Append(others);
            text.Append("Check your change yourself against each requirement of the request, one by one, with a test or a run for each rule it states; fix what is missing or wrong and keep the tests passing. ").Append(Then);
        }

        return text.Append("\nThis message comes from CodeAlta, not from the user.").ToString();
    }

    /// <summary>The prompt that reminds a session that its turn ended with code its critics did not review.</summary>
    internal static string ReminderPrompt(IReadOnlyList<string> paths)
    {
        var files = paths.Where(IsCode).ToArray();
        return "[CodeAlta Adaptive]\n"
            + $"Your turn ended with code that the critics of this session did not review: {Listed(files)}.\n"
            + "If it is your change and it is complete, run `alta adaptive review` now, then end your turn: the verdict comes back to you as a prompt. Do not push, merge or publish it before.\n"
            + "If part of it is not yours (a branch you checked out, a pull, generated files), name the commit your change starts from: `alta adaptive review --base <commit>`. "
            + "If nothing of it needs a review, or your work on it is not finished, say so in one line and go on.\n"
            + "This message comes from CodeAlta, not from the user.";
    }

    // The first files of a list, in code, and how many more there are.
    private static string Listed(IReadOnlyList<string> files)
        => string.Join(", ", files.Take(ListedFiles).Select(static file => "`" + file + "`")) + (files.Count > ListedFiles ? $" and {files.Count - ListedFiles} more" : "");

    /// <summary>The last verdict line of a review, upper case, and the findings around it; (null, answer) without a verdict.</summary>
    internal static (string? Verdict, string Findings) ParseVerdict(string? answer)
    {
        var text = answer ?? string.Empty;
        var matches = VerdictPattern().Matches(text);
        if (matches.Count == 0) return (null, text.Trim());
        var last = matches[^1];
        return (last.Groups[1].Value.ToUpperInvariant(), (text[..last.Index] + text[(last.Index + last.Length)..]).Trim());
    }

    // A session the run that ended still holds (its cleanup, a switch of provider) is busy for a moment: asked again a while.
    private async Task<OwnedSessionCommandAdmission> AdmitAsync(OwnedTextSendRequest request)
    {
        var deadline = Time.GetUtcNow() + ReleaseWait;
        while (true)
        {
            var admission = _commands.AdmitSend(request);
            if (admission.Kind != OwnedSessionCommandAdmissionKind.Busy || Time.GetUtcNow() >= deadline)
            {
                if (admission.Kind != OwnedSessionCommandAdmissionKind.Accepted)
                    Log?.Warn($"Adaptive could not continue session {request.SessionId} ({admission.Kind}).");
                return admission;
            }

            await Task.Delay(ReleasePoll, _closing.Token).ConfigureAwait(false);
        }
    }

    // The send of the run that ended holds the session until its run is cleaned up.
    private async Task<bool> WaitForReleaseAsync(string sessionId)
    {
        var deadline = Time.GetUtcNow() + ReleaseWait;
        while (_commands.IsSessionHeldForFallback(sessionId))
        {
            if (Time.GetUtcNow() >= deadline) return false;
            await Task.Delay(ReleasePoll, _closing.Token).ConfigureAwait(false);
        }

        return true;
    }

    // Runs the decision of a session on its own, which the settlement of the session waits for and the closing awaits.
    private void Track(string sessionId, Func<Task> work)
    {
        Task task;
        lock (_gate)
        {
            if (_closing.IsCancellationRequested) return;
            task = Task.Run(work);
            _running.Add(task);
            _runningSessions[sessionId] = _runningSessions.GetValueOrDefault(sessionId) + 1;
        }

        _ = task.ContinueWith(completed =>
        {
            lock (_gate)
            {
                _running.Remove(completed);
                if (--_runningSessions[sessionId] == 0) _runningSessions.Remove(sessionId);
            }

            _runtime.PulseSettlement();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static string? Bounded(string? text, int length) => text is null || text.Length <= length ? text : text[..length] + "\n[...]";

    private static bool IsToolActivity(AgentActivityKind kind)
        => kind is AgentActivityKind.ToolCall or AgentActivityKind.CommandExecution or AgentActivityKind.FileChange or AgentActivityKind.McpToolCall
            or AgentActivityKind.DynamicToolCall;

    // The tree of the working folder, through a copy of its index: what git would commit with `git add -A`, CodeAlta's
    // own files (.alta) left out. Null without git.
    private static async Task<string?> GitSnapshotAsync(string folder, CancellationToken cancellationToken)
    {
        var index = (await GitAsync(folder, null, cancellationToken, "rev-parse", "--git-path", "index").ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(index)) return null;
        var indexPath = Path.GetFullPath(Path.Combine(folder, index));
        var temporary = Path.Combine(Path.GetTempPath(), "codealta-adaptive-" + Guid.NewGuid().ToString("N") + ".index");
        try
        {
            // The copy keeps what git knows of the files: only what changed is read again.
            if (File.Exists(indexPath)) File.Copy(indexPath, temporary);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["GIT_INDEX_FILE"] = temporary };
            if (await GitAsync(folder, environment, cancellationToken, "add", "-A", "--", ".", ":(exclude).alta").ConfigureAwait(false) is null) return null;
            return (await GitAsync(folder, environment, cancellationToken, "write-tree").ConfigureAwait(false))?.Trim() is { Length: > 0 } tree ? tree : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<IReadOnlyList<string>?> GitChangesAsync(string folder, string before, string after, CancellationToken cancellationToken)
        => (await GitAsync(folder, null, cancellationToken, "diff", "--name-only", "--no-renames", before, after).ConfigureAwait(false))
            ?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Task<string?> GitDiffAsync(string folder, string before, string after, CancellationToken cancellationToken)
        => GitAsync(folder, null, cancellationToken, "diff", "--no-color", "--no-ext-diff", before, after);

    // A name that starts with a dash would be read as an option of git.
    private static async Task<string?> GitTreeOfAsync(string folder, string commit, CancellationToken cancellationToken)
        => commit.StartsWith('-') ? null
            : (await GitAsync(folder, null, cancellationToken, "rev-parse", "--verify", "--quiet", commit + "^{tree}").ConfigureAwait(false))?.Trim() is { Length: > 0 } tree ? tree : null;

    // The output of a git command, or null when it failed, took too long or git is not there.
    private static async Task<string?> GitAsync(string folder, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitTimeout);
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            var failure = await error.ConfigureAwait(false);
            if (process.ExitCode == 0) return text;
            // A folder without git fails here too: the line says why git gave nothing.
            LogFailure($"exit code {process.ExitCode}: {failure.Trim().Split('\n')[0].Trim()}");
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailure($"no answer within {GitTimeout.TotalSeconds:0} s");
            return null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            LogFailure(exception.Message);
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
        }

        void LogFailure(string why) => Log?.Debug($"Adaptive: git {string.Join(' ', arguments)} in {folder} gave nothing ({why}).");
    }

    // A verdict line of a review: `VERDICT: APPROVE`, `**Verdict: revise**`.
    [GeneratedRegex(@"^\W*VERDICT\W*:?\W*(APPROVE|REVISE)\b\W*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VerdictPattern();

    // The tree of a working folder before a turn.
    private sealed record TreeSnapshot(string Folder, string Tree, DateTimeOffset At);

    /// <summary>Code of a request that is to be reviewed.</summary>
    /// <param name="Paths">The paths that changed.</param>
    /// <param name="Diff">The diff, when git gives it.</param>
    /// <param name="Folder">The working folder, when git tells the change.</param>
    /// <param name="After">The tree of the files as they are, when git tells the change.</param>
    internal sealed record TurnChange(IReadOnlyList<string> Paths, string? Diff, string? Folder, string? After);

    // What a decision reads of a request, copied under the gate. From is the tree the code not reviewed yet is compared
    // with: the last reviewed one (Reviewed is then true), else the one the request started on.
    private sealed record Facts(bool Failed, bool ReviewRunning, bool VerdictWaits, bool Revising, string? Request, string? Folder, string? From, bool Reviewed, string? Told,
        IReadOnlyList<string> ChangedFiles);

    // A request of the user, over all its runs: the one its prompt started, and those that continue it (the answer of a
    // sub-agent, the end of a job, a verdict of the critics, a recheck). Read and written under the gate.
    private sealed class Turn(string? runId, DateTimeOffset startedAt)
    {
        private readonly HashSet<string> _changedFiles = new(StringComparer.OrdinalIgnoreCase);
        private string? _folder;
        private string? _reviewed;
        private string? _told;
        private bool _revising;
        private bool _verdictWaits;

        public string? RunId { get; set; } = runId;

        // From the prompt of a run until its idle event: a prompt meanwhile steers the run.
        public bool Active { get; set; } = true;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public string? DecidedRun { get; set; }

        public TreeSnapshot? Before { get; set; }

        // The request of the user, which the critics check the change against.
        public string? Request { get; set; }

        public bool Failed { get; set; }

        // When the review that runs started; null when none runs.
        public DateTimeOffset? Review { get; private set; }

        public void StartRun(string? runId)
        {
            RunId = runId;
            Active = true;
            Failed = false;
            TakeVerdict();
        }

        // A run has the verdict that waited: a running one that was given it, or the one it starts. That run revises the change.
        public void TakeVerdict()
        {
            if (!_verdictWaits) return;
            _verdictWaits = false;
            _revising = true;
        }

        public Facts Read() => new(Failed, Review is not null, _verdictWaits, _revising, Request, _folder ?? Before?.Folder, _reviewed ?? Before?.Tree, _reviewed is not null, _told, [.. _changedFiles]);

        // A review of the files as they are (their tree, or null without git) starts: they are the reviewed ones.
        public void StartReview(DateTimeOffset at, string folder, string? tree)
        {
            Review = at;
            _folder = folder;
            Cover(tree);
        }

        // The review ended. Its verdict waits for a run when the session is to fix what was found, or to recheck its
        // change itself: what that run changes is the revision, which is not reviewed again.
        public void EndReview(bool revising)
        {
            Review = null;
            _verdictWaits = revising;
        }

        // The session was told of the files as they are, once: reminded of their review, or asked to recheck them.
        public void Remind(string? tree, bool revising)
        {
            _told = tree;
            _revising = revising;
            if (tree is null) _changedFiles.Clear();
        }

        // The run that revised the change ended: the files as they are now are the reviewed ones.
        public void Accept(string? tree)
        {
            _revising = false;
            Cover(tree);
        }

        private void Cover(string? tree)
        {
            if (tree is not null) _reviewed = tree;
            _told = null;
            // Without git, the files the tools report from now on are the ones not reviewed.
            _changedFiles.Clear();
        }

        public void Note(JsonElement? details)
        {
            if (details is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("modifiedFiles", out var files) || files.ValueKind != JsonValueKind.Array) return;
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind == JsonValueKind.String && file.GetString() is { Length: > 0 } path && _changedFiles.Count < 256) _changedFiles.Add(path);
            }
        }
    }
}
