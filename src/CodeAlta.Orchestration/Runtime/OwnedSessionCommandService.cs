using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Host-owned, bounded text-send/abort/steer admission. Owns its tasks, not its runtime dependencies.
/// One send and independently one steer per session are reserved; no queue or event reader is created.
/// </summary>
public sealed class OwnedSessionCommandService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SessionRuntimeService _runtime;
    private readonly ProjectCatalog _projects;
    private readonly CatalogOptions _catalog;
    private readonly int _capacity;
    private readonly bool _reviewPermissions;
    private readonly Dictionary<string, ReceiptEntry> _receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, SendOperation> _operations = [];
    private readonly Dictionary<string, SendOperation> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SteerOperation> _steers = [];
    private readonly HashSet<string> _steering = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Exception> _failures = [];
    private readonly List<Exception> _cleanupFailures = [];
    private bool _closed;
    private Task? _disposeTask;

    internal OwnedSessionCommandService(
        SessionRuntimeService runtime, ProjectCatalog projects, CatalogOptions catalog, int capacity, bool reviewPermissions)
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
    }

    /// <summary>Reserves an immutable text submission without doing catalog or provider work inline.</summary>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">A request string is blank or the session identity has leading or trailing whitespace.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before acceptance.</exception>
    public OwnedSessionCommandAdmission AdmitSend(OwnedTextSendRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        if (!string.Equals(request.SessionId, request.SessionId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Session identities must not have leading or trailing whitespace.", nameof(request));
        SendOperation operation;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_receipts.TryGetValue(request.ClientRequestId, out var previous))
            {
                var same = previous.Send is not null &&
                    string.Equals(previous.Send.SessionId, request.SessionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.Send.Text, request.Text, StringComparison.Ordinal);
                return Replay(previous, same);
            }
            if (_closed) return new(OwnedSessionCommandAdmissionKind.Closed);
            if (_receipts.Count == _capacity) return new(OwnedSessionCommandAdmissionKind.Capacity);
            if (_active.ContainsKey(request.SessionId)) return new(OwnedSessionCommandAdmissionKind.Busy);

            var receipt = new OwnedSessionCommandReceipt(request.ClientRequestId, OwnedSessionCommandKind.Send, request.SessionId);
            operation = new SendOperation(request, receipt);
            _receipts.Add(request.ClientRequestId, new ReceiptEntry(receipt, request));
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
            if (_closed) return new(OwnedSessionCommandAdmissionKind.Closed);
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
            var runId = await _runtime.SteerOwnedCommandAsync(operation.Request, operation.Execution.Token).ConfigureAwait(false);
            result = new(OwnedSessionCommandOutcome.Completed, runId);
        }
        catch (OperationCanceledException) when (operation.Execution.IsCancellationRequested)
        {
            result = new(OwnedSessionCommandOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            RecordFailure(ex, cleanup: false);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "steer_failed");
        }
        lock (_gate)
        {
            _steering.Remove(operation.Request.SessionId);
            operation.Receipt.Complete(result);
        }
    }

    private static OwnedSessionCommandAdmission Replay(ReceiptEntry entry, bool same)
        => same ? new(OwnedSessionCommandAdmissionKind.Replay, entry.Receipt) : new(OwnedSessionCommandAdmissionKind.Conflict);

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
        try
        {
            operation.Cancellation = operation.Execution.CancelAsync();
        }
        catch (Exception ex)
        {
            operation.CancellationFailed = true;
            RecordFailure(ex, cleanup: true);
        }
        finally
        {
            operation.CancellationStarted.TrySetResult();
            operation.ControlLaunch.TrySetResult();
        }
    }

    private async Task RunSendAsync(SendOperation operation)
    {
        await operation.Launch.Task.ConfigureAwait(false);
        OwnedSessionCommandResult result;
        try
        {
            operation.Preparation = PrepareAsync(operation);
            var prepared = await operation.Preparation.ConfigureAwait(false);
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
                    if (_reviewPermissions)
                        operation.PermissionExecution = await _runtime.Permissions.CreateOwnedExecutionAsync(
                            operation.Receipt.OperationId, prepared.Session.SessionId, operation.Execution.Token).ConfigureAwait(false);
                    if (_reviewPermissions && operation.PermissionExecution is null)
                    {
                        result = new(OwnedSessionCommandOutcome.Failed, Code: "permission_unavailable");
                    }
                    else
                    {
                        var sendOptions = new AgentSendOptions { Input = AgentInput.Text(operation.Request.Text) };
                        operation.Send = _runtime.SendOwnedCommandAsync(prepared.Session, prepared.Options, sendOptions,
                            operation.PermissionExecution, operation.Execution.Token);
                        var runId = await operation.Send.ConfigureAwait(false);
                        result = new(OwnedSessionCommandOutcome.Completed, runId);
                    }
                }
            }
        }
        catch (OperationCanceledException ex) when (operation.Execution.IsCancellationRequested)
        {
            RecordFailure(ex, cleanup: false);
            result = new(OwnedSessionCommandOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            RecordFailure(ex, cleanup: false);
            result = new(OwnedSessionCommandOutcome.Failed, Code: "send_failed");
        }
        // Also covers runtime admission failure before its body acquires a handle use.
        if (operation.PermissionExecution is { } permission)
        {
            try { await _runtime.Permissions.CloseOwnedExecutionAsync(permission).ConfigureAwait(false); }
            catch (Exception ex)
            {
                RecordFailure(ex, cleanup: true);
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
            var policy = SessionExecutionPolicy.CaptureSession(
                session, project, _catalog.GlobalRoot, default, session.ModelId, session.ReasoningEffort, session.AgentPromptId);
            var options = SessionExecutionPolicy.BuildOptions(
                policy, [],
                _runtime.Permissions.OwnedDefaultPermissionHandler,
                _runtime.Permissions.OwnedDefaultUserInputHandler);
            await _runtime.EnsureOwnedCoordinatorSessionAsync(session, options).ConfigureAwait(false);
            return new Prepared(session, options);
        }
        catch (Exception ex)
        {
            RecordFailure(ex, cleanup: false);
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
            if (_reviewPermissions) await _runtime.Permissions.InvalidateOwnedOperationAsync(operation.Receipt.OperationId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            RecordFailure(ex, cleanup: true);
        }
        try
        {
            attached = await operation.Attachment.Task.ConfigureAwait(false) is not null;
            if (attached)
            {
                // Do not wait cancellation callbacks before making the real abort route available.
                await _runtime.AbortAsync(operation.SessionId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failed = true;
            RecordFailure(ex, cleanup: true);
        }
        try
        {
            await operation.Cancellation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            RecordFailure(ex, cleanup: true);
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
        operation.Released = true;
        _active.Remove(operation.SessionId);
        operation.Receipt.Complete(result);
    }

    private void RecordFailure(Exception failure, bool cleanup)
    {
        lock (_gate)
        {
            _failures.Add(failure);
            if (cleanup) _cleanupFailures.Add(failure);
        }
    }

    /// <summary>Closes admission, signals owned sends and steering, and joins all owned work without disposing dependencies.</summary>
    /// <remarks>Repeated calls share one task. Noncooperative preparation can keep disposal pending indefinitely.</remarks>
    /// <exception cref="Exception">One control or cleanup operation failed, after all work was joined.</exception>
    /// <exception cref="AggregateException">Multiple control or cleanup operations failed, after all work was joined.</exception>
    public ValueTask DisposeAsync()
    {
        SendOperation[] operations;
        SteerOperation[] steers;
        TaskCompletionSource launch;
        Task disposal;
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _closed = true;
            operations = [.. _operations.Values];
            steers = [.. _steers];
            foreach (var operation in operations)
                if (!operation.Released) EnsureControl(operation);
            launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = DisposeCoreAsync(operations, steers, launch.Task);
            disposal = _disposeTask;
        }
        // Release every control before joining any of them. No cancellation callback runs under _gate.
        foreach (var operation in operations) StartControl(operation);
        foreach (var steer in steers)
        {
            // Start every independent cancellation before any dependent join. No callbacks under _gate.
            try { steer.Cancellation = steer.Execution.CancelAsync(); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
        }
        launch.TrySetResult();
        return new(disposal);
    }

    private async Task DisposeCoreAsync(SendOperation[] operations, SteerOperation[] steers, Task launch)
    {
        await launch.ConfigureAwait(false);
        if (_reviewPermissions)
        {
            try { await _runtime.Permissions.CloseOwnedAdmissionAsync().ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
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
            // The wrappers have joined preparation, send, abort and cancellation before this point.
            try { operation.Execution.Dispose(); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
        }
        foreach (var steer in steers)
        {
            try { await steer.Work.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            try { await steer.Cancellation.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            try { steer.Execution.Dispose(); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
        }
        Exception[] failures;
        lock (_gate) failures = [.. _cleanupFailures];
        if (failures.Length == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Length > 1) throw new AggregateException(failures);
    }

    private sealed record ReceiptEntry(OwnedSessionCommandReceipt Receipt, OwnedTextSendRequest? Send = null, OwnedAbortRequest? Abort = null, OwnedTextSteerRequest? Steer = null);
    private sealed record Prepared(SessionViewDescriptor Session, SessionExecutionOptions Options);

    private sealed class SteerOperation(OwnedTextSteerRequest request, OwnedSessionCommandReceipt receipt)
    {
        internal OwnedTextSteerRequest Request { get; } = request;
        internal OwnedSessionCommandReceipt Receipt { get; } = receipt;
        internal CancellationTokenSource Execution { get; } = new();
        internal TaskCompletionSource Launch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Work { get; set; } = Task.CompletedTask;
        internal Task Cancellation { get; set; } = Task.CompletedTask;
    }

    private sealed class SendOperation(OwnedTextSendRequest request, OwnedSessionCommandReceipt receipt)
    {
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
        internal Task? Control { get; set; }
        internal Task Cancellation { get; set; } = Task.CompletedTask;
        internal bool CancelRequested { get; set; }
        internal bool CancellationInitiated { get; set; }
        internal bool CancellationFailed { get; set; }
        internal bool Released { get; set; }
        internal List<OwnedSessionCommandReceipt> AbortReceipts { get; } = [];
        internal OwnedSessionCommandResult? ControlResult { get; set; }
    }
}
