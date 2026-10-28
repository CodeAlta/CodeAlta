using System.Collections.Concurrent;
using System.Diagnostics;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Prompts;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using XenoAtom.Logging;

namespace CodeAlta.Orchestration.Hosting;

/// <summary>
/// Drives sessions of a host to their end without a user: the one loop that a headless command line and a protocol
/// server (ACP) share. It opens a session (new, resumed, or named by the caller), sends it prompts through the owner of
/// the host's commands, waits until the session and every session it started have settled
/// (<see cref="SessionRuntimeService.WhenSettledAsync"/>), and reports how the run ended. It owns the one reader of the
/// runtime events and gives the events of its sessions and of the sessions they started to an observer.
/// </summary>
/// <remarks>
/// The host is borrowed. It is meant to run without a user: created with <see cref="CodeAltaHostOptions.FullTrust"/> and
/// <see cref="CodeAltaHostOptions.HeadlessInstructions"/>, and with <see cref="CodeAltaHostOptions.ReadOnlyGlobalState"/>
/// beside CodeAlta Desktop. Its sessions are given the permission mode that bypasses permissions when their provider offers
/// it. Several prompts can be sent to one session, one at a time, and several sessions can run at once.
/// </remarks>
public sealed class HeadlessSessionDriver : IAsyncDisposable
{
    // The times a run waits for its tree to settle again when the outcome it read says a run is still in flight.
    private const int SettledRereads = 5;

    private static readonly Logger Log = LogManager.GetLogger("CodeAlta.HeadlessSessionDriver");

    private readonly CodeAltaHost _host;
    private readonly HeadlessSessionDriverOptions _options;
    private readonly HeadlessPromptAttachmentService _attachments = new();
    // The sessions the driver opened, with the hold of their leases (SessionRuntimeService.HoldSession).
    private readonly ConcurrentDictionary<string, IDisposable> _open = new(StringComparer.OrdinalIgnoreCase);
    // The session the driver opened that each observed session belongs to. Forgotten when the open sessions change.
    private readonly ConcurrentDictionary<string, string> _rootOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _closing = new();
    // The flushes waiting for the reader to reach their marker.
    private readonly ConcurrentDictionary<FlushMarker, TaskCompletionSource> _flushes = new();
    // The run in progress of each session, which CancelAsync asks to abort.
    private readonly ConcurrentDictionary<string, RunAbort> _runs = new(StringComparer.OrdinalIgnoreCase);
    // What the reader saw of the session of each run in progress: how the run ends when its journal cannot tell it.
    private readonly ConcurrentDictionary<string, RunObservation> _observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _reader;
    // The events the runtime stream dropped while the reader read it (DroppedEventCount).
    private long _droppedEventCount;
    private int _disposed;

    /// <summary>Creates a driver of the sessions of a host.</summary>
    /// <param name="host">The host, borrowed: the driver never disposes it.</param>
    /// <param name="options">The options; null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The abort grace is negative.</exception>
    public HeadlessSessionDriver(CodeAltaHost host, HeadlessSessionDriverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _options = options ?? new HeadlessSessionDriverOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.AbortGrace, TimeSpan.Zero);
        // What the stream dropped before the driver is not the driver's to report.
        var droppedAtStart = host.RuntimeService.DroppedRuntimeEventCount;
        _reader = _options.Observer is { } observer ? Task.Run(() => ReadEventsAsync(observer, droppedAtStart, _closing.Token)) : Task.CompletedTask;
    }

    /// <summary>
    /// Opens a session: the one to resume, the one named by the caller when it exists, or a new one in a folder. It checks
    /// that the provider and the model can be used, gives the session full trust and the tools it does without, and holds
    /// its lease until it is closed when the host takes leases (<see cref="CodeAltaHostOptions.SessionLeaseOwner"/>): a
    /// session another process holds is <see cref="HeadlessOpenStatus.SessionInUse"/>.
    /// </summary>
    /// <param name="request">What to open.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session, or why it could not be opened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The driver is disposed.</exception>
    /// <exception cref="OperationCanceledException">The opening was canceled.</exception>
    /// <exception cref="SessionLeaseUnavailableException">The host takes leases and the lease of the session cannot be taken.</exception>
    public async Task<HeadlessSessionOpenResult> OpenAsync(HeadlessSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (request.SessionId is not null && request.ResumeSessionId is not null) return Invalid("Name either a session to resume or a session id, not both.");
        if (request.ModelRef is not null && request.Tier is not null) return Invalid("Name either a model or a tier, not both.");
        foreach (var pattern in request.DisabledToolPatterns)
        {
            if (AgentToolPatterns.Validate(pattern) is { } error) return Invalid(error);
        }

        if (request.Sandbox is { } asked && !asked.Equals(CodeAlta.Sandbox.SandboxProfiles.Off, StringComparison.OrdinalIgnoreCase)
            && !CodeAlta.Sandbox.SandboxProfiles.Names(_host.RuntimeService.ReadSandboxSettings().Profiles).Contains(asked, StringComparer.OrdinalIgnoreCase))
            return Invalid($"'{asked}' is not a sandbox profile: name `off` or one of {string.Join(", ", CodeAlta.Sandbox.SandboxProfiles.Names(_host.RuntimeService.ReadSandboxSettings().Profiles))}.");

        string? existingId = null;
        if ((request.ResumeSessionId ?? request.SessionId) is { } named)
        {
            if (!SessionRuntimeService.TryNormalizeSessionId(named, out var normalized))
                return Invalid($"'{named}' is not a session id: a session is named by a GUID written with hyphens.");
            existingId = normalized;
        }

        var runtime = _host.RuntimeService;
        var existing = existingId is null ? null : await runtime.TryGetResumableSessionAsync(existingId, cancellationToken).ConfigureAwait(false);
        if (existing is null && request.ResumeSessionId is not null) return new(HeadlessOpenStatus.NotFound, null, $"Session '{existingId}' was not found.");
        string? folder = null;
        if (existing is null)
        {
            if (string.IsNullOrWhiteSpace(request.ProjectFolder) || !Path.IsPathFullyQualified(request.ProjectFolder))
                return Invalid("A new session needs the absolute path of the folder it works in.");
            folder = Path.GetFullPath(request.ProjectFolder);
            if (!Directory.Exists(folder)) return Invalid($"The folder '{folder}' does not exist.");
            if (request.WorktreeDirectory is { } worktree && (!Path.IsPathFullyQualified(worktree) || !Directory.Exists(worktree)))
                return Invalid($"The worktree '{worktree}' is not an existing folder named by its absolute path.");
        }

        var sessionId = existing?.SessionId ?? existingId ?? Guid.CreateVersion7().ToString();
        if (_open.ContainsKey(sessionId)) return new(HeadlessOpenStatus.SessionInUse, null, $"Session '{sessionId}' is already open.");
        // The lease is held from the opening on, so that no other process takes the session between the opening and its run.
        IDisposable lease;
        try
        {
            lease = runtime.HoldSession(sessionId);
        }
        catch (SessionInUseException inUse)
        {
            return new(HeadlessOpenStatus.SessionInUse, null, inUse.Message);
        }

        var opened = false;
        try
        {
            var project = existing is not null ? null : await ResolveProjectAsync(folder!, cancellationToken).ConfigureAwait(false);
            var model = await ResolveModelAsync(request, existing, project, cancellationToken).ConfigureAwait(false);
            if (model.Failure is { } failure) return failure;
            // The child of a session takes the mode its creator gives the sessions it creates; the driver's own, full trust.
            var bypass = request.ParentSessionId is { } parent
                ? runtime.GetCreatedSessionPermissionMode(parent, model.Provider!.ProviderId.Value) ?? BypassModeOf(model.Provider!.ProviderId.Value)
                : BypassModeOf(model.Provider!.ProviderId.Value);
            // The tools the session does without are known before it attaches: a new session is created with them.
            runtime.SetDisabledToolPatterns(sessionId, request.DisabledToolPatterns.Count > 0 ? request.DisabledToolPatterns : null);
            SessionViewDescriptor descriptor;
            if (existing is null)
            {
                descriptor = await _host.Commands.CreateDraftSessionAsync(project, model.Provider, request.Title, request.CreatedBy, worktreeDirectory: request.WorktreeDirectory is { } worktreeFolder ? Path.GetFullPath(worktreeFolder) : null,
                    spaceId: null, bypass, sessionId, request.ParentSessionId, request.SessionOptions).ConfigureAwait(false);
            }
            else
            {
                descriptor = existing;
                if (!string.Equals(existing.ResolvedProviderKey, model.Provider.ProviderId.Value, StringComparison.OrdinalIgnoreCase)
                    && await SwitchProviderAsync(existing.SessionId, model.Provider.ProviderId.Value, model.ModelId, model.Effort, bypass).ConfigureAwait(false) is { } switchFailure)
                    return switchFailure;
            }

            // A session that exists keeps its options; those asked only narrow them (a run turns sub-sessions off, never on).
            if (existing is not null && request.SessionOptions is { } askedOptions
                && AgentSessionOptions.Of(existing.Options) is var kept && kept.Within(askedOptions) is var narrowed && !narrowed.Equals(kept)
                && await _host.Commands.SetSessionOptionsAsync(sessionId, narrowed, cancellationToken).ConfigureAwait(false) is { Status: not "ok" })
                return NotReady($"The options of session '{sessionId}' cannot be changed now.");
            if (model.Tier is { } tier) await runtime.SetSessionTierAsync(sessionId, tier, cancellationToken).ConfigureAwait(false);
            // The sandbox asked is the session's own choice: no setting makes the run leave it.
            if (request.Sandbox is { } sandbox && await _host.Commands.SetSandboxAsync(sessionId, sandbox, cancellationToken).ConfigureAwait(false) is { Status: not "ok" } refused)
                return NotReady($"The sandbox of session '{sessionId}' cannot be chosen now ({refused.Status}).");
            var check = await _host.Commands.CheckSelectionAsync(sessionId, model.ModelId, model.Effort, request.AgentPromptId ?? (existing is null ? AgentPromptCatalog.DefaultPromptName : null),
                bypass, cancellationToken).ConfigureAwait(false);
            if (check is not { Status: OwnedSelectionCheck.Ok, Selection: { } selection })
            {
                return check.Status switch
                {
                    OwnedSelectionCheck.InvalidModel => NotReady($"Model '{model.ModelId}' is not offered by provider '{model.Provider.ProviderId.Value}'."),
                    OwnedSelectionCheck.Unavailable => NotReady($"Session '{sessionId}' cannot be prepared now."),
                    _ => Invalid($"The session cannot run with this selection ({check.Status}): check the agent prompt and the reasoning effort."),
                };
            }

            _open[sessionId] = lease;
            _rootOf.Clear();
            opened = true;
            return new(existing is null ? HeadlessOpenStatus.Created : HeadlessOpenStatus.Opened, new HeadlessSession(sessionId, descriptor,
                selection.ProviderKey, selection.ModelId ?? model.ModelId, selection.ReasoningEffort, selection.AgentPromptId, model.Tier, existing is null)
            {
                Selection = selection,
            });
        }
        finally
        {
            if (!opened) lease.Dispose();
        }
    }

    /// <summary>
    /// Changes what the next prompts of an open session run with: the model (on another provider too), the reasoning
    /// effort or the agent prompt. What the change does not name stays as it is; a model named without an effort keeps
    /// the effort of the session when the model has it, else takes the one the model starts with. A model named by hand
    /// takes the session off its tier. A run in progress goes on with what it started with.
    /// </summary>
    /// <param name="session">The session, opened by this driver.</param>
    /// <param name="change">What changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The session with its new selection (<see cref="HeadlessOpenStatus.Opened"/>), to send the next prompts with, or why
    /// the change was refused: <see cref="HeadlessOpenStatus.InvalidRequest"/> or <see cref="HeadlessOpenStatus.ProviderNotReady"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="change"/> is null.</exception>
    /// <exception cref="ArgumentException">The session is not open in this driver.</exception>
    /// <exception cref="ObjectDisposedException">The driver is disposed.</exception>
    /// <exception cref="OperationCanceledException">The change was canceled.</exception>
    public async Task<HeadlessSessionOpenResult> ChangeSelectionAsync(HeadlessSession session, HeadlessSelectionChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(change);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_open.ContainsKey(session.SessionId)) throw new ArgumentException($"Session '{session.SessionId}' is not open in this driver.", nameof(session));
        var runtime = _host.RuntimeService;
        var descriptor = await runtime.TryGetResumableSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false) ?? session.Descriptor;
        var (providerKey, modelId, effort, tier) = (session.ProviderKey, session.ModelId, change.Reasoning, session.Tier);
        if (change.ModelRef is { } reference)
        {
            var model = await ResolveModelAsync(new HeadlessSessionRequest { ModelRef = reference, Reasoning = change.Reasoning }, descriptor, null, cancellationToken)
                .ConfigureAwait(false);
            if (model.Failure is { } failure) return failure;
            (providerKey, modelId, effort, tier) = (model.Provider!.ProviderId.Value, model.ModelId, model.Effort, null);
        }
        else if (effort is null)
        {
            effort = session.ReasoningEffort;
        }

        var bypass = BypassModeOf(providerKey);
        if (!string.Equals(descriptor.ResolvedProviderKey, providerKey, StringComparison.OrdinalIgnoreCase)
            && await SwitchProviderAsync(session.SessionId, providerKey, modelId, effort, bypass).ConfigureAwait(false) is { } switchFailure)
            return switchFailure;
        if (session.Tier is not null && tier is null) await runtime.SetSessionTierAsync(session.SessionId, null, cancellationToken).ConfigureAwait(false);
        var prompt = change.AgentPromptId ?? session.AgentPromptId;
        var check = await _host.Commands.CheckSelectionAsync(session.SessionId, modelId, effort, prompt, bypass, cancellationToken).ConfigureAwait(false);
        if (check is not { Status: OwnedSelectionCheck.Ok, Selection: { } selection })
        {
            return check.Status switch
            {
                OwnedSelectionCheck.InvalidModel => NotReady($"Model '{modelId}' is not offered by provider '{providerKey}'."),
                OwnedSelectionCheck.InvalidPrompt => Invalid($"The agent prompt '{prompt}' is not available to session '{session.SessionId}'."),
                OwnedSelectionCheck.InvalidEffort => Invalid($"Model '{modelId}' does not take the reasoning effort '{effort?.ToString().ToLowerInvariant()}'."),
                OwnedSelectionCheck.Unavailable => NotReady($"Session '{session.SessionId}' cannot be prepared now."),
                _ => Invalid($"The session cannot run with this selection ({check.Status})."),
            };
        }

        return new(HeadlessOpenStatus.Opened, new HeadlessSession(session.SessionId, descriptor, selection.ProviderKey, selection.ModelId ?? modelId,
            selection.ReasoningEffort, selection.AgentPromptId, tier, Created: false)
        {
            Selection = selection,
        });
    }

    /// <summary>
    /// Sends a prompt to an open session and waits until it and every session it started have settled, or until the time
    /// the run is given runs out: the run is then aborted (<see cref="SessionRuntimeService.AbortTreeAsync"/>) with the
    /// reason <c>timeout</c>. A run the caller's token cancels is aborted with the reason <c>cancelled</c>, one that
    /// <see cref="CancelAsync"/> aborts with its reason. An aborted tree is waited for no longer than
    /// <see cref="HeadlessSessionDriverOptions.AbortGrace"/>.
    /// </summary>
    /// <param name="session">The session, opened by this driver.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="timeout">The time the run is given; null for no limit.</param>
    /// <param name="cancellationToken">Aborts the run.</param>
    /// <returns>How the run ended, once it has settled (or the grace after an abort has passed).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="prompt"/> is null.</exception>
    /// <exception cref="ArgumentException">The session is not open in this driver, or the prompt is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The driver is disposed.</exception>
    public async Task<HeadlessRunResult> RunAsync(HeadlessSession session, HeadlessPrompt prompt, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt.Text, nameof(prompt));
        if (timeout is { } limit) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, TimeSpan.Zero, nameof(timeout));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_open.ContainsKey(session.SessionId)) throw new ArgumentException($"Session '{session.SessionId}' is not open in this driver.", nameof(session));
        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        var attachments = prompt.Attachments.Count == 0 ? null : _attachments.Materialize(string.Empty, prompt.Attachments).Input.Items.Skip(1).ToArray();
        using var timer = timeout is { } span ? new CancellationTokenSource(span) : new CancellationTokenSource();
        using var aborts = new RunAbort();
        _runs[session.SessionId] = aborts;
        var observation = new RunObservation();
        _observations[session.SessionId] = observation;
        try
        {
            return await RunAsync(session, prompt, timeout, attachments, timer, aborts, observation, stopwatch, startedAt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _observations.TryRemove(new KeyValuePair<string, RunObservation>(session.SessionId, observation));
            _runs.TryRemove(new KeyValuePair<string, RunAbort>(session.SessionId, aborts));
            aborts.End();
        }
    }

    private async Task<HeadlessRunResult> RunAsync(HeadlessSession session, HeadlessPrompt prompt, TimeSpan? timeout, IReadOnlyList<AgentInputItem>? attachments,
        CancellationTokenSource timer, RunAbort aborts, RunObservation observation, Stopwatch stopwatch, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var runtime = _host.RuntimeService;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken, _closing.Token, aborts.Token);
        var admission = _host.Commands.AdmitSend(new OwnedTextSendRequest(Guid.NewGuid().ToString("N"), session.SessionId, prompt.Text)
        {
            Selection = session.Selection,
            Attachments = attachments,
            Provenance = prompt.Provenance,
        }, CancellationToken.None);
        if (admission is not { Kind: OwnedSessionCommandAdmissionKind.Accepted, Receipt: { } receipt })
            return Result(HeadlessRunStatus.Failed, null, null, null, null, $"The prompt was not admitted ({admission.Kind.ToString().ToLowerInvariant()}): the session is busy or the host is closing.");

        OwnedSessionCommandResult? sent = null;
        string? abortReason = null;
        try
        {
            sent = await receipt.Completion.WaitAsync(stop.Token).ConfigureAwait(false);
            await runtime.WhenSettledAsync(session.SessionId, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The run aborts its tree with the reason asked (CancelAsync), and waits for it no longer than the grace.
            abortReason = timer.IsCancellationRequested ? "timeout" : aborts.Reason ?? "cancelled";
            await AbortAndWaitAsync(session.SessionId, abortReason, CancellationToken.None).ConfigureAwait(false);
            aborts.End();
            sent ??= receipt.Completion.IsCompleted ? await receipt.Completion.ConfigureAwait(false) : null;
        }

        // A plugin answered the prompt itself (a prompt-submit hook): nothing ran, and what it said is the answer.
        if (abortReason is null && sent is { RunId: null, Code: OwnedSessionCommandService.PluginHandledCode })
            return new(HeadlessRunStatus.Completed, session.SessionId, null, sent.Message, null, null, null, SessionUsageTotals.None, stopwatch.Elapsed);
        if (abortReason is null && sent is { RunId: null, Code: OwnedSessionCommandService.PluginCancelledCode })
            return new(HeadlessRunStatus.Aborted, session.SessionId, null, null, "plugin", null, sent.Message ?? "A plugin cancelled the prompt.",
                SessionUsageTotals.None, stopwatch.Elapsed);

        var runId = sent?.RunId;
        // A run aborted before its send returned has no known run: the events since the prompt are read instead.
        var outcome = await ReadOutcomeAsync().ConfigureAwait(false);
        // A turn that starts as the tree is found settled (the answer to the result of a script that ended in the background)
        // is still running when its outcome is read, and a read that races the journal finds nothing: the run waits for the
        // tree to settle again and reads again, rather than ending without its answer.
        for (var again = 0; abortReason is null && again < SettledRereads
            && (outcome.Status == SessionOutcomeStatus.Running || outcome is { Status: SessionOutcomeStatus.Unknown, EventCount: 0 }); again++)
        {
            try
            {
                await runtime.WhenSettledAsync(session.SessionId, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                abortReason = timer.IsCancellationRequested ? "timeout" : aborts.Reason ?? "cancelled";
                await AbortAndWaitAsync(session.SessionId, abortReason, CancellationToken.None).ConfigureAwait(false);
                aborts.End();
            }

            outcome = await ReadOutcomeAsync().ConfigureAwait(false);
        }

        // A journal read that finds neither an answer nor an error of the run is not how it ended when the reader saw the
        // session answer: what it saw is reported, with the usage it saw.
        if (abortReason is null && outcome.Status == SessionOutcomeStatus.Unknown && _options.Observer is not null)
        {
            await FlushEventsAsync(CancellationToken.None).ConfigureAwait(false);
            if (SessionOutcome.From(observation.Events, isRunning: false) is { Status: SessionOutcomeStatus.Completed or SessionOutcomeStatus.Failed } observed)
            {
                if (LogManager.IsInitialized)
                    Log.Warn($"The journal of session {session.SessionId} did not tell how run {runId?.Value ?? "(not known)"} ended: the {observed.Status.ToString().ToLowerInvariant()} run the driver saw is reported.");
                outcome = observed;
            }
        }

        var usage = await ReadTreeUsageAsync(session.SessionId, outcome.Usage, startedAt).ConfigureAwait(false);
        // A send the driver admitted is cancelled by an abort of its tree (CancelAsync) before its run started, or by the host closing.
        abortReason ??= outcome.AbortReason ?? runtime.GetAbortReason(session.SessionId)
            ?? (sent is { Outcome: OwnedSessionCommandOutcome.Cancelled } ? "cancelled" : null);
        if (abortReason is not null)
        {
            return Result(abortReason == "timeout" ? HeadlessRunStatus.Timeout : HeadlessRunStatus.Aborted, runId, outcome, usage, abortReason,
                abortReason == "timeout" ? $"The run did not end within {timeout}." : $"The run was aborted: {abortReason}.");
        }

        if (sent is { Outcome: not OwnedSessionCommandOutcome.Completed, Code: SessionRuntimeService.SessionInUseCode } && runId is null)
            return Result(HeadlessRunStatus.SessionInUse, null, outcome, usage, null, $"Session '{session.SessionId}' is in use by another process.");
        // A send whose run failed has no run id (an in-process provider fails the send with its run); a run that the answer
        // of a child started after it may still have completed the work, and is how the run ended.
        if (sent is { Outcome: not OwnedSessionCommandOutcome.Completed } && runId is null && outcome.FinalError is null
            && outcome.Status != SessionOutcomeStatus.Completed)
            return Result(HeadlessRunStatus.Failed, null, outcome, usage, null, $"The prompt could not be sent ({sent.Code ?? sent.Outcome.ToString().ToLowerInvariant()}).");

        return outcome.Status switch
        {
            SessionOutcomeStatus.Completed => Result(HeadlessRunStatus.Completed, runId, outcome, usage, null, null),
            SessionOutcomeStatus.Cancelled => Result(HeadlessRunStatus.Aborted, runId, outcome, usage, null, outcome.FailureMessage ?? "The run was stopped."),
            SessionOutcomeStatus.Failed when outcome.FailureKind is AgentFailureKind.Auth or AgentFailureKind.ModelUnavailable
                => Result(HeadlessRunStatus.ProviderNotReady, runId, outcome, usage, null, outcome.FailureMessage),
            SessionOutcomeStatus.Failed => Result(HeadlessRunStatus.Failed, runId, outcome, usage, null, outcome.FailureMessage),
            _ => Result(HeadlessRunStatus.Failed, runId, outcome, usage, null, "The run ended without an answer."),
        };

        Task<SessionOutcome> ReadOutcomeAsync() => _options.ReadOutcome is { } read ? read(session.Descriptor, runId, startedAt)
            : runId is { } run
                ? runtime.ReadOutcomeAsync(session.Descriptor, run, CancellationToken.None)
                : runtime.ReadOutcomeSinceAsync(session.Descriptor, startedAt, CancellationToken.None);

        HeadlessRunResult Result(HeadlessRunStatus status, AgentRunId? run, SessionOutcome? read, SessionUsageTotals? totals, string? reason, string? message)
            => new(status, session.SessionId, run, read?.FinalMessage, reason, read?.FailureKind, message ?? read?.FailureMessage,
                totals ?? read?.Usage ?? SessionUsageTotals.None, stopwatch.Elapsed);
    }

    /// <summary>
    /// Aborts the run of an open session and of the sessions it started, with a reason (<c>signal</c>, <c>cancelled</c>...):
    /// a <see cref="RunAsync"/> that waits for it then returns <see cref="HeadlessRunStatus.Aborted"/> at once, and aborts
    /// its tree with this reason. The runs are waited for no longer than <see cref="HeadlessSessionDriverOptions.AbortGrace"/>:
    /// a provider that does not stop does not hold the caller.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="reason">Why the run is aborted.</param>
    /// <param name="cancellationToken">Stops waiting for the runs to stop.</param>
    /// <returns>A task that completes when the runs have stopped and the abort is recorded, or when the grace has passed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is blank.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    public async Task CancelAsync(HeadlessSession session, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (_runs.TryGetValue(session.SessionId, out var run))
        {
            // The run aborts its own tree: one abort, recorded with this reason.
            run.Request(reason.Trim());
            await run.Ended.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await AbortAndWaitAsync(session.SessionId, reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the observer was given every event the runtime published before the call, and has returned from the last
    /// of them: a face calls it once a run has settled, so that what it writes after it (a result, the response of a prompt)
    /// comes after every update of the run. The marker of the flush is never dropped: in a full stream it waits for room.
    /// Events the stream dropped before it are counted in <see cref="DroppedEventCount"/> once the flush ends.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>
    /// A task that completes when the observer was given the events: at once without an observer, or once the reader of the
    /// events ended (the driver is disposed, the runtime closed).
    /// </returns>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    public async Task FlushEventsAsync(CancellationToken cancellationToken = default)
    {
        if (_options.Observer is null || Volatile.Read(ref _disposed) != 0) return;
        var marker = new FlushMarker(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _flushes[marker] = reached;
        try
        {
            // A reader that ended gives nothing more to the observer.
            if (_reader.IsCompleted) return;
            CancellationTokenSource stop;
            try { stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token); }
            catch (ObjectDisposedException) { return; }
            using (stop)
            {
                try
                {
                    // Not published when the runtime closed: its reader drains what it accepted, then ends.
                    await _host.RuntimeService.PublishToReaderAsync(marker, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The driver closes: its reader ends.
                }
            }

            // The reader completes the flush when it reaches the marker, or when it ends.
            await Task.WhenAny(reached.Task, _reader).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flushes.TryRemove(marker, out _);
        }
    }

    /// <summary>
    /// Gets how many runtime events, of any session, the bounded runtime stream dropped while the driver read it, as far as
    /// its reader got: after <see cref="FlushEventsAsync"/>, every event dropped before the flush is counted.
    /// </summary>
    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    /// <summary>
    /// Closes a session of the driver: the driver no longer holds its lease (its attachment holds it until it goes) and its
    /// events are no longer observed. The session is kept.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public ValueTask CloseAsync(HeadlessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_open.TryRemove(session.SessionId, out var lease))
        {
            _rootOf.Clear();
            lease.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the reader of the events and releases the leases of the open sessions. The host and its sessions are left as they are.</summary>
    /// <returns>A task that completes when the reader ended.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _closing.CancelAsync().ConfigureAwait(false);
        try { await _reader.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        foreach (var sessionId in _open.Keys.ToArray())
        {
            if (_open.TryRemove(sessionId, out var lease)) lease.Dispose();
        }

        _closing.Dispose();
    }

    private static HeadlessSessionOpenResult Invalid(string message) => new(HeadlessOpenStatus.InvalidRequest, null, message);

    private static HeadlessSessionOpenResult NotReady(string message) => new(HeadlessOpenStatus.ProviderNotReady, null, message);

    // The mode that bypasses permissions, for a provider that offers it.
    private string? BypassModeOf(string providerKey)
        => _host.Commands.GetNewSessionPermissionModes(providerKey).Modes.Contains(SessionPermissionModes.Bypass, StringComparer.Ordinal)
            ? SessionPermissionModes.Bypass
            : null;

    // The project of a folder: the one the catalog lists, else a transient one; none for the global root (a chat).
    private async Task<ProjectDescriptor?> ResolveProjectAsync(string folder, CancellationToken cancellationToken)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(_host.CatalogOptions.GlobalRoot),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        return await _host.ProjectCatalog.GetByPathAsync(folder, cancellationToken).ConfigureAwait(false)
            ?? await _host.ProjectCatalog.GetOrAddTransientAsync(folder, cancellationToken).ConfigureAwait(false);
    }

    private sealed record ModelChoice(ModelProviderDescriptor? Provider, string ModelId, AgentReasoningEffort? Effort, ModelTier? Tier, HeadlessSessionOpenResult? Failure)
    {
        public static ModelChoice Fail(HeadlessSessionOpenResult failure) => new(null, string.Empty, null, null, failure);
    }

    // The provider and the model a session runs with: the model named, the first ready one of the tier named, the one of
    // the session that is resumed, or for a new session the standard tier when it has a ready model, else the default
    // provider with its model. The provider must be configured, enabled and ready, and list the model when it lists models.
    private async Task<ModelChoice> ResolveModelAsync(HeadlessSessionRequest request, SessionViewDescriptor? existing, ProjectDescriptor? project,
        CancellationToken cancellationToken)
    {
        var runtime = _host.RuntimeService;
        string? providerKey = null;
        string? modelId = null;
        AgentReasoningEffort? effort = null;
        ModelTier? tier = null;
        if (request.ModelRef is { } reference)
        {
            if (!ModelTierTarget.TryParse(reference, out var target, out var error) || target.Reasoning.Count > 1)
                return ModelChoice.Fail(Invalid(error ?? $"'{reference}' names more than one reasoning effort."));
            (providerKey, modelId, effort) = (target.ProviderKey, target.ModelId, target.PreferredReasoning);
        }
        else if (request.Tier is not null || existing is null)
        {
            var asked = request.Tier ?? ModelTier.Standard;
            var scope = await runtime.LoadModelTierScopeAsync(existing?.SpaceId, existing?.ProjectRef ?? project?.Id, cancellationToken).ConfigureAwait(false);
            if (ModelTierSelection.SelectForNewSession(scope, asked, _host.ModelProviderInitializationService.CurrentStates, FindProvider, request.Reasoning) is { } choice)
            {
                (providerKey, modelId, effort, tier) = (choice.ProviderKey, choice.ModelId, choice.Reasoning, asked);
            }
            else if (request.Tier is not null)
            {
                var targets = scope.Tiers.Get(asked);
                return ModelChoice.Fail(NotReady(targets.Count == 0
                    ? $"The {ModelTiers.NameOf(asked)} tier has no model: set the tiers in the settings, or name a model."
                    : $"No model of the {ModelTiers.NameOf(asked)} tier is ready ({string.Join(", ", targets.Select(static target => target.Format()))})."));
            }
        }
        else
        {
            (providerKey, modelId, effort) = (existing.ResolvedProviderKey, existing.ModelId, existing.ReasoningEffort);
        }

        providerKey ??= DefaultProviderKey(project?.ProjectPath);
        if (providerKey is null) return ModelChoice.Fail(NotReady("No provider is configured: add one in the settings, or give its key through the environment."));
        effort = request.Reasoning ?? effort;
        if (FindProvider(providerKey) is not { IsEnabled: true } provider)
            return ModelChoice.Fail(NotReady($"Provider '{providerKey}' is not configured, or is turned off."));
        IReadOnlyList<AgentModelInfo> models;
        try
        {
            models = await _host.ModelProviderInitializationService.GetModelsAsync(provider.ProviderId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return ModelChoice.Fail(NotReady($"Provider '{providerKey}' cannot be prepared: {exception.Message}"));
        }

        var state = _host.ModelProviderInitializationService.CurrentStates.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderId.Value, provider.ProviderId.Value, StringComparison.OrdinalIgnoreCase));
        if (state is not { Availability: ModelProviderAvailability.Ready })
        {
            return ModelChoice.Fail(NotReady($"Provider '{providerKey}' is not ready ({state?.Availability.ToString().ToLowerInvariant() ?? "unknown"})"
                + (string.IsNullOrWhiteSpace(state?.StatusMessage) ? "." : $": {state.StatusMessage}") + " Sign in, or check its settings."));
        }

        modelId ??= AgentModelDefaults.ResolveModelId(models, provider.DefaultModelId) ?? state.SelectedModelId;
        if (string.IsNullOrWhiteSpace(modelId)) return ModelChoice.Fail(NotReady($"Provider '{providerKey}' lists no model and has no default one: name a model."));
        if (models.Count > 0)
        {
            var listed = models.FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase));
            if (listed is null) return ModelChoice.Fail(NotReady($"Model '{modelId}' is not available on provider '{providerKey}'."));
            modelId = listed.Id;
        }

        return new ModelChoice(provider, modelId, effort, tier, null);
    }

    private ModelProviderDescriptor? FindProvider(string providerKey)
        => _host.ModelProviderRegistry.TryGetProvider(new ModelProviderId(providerKey), out var descriptor) ? descriptor : null;

    // The provider the configuration names as the default, for the project or globally, else the first enabled one.
    private string? DefaultProviderKey(string? projectRoot)
    {
        var providers = _host.ModelProviderRegistry.ListProviders().Where(static provider => provider.IsEnabled).ToArray();
        string? configured = null;
        try { configured = _host.RuntimeService.ConfigStore.GetEffectiveDefaultProvider(projectRoot); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException) { }
        return (providers.FirstOrDefault(provider => string.Equals(provider.ProviderId.Value, configured, StringComparison.OrdinalIgnoreCase))
            ?? providers.FirstOrDefault())?.ProviderId.Value;
    }

    // A resumed session asked to run on another provider switches to it first, with the model, effort and mode asked.
    private async Task<HeadlessSessionOpenResult?> SwitchProviderAsync(string sessionId, string providerKey, string modelId, AgentReasoningEffort? effort, string? permissionMode)
    {
        var context = await _host.Commands.GetProviderSelectionAsync(sessionId).ConfigureAwait(false);
        var result = context is null ? "busy"
            : await _host.Commands.SelectProviderAsync(context, providerKey, new SessionViewProviderCarryOver(modelId, effort, permissionMode)).ConfigureAwait(false);
        return result == "ok" ? null : new(HeadlessOpenStatus.InvalidRequest, null, $"Session '{sessionId}' could not switch to provider '{providerKey}' ({result}).");
    }

    // Aborts a tree and waits a bounded time for it to settle: what does not stop within the grace is reported as it is.
    private async Task AbortAndWaitAsync(string sessionId, string reason, CancellationToken cancellationToken)
    {
        using var grace = new CancellationTokenSource(_options.AbortGrace);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(grace.Token, cancellationToken);
        try
        {
            await _host.RuntimeService.AbortTreeAsync(sessionId, reason, wait.Token).ConfigureAwait(false);
            await _host.RuntimeService.WhenSettledAsync(sessionId, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (grace.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // How a run in progress is asked to abort (CancelAsync), and when it no longer waits for its tree.
    private sealed class RunAbort : IDisposable
    {
        private readonly CancellationTokenSource _requested = new();
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _reason;

        public CancellationToken Token => _requested.Token;

        // The reason of the first request.
        public string? Reason => Volatile.Read(ref _reason);

        // Completes once the run aborted its tree (or the grace passed), or ended.
        public Task Ended => _ended.Task;

        public void Request(string reason)
        {
            Interlocked.CompareExchange(ref _reason, reason, null);
            try
            {
                _requested.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run has ended.
            }
        }

        public void End() => _ended.TrySetResult();

        public void Dispose() => _requested.Dispose();
    }

    // The events of the session of a run that tell how it ends (its prompts, its answers, its errors, its usage and an
    // abort), as the reader saw them: a few per request to the model, not the whole stream.
    private sealed class RunObservation
    {
        private readonly Lock _gate = new();
        private readonly List<AgentEvent> _events = [];

        public IReadOnlyList<AgentEvent> Events
        {
            get
            {
                lock (_gate) return [.. _events];
            }
        }

        public void Add(AgentEvent agentEvent)
        {
            var kept = agentEvent switch
            {
                AgentContentCompletedEvent content => content.Kind is AgentContentKind.Assistant or AgentContentKind.User,
                AgentErrorEvent => true,
                AgentSessionUpdateEvent update => update.Usage is not null || SessionOutcome.AbortReasonOf(update) is not null,
                _ => false,
            };
            if (!kept) return;
            lock (_gate) _events.Add(agentEvent);
        }
    }

    // The usage of the root with what the sessions it started used since the prompt: those that ended too, which the
    // runtime may no longer hold.
    private async Task<SessionUsageTotals> ReadTreeUsageAsync(string rootSessionId, SessionUsageTotals root, DateTimeOffset since)
    {
        var runtime = _host.RuntimeService;
        var updates = new List<AgentSessionUpdateEvent>();
        foreach (var sessionId in (await runtime.ListSessionTreeAsync(rootSessionId, CancellationToken.None).ConfigureAwait(false)).Skip(1))
        {
            if (await runtime.TryGetResumableSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is not { } child) continue;
            var history = await runtime.TryReadStoredHistoryAsync(child, null, CancellationToken.None).ConfigureAwait(false) ?? [];
            updates.AddRange(history.OfType<AgentSessionUpdateEvent>().Where(update => update.Usage is not null && update.Timestamp >= since));
        }

        if (updates.Count == 0) return root;
        var children = SessionOutcome.TotalUsage(updates);
        var sameUnit = root.Cost is null || children.Cost is null || string.Equals(root.CostUnit, children.CostUnit, StringComparison.Ordinal);
        return new SessionUsageTotals(
            root.OperationCount + children.OperationCount,
            root.InputTokens + children.InputTokens,
            root.OutputTokens + children.OutputTokens,
            root.CachedInputTokens + children.CachedInputTokens,
            root.ReasoningTokens + children.ReasoningTokens,
            root.CacheWriteTokens + children.CacheWriteTokens,
            root.Cost is null && children.Cost is null ? null : (root.Cost ?? 0) + (sameUnit ? children.Cost ?? 0 : 0),
            root.CostUnit ?? children.CostUnit)
        {
            ReportsCachedInputTokens = root.ReportsCachedInputTokens || children.ReportsCachedInputTokens,
            ReportsCacheWriteTokens = root.ReportsCacheWriteTokens || children.ReportsCacheWriteTokens,
            ReportsReasoningTokens = root.ReportsReasoningTokens || children.ReportsReasoningTokens,
        };
    }

    private async Task ReadEventsAsync(Func<HeadlessSessionEvent, CancellationToken, ValueTask> observer, long droppedAtStart, CancellationToken cancellationToken)
    {
        // The drop count of the last event read, and what was dropped since the last event given to the observer: the
        // drops before an event of another session or before a marker are told with the next event observed.
        var stamp = droppedAtStart;
        long unreported = 0;
        try
        {
            await foreach (var (runtimeEvent, droppedBefore) in _host.RuntimeService.StreamStampedEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (droppedBefore > stamp)
                {
                    unreported += droppedBefore - stamp;
                    Interlocked.Add(ref _droppedEventCount, droppedBefore - stamp);
                    stamp = droppedBefore;
                }

                if (runtimeEvent is FlushMarker marker)
                {
                    if (_flushes.TryGetValue(marker, out var reached)) reached.TrySetResult();
                    continue;
                }

                if (RootOf(runtimeEvent.SessionId) is not { } root) continue;
                if (runtimeEvent is SessionAgentEvent { Event: var agentEvent } && string.Equals(runtimeEvent.SessionId, root, StringComparison.OrdinalIgnoreCase)
                    && _observations.TryGetValue(root, out var observation))
                    observation.Add(agentEvent);
                var gap = unreported;
                unreported = 0;
                try
                {
                    await observer(new HeadlessSessionEvent(root, runtimeEvent) { DroppedBefore = gap }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The observer's failure is its own: the run goes on, and so do the events of the other sessions.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            // Nothing more reaches the observer: a flush still waiting has nothing to wait for.
            foreach (var reached in _flushes.Values) reached.TrySetResult();
        }
    }

    // The marker of a flush: an event of no session, which only the reader of the driver sees.
    private sealed record FlushMarker(Guid Id, DateTimeOffset Timestamp) : SessionRuntimeEvent(string.Empty, Timestamp);

    // Only what belongs is kept: a session that starts is published before the runtime holds it under its parent.
    private string? RootOf(string sessionId)
    {
        if (_rootOf.TryGetValue(sessionId, out var known)) return known;
        string? root = null;
        if (_open.ContainsKey(sessionId))
        {
            root = sessionId;
        }
        else
        {
            foreach (var open in _open.Keys)
            {
                if (_host.RuntimeService.GetSessionTree(open).Contains(sessionId, StringComparer.OrdinalIgnoreCase))
                {
                    root = open;
                    break;
                }
            }
        }

        if (root is not null) _rootOf[sessionId] = root;
        return root;
    }
}
