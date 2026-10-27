using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using CodeAlta.Agent;
using CodeAlta.CodeMode;

namespace CodeAlta.Orchestration.Runtime.Scripts;

/// <summary>What a session is told of a run it started.</summary>
/// <param name="Id">The id of the run, such as <c>run-7f3c2a</c>.</param>
/// <param name="SessionId">The session that runs it.</param>
/// <param name="Title">Its title, or null.</param>
/// <param name="Elapsed">How long it has run.</param>
/// <param name="Calls">The calls it made.</param>
/// <param name="Running">Whether it still runs.</param>
public sealed record CodeModeRunInfo(string Id, string SessionId, string? Title, TimeSpan Elapsed, int Calls, bool Running)
{
    /// <summary>Gets when it started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Gets when it ended, or null while it runs.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Gets the tool call that runs it (<c>exec</c>), or null.</summary>
    public string? CallId { get; init; }

    /// <summary>Gets how it ended (<c>completed</c>, <c>failed</c> or <c>cancelled</c>), or null while it runs.</summary>
    public string? Status { get; init; }
}

/// <summary>What the tab of a run shows besides its records.</summary>
/// <param name="Info">The run.</param>
/// <param name="Source">Its script, or null when it is not kept.</param>
/// <param name="Result">What its session was given when it ended, or null while it runs or when it is not kept.</param>
public sealed record CodeModeRunDetail(CodeModeRunInfo Info, string? Source, string? Result);

/// <summary>
/// The runs of the scripts of a host. <c>exec</c> waits for a run up to its yield; a run still going then goes on in the
/// background, and its result comes back to its session as a prompt (provenance <c>workflow</c>) unless a <c>wait</c>
/// collected it first. The limits count runs: a run over a limit is refused with the limit's name, never queued.
/// </summary>
public sealed class CodeModeRunRegistry : IAsyncDisposable, ISessionScriptRuns
{
    private readonly ConcurrentDictionary<string, CodeModeRunHandle> _runs = new(StringComparer.Ordinal);
    private readonly Func<string, string, CancellationToken, Task>? _deliver;
    private readonly Func<string, IDisposable?>? _hold;
    private readonly ConcurrentDictionary<string, byte> _codeModeSessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Occurs when a run ended and its result was given, with the id of the run.</summary>
    public event Action<string>? RunEnded;

    /// <summary>
    /// Gets or sets what writes the records of a run in the journal of its session (<see cref="AgentWorkflowEvent"/>): the
    /// session, the record. Null writes none. The records of a run are written in their order, before its result is given.
    /// </summary>
    public Func<string, AgentWorkflowEvent, Task>? Journal { get; set; }

    /// <summary>Gets the durable record of the runs, for a resume after a restart; null for a host that keeps none (<c>altacli</c>).</summary>
    public CodeModeRunStore? Store { get; private set; }

    private readonly int _maxPerSession;
    private readonly int _maxPerHost;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Creates the registry of a host.</summary>
    /// <param name="deliver">Delivers the result of a run that went on in the background to its session: the session, the prompt. Null for a host without sessions to deliver to.</param>
    /// <param name="maxPerSession">The most runs of one session at the same time.</param>
    /// <param name="maxPerHost">The most runs of the host at the same time.</param>
    public CodeModeRunRegistry(Func<string, string, CancellationToken, Task>? deliver, int maxPerSession = 4, int maxPerHost = 32)
        : this(deliver, hold: null, maxPerSession, maxPerHost)
    {
    }

    /// <summary>Creates the registry of a host whose sessions a run holds while it runs.</summary>
    /// <param name="deliver">Delivers the result of a run that went on in the background to its session: the session, the prompt. Null for a host without sessions to deliver to.</param>
    /// <param name="hold">Holds the session of a run until its result is given (<see cref="SessionRuntimeService.TryHoldForScript"/>): the session is not settled meanwhile. Null when nothing holds; a null hold refuses the run.</param>
    /// <param name="maxPerSession">The most runs of one session at the same time.</param>
    /// <param name="maxPerHost">The most runs of the host at the same time.</param>
    public CodeModeRunRegistry(Func<string, string, CancellationToken, Task>? deliver, Func<string, IDisposable?>? hold, int maxPerSession = 4, int maxPerHost = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPerSession);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPerHost);
        _deliver = deliver;
        _hold = hold;
        _maxPerSession = maxPerSession;
        _maxPerHost = maxPerHost;
    }

    /// <summary>
    /// Gives a session the tools of Code Mode whatever the setting says, for as long as the host runs: the session of an
    /// automation that runs a script.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is null or blank.</exception>
    public void RequireCodeMode(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _codeModeSessions.TryAdd(sessionId, 0);
    }

    /// <summary>Gets whether a session has the tools of Code Mode whatever the setting says (<see cref="RequireCodeMode"/>).</summary>
    /// <param name="sessionId">The session, or null.</param>
    /// <returns>True when it has them.</returns>
    public bool RequiresCodeMode(string? sessionId) => sessionId is not null && _codeModeSessions.ContainsKey(sessionId);

    /// <summary>
    /// Keeps the runs in a store from now on, for a resume after a restart. The runs the store has without an end ran when
    /// CodeAlta ended: they are marked interrupted at once, and their sessions get the end of each in their journal, so that
    /// its card offers <b>Resume</b>. Called once, while the host starts and before a script runs.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <returns>A task that completes when the journals were written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The registry already has a store.</exception>
    public Task UseStoreAsync(CodeModeRunStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<CodeModeStoredRun> interrupted;
        lock (_gate)
        {
            if (Store is not null)
            {
                throw new InvalidOperationException("The runs already have a store.");
            }

            Store = store;
            interrupted = store.MarkInterrupted(_runs.ContainsKey);
        }

        return Task.Run(async () =>
        {
            store.Prune();
            // The runs that stopped while they slept wake on time, also those of an earlier start: the store is the schedule.
            var wakes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var sleeping in store.ListSleepingInterrupted())
            {
                if (CodeModeResume.AutoResumeTime(store, sleeping, out _) is { } wakeAt)
                {
                    wakes[sleeping.Id] = wakeAt;
                    _ = ResumeAtAsync(store, sleeping.Id, wakeAt, _lifetime.Token);
                }
            }

            if (Journal is not { } journal)
            {
                return;
            }

            foreach (var run in interrupted)
            {
                var wakeAt = wakes.TryGetValue(run.Id, out var at) ? at : (DateTimeOffset?)null;
                try
                {
                    await journal(run.SessionId, new AgentWorkflowEvent(new ModelProviderId("codealta"), run.SessionId, DateTimeOffset.UtcNow, null, AgentWorkflowEventKind.RunEnded, run.Id)
                    {
                        CallId = run.CallId,
                        Status = "interrupted",
                        Message = wakeAt is null ? "CodeAlta ended while the script ran. It can be resumed." : "CodeAlta ended while the script slept: it resumes by itself when it wakes.",
                        WakeAt = wakeAt,
                    }).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
                {
                }
            }
        });
    }

    // The one automatic resume: at the wake time of the sleep a run stopped in, its session is asked to resume it, as Resume
    // asks, once, unless it no longer qualifies (a resume of the user took it over, its script or its calls forbid it).
    private async Task ResumeAtAsync(CodeModeRunStore store, string runId, DateTimeOffset wakeAt, CancellationToken cancellationToken)
    {
        try
        {
            var left = wakeAt - DateTimeOffset.UtcNow;
            if (left > TimeSpan.Zero)
            {
                await Task.Delay(left, cancellationToken).ConfigureAwait(false);
            }

            if (store.ListSleepingInterrupted().FirstOrDefault(run => run.Id == runId) is not { } run
                || CodeModeResume.AutoResumeTime(store, run, out _) is null || _deliver is null || !store.MarkAutoResumed(runId))
            {
                return;
            }

            await _deliver(run.SessionId, CodeModeResume.Prompt(runId, null, all: false), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or ObjectDisposedException or IOException)
        {
            // CodeAlta closes, or the session is gone: the run stays interrupted, with Resume.
        }
    }

    // Ends the timers of the automatic resumes when the host closes.
    private readonly CancellationTokenSource _lifetime = new();

    /// <inheritdoc />
    public IReadOnlyCollection<string> ListSessionsWithRunningScripts()
        => [.. _runs.Values.Where(static run => run.Running).Select(static run => run.SessionId).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc />
    public IReadOnlyList<SessionRuntimeBackgroundTask> ListTasks(string sessionId)
        // A script its turn waits for is part of the turn: only one that went on in the background is a background task.
        => [.. _runs.Values.Where(run => run.Backgrounded && string.Equals(run.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static run => run.Running).ThenByDescending(static run => run.StartedAt).Take(8)
            .Select(static run => new SessionRuntimeBackgroundTask(run.Id, "workflow", run.Title ?? "Script", run.CallId, run.StartedAt, run.Outcome)
            {
                EndedAt = run.EndedAt,
            })];

    /// <inheritdoc />
    public Task StopAsync(string sessionId)
    {
        var runs = _runs.Values.Where(run => run.Running && string.Equals(run.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var run in runs)
        {
            // The tree of the session was stopped: its result is not given back to it, which would start a new turn.
            run.Silence();
            run.Stop();
        }

        return Task.WhenAll(runs.Select(static run => run.Completion.ContinueWith(static _ => { }, TaskScheduler.Default)));
    }

    /// <summary>Lists the runs of a session, or of the host.</summary>
    /// <param name="sessionId">The session, or null for every session.</param>
    /// <returns>The runs, running or not yet collected.</returns>
    public IReadOnlyList<CodeModeRunInfo> List(string? sessionId = null)
        => [.. _runs.Values.Where(run => sessionId is null || run.SessionId == sessionId).Select(static run => run.Info())];

    /// <summary>
    /// Lists the runs of a session, or of the host, that this process knows or the store keeps (<see cref="Store"/>): after
    /// a restart, the runs that ended before it. The store is the index: nothing reads a journal.
    /// </summary>
    /// <param name="sessionId">The session, or null for every session.</param>
    /// <returns>The runs, those of this process first.</returns>
    public IReadOnlyList<CodeModeRunInfo> ListKnown(string? sessionId = null)
    {
        var known = List(sessionId).ToList();
        if (Store is { } store)
        {
            var listed = known.Select(static run => run.Id).ToHashSet(StringComparer.Ordinal);
            known.AddRange(store.List().Where(run => !listed.Contains(run.Id) && (sessionId is null || string.Equals(run.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
                .Select(InfoOf));
        }

        return known;
    }

    /// <summary>
    /// Reads what the tab of a run shows besides its records: the run, its script (the store keeps it), and what its session
    /// was given once it ended.
    /// </summary>
    /// <param name="runId">The id of the run.</param>
    /// <returns>The run, or null when neither this process nor the store knows it.</returns>
    public async Task<CodeModeRunDetail?> ReadDetailAsync(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        await WhenKeptAsync(runId).ConfigureAwait(false);
        if (TryInfo(runId) is not { } info)
        {
            return null;
        }

        var source = Store?.TryLoad(runId)?.Source;
        var result = FindAny(runId) is { Completion.IsCompletedSuccessfully: true } run
            ? CodeModeResultFormatter.Format(run.Completion.Result, run.MaxOutputTokens)
            : Store?.TryReadResult(runId);
        return new CodeModeRunDetail(info, string.IsNullOrEmpty(source) ? null : source, result);
    }

    /// <summary>Describes a run this process knows, or one the store keeps.</summary>
    /// <param name="runId">The id of the run.</param>
    /// <returns>The run, or null when neither has it.</returns>
    public CodeModeRunInfo? TryInfo(string runId)
        => FindAny(runId)?.Info() ?? (Store?.TryLoad(runId) is { } stored ? InfoOf(stored) : null);

    // A run of the store, which no longer runs here: one without an end ran when CodeAlta ended.
    private static CodeModeRunInfo InfoOf(CodeModeStoredRun run)
        => new(run.Id, run.SessionId, run.Title, (run.EndedAt ?? run.StartedAt) - run.StartedAt, 0, false)
        {
            StartedAt = run.StartedAt,
            EndedAt = run.EndedAt ?? run.StartedAt,
            CallId = run.CallId,
            Status = run.EndedAt is null || run.Status == "running" ? "interrupted" : run.Status,
        };

    /// <summary>Starts a run, unless a limit refuses it.</summary>
    /// <param name="sessionId">The session that runs it.</param>
    /// <param name="title">Its title, or null.</param>
    /// <param name="maxOutputTokens">The output budget of its result.</param>
    /// <param name="run">Runs the script, with the observer of the run and its token.</param>
    /// <param name="refusal">Why it was refused.</param>
    /// <returns>The run, or null when <paramref name="refusal"/> is set.</returns>
    public CodeModeRunHandle? TryStart(
        string sessionId,
        string? title,
        int maxOutputTokens,
        Func<ICodeModeRunObserver, CancellationToken, Task<CodeModeRunResult>> run,
        out string? refusal)
        => TryStart(sessionId, title, maxOutputTokens, callId: null, run, out refusal);

    /// <summary>Starts a run of a tool call, unless a limit refuses it.</summary>
    /// <param name="sessionId">The session that runs it.</param>
    /// <param name="title">Its title, or null.</param>
    /// <param name="maxOutputTokens">The output budget of its result.</param>
    /// <param name="callId">The tool call that runs it (<c>exec</c>), or null.</param>
    /// <param name="run">Runs the script, with the observer of the run and its token.</param>
    /// <param name="refusal">Why it was refused.</param>
    /// <returns>The run, or null when <paramref name="refusal"/> is set.</returns>
    public CodeModeRunHandle? TryStart(
        string sessionId,
        string? title,
        int maxOutputTokens,
        string? callId,
        Func<ICodeModeRunObserver, CancellationToken, Task<CodeModeRunResult>> run,
        out string? refusal)
        => TryStart(sessionId, title, maxOutputTokens, callId, start: null, run, out refusal);

    /// <summary>
    /// Starts a run of a tool call that the store keeps for a resume (<see cref="Store"/>), unless a limit refuses it.
    /// </summary>
    /// <param name="sessionId">The session that runs it.</param>
    /// <param name="title">Its title, or null.</param>
    /// <param name="maxOutputTokens">The output budget of its result.</param>
    /// <param name="callId">The tool call that runs it (<c>exec</c>), or null.</param>
    /// <param name="start">What the store keeps of it, or null for a run it does not keep.</param>
    /// <param name="run">Runs the script, with the observer of the run and its token.</param>
    /// <param name="refusal">Why it was refused.</param>
    /// <returns>The run, or null when <paramref name="refusal"/> is set.</returns>
    public CodeModeRunHandle? TryStart(
        string sessionId,
        string? title,
        int maxOutputTokens,
        string? callId,
        CodeModeRunStart? start,
        Func<ICodeModeRunObserver, CancellationToken, Task<CodeModeRunResult>> run,
        out string? refusal)
    {
        CodeModeRunHandle handle;
        lock (_gate)
        {
            if (_disposed)
            {
                refusal = "CodeAlta is closing: no script starts.";
                return null;
            }

            var running = _runs.Values.Where(static run => run.Running).ToArray();
            if (running.Count(run => run.SessionId == sessionId) >= _maxPerSession)
            {
                refusal = $"This session already runs {_maxPerSession} scripts, the most at the same time. Wait for one, or stop one with `wait` and `terminate: true`.";
                return null;
            }

            if (running.Length >= _maxPerHost)
            {
                refusal = $"CodeAlta already runs {_maxPerHost} scripts, the most at the same time.";
                return null;
            }

            handle = new CodeModeRunHandle(NewId(), sessionId, title, maxOutputTokens) { CallId = callId, Journal = Journal };
            if (_hold is not null)
            {
                // The session is held until the result is given: a headless run waits for it, and the session is not retired.
                if (_hold(sessionId) is not { } held)
                {
                    refusal = "This session is closing: no script starts.";
                    return null;
                }

                handle.Hold = held;
            }

            _runs[handle.Id] = handle;
            if (start is not null && Store is { } store)
            {
                handle.Writer = store.Begin(handle.Id, sessionId, title, callId, start);
            }
        }

        handle.Record(AgentWorkflowEventKind.RunStarted, record => record with { Title = title, ResumesRunId = start?.ResumesRunId });
        handle.Start(run, OnEndedAsync);
        refusal = null;
        return handle;
    }

    /// <summary>Writes a record of a run in the journal of its session, after its other records.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="kind">What happened.</param>
    /// <param name="fill">Fills the record.</param>
    public void Record(string runId, AgentWorkflowEventKind kind, Func<AgentWorkflowEvent, AgentWorkflowEvent> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (_runs.TryGetValue(runId, out var run))
        {
            run.Record(kind, fill);
        }
    }

    /// <summary>
    /// Plans a resume or a rerun of a run of a session (<see cref="CodeModeResume.TryPlan"/>), once what the store keeps of
    /// the run so far is written: a run that just ended in this process is read whole.
    /// </summary>
    /// <param name="sessionId">The session that resumes.</param>
    /// <param name="code">The code <c>exec</c> was given.</param>
    /// <param name="options">The options of its first line, which name the run.</param>
    /// <returns>The plan, or why it is refused.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public async Task<(CodeModeResumePlan? Plan, string? Refusal)> PlanResumeAsync(string sessionId, string code, CodeModeExecOptions options)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Resume is { } runId)
        {
            await WhenKeptAsync(runId).ConfigureAwait(false);
        }

        var plan = CodeModeResume.TryPlan(Store, sessionId, code, options, id => _runs.TryGetValue(id, out var known) ? known.Running : null, out var refusal);
        return (plan, refusal);
    }

    /// <summary>Waits until what the store was asked to keep of a run of this process is written.</summary>
    /// <param name="runId">The run.</param>
    /// <returns>A task that completes then; at once for a run this process does not know, or does not keep.</returns>
    public Task WhenKeptAsync(string runId)
        => runId is not null && _runs.TryGetValue(runId, out var run) && run.Writer is { } writer ? writer.FlushAsync() : Task.CompletedTask;

    /// <summary>Finds the run that runs a script whose call a tool call is (<c>&lt;exec call&gt;/&lt;n&gt;</c>), while it runs.</summary>
    /// <param name="sessionId">The session of the tool call.</param>
    /// <param name="toolCallId">The tool call, such as <c>call_7/3</c>.</param>
    /// <returns>The run, or null for a tool call of no running script.</returns>
    public CodeModeRunHandle? FindByToolCall(string? sessionId, string? toolCallId)
    {
        var slash = toolCallId?.LastIndexOf('/') ?? -1;
        if (sessionId is null || slash <= 0)
        {
            return null;
        }

        var callId = toolCallId![..slash];
        return _runs.Values.FirstOrDefault(run => run.Running && string.Equals(run.CallId, callId, StringComparison.Ordinal)
            && string.Equals(run.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Waits for the turn of a question of a run to the user (an <c>ask()</c> of its script, or the approval of one of its
    /// calls): one at a time per run. Its card then says that the run waits, until the returned value is disposed.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="kind">What it waits for: <c>question</c> or <c>approval</c>.</param>
    /// <param name="stepId">The step that asks, or null.</param>
    /// <param name="message">The question, or null.</param>
    /// <param name="cancellationToken">Stops waiting for the turn.</param>
    /// <returns>What ends the wait and gives the turn to the next question.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> or <paramref name="kind"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The wait for the turn was cancelled.</exception>
    public async Task<IDisposable> WaitForUserAsync(CodeModeRunHandle run, string kind, string? stepId, string? message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(kind);
        // From now on the run waits for the user, its turn among the questions included: exec does not count that time.
        run.WaitForUser(true);
        try
        {
            await run.Questions.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            run.WaitForUser(false);
            throw;
        }

        run.Record(AgentWorkflowEventKind.WaitStarted, record => record with { Status = kind, StepId = stepId, Message = message });
        return new WaitEnd(() =>
        {
            run.Record(AgentWorkflowEventKind.WaitEnded, record => record with { Status = kind, StepId = stepId });
            run.Questions.Release();
            run.WaitForUser(false);
        });
    }

    private sealed class WaitEnd(Action end) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                end();
            }
        }
    }

    /// <summary>Finds a run of any session, running or ended a short while ago.</summary>
    /// <param name="runId">The id of the run.</param>
    /// <returns>The run, or null.</returns>
    public CodeModeRunHandle? FindAny(string runId) => _runs.TryGetValue(runId, out var run) ? run : null;

    /// <summary>Finds a run of a session, running or ended a short while ago.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="runId">The id of the run.</param>
    /// <returns>The run, or null.</returns>
    public CodeModeRunHandle? Find(string sessionId, string runId)
        => _runs.TryGetValue(runId, out var run) && run.SessionId == sessionId ? run : null;

    /// <summary>
    /// Waits for a run, for a <c>wait</c> of its session: while it waits, the result of a run that ends is its own, not
    /// a prompt; a wait that is cancelled after the run ended leaves the result to the prompt.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait, not the run.</param>
    /// <returns>Whether the run ended, and its result when this wait took it; null when the session was already given it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task<(bool Ended, CodeModeRunResult? Result)> WaitAsync(CodeModeRunHandle run, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        run.AddWaiter(1);
        try
        {
            await run.Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            run.AddWaiter(-1);
            if (cancellationToken.IsCancellationRequested && run.Completion.IsCompleted)
            {
                // The run ended while this wait stopped: its delivery may have left it to this wait.
                _ = DeliverAsync(run, await run.Completion.ConfigureAwait(false));
            }
        }

        if (!run.Completion.IsCompleted)
        {
            return (false, null);
        }

        return run.TryCollect() ? (true, await run.Completion.ConfigureAwait(false)) : (true, null);
    }

    // A run that ended in the background: its result goes to its session, unless a wait takes it. The session is held until
    // then: what waits for it to settle sees the result arrive before it settles.
    private async Task OnEndedAsync(CodeModeRunHandle run, CodeModeRunResult result)
    {
        ForgetLater(run);
        try
        {
            // CodeAlta closes: a run it stops is not over but interrupted. Its end is left to the next start, which marks
            // it so, and resumes it by itself when it slept.
            if (Volatile.Read(ref _disposed) && Store is not null && result.Status == CodeModeRunStatus.Cancelled)
            {
                return;
            }

            if (run.Writer is { } writer)
            {
                await writer.EndAsync(result.Status.ToString().ToLowerInvariant(), CodeModeResultFormatter.Format(result, run.MaxOutputTokens)).ConfigureAwait(false);
            }

            run.Record(AgentWorkflowEventKind.RunEnded, record => record with
            {
                Status = result.Status.ToString().ToLowerInvariant(),
                Message = result.Error is { } error ? $"{error.Name}: {error.Message}" : null,
                DurationMs = (long)result.Duration.TotalMilliseconds,
                Calls = result.Calls.Count,
            });
            // What reads the journal after the result sees the whole run.
            await run.JournalWritten.ConfigureAwait(false);
            if (run.Backgrounded && run.Waiters == 0 && !run.Silenced)
            {
                await DeliverAsync(run, result).ConfigureAwait(false);
            }
        }
        finally
        {
            run.Hold?.Dispose();
            RunEnded?.Invoke(run.Id);
        }
    }

    private async Task DeliverAsync(CodeModeRunHandle run, CodeModeRunResult result)
    {
        if (!run.TryCollect() || _deliver is null)
        {
            return;
        }

        var prompt = $"Script {run.Id}{(run.Title is null ? string.Empty : $" ({run.Title})")} ended:\n{CodeModeResultFormatter.Format(result, run.MaxOutputTokens)}";
        try
        {
            await _deliver(run.SessionId, prompt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // The session or the host is gone: the result has nowhere to go.
        }
    }

    // An ended run stays known a little while, so that a `wait` that arrives late still finds it.
    private void ForgetLater(CodeModeRunHandle run)
        => _ = Task.Delay(TimeSpan.FromMinutes(10)).ContinueWith(_ => _runs.TryRemove(run.Id, out var _), TaskScheduler.Default);

    private static string NewId() => "run-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));

    /// <summary>Stops every run and waits for them to end.</summary>
    /// <returns>A task that completes when the runs ended.</returns>
    public async ValueTask DisposeAsync()
    {
        CodeModeRunHandle[] runs;
        lock (_gate)
        {
            _disposed = true;
            runs = [.. _runs.Values];
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);

        foreach (var run in runs)
        {
            run.Stop();
        }

        await Task.WhenAll(runs.Select(static run => run.Completion.ContinueWith(static _ => { }, TaskScheduler.Default))).ConfigureAwait(false);
    }
}

/// <summary>One run of a script, as the registry, <c>exec</c> and <c>wait</c> see it.</summary>
public sealed class CodeModeRunHandle : ICodeModeRunObserver
{
    private const int RecentLines = 20;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _calls;
    private int _collected;
    private int _backgrounded;
    private int _waiters;

    internal CodeModeRunHandle(string id, string sessionId, string? title, int maxOutputTokens)
    {
        Id = id;
        SessionId = sessionId;
        Title = title;
        MaxOutputTokens = maxOutputTokens;
    }

    /// <summary>Gets the id of the run.</summary>
    public string Id { get; }

    /// <summary>Gets the session that runs it.</summary>
    public string SessionId { get; }

    /// <summary>Gets its title, or null.</summary>
    public string? Title { get; }

    /// <summary>Gets the output budget of its result, in tokens.</summary>
    public int MaxOutputTokens { get; }

    /// <summary>Gets the run itself: it completes with the result, never with an exception.</summary>
    public Task<CodeModeRunResult> Completion { get; private set; } = Task.FromResult(new CodeModeRunResult(CodeModeRunStatus.Cancelled, TimeSpan.Zero));

    /// <summary>Gets whether it still runs.</summary>
    public bool Running => !Completion.IsCompleted;

    /// <summary>Gets whether it went on in the background after its tool call.</summary>
    public bool Backgrounded => Volatile.Read(ref _backgrounded) == 1;

    /// <summary>Gets how many calls it made so far.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Gets how long it has run.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    internal int Waiters => Volatile.Read(ref _waiters);

    // The most records of steps a run writes: a step in a loop of a thousand items writes the first ones.
    private const int MaxStepRecords = 200;

    // The most steps of one script the record of its declaration keeps.
    private const int MaxDeclaredSteps = 64;
    private readonly Lock _journalGate = new();
    private Task _journal = Task.CompletedTask;
    private int _stepRecords;

    /// <summary>Gets the tool call that runs the script, or null.</summary>
    public string? CallId { get; init; }

    /// <summary>Gets when the run started.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Gets when the run ended, or null while it runs.</summary>
    public DateTimeOffset? EndedAt => Completion.IsCompletedSuccessfully ? StartedAt + Completion.Result.Duration : null;

    /// <summary>Gets how the run ended, or null while it runs.</summary>
    public AgentBackgroundTaskOutcome? Outcome => !Completion.IsCompletedSuccessfully ? null : Completion.Result.Status switch
    {
        CodeModeRunStatus.Completed => AgentBackgroundTaskOutcome.Completed,
        CodeModeRunStatus.Cancelled => AgentBackgroundTaskOutcome.Stopped,
        _ => AgentBackgroundTaskOutcome.Failed,
    };

    internal Func<string, AgentWorkflowEvent, Task>? Journal { get; init; }

    // The records written so far: a task that completes when the last one is in the journal.
    internal Task JournalWritten { get { lock (_journalGate) return _journal; } }

    // Writes a record after the others; a record that cannot be written is lost, never the run.
    internal void Record(AgentWorkflowEventKind kind, Func<AgentWorkflowEvent, AgentWorkflowEvent> fill)
    {
        if (Journal is not { } journal)
        {
            return;
        }

        if (kind is AgentWorkflowEventKind.StepStarted or AgentWorkflowEventKind.StepEnded && Interlocked.Increment(ref _stepRecords) > MaxStepRecords)
        {
            return;
        }

        var record = fill(new AgentWorkflowEvent(new ModelProviderId("codealta"), SessionId, DateTimeOffset.UtcNow, null, kind, Id) { CallId = CallId });
        lock (_journalGate)
        {
            _journal = _journal.ContinueWith(async _ =>
            {
                try
                {
                    await journal(SessionId, record).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
                {
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    // The hold of the session of the run, released once its result is given.
    internal IDisposable? Hold { get; set; }

    // What keeps the run for a resume, or null.
    internal CodeModeRunStore.Writer? Writer { get; set; }

    // The questions of the run to the user come one at a time: ask() and the approvals of its calls.
    internal SemaphoreSlim Questions { get; } = new(1, 1);

    private readonly Lock _userGate = new();
    private readonly Stopwatch _notWaitingForUser = Stopwatch.StartNew();
    private int _userWaits;

    /// <summary>Gets whether the run waits for the user: an answer of <c>ask()</c>, or the approval of one of its calls.</summary>
    public bool WaitingForUser => Volatile.Read(ref _userWaits) > 0;

    /// <summary>
    /// Gets how long the run has run without waiting for the user: what <c>exec</c> counts against its <c>yield_ms</c>, so
    /// that a turn does not send a script to the background because the user takes time to answer it.
    /// </summary>
    public TimeSpan TimeNotWaitingForUser
    {
        get
        {
            lock (_userGate)
            {
                return _notWaitingForUser.Elapsed;
            }
        }
    }

    // The first wait stops the clock of the time not waiting for the user, the last one to end starts it again.
    internal void WaitForUser(bool waiting)
    {
        lock (_userGate)
        {
            if (waiting ? _userWaits++ == 0 : --_userWaits == 0)
            {
                if (waiting)
                {
                    _notWaitingForUser.Stop();
                }
                else
                {
                    _notWaitingForUser.Start();
                }
            }
        }
    }

    internal void AddWaiter(int delta) => Interlocked.Add(ref _waiters, delta);

    internal void Start(Func<ICodeModeRunObserver, CancellationToken, Task<CodeModeRunResult>> run, Func<CodeModeRunHandle, CodeModeRunResult, Task> ended)
    {
        Completion = Task.Run(async () =>
        {
            try
            {
                return await run(this, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new CodeModeRunResult(CodeModeRunStatus.Cancelled, _clock.Elapsed);
            }
            catch (Exception exception)
            {
                return new CodeModeRunResult(CodeModeRunStatus.Failed, _clock.Elapsed) { Error = new CodeModeRunError(CodeModeErrorKind.Script, "Error", exception.Message) };
            }
        });
        _ = Completion.ContinueWith(task => ended(this, task.Result), TaskScheduler.Default).Unwrap();
    }

    /// <summary>Takes the result for the caller that reports it: true once, for the first caller.</summary>
    /// <returns>True for the first caller.</returns>
    public bool TryCollect() => Interlocked.Exchange(ref _collected, 1) == 0;

    /// <summary>Marks the run as going on in the background: its result then comes back as a prompt, unless a wait takes it.</summary>
    public void Background() => Volatile.Write(ref _backgrounded, 1);

    /// <summary>Stops the run.</summary>
    public void Stop() => _cancellation.Cancel();

    private int _silenced;

    // Whether the end of the run is given to nobody: the user stopped the tree of its session.
    internal bool Silenced => Volatile.Read(ref _silenced) == 1;

    internal void Silence() => Volatile.Write(ref _silenced, 1);

    /// <summary>Gets the last lines it wrote.</summary>
    /// <returns>The lines, joined by new lines.</returns>
    public string RecentOutput() => string.Join('\n', _recent);

    /// <summary>Describes the run.</summary>
    /// <returns>What a session is told of it.</returns>
    public CodeModeRunInfo Info()
    {
        var ended = Completion.IsCompletedSuccessfully ? Completion.Result : null;
        return new(Id, SessionId, Title, ended?.Duration ?? _clock.Elapsed, Calls, ended is null)
        {
            StartedAt = StartedAt,
            EndedAt = ended is null ? null : StartedAt + ended.Duration,
            CallId = CallId,
            Status = ended?.Status.ToString().ToLowerInvariant(),
        };
    }

    void ICodeModeRunObserver.CallStarted(CodeModeCall call)
    {
        Interlocked.Increment(ref _calls);
        Writer?.CallStarted(call);
    }

    void ICodeModeRunObserver.CallEnded(CodeModeCallRecord record) => Writer?.CallEnded(record);

    void ICodeModeRunObserver.StepStarted(string stepId) => Record(AgentWorkflowEventKind.StepStarted, record => record with { StepId = stepId });

    // The steps a script declares, which the graph of the run draws from its start: once per script of the run.
    void ICodeModeRunObserver.StepsDeclared(CodeModeDescriptor descriptor)
        => Record(AgentWorkflowEventKind.StepsDeclared, record => record with
        {
            Title = descriptor.Title ?? descriptor.Name,
            Steps = [.. descriptor.Steps.Take(MaxDeclaredSteps).Select(static step => new AgentWorkflowStep(step.Id, step.Title) { After = step.After, Each = step.Each })],
        });

    void ICodeModeRunObserver.StepEnded(CodeModeStepRecord record)
    {
        Writer?.StepEnded(record);
        Record(AgentWorkflowEventKind.StepEnded, written => written with
        {
            StepId = record.StepId,
            StepKey = record.Key,
            Replayed = record.Replayed ? true : null,
            Status = record.Succeeded ? "completed" : "failed",
            Message = record.ErrorMessage,
            DurationMs = (long)record.Duration.TotalMilliseconds,
        });
    }

    void ICodeModeRunObserver.SleepStarted(CodeModeSleep sleep)
    {
        Writer?.SleepStarted(sleep);
        Record(AgentWorkflowEventKind.WaitStarted, record => record with { Status = "sleep", StepId = sleep.StepId, WakeAt = sleep.WakeAt });
    }

    void ICodeModeRunObserver.SleepEnded(CodeModeSleep sleep)
    {
        Writer?.SleepEnded(sleep);
        Record(AgentWorkflowEventKind.WaitEnded, record => record with { Status = "sleep", StepId = sleep.StepId });
    }

    void ICodeModeRunObserver.Output(CodeModeOutputEntry entry)
    {
        foreach (var line in entry.Text.Split('\n'))
        {
            _recent.Enqueue(line.Length > 300 ? line[..300] + "…" : line);
        }

        while (_recent.Count > RecentLines && _recent.TryDequeue(out _))
        {
        }
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Id} {SessionId} {(Running ? "running" : "ended")}");
}
