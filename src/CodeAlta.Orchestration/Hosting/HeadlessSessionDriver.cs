using System.Collections.Concurrent;
using System.Diagnostics;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Prompts;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

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
    private readonly CodeAltaHost _host;
    private readonly HeadlessSessionDriverOptions _options;
    private readonly HeadlessPromptAttachmentService _attachments = new();
    // The sessions the driver opened, with the lease each holds.
    private readonly ConcurrentDictionary<string, IAsyncDisposable?> _open = new(StringComparer.OrdinalIgnoreCase);
    // The session the driver opened that each observed session belongs to. Forgotten when the open sessions change.
    private readonly ConcurrentDictionary<string, string> _rootOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _reader;
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
        _reader = _options.Observer is { } observer ? Task.Run(() => ReadEventsAsync(observer, _closing.Token)) : Task.CompletedTask;
    }

    /// <summary>
    /// Opens a session: the one to resume, the one named by the caller when it exists, or a new one in a folder. It checks
    /// that the provider and the model can be used, gives the session full trust and the tools it does without, and takes
    /// its lease when the driver has one.
    /// </summary>
    /// <param name="request">What to open.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session, or why it could not be opened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The driver is disposed.</exception>
    /// <exception cref="OperationCanceledException">The opening was canceled.</exception>
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
        }

        var sessionId = existing?.SessionId ?? existingId ?? Guid.CreateVersion7().ToString();
        if (_open.ContainsKey(sessionId)) return new(HeadlessOpenStatus.SessionInUse, null, $"Session '{sessionId}' is already open.");
        IAsyncDisposable? lease = null;
        if (_options.AcquireSessionLease is { } acquire)
        {
            lease = await acquire(sessionId, cancellationToken).ConfigureAwait(false);
            if (lease is null) return new(HeadlessOpenStatus.SessionInUse, null, $"Session '{sessionId}' is in use by another process.");
        }

        var opened = false;
        try
        {
            var project = existing is not null ? null : await ResolveProjectAsync(folder!, cancellationToken).ConfigureAwait(false);
            var model = await ResolveModelAsync(request, existing, project, cancellationToken).ConfigureAwait(false);
            if (model.Failure is { } failure) return failure;
            var bypass = _host.Commands.GetNewSessionPermissionModes(model.Provider!.ProviderId.Value).Modes.Contains(SessionPermissionModes.Bypass, StringComparer.Ordinal)
                ? SessionPermissionModes.Bypass
                : null;
            // The tools the session does without are known before it attaches: a new session is created with them.
            runtime.SetDisabledToolPatterns(sessionId, request.DisabledToolPatterns.Count > 0 ? request.DisabledToolPatterns : null);
            SessionViewDescriptor descriptor;
            if (existing is null)
            {
                descriptor = await _host.Commands.CreateDraftSessionAsync(project, model.Provider, request.Title, createdBy: null, worktreeDirectory: null,
                    spaceId: null, bypass, sessionId).ConfigureAwait(false);
            }
            else
            {
                descriptor = existing;
                if (!string.Equals(existing.ResolvedProviderKey, model.Provider.ProviderId.Value, StringComparison.OrdinalIgnoreCase)
                    && await SwitchProviderAsync(existing.SessionId, model.Provider.ProviderId.Value, model.ModelId, model.Effort, bypass).ConfigureAwait(false) is { } switchFailure)
                    return switchFailure;
            }

            if (model.Tier is { } tier) await runtime.SetSessionTierAsync(sessionId, tier, cancellationToken).ConfigureAwait(false);
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
            if (!opened && lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a prompt to an open session and waits until it and every session it started have settled, or until the time
    /// the run is given runs out: the run is then aborted (<see cref="SessionRuntimeService.AbortTreeAsync"/>) with the
    /// reason <c>timeout</c>. A run the caller's token cancels is aborted with the reason <c>cancelled</c>.
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
        var runtime = _host.RuntimeService;
        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        var attachments = prompt.Attachments.Count == 0 ? null : _attachments.Materialize(string.Empty, prompt.Attachments).Input.Items.Skip(1).ToArray();
        using var timer = timeout is { } span ? new CancellationTokenSource(span) : new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken, _closing.Token);
        var admission = _host.Commands.AdmitSend(new OwnedTextSendRequest(Guid.NewGuid().ToString("N"), session.SessionId, prompt.Text)
        {
            Selection = session.Selection,
            Attachments = attachments,
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
            abortReason = timer.IsCancellationRequested ? "timeout" : "cancelled";
            await AbortAndWaitAsync(session.SessionId, abortReason).ConfigureAwait(false);
            sent ??= receipt.Completion.IsCompleted ? await receipt.Completion.ConfigureAwait(false) : null;
        }

        var runId = sent?.RunId;
        // A run aborted before its send returned has no known run: the events since the prompt are read instead.
        var outcome = runId is { } run
            ? await runtime.ReadOutcomeAsync(session.Descriptor, run, CancellationToken.None).ConfigureAwait(false)
            : await runtime.ReadOutcomeSinceAsync(session.Descriptor, startedAt, CancellationToken.None).ConfigureAwait(false);
        var usage = await ReadTreeUsageAsync(session.SessionId, outcome.Usage, startedAt).ConfigureAwait(false);
        abortReason ??= outcome.AbortReason ?? runtime.GetAbortReason(session.SessionId);
        if (abortReason is not null)
        {
            return Result(abortReason == "timeout" ? HeadlessRunStatus.Timeout : HeadlessRunStatus.Aborted, runId, outcome, usage, abortReason,
                abortReason == "timeout" ? $"The run did not end within {timeout}." : $"The run was aborted: {abortReason}.");
        }

        if (sent is { Outcome: not OwnedSessionCommandOutcome.Completed } && runId is null && outcome.FinalError is null)
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

        HeadlessRunResult Result(HeadlessRunStatus status, AgentRunId? run, SessionOutcome? read, SessionUsageTotals? totals, string? reason, string? message)
            => new(status, session.SessionId, run, read?.FinalMessage, reason, read?.FailureKind, message ?? read?.FailureMessage,
                totals ?? read?.Usage ?? SessionUsageTotals.None, stopwatch.Elapsed);
    }

    /// <summary>
    /// Aborts the run of an open session and of the sessions it started, with a reason (<c>signal</c>, <c>cancelled</c>...):
    /// a <see cref="RunAsync"/> that waits for it then returns <see cref="HeadlessRunStatus.Aborted"/>.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="reason">Why the run is aborted.</param>
    /// <param name="cancellationToken">Stops waiting for the runs to stop.</param>
    /// <returns>A task that completes when the runs have stopped and the abort is recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is blank.</exception>
    public Task CancelAsync(HeadlessSession session, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return _host.RuntimeService.AbortTreeAsync(session.SessionId, reason, cancellationToken);
    }

    /// <summary>Closes a session of the driver: its lease is released and its events are no longer observed. The session is kept.</summary>
    /// <param name="session">The session.</param>
    /// <returns>A task that completes when the lease is released.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public async ValueTask CloseAsync(HeadlessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_open.TryRemove(session.SessionId, out var lease))
        {
            _rootOf.Clear();
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
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
            if (_open.TryRemove(sessionId, out var lease) && lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }

        _closing.Dispose();
    }

    private static HeadlessSessionOpenResult Invalid(string message) => new(HeadlessOpenStatus.InvalidRequest, null, message);

    private static HeadlessSessionOpenResult NotReady(string message) => new(HeadlessOpenStatus.ProviderNotReady, null, message);

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
    private async Task AbortAndWaitAsync(string sessionId, string reason)
    {
        using var grace = new CancellationTokenSource(_options.AbortGrace);
        try
        {
            await _host.RuntimeService.AbortTreeAsync(sessionId, reason, grace.Token).ConfigureAwait(false);
            await _host.RuntimeService.WhenSettledAsync(sessionId, grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (grace.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // The usage of the root with what the sessions it started used since the prompt.
    private async Task<SessionUsageTotals> ReadTreeUsageAsync(string rootSessionId, SessionUsageTotals root, DateTimeOffset since)
    {
        var runtime = _host.RuntimeService;
        var updates = new List<AgentSessionUpdateEvent>();
        foreach (var sessionId in runtime.GetSessionTree(rootSessionId).Skip(1))
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
            root.CostUnit ?? children.CostUnit);
    }

    private async Task ReadEventsAsync(Func<HeadlessSessionEvent, CancellationToken, ValueTask> observer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var runtimeEvent in _host.RuntimeService.StreamEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (RootOf(runtimeEvent.SessionId) is not { } root) continue;
                try
                {
                    await observer(new HeadlessSessionEvent(root, runtimeEvent), cancellationToken).ConfigureAwait(false);
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
    }

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
