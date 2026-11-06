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
/// <param name="Timeout">The time the review is given.</param>
internal sealed record AdaptiveCriticRequest(string SessionId, ModelTierTarget Critic, string Prompt, TimeSpan Timeout);

/// <summary>What a critic answered.</summary>
/// <param name="Answer">The final answer of the review, or <see langword="null"/> when it gave none.</param>
/// <param name="Failure">Why the review did not complete, or <see langword="null"/>.</param>
internal sealed record AdaptiveCriticAnswer(string? Answer, string? Failure);

/// <summary>
/// Runs the turns of the sessions on Adaptive (<c>[models.adaptive]</c>), as the evaluation of November 2026 found best: a
/// turn the user starts runs on the model of the level of the session; when it changed code, the critics of the level,
/// models of another family, review the change read-only, each in a session of its own given the request and the diff,
/// and the session revises it once with what they found. A level without critics, or whose critics could not review,
/// has the model of the turn recheck its change against the request instead.
/// </summary>
/// <remarks>
/// It follows the events the runtime publishes, never in their way: what a run did is counted as it goes, and the end
/// of a run is decided on its own, once the send of the run has released the session. Whether a turn changed code is
/// read from git, a tree of the working folder taken before the turn and after it, so that a turn that commits what was
/// there changes nothing; a folder without git falls back on the files the tool calls say they changed. The session
/// keeps its own cache: the critics get only the request and the diff, as Copilot Fusion's critics do.
/// </remarks>
internal sealed partial class AdaptiveTurnCoordinator
{
    private const string LoggerName = "CodeAlta.Adaptive";
    private const int MaximumSessions = 1024;
    // What a review shows of the diff and of the findings it passes on; the rest is in the files.
    private const int MaximumDiff = 60_000;
    private const int MaximumFindings = 8_000;
    private const int MaximumRequest = 20_000;
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReleasePoll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
    // A review reads the code and runs tests: the measured ones took one to six minutes.
    private static readonly TimeSpan ReviewTime = TimeSpan.FromMinutes(10);
    // A tree taken for a send whose run never started is not the tree of a later turn.
    private static readonly TimeSpan SnapshotLife = TimeSpan.FromMinutes(10);

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
    /// Gets or sets what runs a review by a critic, in a read-only child session of the reviewed one; the host sets it.
    /// Without it, a level with critics has its change rechecked instead.
    /// </summary>
    internal Func<AdaptiveCriticRequest, CancellationToken, Task<AdaptiveCriticAnswer>>? Critic { get; set; }

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
            LogManager.GetLogger(LoggerName).Error(exception, "Adaptive could not follow an event of session " + envelope.SessionId + ".");
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

        if (turn is not null && provenance is AgentPromptProvenance.AdaptiveRecheck or AgentPromptProvenance.AdaptiveReview
                or AgentPromptProvenance.AdaptiveEscalation or AgentPromptProvenance.ModelFallback)
        {
            turn.StartRun(runId, provenance == AgentPromptProvenance.ModelFallback ? turn.Phase : provenance);
            return;
        }

        if (turn is null && _turns.Count >= MaximumSessions)
        {
            // Bounded: the oldest turn is forgotten, which only ends the follow-up of that turn.
            var oldest = _turns.MinBy(static pair => pair.Value.StartedAt).Key;
            _turns.Remove(oldest);
        }

        var fresh = new Turn(runId, Time.GetUtcNow()) { Request = Bounded(text, MaximumRequest) };
        if (_snapshots.Remove(sessionId, out var snapshot) && Time.GetUtcNow() - snapshot.At < SnapshotLife) fresh.Before = snapshot;
        _turns[sessionId] = fresh;
    }

    // Under _gate: the turn of a session while its current run is the one of an event (any run when the event or the turn
    // names none; the first event that names it gives the turn its run).
    private Turn? CurrentTurn(string sessionId, AgentRunId? runId)
    {
        if (!_turns.TryGetValue(sessionId, out var turn)) return null;
        if (runId is not { } named || turn.RunId == named.Value) return turn;
        if (turn.RunId is not null || !turn.Active) return null;
        turn.RunId = named.Value;
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
            LogManager.GetLogger(LoggerName).Error(exception, "Adaptive could not choose the model of a turn of session " + session.SessionId + ".");
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
                return;
            }

            var notice = await _runtime.ReadAdaptiveNoticeAsync(sessionId, _closing.Token).ConfigureAwait(false);
            if (notice is not { Adaptive: true, Scope.Adaptive: { } settings }) return;
            Facts facts;
            lock (_gate) facts = turn.Read();
            // A failed run is the fallback's; a recheck or a revision ends the turn.
            if (facts.Failed || facts.Phase is not null || facts.Checked) return;
            // A prompt that waits starts the next turn: the user, or another session, said what comes next.
            if (notice.HasQueuedPrompts) return;
            var change = await ChangeOfTurnAsync(facts).ConfigureAwait(false);
            if (change is null) return;
            lock (_gate) turn.Checked = true;
            var level = settings.Resolve(notice.Level);
            if (level is { Critics.Count: > 0 } && Critic is { } critic
                && await ReviewAsync(notice, level, critic, facts, change, runId).ConfigureAwait(false))
                return;
            await RecheckAsync(notice, runId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (exception is OperationCanceledException or ObjectDisposedException && _closing.IsCancellationRequested) return;
            LogManager.GetLogger(LoggerName).Error(exception, "Adaptive could not decide the end of a run of session " + sessionId + ".");
        }
    }

    private void Forget(string sessionId, Turn turn)
    {
        lock (_gate)
        {
            if (_turns.TryGetValue(sessionId, out var current) && ReferenceEquals(current, turn)) _turns.Remove(sessionId);
        }
    }

    // The code a turn changed: the paths, the diff when git gives it, and the tree after the turn; null when it changed
    // no code (documentation only, or nothing).
    private async Task<TurnChange?> ChangeOfTurnAsync(Facts facts)
    {
        if (facts.Before is { } before)
        {
            var after = await Snapshot(before.Folder, _closing.Token).ConfigureAwait(false);
            if (after is not null)
            {
                if (string.Equals(after, before.Tree, StringComparison.Ordinal)) return null;
                var changed = await Changes(before.Folder, before.Tree, after, _closing.Token).ConfigureAwait(false);
                if (changed is not null && !changed.Any(IsCode)) return null;
                var diff = await Diff(before.Folder, before.Tree, after, _closing.Token).ConfigureAwait(false);
                return new TurnChange(changed ?? facts.ChangedFiles, diff, before.Folder, after);
            }
        }

        return facts.ChangedFiles.Any(IsCode) ? new TurnChange(facts.ChangedFiles, null, facts.Before?.Folder, null) : null;
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

    // The critics of the level review the change at the same time; the session revises it once with what they found.
    // False when no critic could review: the change is then rechecked.
    private async Task<bool> ReviewAsync(AdaptiveSessionNotice notice, AdaptiveLevelSettings level, Func<AdaptiveCriticRequest, CancellationToken, Task<AdaptiveCriticAnswer>> critic,
        Facts facts, TurnChange change, string runId)
    {
        var prompt = CriticPrompt(facts.Request, change);
        var reviews = await Task.WhenAll(level.Critics.Select(async model =>
        {
            try { return (Model: model, Answer: await critic(new AdaptiveCriticRequest(notice.SessionId, model, prompt, ReviewTime), _closing.Token).ConfigureAwait(false)); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
            {
                LogManager.GetLogger(LoggerName).Error(exception, $"Adaptive could not run the review by {model.Format()} of session {notice.SessionId}.");
                return (Model: model, Answer: new AdaptiveCriticAnswer(null, exception.Message));
            }
        })).ConfigureAwait(false);

        // A critic is read-only: what changed in the files while it reviewed is said, never undone behind the user's back.
        if (change is { Folder: { } folder, After: { } after } && await Snapshot(folder, _closing.Token).ConfigureAwait(false) is { } now
            && !string.Equals(now, after, StringComparison.Ordinal))
        {
            var touched = await Changes(folder, after, now, _closing.Token).ConfigureAwait(false) ?? [];
            await WriteCardAsync(notice.SessionId, $"The review of Adaptive changed files it should only have read: {string.Join(", ", touched.Take(10))}.", "review").ConfigureAwait(false);
        }

        var findings = new List<(ModelTierTarget Model, string Text)>();
        var approved = new List<ModelTierTarget>();
        var silent = new List<(ModelTierTarget Model, string Why)>();
        foreach (var (model, answer) in reviews)
        {
            var (verdict, text) = ParseVerdict(answer.Answer);
            switch (verdict)
            {
                case "REVISE" when text.Length > 0: findings.Add((model, text)); break;
                case "APPROVE": approved.Add(model); break;
                default: silent.Add((model, answer.Failure ?? (answer.Answer is null ? "no answer" : "no verdict"))); break;
            }
        }

        if (silent.Count > 0)
            await WriteCardAsync(notice.SessionId, "Adaptive could not get a review from " + string.Join(", ", silent.Select(static item => $"{item.Model.Format()} ({item.Why})")) + ".",
                "review").ConfigureAwait(false);
        if (findings.Count == 0 && approved.Count == 0) return false;
        if (findings.Count == 0)
        {
            await WriteCardAsync(notice.SessionId, "Adaptive review: the change was approved by " + string.Join(", ", approved.Select(static model => model.Format())) + ".",
                "review", AgentSessionUpdateKind.Info).ConfigureAwait(false);
            return true;
        }

        if (!await WaitForReleaseAsync(notice.SessionId).ConfigureAwait(false)) return true;
        // The prompt is the card: the timeline titles it Adaptive review, with what each critic found.
        await AdmitAsync(new OwnedTextSendRequest($"adaptive-review:{notice.SessionId}:{runId}", notice.SessionId, RevisionPrompt(findings))
        {
            Provenance = AgentPromptProvenance.AdaptiveReview,
        }).ConfigureAwait(false);
        return true;
    }

    /// <summary>The prompt of a critic: the request, the change of the turn, what to check and the verdict to end with.</summary>
    internal static string CriticPrompt(string? request, TurnChange change)
    {
        var text = new StringBuilder();
        text.Append("[CodeAlta Adaptive]\nYou review a change that another engineer, another AI model, made to answer the request below. ");
        text.Append("You are read-only: do not modify, create or delete any file of the project; a scratch copy outside it is fine. ");
        text.Append("Read the code, and run the tests or the commands you need to check the change.\n\n## The request\n\n");
        text.Append(string.IsNullOrWhiteSpace(request) ? "(The request is in the session that made the change; judge the change on its own.)" : request.Trim());
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

    /// <summary>The prompt that gives the session what the critics found, for it to check and fix.</summary>
    internal static string RevisionPrompt(IReadOnlyList<(ModelTierTarget Model, string Text)> findings)
    {
        var text = new StringBuilder("[CodeAlta Adaptive]\nReviewers of other models checked your change against the request, read-only. What they found:\n");
        foreach (var (model, found) in findings) text.Append("\n### ").Append(model.Format()).Append("\n\n").Append(Bounded(found, MaximumFindings)).Append('\n');
        text.Append("\nCheck each finding yourself. Fix the ones that are real and keep the tests passing; leave the ones that are wrong. Then answer as you would have.\n");
        text.Append("This message comes from CodeAlta, not from the user.");
        return text.ToString();
    }

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
                    LogManager.GetLogger(LoggerName).Warn($"Adaptive could not continue session {request.SessionId} ({admission.Kind}).");
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

    // A card of the timeline; its details name what Adaptive did, which the timeline titles it by.
    private async Task WriteCardAsync(string sessionId, string message, string what, AgentSessionUpdateKind kind = AgentSessionUpdateKind.Warning)
    {
        var session = await _runtime.ResolveOwnedSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (session is null) return;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("adaptive", what);
            writer.WriteEndObject();
        }

        var details = JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
        await _runtime.AppendSessionEventAsync(session, new AgentSessionUpdateEvent(
            new ModelProviderId(session.ResolvedProviderKey), sessionId, Time.GetUtcNow(), null, kind, message, details), CancellationToken.None).ConfigureAwait(false);
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
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? text : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
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
    }

    // A verdict line of a review: `VERDICT: APPROVE`, `**Verdict: revise**`.
    [GeneratedRegex(@"^\W*VERDICT\W*:?\W*(APPROVE|REVISE)\b\W*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VerdictPattern();

    // The tree of a working folder before a turn.
    private sealed record TreeSnapshot(string Folder, string Tree, DateTimeOffset At);

    /// <summary>The code a turn changed.</summary>
    /// <param name="Paths">The paths it changed.</param>
    /// <param name="Diff">The diff of the turn, when git gives it.</param>
    /// <param name="Folder">The working folder, when git tells the change.</param>
    /// <param name="After">The tree after the turn, when git tells the change.</param>
    internal sealed record TurnChange(IReadOnlyList<string> Paths, string? Diff, string? Folder, string? After);

    // What the decision reads of a turn, copied under the gate.
    private sealed record Facts(string? Phase, bool Failed, bool Checked, string? Request, TreeSnapshot? Before, IReadOnlyList<string> ChangedFiles);

    // A turn the user started, with the recheck or the revision that continues it. Read and written under the gate.
    private sealed class Turn(string? runId, DateTimeOffset startedAt)
    {
        private readonly HashSet<string> _changedFiles = new(StringComparer.OrdinalIgnoreCase);

        public string? RunId { get; set; } = runId;

        // From the prompt of a run until its idle event: a prompt meanwhile steers the run.
        public bool Active { get; set; } = true;

        public DateTimeOffset StartedAt { get; } = startedAt;

        // Null for the run the user started; the provenance of a recheck or a revision.
        public string? Phase { get; private set; }

        public string? DecidedRun { get; set; }

        public TreeSnapshot? Before { get; set; }

        // The request of the user, which the critics check the change against.
        public string? Request { get; set; }

        // Whether the change of the turn was rechecked or reviewed: a turn is checked once.
        public bool Checked { get; set; }

        public bool Failed { get; set; }

        public void StartRun(string? runId, string? phase)
        {
            RunId = runId;
            Active = true;
            Phase = phase;
            Failed = false;
        }

        public Facts Read() => new(Phase, Failed, Checked, Request, Before, [.. _changedFiles]);

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
