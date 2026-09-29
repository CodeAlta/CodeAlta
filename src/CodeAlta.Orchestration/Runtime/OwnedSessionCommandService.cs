using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Host-owned, bounded send/abort/steer/idle-compaction/exact-cancellation/volatile-queue admission. Owns tasks, not runtime dependencies.
/// One send and independently one steer, compact, abort-run and volatile queue operation per session are reserved.
/// No durable queue or event reader is created.
/// </summary>
public sealed partial class OwnedSessionCommandService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SessionRuntimeService _runtime;
    private readonly ProjectCatalog _projects;
    private readonly CatalogOptions _catalog;
    private readonly int _capacity;
    private readonly bool _reviewPermissions;
    private readonly bool _enableUserInput;
    private readonly Dictionary<string, ReceiptEntry> _receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, SendOperation> _operations = [];
    private readonly Dictionary<string, SendOperation> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SteerOperation> _steers = [];
    private readonly HashSet<string> _steering = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CompactOperation> _compacts = [];
    private readonly HashSet<string> _compacting = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AbortRunOperation> _abortRuns = [];
    private readonly HashSet<string> _abortingRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, QueueOperation> _queues = [];
    private readonly HashSet<string> _queueing = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Exception> _failures = [];
    private readonly List<Exception> _cleanupFailures = [];
    private bool _closed;
    private bool _retained;
    private Task? _disposeTask;
    private Task<string>? _deleteWork;
    private readonly OriginalInvocation _permissionShutdown = new();
    private readonly OriginalInvocation _askDrain = new();
    private readonly BoundedPromptReferences _references = new();

    /// <summary>Searches a verified nonarchived project without creating a session or provider.</summary>
    /// <param name="scope">Expected project identity.</param><param name="sessionId">Optional exact session identity.</param>
    /// <param name="query">Bounded literal substring query.</param><param name="cancellationToken">Cancels the read.</param>
    /// <returns>Bounded metadata matches, never permission to read an arbitrary renderer root.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<OwnedReferenceSearchResult> SearchReferencesAsync(OwnedProjectReferenceScope scope, string? sessionId, string query, CancellationToken cancellationToken)
    {
        lock (_gate) { if (_closed || _retained) return new("closed", [], false); }
        if (scope is null || query is null || query.Length > 256 || query.Any(char.IsControl)) return new("invalid_request", [], false);
        var project = await ResolveReferenceProjectAsync(scope, cancellationToken).ConfigureAwait(false);
        if (!ReferenceScopeMatches(scope, project)) return new("scope_missing", [], false);
        if (sessionId is not null)
        {
            var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || session.SessionId != sessionId || session.ProjectRef != project!.Id || session.WorkingDirectory != project.ProjectPath)
                return new("scope_missing", [], false);
        }
        lock (_gate) { if (_closed || _retained) return new("closed", [], false); }
        return _references.Search(project!.ProjectPath, query, cancellationToken);
    }

    private static bool ReferenceScopeMatches(OwnedProjectReferenceScope scope, ProjectDescriptor? project)
        => project is { Archived: false } && project.Id == scope.ProjectId && project.ProjectPath == scope.ProjectPath;

    /// <summary>Observes raw prompt reference spans using the dispatch parser/path policy without provider work or recency changes.</summary>
    /// <param name="scope">Expected catalog identity, never an authoritative renderer root.</param>
    /// <param name="sessionId">Optional exact session identity.</param><param name="text">Bounded original prompt.</param>
    /// <param name="cancellationToken">Cancels the metadata read.</param>
    /// <returns>Bounded observed spans; resolution remains the original Send worker's responsibility.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async Task<OwnedReferenceObservation> ObserveReferencesAsync(OwnedProjectReferenceScope scope, string? sessionId, string text, CancellationToken cancellationToken)
    {
        lock (_gate) { if (_closed || _retained) return new("closed", [], false); }
        if (scope is null || text is null || text.Length > 32768) return new("invalid_request", [], false);
        var project = await ResolveReferenceProjectAsync(scope, cancellationToken).ConfigureAwait(false);
        if (!ReferenceScopeMatches(scope, project)) return new("scope_missing", [], false);
        if (sessionId is not null)
        {
            var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || session.SessionId != sessionId || session.ProjectRef != project!.Id || session.WorkingDirectory != project.ProjectPath)
                return new("scope_missing", [], false);
        }
        lock (_gate) { if (_closed || _retained) return new("closed", [], false); }
        return _references.Observe(text, project!.ProjectPath, cancellationToken);
    }

    private async Task<ProjectDescriptor?> ResolveReferenceProjectAsync(OwnedProjectReferenceScope scope, CancellationToken token)
    {
        var projects = await _projects.LoadAsync(token).ConfigureAwait(false);
        var matches = projects.Where(project => string.Equals(project.Id, scope.ProjectId, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 && ReferenceScopeMatches(scope, matches[0]) ? matches[0] : null;
    }

    internal OwnedSessionCommandService(
        SessionRuntimeService runtime, ProjectCatalog projects, CatalogOptions catalog, int capacity, bool reviewPermissions, bool enableAsks = false, bool enableUserInput = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _runtime = runtime;
        _projects = projects;
        _catalog = catalog;
        _capacity = capacity;
        _reviewPermissions = reviewPermissions;
        _enableUserInput = enableUserInput;
        Asks = new OwnedSessionAskService(enableAsks, (request, context) => AdmitSendCore(request, context, CancellationToken.None));
    }

    /// <summary>Gets the command-owned restricted ask service, disabled unless explicitly configured.</summary>
    public OwnedSessionAskService Asks { get; }

    /// <summary>Creates a draft through the host runtime using the same restricted permission and input policy as owned sends.</summary>
    /// <remarks>The caller owns admission and must drain the returned task before disposing the host.</remarks>
    /// <param name="project">An already resolved, trusted catalog project; null creates a global session.</param>
    /// <param name="provider">An enabled provider selected from the host registry.</param>
    /// <param name="title">An optional validated title.</param>
    /// <returns>The persisted session descriptor.</returns>
    /// <exception cref="ArgumentNullException">The provider is null.</exception>
    /// <exception cref="ArgumentException">The provider is disabled or the project is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The host command owner is closing.</exception>
    public Task<SessionViewDescriptor> CreateDraftSessionAsync(ProjectDescriptor? project, ModelProviderDescriptor provider, string? title)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!provider.IsEnabled) throw new ArgumentException("An enabled provider is required.", nameof(provider));
        lock (_gate) { if (_closed || _retained) throw new ObjectDisposedException(nameof(OwnedSessionCommandService)); }
        var directory = project?.ProjectPath ?? _catalog.GlobalRoot;
        var policy = SessionExecutionPolicy.CapturePreferred(provider.ProviderId, directory,
            project is null ? [] : [directory], project, provider.DefaultModelId, provider.DefaultReasoningEffort, null);
        var options = SessionExecutionPolicy.BuildOptions(policy, [], _runtime.Permissions.OwnedDefaultPermissionHandler,
            _runtime.Permissions.OwnedDefaultUserInputHandler);
        return project is null ? _runtime.CreateGlobalSessionAsync(options, title, CancellationToken.None)
            : _runtime.CreateProjectSessionAsync(project, options, title, CancellationToken.None);
    }

    /// <summary>Renames only an exact catalog session without changing its active run.</summary>
    /// <param name="sessionId">Exact session identity.</param>
    /// <param name="projectId">Exact project identity, or null for a global session.</param>
    /// <param name="workspacePath">Exact catalog workspace path.</param>
    /// <param name="title">Validated nonempty title.</param>
    /// <returns>True if the session still belongs to the requested scope.</returns>
    /// <exception cref="ArgumentException">An identity or title is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The owner is closing.</exception>
    public Task<bool> RenameSessionAsync(string sessionId, string? projectId, string workspacePath, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Length > 256 || title != title.Trim() || title.Any(char.IsControl))
            throw new ArgumentException("A title must be unpadded, free of control characters, and at most 256 characters.", nameof(title));
        for (var index = 0; index < title.Length; index++)
        {
            if (!char.IsSurrogate(title[index])) continue;
            if (!char.IsHighSurrogate(title[index]) || ++index == title.Length || !char.IsLowSurrogate(title[index]))
                throw new ArgumentException("A title must contain only valid Unicode scalars.", nameof(title));
        }
        lock (_gate) { if (_closed || _retained) throw new ObjectDisposedException(nameof(OwnedSessionCommandService)); }
        return _runtime.RenameOwnedSessionAsync(sessionId, projectId, workspacePath, title);
    }

    internal Func<ModelProviderId, CancellationToken, Task<IReadOnlyList<AgentModelInfo>>>? SelectionModels { get; init; }
    internal Func<ModelProviderId, IReadOnlyList<AgentModelInfo>>? ObservedImageModels { get; init; }

    internal static bool SameAskContext(OwnedAskSubmission? first, OwnedAskSubmission? second) => ReferenceEquals(first, second);

    /// <summary>Reserves an immutable text submission without doing catalog or provider work inline.</summary>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">A request string is blank or the session identity has leading or trailing whitespace.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before acceptance.</exception>
    public OwnedSessionCommandAdmission AdmitSend(OwnedTextSendRequest request, CancellationToken cancellationToken = default)
        => AdmitSendCore(request, null, cancellationToken);

    private OwnedSessionCommandAdmission AdmitSendCore(OwnedTextSendRequest request, OwnedAskSubmission? askSubmission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentNullException.ThrowIfNull(request.Text);
        if (request.Text.Length != 0 || request.Images is not { Count: > 0 })
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        if (request.Images is { Count: > 0 } images)
        {
            if (request.Selection?.ModelId is null || request.Text.Length > 32768)
                throw new ArgumentException("Images require an explicit model and text within the prompt limit.", nameof(request));
            request = request with { Images = Array.AsReadOnly(images.ToArray()) };
        }
        if (request.References is { } scope && (request.Text.Length > 32768 || string.IsNullOrWhiteSpace(scope.ProjectId)
            || scope.ProjectId.Length > 256 || string.IsNullOrWhiteSpace(scope.ProjectPath) || scope.ProjectPath.Length > 4096))
            throw new ArgumentException("Invalid bounded reference scope.", nameof(request));
        if (request.Selection is { } selection &&
            (string.IsNullOrWhiteSpace(selection.ProviderKey) || selection.ProviderKey.Length > 256
             || string.IsNullOrWhiteSpace(selection.AgentPromptId) || selection.AgentPromptId.Length > 256
             || selection.ModelId is { } model && (string.IsNullOrWhiteSpace(model) || model.Length > 256)
             || selection.ReasoningEffort is { } effort && !Enum.IsDefined(effort)))
            throw new ArgumentException("Invalid next-send selection.", nameof(request));
        if (!string.Equals(request.SessionId, request.SessionId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Session identities must not have leading or trailing whitespace.", nameof(request));
        SendOperation operation;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receipts.TryGetValue(request.ClientRequestId, out var previous))
            {
                var same = previous.Send is not null && SameAskContext(previous.Ask, askSubmission) &&
                    string.Equals(previous.Send.SessionId, request.SessionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.Send.Text, request.Text, StringComparison.Ordinal) && previous.Send.Selection == request.Selection
                    && previous.Send.References == request.References
                    && (previous.Send.Images ?? []).SequenceEqual(request.Images ?? []);
                return Replay(previous, same);
            }
            if (_closed || _retained) return new(OwnedSessionCommandAdmissionKind.Closed);
            if (_deleteWork is not null) return new(OwnedSessionCommandAdmissionKind.Busy);
            if (_receipts.Count == _capacity) return new(OwnedSessionCommandAdmissionKind.Capacity);
            if (request.Images is { Count: > 0 })
            {
                // Payload evidence remains with receipts for the host lifetime; no unbounded image history.
                if (_receipts.Values.Count(entry => entry.Send?.Images is { Count: > 0 }) >= 8)
                    return new(OwnedSessionCommandAdmissionKind.Capacity);
                request = request with { Images = OwnedPromptImages.Freeze(request.Images) };
            }
            if (_active.ContainsKey(request.SessionId)) return new(OwnedSessionCommandAdmissionKind.Busy);
            if (request.Selection is not null && (_queueing.Contains(request.SessionId) || _compacting.Contains(request.SessionId)
                || _steering.Contains(request.SessionId) || _abortingRuns.Contains(request.SessionId)))
                return new(OwnedSessionCommandAdmissionKind.Busy);

            var receipt = new OwnedSessionCommandReceipt(request.ClientRequestId, OwnedSessionCommandKind.Send, request.SessionId);
            operation = new SendOperation(request, receipt) { AskSubmission = askSubmission };
            _receipts.Add(request.ClientRequestId, new ReceiptEntry(receipt, request, Ask: askSubmission));
            _operations.Add(receipt.OperationId, operation);
            _active.Add(request.SessionId, operation);
            // The first await is an unreleased asynchronous launch gate, not fallible setup.
            operation.Work = RunSendAsync(operation);
        }
        operation.Launch.TrySetResult();
        return new(OwnedSessionCommandAdmissionKind.Accepted, operation.Receipt);
    }

    /// <summary>Reserves attachment-aware control for one owned send; retries never target a later send.</summary>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">The retry key is blank or the operation identity is empty.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before acceptance.</exception>
    public OwnedSessionCommandAdmission AdmitAbort(OwnedAbortRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientRequestId);
        if (request.TargetOperationId == Guid.Empty) throw new ArgumentException("An abort requires a send operation identity.", nameof(request));
        SendOperation operation;
        OwnedSessionCommandReceipt receipt;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receipts.TryGetValue(request.ClientRequestId, out var previous))
                return Replay(previous, previous.Abort == request);
            if (_closed) return new(OwnedSessionCommandAdmissionKind.Closed);
            if (_receipts.Count == _capacity) return new(OwnedSessionCommandAdmissionKind.Capacity);
            if (!_operations.TryGetValue(request.TargetOperationId, out var target)) return new(OwnedSessionCommandAdmissionKind.UnknownTarget);
            operation = target;

            receipt = new OwnedSessionCommandReceipt(request.ClientRequestId, OwnedSessionCommandKind.Abort, operation.SessionId, request.TargetOperationId);
            _receipts.Add(request.ClientRequestId, new ReceiptEntry(receipt, Abort: request));
            if (operation.Released)
            {
                receipt.Complete(new(OwnedSessionCommandOutcome.Completed, Code: "already_terminal"));
            }
            else
            {
                operation.AbortReceipts.Add(receipt);
                EnsureControl(operation);
                if (operation.ControlResult is not null) receipt.Complete(operation.ControlResult);
            }
        }
        StartControl(operation);
        return new(OwnedSessionCommandAdmissionKind.Accepted, receipt);
    }

    /// <summary>Reserves exact-target text steering independently of an in-flight owned send.</summary>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">An identity or text is blank, an identity is padded, or the runtime/attachment identity is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before acceptance.</exception>
    public OwnedSessionCommandAdmission AdmitSteer(OwnedTextSteerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        if (request.SessionId != request.SessionId.Trim() || request.ExpectedRunId != request.ExpectedRunId.Trim()
            || request.ExpectedRuntimeInstanceId == Guid.Empty || request.ExpectedAttachmentGeneration <= 0)
            throw new ArgumentException("Steering requires exact session, runtime, attachment and run identities.", nameof(request));
        SteerOperation operation;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receipts.TryGetValue(request.ClientRequestId, out var previous))
                return Replay(previous, previous.Steer == request);
            if (_closed || _retained) return new(OwnedSessionCommandAdmissionKind.Closed);
            if (_deleteWork is not null) return new(OwnedSessionCommandAdmissionKind.Busy);
            if (_receipts.Count == _capacity) return new(OwnedSessionCommandAdmissionKind.Capacity);
            if (_steering.Contains(request.SessionId)) return new(OwnedSessionCommandAdmissionKind.Busy);
            var receipt = new OwnedSessionCommandReceipt(request.ClientRequestId, OwnedSessionCommandKind.Steer, request.SessionId);
            operation = new(request, receipt);
            _receipts.Add(request.ClientRequestId, new ReceiptEntry(receipt, Steer: request));
            _steers.Add(operation);
            _steering.Add(request.SessionId);
            operation.Work = RunSteerAsync(operation);
        }
        operation.Launch.TrySetResult();
        return new(OwnedSessionCommandAdmissionKind.Accepted, operation.Receipt);
    }

    private async Task RunSteerAsync(SteerOperation operation)
    {
        await operation.Launch.Task.ConfigureAwait(false);
        OwnedSessionCommandResult result;
        try
        {
            var runId = await operation.RuntimeInvocation.RunAsync(() => _runtime.SteerOwnedCommandAsync(operation.Request, operation.Execution.Token)).ConfigureAwait(false);
            result = new(OwnedSessionCommandOutcome.Completed, runId);
        }
        catch (OperationCanceledException failure) when (operation.Execution.IsCancellationRequested)
        {
            RecordFailure(failure, cleanup: false, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            RecordFailure(ex, cleanup: false, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "steer_failed");
        }
        lock (_gate)
        {
            operation.ReleaseDecision.Complete();
            _steering.Remove(operation.Request.SessionId);
            operation.Receipt.Complete(result);
        }
    }

    /// <summary>Reserves an exact-attachment idle compaction attempt in an independent bounded slot.</summary>
    /// <remarks>Caller cancellation is checked only before acceptance. Busy requires a new explicit request;
    /// an exact retry always replays its original receipt. No permission execution or new attachment is created.</remarks>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">An identity is blank or padded, or the runtime/attachment identity is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before acceptance.</exception>
    public OwnedSessionCommandAdmission AdmitCompact(OwnedCompactRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        if (request.SessionId != request.SessionId.Trim() || request.ExpectedRuntimeInstanceId == Guid.Empty || request.ExpectedAttachmentGeneration <= 0)
            throw new ArgumentException("Compaction requires exact session, runtime and attachment identities.", nameof(request));
        CompactOperation operation;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receipts.TryGetValue(request.ClientRequestId, out var previous))
                return Replay(previous, previous.Compact == request);
            if (_closed || _retained) return new(OwnedSessionCommandAdmissionKind.Closed);
            if (_deleteWork is not null) return new(OwnedSessionCommandAdmissionKind.Busy);
            if (_receipts.Count == _capacity) return new(OwnedSessionCommandAdmissionKind.Capacity);
            if (_compacting.Contains(request.SessionId)) return new(OwnedSessionCommandAdmissionKind.Busy);
            var receipt = new OwnedSessionCommandReceipt(request.ClientRequestId, OwnedSessionCommandKind.Compact, request.SessionId);
            operation = new(request, receipt);
            _receipts.Add(request.ClientRequestId, new ReceiptEntry(receipt, Compact: request));
            _compacts.Add(operation);
            _compacting.Add(request.SessionId);
            operation.Work = RunCompactAsync(operation);
        }
        operation.Launch.TrySetResult();
        return new(OwnedSessionCommandAdmissionKind.Accepted, operation.Receipt);
    }

    private async Task RunCompactAsync(CompactOperation operation)
    {
        await operation.Launch.Task.ConfigureAwait(false);
        OwnedSessionCommandResult result;
        try
        {
            var outcome = await operation.RuntimeInvocation.RunAsync(() => _runtime.CompactOwnedCommandAsync(operation.Request, operation.Execution.Token)).ConfigureAwait(false);
            result = outcome is null ? new(OwnedSessionCommandOutcome.Failed, Code: "compact_busy")
                : outcome.Success ? new(OwnedSessionCommandOutcome.Completed)
                : new(OwnedSessionCommandOutcome.Failed, Code: "compact_unsuccessful");
        }
        catch (OperationCanceledException failure) when (operation.Execution.IsCancellationRequested)
        {
            RecordFailure(failure, cleanup: false, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Cancelled);
        }
        catch (NotSupportedException failure)
        {
            RecordFailure(failure, cleanup: false, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "compact_unsupported");
        }
        catch (Exception ex)
        {
            RecordFailure(ex, cleanup: false, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "compact_failed");
        }
        lock (_gate)
        {
            operation.ReleaseDecision.Complete();
            _compacting.Remove(operation.Request.SessionId);
            operation.Receipt.Complete(result);
        }
    }

    /// <summary>Reserves one volatile deferred text operation. Actual insertion and eventual dispatch
    /// have separate retained results; acceptance here only reserves ownership and replay protection.</summary>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">An identity or text is invalid or exceeds its bound.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before reservation.</exception>
    public OwnedSessionCommandAdmission AdmitQueue(OwnedTextQueueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        QueueOperation operation;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receipts.TryGetValue(request.ClientRequestId, out var previous)) return Replay(previous, previous.Queue == request);
            if (_closed || _retained) return new(OwnedSessionCommandAdmissionKind.Closed);
            if (_deleteWork is not null) return new(OwnedSessionCommandAdmissionKind.Busy);
            if (_receipts.Count == _capacity) return new(OwnedSessionCommandAdmissionKind.Capacity);
            if (_queueing.Contains(request.SessionId)) return new(OwnedSessionCommandAdmissionKind.Busy);
            var receipt = new OwnedSessionCommandReceipt(request.ClientRequestId, OwnedSessionCommandKind.Queue, request.SessionId);
            operation = new(request, receipt);
            _receipts.Add(request.ClientRequestId, new(receipt, Queue: request));
            _queues.Add(receipt.OperationId, operation);
            _queueing.Add(request.SessionId);
            operation.Work = RunQueueAsync(operation);
        }
        operation.Launch.TrySetResult();
        return new(OwnedSessionCommandAdmissionKind.Accepted, operation.Receipt);
    }

    /// <summary>Reserves cancellation of an original queue operation. Caller cancellation after
    /// reservation abandons only its waiter. No runtime-wide or latest-run abort is invoked.</summary>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">The key is invalid or the operation identity is empty.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled before reservation.</exception>
    public OwnedSessionCommandAdmission AdmitCancelQueue(OwnedCancelQueueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        OwnedTextQueueRequest.ValidateIdentity(request.ClientRequestId);
        if (request.TargetOperationId == Guid.Empty) throw new ArgumentException("Queue cancellation requires an operation identity.", nameof(request));
        QueueOperation operation;
        OwnedSessionCommandReceipt receipt;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receipts.TryGetValue(request.ClientRequestId, out var previous)) return Replay(previous, previous.CancelQueue == request);
            if (_closed) return new(OwnedSessionCommandAdmissionKind.Closed);
            if (_deleteWork is not null) return new(OwnedSessionCommandAdmissionKind.Busy);
            if (_receipts.Count == _capacity) return new(OwnedSessionCommandAdmissionKind.Capacity);
            if (!_queues.TryGetValue(request.TargetOperationId, out var target)) return new(OwnedSessionCommandAdmissionKind.UnknownTarget);
            operation = target;
            receipt = new(request.ClientRequestId, OwnedSessionCommandKind.CancelQueue, operation.Request.SessionId, request.TargetOperationId);
            _receipts.Add(request.ClientRequestId, new(receipt, CancelQueue: request));
            if (operation.Released) receipt.Complete(new(OwnedSessionCommandOutcome.Completed, Code: "already_terminal"));
            else
            {
                operation.CancelReceipts.Add(receipt);
                EnsureQueueCancellation(operation);
                if (operation.CancelResult is { } result) receipt.Complete(result);
            }
        }
        StartQueueCancellation(operation);
        return new(OwnedSessionCommandAdmissionKind.Accepted, receipt);
    }

    private async Task RunQueueAsync(QueueOperation operation)
    {
        await operation.Launch.Task.ConfigureAwait(false);
        OwnedSessionCommandResult result;
        try
        {
            Task<OwnedSessionCommandResult>? runtimeOriginal = null;
            operation.RuntimeInvocation.Launch(() => runtimeOriginal = _runtime.QueueOwnedCommandAsync(operation.Request, operation.Receipt, _reviewPermissions,
                operation.Execution.Token, _enableUserInput));
            if (await operation.RuntimeInvocation.Outcome.ConfigureAwait(false) is { } failure) ExceptionDispatchInfo.Throw(failure);
            result = await runtimeOriginal!.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordFailure(ex, cleanup: false, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "queue_failed");
        }
        // Also covers runtime admission refusal before its original body starts.
        operation.Receipt.CompleteQueueInsertion(new(false, result.Code ?? "queue_failed"));
        operation.RuntimeResult = result;
        operation.RuntimeSettled.TrySetResult();
        Task? cancellation;
        lock (_gate)
        {
            cancellation = operation.Cancellation;
            if (cancellation is null) { ReleaseQueue(operation, result); return; }
        }
        await cancellation.ConfigureAwait(false);
        lock (_gate) ReleaseQueue(operation, result);
    }

    private void ReleaseQueue(QueueOperation operation, OwnedSessionCommandResult result)
    {
        operation.ReleaseDecision.Complete();
        operation.Released = operation.ReleaseDecision.Released;
        _queueing.Remove(operation.Request.SessionId);
        operation.Receipt.Complete(operation.CancellationFailed
            ? new(OwnedSessionCommandOutcome.Failed, Code: "queue_cancel_failed") : result);
    }

    // Called only under the owner gate; the original control cannot start until its launch opens.
    private void EnsureQueueCancellation(QueueOperation operation)
    {
        if (!operation.Released && operation.Cancellation is null)
            operation.Cancellation = CancelQueueAsync(operation);
    }

    private void StartQueueCancellation(QueueOperation operation)
    {
        lock (_gate)
        {
            if (operation.Cancellation is null || operation.CancellationInitiated) return;
            operation.CancellationInitiated = true;
        }
        operation.CancellationInvocation.Launch(() => operation.SourceCancellation = operation.Execution.CancelAsync());
        operation.CancelLaunch.TrySetResult();
    }

    private async Task CancelQueueAsync(QueueOperation operation)
    {
        await operation.CancelLaunch.Task.ConfigureAwait(false);
        try
        {
            // Runtime owns the exact execution cancellation, registrations and send. Its callback
            // only signals that owner; joining this source alone would falsely report settled control.
            if (await operation.CancellationInvocation.Outcome.ConfigureAwait(false) is { } failure)
            {
                if (operation.CancellationInvocation.Original is null)
                    throw new AgentDependencyRetentionException("queue command", "cancellation launch", [failure], operation);
                ExceptionDispatchInfo.Throw(failure);
            }
        }
        catch (Exception ex)
        {
            operation.CancellationFailed = true;
            RecordFailure(ex, cleanup: true, operation.ReleaseDecision);
        }
        await operation.RuntimeSettled.Task.ConfigureAwait(false);
        if (operation.RuntimeResult?.Code is "queue_cleanup_failed" or "queue_cancel_failed") operation.CancellationFailed = true;
        var result = new OwnedSessionCommandResult(operation.CancellationFailed ? OwnedSessionCommandOutcome.Failed : OwnedSessionCommandOutcome.Completed,
            Code: operation.CancellationFailed ? "queue_cancel_failed" : "queue_cancellation_signalled");
        lock (_gate)
        {
            operation.CancelResult = result;
            foreach (var receipt in operation.CancelReceipts) receipt.Complete(result);
        }
    }

    private static OwnedSessionCommandAdmission Replay(ReceiptEntry entry, bool same)
        => same ? new(OwnedSessionCommandAdmissionKind.Replay, entry.Receipt) : new(OwnedSessionCommandAdmissionKind.Conflict);

    /// <summary>Reserves cancellation of an immutable observed run in an independent bounded control slot.</summary>
    /// <remarks>Exact replay returns the original receipt. Caller cancellation after acceptance cannot
    /// abandon original provider work. Success means cancellation signalled, not run completion.</remarks>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">An identity is malformed, padded, blank, too long, or invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before acceptance.</exception>
    public OwnedSessionCommandAdmission AdmitAbortRun(OwnedAbortRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        AbortRunOperation operation;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receipts.TryGetValue(request.ClientRequestId, out var previous))
                return Replay(previous, previous.AbortRun == request);
            if (_closed) return new(OwnedSessionCommandAdmissionKind.Closed);
            if (_deleteWork is not null) return new(OwnedSessionCommandAdmissionKind.Busy);
            if (_receipts.Count == _capacity) return new(OwnedSessionCommandAdmissionKind.Capacity);
            if (_abortingRuns.Contains(request.SessionId)) return new(OwnedSessionCommandAdmissionKind.Busy);
            var receipt = new OwnedSessionCommandReceipt(request.ClientRequestId, OwnedSessionCommandKind.AbortRun, request.SessionId);
            operation = new(request, receipt);
            _receipts.Add(request.ClientRequestId, new ReceiptEntry(receipt, AbortRun: request));
            _abortRuns.Add(operation);
            _abortingRuns.Add(request.SessionId);
            operation.Work = RunAbortRunAsync(operation);
        }
        operation.Launch.TrySetResult();
        return new(OwnedSessionCommandAdmissionKind.Accepted, operation.Receipt);
    }

    private async Task RunAbortRunAsync(AbortRunOperation operation)
    {
        await operation.Launch.Task.ConfigureAwait(false);
        OwnedSessionCommandResult result;
        try
        {
            var outcome = await operation.RuntimeInvocation.RunAsync(() => _runtime.AbortRunOwnedCommandAsync(operation.Request, operation.Execution.Token)).ConfigureAwait(false);
            result = outcome switch
            {
                AgentTargetedAbortOutcome.CancellationSignalled => new(OwnedSessionCommandOutcome.Completed,
                    new AgentRunId(operation.Request.ExpectedRunId), "cancellation_signalled"),
                AgentTargetedAbortOutcome.TargetNotActive => new(OwnedSessionCommandOutcome.Failed, Code: "abort_run_not_active"),
                null => new(OwnedSessionCommandOutcome.Failed, Code: "abort_run_target_unavailable"),
                _ => new(OwnedSessionCommandOutcome.Failed, Code: "abort_run_failed"),
            };
        }
        catch (OperationCanceledException failure) when (operation.Execution.IsCancellationRequested)
        {
            RecordFailure(failure, cleanup: false, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "abort_run_failed");
        }
        catch (NotSupportedException failure)
        {
            RecordFailure(failure, cleanup: false, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "abort_run_unsupported");
        }
        catch (Exception ex)
        {
            RecordFailure(ex, cleanup: false, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "abort_run_failed");
        }
        lock (_gate)
        {
            operation.ReleaseDecision.Complete();
            _abortingRuns.Remove(operation.Request.SessionId);
            operation.Receipt.Complete(result);
        }
    }

    // Called only under _gate. The worker cannot run until its record is published and unlocked.
    private void EnsureControl(SendOperation operation)
    {
        operation.CancelRequested = true;
        if (operation.Control is null)
            operation.Control = RunControlAsync(operation);
    }

    private void StartControl(SendOperation operation)
    {
        lock (_gate)
        {
            if (operation.Control is null || operation.CancellationInitiated) return;
            operation.CancellationInitiated = true;
        }
        // CancelAsync marks the token before returning, but runs callbacks asynchronously.
        // The retained control wrapper joins them; admission never runs callbacks under _gate.
        operation.CancellationInvocation.Launch(() => operation.Cancellation = operation.Execution.CancelAsync());
        operation.CancellationStarted.TrySetResult();
        operation.ControlLaunch.TrySetResult();
    }

    private async Task RunSendAsync(SendOperation operation)
    {
        await operation.Launch.Task.ConfigureAwait(false);
        OwnedSessionCommandResult result;
        try
        {
            operation.PreparationInvocation.Launch(() => operation.Preparation = PrepareAsync(operation));
            if (await operation.PreparationInvocation.Outcome.ConfigureAwait(false) is { } preparationFailure) ExceptionDispatchInfo.Throw(preparationFailure);
            var prepared = await operation.Preparation!.ConfigureAwait(false);
            operation.Attachment.TrySetResult(prepared);
            if (prepared is null)
            {
                result = new(OwnedSessionCommandOutcome.Failed, Code: "preparation_failed");
            }
            else
            {
                bool cancelled;
                lock (_gate) cancelled = operation.CancelRequested;
                if (cancelled)
                {
                    result = new(OwnedSessionCommandOutcome.Cancelled);
                }
                else
                {
                    if (_reviewPermissions || _enableUserInput)
                        operation.PermissionExecution = await _runtime.Permissions.CreateOwnedExecutionAsync(
                            operation.Receipt.OperationId, prepared.Session.SessionId, operation.Execution.Token, _reviewPermissions, _enableUserInput).ConfigureAwait(false);
                    if ((_reviewPermissions || _enableUserInput) && operation.PermissionExecution is null)
                    {
                        result = new(OwnedSessionCommandOutcome.Failed, Code: "permission_unavailable");
                    }
                    else
                    {
                        if (Asks.Enabled) operation.AskExecution = Asks.CreateExecution(operation.Receipt.OperationId, prepared.Session.SessionId, operation.Execution.Token);
                        var sendOptions = new AgentSendOptions { Input = prepared.Input, AskId = operation.AskSubmission?.AskId };
                        operation.SendInvocation.Launch(() => operation.Send = _runtime.SendOwnedCommandAsync(prepared.Session, prepared.Options, sendOptions,
                            operation.PermissionExecution, operation.Execution.Token, operation.AskExecution, operation.AskSubmission));
                        if (await operation.SendInvocation.Outcome.ConfigureAwait(false) is { } sendFailure) ExceptionDispatchInfo.Throw(sendFailure);
                        var runId = await operation.Send!.ConfigureAwait(false);
                        result = new(OwnedSessionCommandOutcome.Completed, runId);
                    }
                }
            }
        }
        catch (OperationCanceledException ex) when (operation.Execution.IsCancellationRequested)
        {
            RecordFailure(ex, cleanup: false, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            RecordFailure(ex, cleanup: false, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "send_failed");
        }
        // Also covers runtime admission failure before its body acquires a handle use.
        try { operation.AskExecution?.Close(); }
        catch (Exception ex)
        {
            RecordFailure(ex, cleanup: true, operation.ReleaseDecision);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "ask_close_failed");
        }
        if (operation.PermissionExecution is { } permission)
        {
            try
            {
                operation.PermissionCloseInvocation.Launch(() => _runtime.Permissions.CloseOwnedExecutionAsync(permission));
                if (await operation.PermissionCloseInvocation.Outcome.ConfigureAwait(false) is { } failure) ExceptionDispatchInfo.Throw(failure);
            }
            catch (Exception ex)
            {
                RecordFailure(ex, cleanup: true, operation.ReleaseDecision);
                result = new(OwnedSessionCommandOutcome.Failed, Code: "permission_close_failed");
            }
        }
        // Also releases a control waiter if setup failed before publishing preparation.
        operation.Attachment.TrySetResult(null);

        Task? control;
        lock (_gate)
        {
            control = operation.Control;
            if (control is null)
            {
                Release(operation, result);
                return;
            }
        }
        // Actual preparation/send tasks have settled; keep the slot until actual control settles too.
        await control.ConfigureAwait(false);
        lock (_gate) Release(operation, result);
    }

    private async Task<Prepared?> PrepareAsync(SendOperation operation)
    {
        try
        {
            var session = await _runtime.ResolveOwnedSessionAsync(operation.SessionId, CancellationToken.None).ConfigureAwait(false);
            if (session is null || !string.Equals(session.SessionId, operation.SessionId, StringComparison.OrdinalIgnoreCase)) return null;
            var project = string.IsNullOrWhiteSpace(session.ProjectRef) ? null
                : await _projects.GetByIdAsync(session.ProjectRef, CancellationToken.None).ConfigureAwait(false);
            var input = AgentInput.Text(operation.Request.Text);
            if (operation.Request.References is { } referenceScope)
            {
                project = await ResolveReferenceProjectAsync(referenceScope, operation.Execution.Token).ConfigureAwait(false);
                if (!ReferenceScopeMatches(referenceScope, project) || session.ProjectRef != referenceScope.ProjectId
                    || session.WorkingDirectory != project!.ProjectPath || session.SessionId != operation.SessionId) return null;
                // Once per admitted original, before any provider creation. Replays retain the
                // original receipt/work and never resolve mutable filesystem metadata again.
                input = _references.Resolve(operation.Request.Text, project.ProjectPath, operation.Execution.Token);
            }
            // Preserve exact empty request identity without inventing a textual provider prompt.
            if (operation.Request.Text.Length == 0) input = new AgentInput([]);
            var selection = operation.Request.Selection;
            if (selection is not null)
            {
                var choices = await GetSelectionChoicesCoreAsync(operation.SessionId, CancellationToken.None,
                    observedOnly: operation.Request.Images is { Count: > 0 }).ConfigureAwait(false);
                if (choices is null || !IsValidSelection(choices, selection))
                    throw new ArgumentException("The selected session configuration is no longer available.");
            }
            if (operation.Request.Images is { Count: > 0 } images)
            {
                var model = ObservedImageModels?.Invoke(new ModelProviderId(session.ResolvedProviderKey))
                    .SingleOrDefault(model => model.Id == selection!.ModelId);
                if (selection?.ProviderKey != session.ResolvedProviderKey || AgentImageInputCapability.Read(model) != true)
                    throw new ArgumentException("Image input capability is unknown, unsupported, or no longer available.");
                if (project is { Archived: true } || (!string.IsNullOrWhiteSpace(session.ProjectRef)
                    && (project is null || operation.Request.References is null)))
                    throw new ArgumentException("Image Send requires the exact current project scope.");
                operation.Execution.Token.ThrowIfCancellationRequested();
                var attachments = images.Select((image, index) => new PromptImageAttachment(
                    operation.Receipt.OperationId.ToString("N") + index, image.Title, OwnedPromptImages.Decode(image), image.MediaType, ".png")).ToArray();
                var saved = await new PromptImageAttachmentStore(_catalog).SaveAsync(session, attachments, operation.Execution.Token).ConfigureAwait(false);
                input = new AgentInput(input.Items.Concat(saved.Select(image => new AgentInputItem.LocalImage(image.Path, image.Title, image.MediaType))).ToArray());
            }
            var policy = SessionExecutionPolicy.CaptureSession(
                session, project, _catalog.GlobalRoot, default,
                selection is null ? session.ModelId : selection.ModelId,
                selection is null ? session.ReasoningEffort : selection.ReasoningEffort,
                selection?.AgentPromptId ?? session.AgentPromptId);
            var options = SessionExecutionPolicy.BuildOptions(
                policy, [],
                _runtime.Permissions.OwnedDefaultPermissionHandler,
                _runtime.Permissions.OwnedDefaultUserInputHandler);
            if (selection is null)
                await _runtime.EnsureOwnedCoordinatorSessionAsync(session, options).ConfigureAwait(false);
            else
                await _runtime.EnsureOwnedCoordinatorSessionAsync(session, options, useExplicitPrompt: true).ConfigureAwait(false);
            return new Prepared(session, options, input);
        }
        catch (OperationCanceledException) when ((operation.Request.References is not null || operation.Request.Images is { Count: > 0 }) && operation.Execution.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            RecordFailure(ex, cleanup: false, operation.ReleaseDecision);
            return null;
        }
    }

    private async Task RunControlAsync(SendOperation operation)
    {
        await operation.ControlLaunch.Task.ConfigureAwait(false);
        var failed = operation.CancellationFailed;
        var attached = false;

        try
        {
            // StartControl already initiated source cancellation independently. Close this exact
            // operation before preparation/provider/cancellation joins, including provider None tokens.
            if (_reviewPermissions || _enableUserInput)
                operation.PermissionInvalidationInvocation.Launch(() => _runtime.Permissions.InvalidateOwnedOperationAsync(operation.Receipt.OperationId));
        }
        catch (Exception ex)
        {
            failed = true;
            RecordFailure(ex, cleanup: true, operation.ReleaseDecision);
        }
        try
        {
            attached = await operation.Attachment.Task.ConfigureAwait(false) is not null;
            if (attached)
            {
                // Do not wait cancellation callbacks before making the real abort route available.
                operation.AbortInvocation.Launch(() => _runtime.AbortAsync(operation.SessionId, CancellationToken.None));
                if (await operation.AbortInvocation.Outcome.ConfigureAwait(false) is { } failure) ExceptionDispatchInfo.Throw(failure);
            }
        }
        catch (Exception ex)
        {
            failed = true;
            RecordFailure(ex, cleanup: true, operation.ReleaseDecision);
        }
        try
        {
            if (_reviewPermissions || _enableUserInput)
            {
                if (await operation.PermissionInvalidationInvocation.Outcome.ConfigureAwait(false) is { } permissionFailure)
                {
                    failed = true;
                    RecordFailure(permissionFailure, cleanup: true, operation.ReleaseDecision);
                }
            }
            if (await operation.CancellationInvocation.Outcome.ConfigureAwait(false) is { } failure)
            {
                if (operation.CancellationInvocation.Original is null)
                    throw new AgentDependencyRetentionException("send command", "cancellation launch", [failure], operation);
                ExceptionDispatchInfo.Throw(failure);
            }
        }
        catch (Exception ex)
        {
            failed = true;
            RecordFailure(ex, cleanup: true, operation.ReleaseDecision);
        }
        var result = new OwnedSessionCommandResult(
            failed ? OwnedSessionCommandOutcome.Failed : OwnedSessionCommandOutcome.Completed,
            Code: failed ? "control_failed" : attached ? null : "not_attached");
        lock (_gate)
        {
            operation.ControlResult = result;
            foreach (var receipt in operation.AbortReceipts) receipt.Complete(result);
        }
    }

    // _gate owns slot release, receipt state and admission; no provider code runs here.
    private void Release(SendOperation operation, OwnedSessionCommandResult result)
    {
        operation.ReleaseDecision.Complete();
        operation.Released = operation.ReleaseDecision.Released;
        _active.Remove(operation.SessionId);
        operation.Receipt.Complete(result);
    }

    private void RecordFailure(Exception failure, bool cleanup, DependencyReleaseDecision? decision = null)
    {
        lock (_gate)
        {
            decision?.Observe(failure);
            _failures.Add(failure);
            if (cleanup) _cleanupFailures.Add(failure);
            if (OwnedProviderEventForwarding.HasRetention(failure))
            {
                _retained = true;
                if (!cleanup) _cleanupFailures.Add(failure);
            }
        }
    }

    // Actual work completion is distinct from permission to release its source and admission slot.
    internal sealed class DependencyReleaseDecision
    {
        internal bool Active { get; private set; } = true;
        internal bool Retained { get; private set; }
        internal bool Released => !Active && !Retained;
        internal List<Exception> Failures { get; } = [];
        internal void Observe(Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            Failures.Add(failure);
            Retained |= OwnedProviderEventForwarding.HasRetention(failure);
        }
        internal void Complete() => Active = false;
    }

    internal sealed class OriginalInvocation
    {
        private readonly TaskCompletionSource _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        internal OriginalInvocation() => Outcome = ObserveAsync();
        internal Task? Original { get; private set; }
        internal Task<Exception?> Outcome { get; }
        internal Exception? AwaitedFailure { get; private set; }
        internal AggregateException? OriginalFaults { get; private set; }
        internal object? Invocation { get; private set; }
        internal async Task<T> RunAsync<T>(Func<Task<T>> invoke)
        {
            Task<T>? original = null;
            Launch(() => original = invoke());
            if (await Outcome.ConfigureAwait(false) is { } failure) ExceptionDispatchInfo.Throw(failure);
            return await original!.ConfigureAwait(false);
        }
        internal void Launch(Func<Task> invoke)
        {
            ArgumentNullException.ThrowIfNull(invoke);
            if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Invocation already launched.");
            Invocation = invoke;
            try
            {
                Original = invoke() ?? throw new InvalidOperationException("Invocation returned no original.");
                _launched.TrySetResult();
            }
            catch (Exception failure) { _launched.TrySetException(failure); }
        }
        private async Task<Exception?> ObserveAsync()
        {
            try
            {
                await _launched.Task.ConfigureAwait(false);
                await Original!.ConfigureAwait(false);
                return null;
            }
            catch (Exception failure)
            {
                AwaitedFailure = failure;
                OriginalFaults = Original?.Exception;
                return OriginalFaults is { InnerExceptions.Count: > 1 } ? OriginalFaults : failure;
            }
        }
    }

    /// <summary>Closes admission, signals every owned operation, and joins all original work without disposing dependencies.</summary>
    /// <remarks>Repeated calls share one task. Noncooperative preparation can keep disposal pending indefinitely.</remarks>
    /// <exception cref="Exception">One control or cleanup operation failed, after all work was joined.</exception>
    /// <exception cref="AggregateException">Multiple control or cleanup operations failed, after all work was joined.</exception>
    public ValueTask DisposeAsync()
    {
        SendOperation[] operations;
        SteerOperation[] steers;
        CompactOperation[] compacts;
        AbortRunOperation[] abortRuns;
        QueueOperation[] queues;
        Task<string>? deleteWork;
        TaskCompletionSource launch;
        Task disposal;
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _closed = true;
            operations = [.. _operations.Values];
            steers = [.. _steers];
            compacts = [.. _compacts];
            abortRuns = [.. _abortRuns];
            queues = [.. _queues.Values];
            deleteWork = _deleteWork;
            foreach (var queue in queues) EnsureQueueCancellation(queue);
            foreach (var operation in operations)
                if (!operation.Released) EnsureControl(operation);
            launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = DisposeCoreAsync(operations, steers, compacts, abortRuns, queues, deleteWork, launch.Task);
            disposal = _disposeTask;
        }
        // Release every control before joining any of them. No cancellation callback runs under _gate.
        try { Asks.CloseAdmission(); }
        catch (Exception failure)
        {
            RecordFailure(new AgentDependencyRetentionException("command owner", "ask admission closure", [failure], this), cleanup: true);
        }
        foreach (var operation in operations) StartControl(operation);
        foreach (var queue in queues) StartQueueCancellation(queue);
        foreach (var steer in steers)
        {
            // Start every independent cancellation before any dependent join. No callbacks under _gate.
            steer.CancellationInvocation.Launch(() => steer.Cancellation = steer.Execution.CancelAsync());
        }
        foreach (var compact in compacts)
        {
            compact.CancellationInvocation.Launch(() => compact.Cancellation = compact.Execution.CancelAsync());
        }
        foreach (var abortRun in abortRuns)
        {
            abortRun.CancellationInvocation.Launch(() => abortRun.Cancellation = abortRun.Execution.CancelAsync());
        }
        launch.TrySetResult();
        return new(disposal);
    }

    private async Task ObserveCancellationAsync(OriginalInvocation invocation, DependencyReleaseDecision decision)
    {
        if (await invocation.Outcome.ConfigureAwait(false) is not { } failure) return;
        RecordFailure(invocation.Original is null
            ? new AgentDependencyRetentionException("command owner", "cancellation launch", [failure], this)
            : failure, cleanup: true, decision);
    }

    private async Task DisposeCoreAsync(SendOperation[] operations, SteerOperation[] steers, CompactOperation[] compacts,
        AbortRunOperation[] abortRuns, QueueOperation[] queues, Task<string>? deleteWork, Task launch)
    {
        await launch.ConfigureAwait(false);
        if (deleteWork is not null) await deleteWork.ConfigureAwait(false);
        if (_reviewPermissions || _enableUserInput)
        {
            _permissionShutdown.Launch(() => _runtime.Permissions.CloseOwnedAdmissionAsync());
            if (await _permissionShutdown.Outcome.ConfigureAwait(false) is { } failure)
                RecordFailure(_permissionShutdown.Original is null
                    ? new AgentDependencyRetentionException("command owner", "permission shutdown launch", [failure], this) : failure, cleanup: true);
        }
        foreach (var operation in operations)
        {
            if (operation.Control is not null)
                await operation.CancellationStarted.Task.ConfigureAwait(false);
        }
        foreach (var operation in operations)
        {
            try { await operation.Work.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            if (operation.Control is { } control)
            {
                try { await control.ConfigureAwait(false); }
                catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            }
        }
        foreach (var steer in steers)
        {
            try { await steer.Work.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            await ObserveCancellationAsync(steer.CancellationInvocation, steer.ReleaseDecision).ConfigureAwait(false);
        }
        foreach (var compact in compacts)
        {
            try { await compact.Work.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            await ObserveCancellationAsync(compact.CancellationInvocation, compact.ReleaseDecision).ConfigureAwait(false);
        }
        foreach (var abortRun in abortRuns)
        {
            try { await abortRun.Work.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            await ObserveCancellationAsync(abortRun.CancellationInvocation, abortRun.ReleaseDecision).ConfigureAwait(false);
        }
        foreach (var queue in queues)
        {
            try { await queue.Work.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            if (queue.Cancellation is { } cancellation)
            {
                try { await cancellation.ConfigureAwait(false); }
                catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            }
        }
        _askDrain.Launch(Asks.DrainAsync);
        if (await _askDrain.Outcome.ConfigureAwait(false) is { } askFailure)
            RecordFailure(_askDrain.Original is null
                ? new AgentDependencyRetentionException("command owner", "ask drainage launch", [askFailure], this) : askFailure, cleanup: true);
        // All real work and independent controls have settled. Only now is release eligibility stable:
        // a later operation's retained marker must not arrive after an earlier dependent source release.
        bool retained;
        lock (_gate) retained = _retained;
        if (!retained)
        {
            foreach (var source in operations.Select(static operation => operation.Execution)
                .Concat(steers.Select(static operation => operation.Execution))
                .Concat(compacts.Select(static operation => operation.Execution))
                .Concat(abortRuns.Select(static operation => operation.Execution))
                .Concat(queues.Select(static operation => operation.Execution)))
            {
                try { source.Dispose(); }
                catch (Exception failure)
                {
                    RecordFailure(new AgentDependencyRetentionException("command owner", "source release", [failure], this), cleanup: true);
                    break;
                }
            }
        }
        Exception[] failures;
        lock (_gate) failures = [.. _cleanupFailures];
        lock (_gate) retained = _retained;
        if (retained) throw new AgentDependencyRetentionException("command owner", "retained terminal operations", failures, this);
        if (failures.Length == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Length > 1) throw new AggregateException(failures);
    }

    private sealed record ReceiptEntry(OwnedSessionCommandReceipt Receipt, OwnedTextSendRequest? Send = null, OwnedAbortRequest? Abort = null,
        OwnedTextSteerRequest? Steer = null, OwnedCompactRequest? Compact = null, OwnedAbortRunRequest? AbortRun = null,
        OwnedTextQueueRequest? Queue = null, OwnedCancelQueueRequest? CancelQueue = null, OwnedAskSubmission? Ask = null);
    private sealed record Prepared(SessionViewDescriptor Session, SessionExecutionOptions Options, AgentInput Input);

    private sealed class QueueOperation(OwnedTextQueueRequest request, OwnedSessionCommandReceipt receipt)
    {
        internal DependencyReleaseDecision ReleaseDecision { get; } = new();
        internal OriginalInvocation RuntimeInvocation { get; } = new();
        internal OriginalInvocation CancellationInvocation { get; } = new();
        internal OwnedTextQueueRequest Request { get; } = request;
        internal OwnedSessionCommandReceipt Receipt { get; } = receipt;
        internal CancellationTokenSource Execution { get; } = new();
        internal TaskCompletionSource Launch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelLaunch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RuntimeSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work { get; set; } = Task.CompletedTask;
        internal Task? Cancellation { get; set; }
        internal Task? SourceCancellation { get; set; }
        internal bool CancellationInitiated { get; set; }
        internal List<OwnedSessionCommandReceipt> CancelReceipts { get; } = [];
        internal OwnedSessionCommandResult? CancelResult { get; set; }
        internal OwnedSessionCommandResult? RuntimeResult { get; set; }
        internal bool Released { get; set; }
        internal bool CancellationFailed { get; set; }
    }

    private sealed class AbortRunOperation(OwnedAbortRunRequest request, OwnedSessionCommandReceipt receipt)
    {
        internal DependencyReleaseDecision ReleaseDecision { get; } = new();
        internal OriginalInvocation RuntimeInvocation { get; } = new();
        internal OriginalInvocation CancellationInvocation { get; } = new();
        internal OwnedAbortRunRequest Request { get; } = request;
        internal OwnedSessionCommandReceipt Receipt { get; } = receipt;
        internal CancellationTokenSource Execution { get; } = new();
        internal TaskCompletionSource Launch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work { get; set; } = Task.CompletedTask;
        internal Task? Cancellation { get; set; }
    }

    private sealed class CompactOperation(OwnedCompactRequest request, OwnedSessionCommandReceipt receipt)
    {
        internal DependencyReleaseDecision ReleaseDecision { get; } = new();
        internal OriginalInvocation RuntimeInvocation { get; } = new();
        internal OriginalInvocation CancellationInvocation { get; } = new();
        internal OwnedCompactRequest Request { get; } = request;
        internal OwnedSessionCommandReceipt Receipt { get; } = receipt;
        internal CancellationTokenSource Execution { get; } = new();
        internal TaskCompletionSource Launch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work { get; set; } = Task.CompletedTask;
        internal Task? Cancellation { get; set; }
    }

    private sealed class SteerOperation(OwnedTextSteerRequest request, OwnedSessionCommandReceipt receipt)
    {
        internal DependencyReleaseDecision ReleaseDecision { get; } = new();
        internal OriginalInvocation RuntimeInvocation { get; } = new();
        internal OriginalInvocation CancellationInvocation { get; } = new();
        internal OwnedTextSteerRequest Request { get; } = request;
        internal OwnedSessionCommandReceipt Receipt { get; } = receipt;
        internal CancellationTokenSource Execution { get; } = new();
        internal TaskCompletionSource Launch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work { get; set; } = Task.CompletedTask;
        internal Task? Cancellation { get; set; }
    }

    private sealed class SendOperation(OwnedTextSendRequest request, OwnedSessionCommandReceipt receipt)
    {
        internal DependencyReleaseDecision ReleaseDecision { get; } = new();
        internal OriginalInvocation PreparationInvocation { get; } = new();
        internal OriginalInvocation SendInvocation { get; } = new();
        internal OriginalInvocation PermissionCloseInvocation { get; } = new();
        internal OriginalInvocation PermissionInvalidationInvocation { get; } = new();
        internal OriginalInvocation AbortInvocation { get; } = new();
        internal OriginalInvocation CancellationInvocation { get; } = new();
        internal OwnedTextSendRequest Request { get; } = request;
        internal OwnedSessionCommandReceipt Receipt { get; } = receipt;
        internal string SessionId => Request.SessionId;
        internal CancellationTokenSource Execution { get; } = new();
        internal TaskCompletionSource Launch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ControlLaunch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Prepared?> Attachment { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work { get; set; } = Task.CompletedTask;
        internal Task<Prepared?>? Preparation { get; set; }
        internal Task<AgentRunId>? Send { get; set; }
        internal SessionPermissionService.OwnedPermissionExecution? PermissionExecution { get; set; }
        internal OwnedSessionAskExecution? AskExecution { get; set; }
        internal OwnedAskSubmission? AskSubmission { get; init; }
        internal Task? Control { get; set; }
        internal Task? Cancellation { get; set; }
        internal bool CancelRequested { get; set; }
        internal bool CancellationInitiated { get; set; }
        internal bool CancellationFailed { get; set; }
        internal bool Released { get; set; }
        internal List<OwnedSessionCommandReceipt> AbortReceipts { get; } = [];
        internal OwnedSessionCommandResult? ControlResult { get; set; }
    }
}
