using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Runtime.Plugins;
using XenoAtom.Logging;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Chooses the model of the turns of the sessions on Adaptive (<c>[models.adaptive]</c>), as the evaluation of #273 found
/// that agents do not change their model by themselves: a turn the user starts runs on the turn tier; a turn that
/// changed code is asked once to check its change against the request, on the same model; a turn that ends in visible
/// trouble (no answer, a refusal to use tools it did not call, a last test run that failed) continues once on the
/// escalation tier, and the next turn starts again on the turn tier.
/// </summary>
/// <remarks>
/// It follows the events the runtime publishes, never in their way: what a run did is counted as it goes, and the end
/// of a run is decided on its own, once the send of the run has released the session. Whether a turn changed code is
/// read from git, a tree of the working folder taken before the turn and after it, so that a turn that commits what was
/// there changes nothing; a folder without git falls back on the files the tool calls say they changed.
/// </remarks>
internal sealed partial class AdaptiveTurnCoordinator
{
    private const string LoggerName = "CodeAlta.Adaptive";
    private const int MaximumSessions = 1024;
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReleasePoll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
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
    }

    /// <summary>Gets or sets the clock; the system clock unless a test sets another.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Gets or sets what takes the tree of a working folder: its hash, or null when the folder has no git.</summary>
    internal Func<string, CancellationToken, Task<string?>> Snapshot { get; set; }

    /// <summary>Gets or sets what lists the paths that differ between two trees of a folder; null when it cannot tell.</summary>
    internal Func<string, string, string, CancellationToken, Task<IReadOnlyList<string>?>> Changes { get; set; }

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
                    StartRun(sessionId, user.RunId?.Value, user.Provenance);
                    return;
                case AgentActivityEvent activity when IsToolActivity(activity.Kind) && !string.IsNullOrEmpty(activity.ActivityId)
                    && CurrentTurn(sessionId, activity.RunId) is { } turn:
                    turn.Note(activity);
                    return;
                case AgentContentCompletedEvent { Kind: AgentContentKind.ToolOutput } output when CurrentTurn(sessionId, output.RunId) is { } turn:
                    turn.Note(output);
                    return;
                case AgentContentCompletedEvent { Kind: AgentContentKind.Assistant } answer when CurrentTurn(sessionId, answer.RunId) is { } turn:
                    if (!string.IsNullOrWhiteSpace(answer.Content)) turn.Answer = answer.Content;
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
    internal void BeginRun(string sessionId, string? provenance)
    {
        lock (_gate)
        {
            if (!_closing.IsCancellationRequested) StartRun(sessionId, runId: null, provenance, sent: true);
        }
    }

    // Under _gate. A prompt starts a run: a new turn, unless it steers the run that goes on, or continues the turn
    // (a recheck, an escalation, the continuation of a fallback). A provider may name the run only in its later events.
    // A prompt the host sends always starts a run; the user message the run then publishes is the same run.
    private void StartRun(string sessionId, string? runId, string? provenance, bool sent = false)
    {
        var turn = _turns.GetValueOrDefault(sessionId);
        if (!sent && turn is { Active: true } && (runId is null || turn.RunId is null || turn.RunId == runId)) return;
        if (turn is not null && provenance is AgentPromptProvenance.AdaptiveRecheck or AgentPromptProvenance.AdaptiveEscalation or AgentPromptProvenance.ModelFallback)
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

        var fresh = new Turn(runId, Time.GetUtcNow());
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
    /// Chooses the model of a turn the user starts in a session: the session on Adaptive takes the first ready model of
    /// the turn tier that its provider has, and the tree of its working folder is taken to tell, at the end of the turn,
    /// whether it changed code. Never throws: what cannot be read keeps the selection sent.
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

            // A send keeps the provider of the session: the turn tier gives the model it has there.
            var choice = Select(notice.Scope, settings.Code, onProvider: session.ResolvedProviderKey);
            if (choice is null) return selection;
            if (notice.Tier != settings.Code) await _runtime.SetSessionTierAsync(session.SessionId, settings.Code, cancellationToken).ConfigureAwait(false);
            return (selection ?? new OwnedSessionSelection(session.ResolvedProviderKey, notice.AgentPromptId ?? "default", null, null))
                with { ModelId = choice.Target.ModelId, ReasoningEffort = choice.Reasoning };
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
            if (facts.Failed) return;
            if (facts.Phase == AgentPromptProvenance.AdaptiveEscalation)
            {
                await ReturnToTurnTierAsync(notice, settings).ConfigureAwait(false);
                return;
            }

            // A prompt that waits starts the next turn: the user, or another session, said what comes next.
            if (notice.HasQueuedPrompts) return;
            if (facts.Phase is null && settings.Recheck && !facts.Rechecked && notice.Tier is not ModelTier.Complex
                && await ChangedCodeAsync(facts).ConfigureAwait(false))
            {
                lock (_gate) turn.Rechecked = true;
                await RecheckAsync(notice, runId).ConfigureAwait(false);
                return;
            }

            if (!facts.Escalated && notice.Tier != settings.Escalate && Trouble(facts.Answer, facts.ToolCalls, facts.LastTestExit) is { } sign)
            {
                lock (_gate) turn.Escalated = true;
                await EscalateAsync(notice, settings, sign, runId).ConfigureAwait(false);
            }
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

    /// <summary>
    /// A sign of trouble at the end of a turn that the host sees without knowing the task, as the evaluation measured
    /// it: the turn ended without an answer, its answer says it cannot use tools though it called none, or the last test
    /// command of the turn failed. Null when the turn looks done.
    /// </summary>
    internal static string? Trouble(string? answer, int toolCalls, int? lastTestExit)
    {
        if (string.IsNullOrWhiteSpace(answer)) return "the turn ended without an answer";
        if (toolCalls == 0 && RefusalPattern().IsMatch(answer)) return "the answer says the tools cannot be used, though it called none";
        // A failure read from the summary of the runner counts as 1: the code is said only when the command gave another.
        if (lastTestExit is { } code and not 0) return code == 1 ? "the last test run failed" : $"the last test run failed (exit code {code})";
        return null;
    }

    private async Task<bool> ChangedCodeAsync(Facts facts)
    {
        if (facts.Before is { } before)
        {
            var after = await Snapshot(before.Folder, _closing.Token).ConfigureAwait(false);
            if (after is not null)
            {
                if (string.Equals(after, before.Tree, StringComparison.Ordinal)) return false;
                var changed = await Changes(before.Folder, before.Tree, after, _closing.Token).ConfigureAwait(false);
                return changed?.Any(IsCode) ?? true;
            }
        }

        return facts.ChangedFiles.Any(IsCode);
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

    private async Task EscalateAsync(AdaptiveSessionNotice notice, AdaptiveModelSettings settings, string sign, string runId)
    {
        var choice = Select(notice.Scope, settings.Escalate, onProvider: null);
        if (choice is null)
        {
            await WriteCardAsync(notice.SessionId, $"Adaptive did not continue the turn ({sign}): no model of the {ModelTiers.NameOf(settings.Escalate)} tier is ready.",
                "escalation", AgentSessionUpdateKind.Warning).ConfigureAwait(false);
            return;
        }

        if (!await WaitForReleaseAsync(notice.SessionId).ConfigureAwait(false)) return;
        var target = choice.Target;
        string? permissionMode = null;
        if (!string.Equals(target.ProviderKey, notice.ProviderKey, StringComparison.OrdinalIgnoreCase))
        {
            permissionMode = _commands.Fallback.KeepOrNarrow(notice.PermissionMode, notice.ProviderKey, target.ProviderKey);
            var switched = await SwitchProviderAsync(notice.SessionId, target.ProviderKey,
                new SessionViewProviderCarryOver(target.ModelId, choice.Reasoning, permissionMode)).ConfigureAwait(false);
            if (switched != "ok")
            {
                await WriteCardAsync(notice.SessionId, $"Adaptive did not continue the turn ({sign}): the switch to {choice.ProviderDisplayName} was refused ({switched}).",
                    "escalation", AgentSessionUpdateKind.Warning).ConfigureAwait(false);
                return;
            }
        }

        await _runtime.SetSessionTierAsync(notice.SessionId, settings.Escalate, _closing.Token).ConfigureAwait(false);
        var to = $"{choice.ProviderDisplayName} {choice.ModelDisplayName}{(choice.Reasoning is { } effort ? " " + CodeAltaConfigStore.FormatReasoningEffort(effort) : string.Empty)}";
        // The prompt is the card: the timeline titles it Adaptive escalation, and it says why and on which model.
        await AdmitAsync(new OwnedTextSendRequest($"adaptive-escalation:{notice.SessionId}:{runId}", notice.SessionId, EscalationPrompt(sign, to))
        {
            Selection = new OwnedSessionSelection(target.ProviderKey, notice.AgentPromptId ?? "default", target.ModelId, choice.Reasoning) { PermissionMode = permissionMode },
            Provenance = AgentPromptProvenance.AdaptiveEscalation,
        }).ConfigureAwait(false);
    }

    // After an escalation, the session goes back to the turn tier: on its provider, the next turn takes the model
    // there; on another one, the session switches back now, while it is idle.
    private async Task ReturnToTurnTierAsync(AdaptiveSessionNotice notice, AdaptiveModelSettings settings)
    {
        var choice = Select(notice.Scope, settings.Code, onProvider: notice.ProviderKey) ?? Select(notice.Scope, settings.Code, onProvider: null);
        if (choice is not null && !notice.HasQueuedPrompts && !string.Equals(choice.Target.ProviderKey, notice.ProviderKey, StringComparison.OrdinalIgnoreCase))
        {
            if (!await WaitForReleaseAsync(notice.SessionId).ConfigureAwait(false)) return;
            var permissionMode = _commands.Fallback.KeepOrNarrow(notice.PermissionMode, notice.ProviderKey, choice.Target.ProviderKey);
            var switched = await SwitchProviderAsync(notice.SessionId, choice.Target.ProviderKey,
                new SessionViewProviderCarryOver(choice.Target.ModelId, choice.Reasoning, permissionMode)).ConfigureAwait(false);
            if (switched != "ok") return;
        }

        await _runtime.SetSessionTierAsync(notice.SessionId, settings.Code, _closing.Token).ConfigureAwait(false);
    }

    // The first ready target of a tier, among those of a provider when one is named.
    private ModelTierChoice? Select(ModelTierScope scope, ModelTier tier, string? onProvider)
    {
        var states = _commands.Fallback.ProviderStates?.Invoke() ?? [];
        if (onProvider is not null)
        {
            IReadOnlyList<ModelTierTarget> Kept(ModelTier which)
                => which == tier ? [.. scope.Tiers.Get(which).Where(target => string.Equals(target.ProviderKey, onProvider, StringComparison.OrdinalIgnoreCase))] : [];
            scope = scope with { Tiers = new ModelTierSettings(Kept(ModelTier.Complex), Kept(ModelTier.Standard), Kept(ModelTier.Quick)) };
        }

        return ModelTierSelection.SelectForNewSession(scope, tier, states, key => _commands.SelectionProvider?.Invoke(new ModelProviderId(key)), requestedReasoning: null);
    }

    internal static string EscalationPrompt(string sign, string to)
        => $"""
            [CodeAlta Adaptive]
            The last turn ended in trouble: {sign}. CodeAlta moved this session to {to} to finish it.
            Check what was done (the files, git status, the tests), then finish the request before this message. Do not redo what is already done and right.
            This message comes from CodeAlta, not from the user.
            """;

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

    private async Task<string> SwitchProviderAsync(string sessionId, string providerKey, SessionViewProviderCarryOver carryOver)
    {
        var deadline = Time.GetUtcNow() + ReleaseWait;
        while (true)
        {
            var context = await _commands.GetProviderSelectionAsync(sessionId).ConfigureAwait(false);
            var result = context is null ? "busy" : await _commands.SelectProviderAsync(context, providerKey, carryOver).ConfigureAwait(false);
            if (result is not ("busy" or "session_in_use" or "stale_selection" or "stale_attachment") || Time.GetUtcNow() >= deadline) return result;
            await Task.Delay(ReleasePoll, _closing.Token).ConfigureAwait(false);
        }
    }

    // A warning of the timeline, when Adaptive could not continue a turn; its details name what it tried, which the
    // timeline titles it by.
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

    // A command that runs tests, as the evaluation recognized them in the title of a tool call.
    [GeneratedRegex(@"\b(npm (run )?test|yarn test|pnpm test|node --test|npx (jest|vitest|mocha)|jest|vitest|mocha|pytest|python3? -m pytest|tox|go test|cargo test|dotnet test|mvn( -\S+)* test|gradle test|make test|phpunit|rspec|bundle exec rspec)\b")]
    private static partial Regex TestCommandPattern();

    // The first line of the output of CodeAlta's shell tool.
    [GeneratedRegex(@"^exit_code: (-?\d+)", RegexOptions.Multiline)]
    private static partial Regex ExitCodePattern();

    // A summary that says tests failed: `ℹ fail 1` and `# fail 1` (node), `1 failed`, `2 failing`, `1 failure`, `Failed: 1`
    // (dotnet test), a line `FAIL ...` (Go, Jest), `test result: FAILED` (Cargo).
    [GeneratedRegex(@"^\s*(ℹ|#)\s*fail\s+[1-9]\d*\b|\b[1-9]\d*\s+(failed|failing|failures?)\b|\bFailed:\s*[1-9]\d*\b|^(--- )?FAIL\b|test result: FAILED", RegexOptions.Multiline)]
    private static partial Regex TestsFailedPattern();

    // A summary that says no test failed: `ℹ fail 0`, `0 failed`, `Failed: 0`, `Passed!`, `test result: ok`, `ok  ` (Go), `N passed`.
    [GeneratedRegex(@"^\s*(ℹ|#)\s*fail\s+0\b|\b0\s+(failed|failures)\b|\bFailed:\s*0\b|\bPassed!|test result: ok\b|^ok\s+\S|\b[1-9]\d*\s+passed\b|\b[1-9]\d*\s+passing\b", RegexOptions.Multiline)]
    private static partial Regex TestsPassedPattern();

    // An answer that says the tools cannot be used: only a refusal about tools, files or access, never a plain "cannot".
    [GeneratedRegex(@"\b(cannot|can't|can not|unable to|do not have|don't have|no)\b[^.\n]{0,40}\b(access|tools?|run commands?|execute|read (the )?files?|edit (the )?files?|file ?system)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RefusalPattern();

    internal static bool IsTestCommand(string? name, JsonElement? details)
    {
        string? command = null;
        if (details is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("command", out var written) && written.ValueKind == JsonValueKind.String)
            command = written.GetString();
        return TestCommandPattern().IsMatch(command ?? name ?? string.Empty);
    }

    /// <summary>
    /// Reads how a test run ended from the summary its runner writes (node, Jest, Vitest, Mocha, pytest, dotnet test, Go,
    /// Cargo, RSpec): 1 when tests failed, 0 when it says none did, null when the output says neither.
    /// </summary>
    internal static int? TestOutcome(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        if (TestsFailedPattern().IsMatch(output)) return 1;
        return TestsPassedPattern().IsMatch(output) ? 0 : null;
    }

    internal static int? ExitCode(string? output)
        => output is not null && ExitCodePattern().Match(output) is { Success: true } match && int.TryParse(match.Groups[1].ValueSpan, out var code) ? code : null;

    // The tree of a working folder before a turn.
    private sealed record TreeSnapshot(string Folder, string Tree, DateTimeOffset At);

    // What the decision reads of a turn, copied under the gate.
    private sealed record Facts(string? Phase, bool Failed, string? Answer, int ToolCalls, int? LastTestExit, bool Rechecked, bool Escalated,
        TreeSnapshot? Before, IReadOnlyList<string> ChangedFiles);

    // A turn the user started, with the recheck and the escalation that continue it. Read and written under the gate.
    private sealed class Turn(string? runId, DateTimeOffset startedAt)
    {
        private readonly HashSet<string> _calls = new(StringComparer.Ordinal);
        private readonly HashSet<string> _tests = new(StringComparer.Ordinal);
        private readonly HashSet<string> _exitsSeen = new(StringComparer.Ordinal);
        private readonly HashSet<string> _changedFiles = new(StringComparer.OrdinalIgnoreCase);

        public string? RunId { get; set; } = runId;

        // From the prompt of a run until its idle event: a prompt meanwhile steers the run.
        public bool Active { get; set; } = true;

        public DateTimeOffset StartedAt { get; } = startedAt;

        // Null for the run the user started; the provenance of a recheck or an escalation.
        public string? Phase { get; private set; }

        public string? DecidedRun { get; set; }

        public TreeSnapshot? Before { get; set; }

        public bool Rechecked { get; set; }

        public bool Escalated { get; set; }

        public bool Failed { get; set; }

        public string? Answer { get; set; }

        // Of the current run only: a refusal is read in the run that refuses.
        public int ToolCalls { get; private set; }

        // Of the whole turn: the last test run decides, whichever run ran it.
        public int? LastTestExit { get; private set; }

        public void StartRun(string? runId, string? phase)
        {
            RunId = runId;
            Active = true;
            Phase = phase;
            Answer = null;
            ToolCalls = 0;
            Failed = false;
        }

        public void Note(AgentActivityEvent activity)
        {
            if (_calls.Add(activity.ActivityId))
            {
                ToolCalls++;
                if (IsTestCommand(activity.Name, activity.Details)) _tests.Add(activity.ActivityId);
            }

            NoteFiles(activity.Details);
            // A call whose output said no exit code ends as it says: failed or not.
            if (activity.Phase is AgentActivityPhase.Completed or AgentActivityPhase.Failed && _tests.Contains(activity.ActivityId) && !_exitsSeen.Contains(activity.ActivityId))
                LastTestExit = activity.Phase == AgentActivityPhase.Completed ? 0 : 1;
        }

        public void Note(AgentContentCompletedEvent output)
        {
            NoteFiles(output.Details);
            // What the test runner says of its tests wins over the exit code, which a pipe (`npm test | grep`) hides.
            if (output.ParentActivityId is { Length: > 0 } callId && _tests.Contains(callId) && (TestOutcome(output.Content) ?? ExitCode(output.Content)) is { } code)
            {
                LastTestExit = code;
                _exitsSeen.Add(callId);
            }
        }

        public Facts Read()
            => new(Phase, Failed, Answer, ToolCalls, LastTestExit, Rechecked, Escalated, Before, [.. _changedFiles]);

        private void NoteFiles(JsonElement? details)
        {
            if (details is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("modifiedFiles", out var files) || files.ValueKind != JsonValueKind.Array) return;
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind == JsonValueKind.String && file.GetString() is { Length: > 0 } path && _changedFiles.Count < 256) _changedFiles.Add(path);
            }
        }
    }
}
