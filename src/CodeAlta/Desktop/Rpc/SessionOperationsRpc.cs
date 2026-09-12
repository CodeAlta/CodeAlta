using System.Globalization;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// The host owns every operation. This application-scoped transport index retains receipt references
// only, never prompt text, executable workers, cancellation sources or another retry policy.
[NeoRpcService("sessions", Version = 1)]
internal sealed class SessionOperationsService
{
    private readonly object _gate = new();
    private readonly string? _epoch;
    private readonly Func<OwnedTextSendRequest, CancellationToken, OwnedSessionCommandAdmission>? _send;
    private readonly Func<OwnedAbortRequest, CancellationToken, OwnedSessionCommandAdmission>? _abort;
    private readonly Func<OwnedTextSteerRequest, CancellationToken, OwnedSessionCommandAdmission>? _steer;
    private readonly Func<OwnedCompactRequest, CancellationToken, OwnedSessionCommandAdmission>? _compact;
    private readonly Func<OwnedAbortRunRequest, CancellationToken, OwnedSessionCommandAdmission>? _abortRun;
    private readonly Func<OwnedTextQueueRequest, CancellationToken, OwnedSessionCommandAdmission>? _queue;
    private readonly Func<OwnedCancelQueueRequest, CancellationToken, OwnedSessionCommandAdmission>? _cancelQueue;
    private readonly Dictionary<Guid, OwnedSessionCommandReceipt> _receipts = [];
    private bool _closed;

    internal SessionOperationsService() { }
    internal SessionOperationsService(OwnedSessionCommandService commands, string epoch)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _epoch = epoch;
        _send = commands.AdmitSend;
        _abort = commands.AdmitAbort;
        _steer = commands.AdmitSteer;
        _compact = commands.AdmitCompact;
        _abortRun = commands.AdmitAbortRun;
        _queue = commands.AdmitQueue;
        _cancelQueue = commands.AdmitCancelQueue;
    }
    // Mandatory rejection-route seam: tests use throwing literal callbacks, never fabricate receipts.
    internal SessionOperationsService(string epoch, Func<OwnedTextSendRequest, OwnedSessionCommandAdmission> send,
        Func<OwnedAbortRequest, OwnedSessionCommandAdmission> abort)
        : this(epoch, send, abort, _ => throw new InvalidOperationException("No steering callback configured.")) { }

    internal SessionOperationsService(string epoch, Func<OwnedTextSendRequest, OwnedSessionCommandAdmission> send,
        Func<OwnedAbortRequest, OwnedSessionCommandAdmission> abort, Func<OwnedTextSteerRequest, OwnedSessionCommandAdmission> steer)
        : this(epoch, send, abort, steer, _ => throw new InvalidOperationException("No compaction callback configured.")) { }

    internal SessionOperationsService(string epoch, Func<OwnedTextSendRequest, OwnedSessionCommandAdmission> send,
        Func<OwnedAbortRequest, OwnedSessionCommandAdmission> abort, Func<OwnedTextSteerRequest, OwnedSessionCommandAdmission>? steer,
        Func<OwnedCompactRequest, OwnedSessionCommandAdmission> compact)
        : this(epoch, send, abort, steer, compact, _ => throw new InvalidOperationException("No exact cancellation callback configured.")) { }

    internal SessionOperationsService(string epoch, Func<OwnedTextSendRequest, OwnedSessionCommandAdmission> send,
        Func<OwnedAbortRequest, OwnedSessionCommandAdmission> abort, Func<OwnedTextSteerRequest, OwnedSessionCommandAdmission>? steer,
        Func<OwnedCompactRequest, OwnedSessionCommandAdmission> compact, Func<OwnedAbortRunRequest, OwnedSessionCommandAdmission> abortRun)
        : this(epoch, send, abort, steer, compact, abortRun,
            _ => throw new InvalidOperationException("No queue callback configured."),
            _ => throw new InvalidOperationException("No queue cancellation callback configured.")) { }

    internal SessionOperationsService(string epoch, Func<OwnedTextSendRequest, OwnedSessionCommandAdmission> send,
        Func<OwnedAbortRequest, OwnedSessionCommandAdmission> abort, Func<OwnedTextSteerRequest, OwnedSessionCommandAdmission>? steer,
        Func<OwnedCompactRequest, OwnedSessionCommandAdmission> compact, Func<OwnedAbortRunRequest, OwnedSessionCommandAdmission> abortRun,
        Func<OwnedTextQueueRequest, OwnedSessionCommandAdmission> queue, Func<OwnedCancelQueueRequest, OwnedSessionCommandAdmission> cancelQueue)
    {
        _epoch = epoch;
        _send = (request, _) => send(request);
        _abort = (request, _) => abort(request);
        _steer = steer is null ? null : (request, _) => steer(request);
        _compact = (request, _) => compact(request);
        _abortRun = (request, _) => abortRun(request);
        _queue = (request, _) => queue(request);
        _cancelQueue = (request, _) => cancelQueue(request);
    }

    [NeoRpcMethod("send")]
    public SessionAdmission Send(SessionSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, null);
            if (!Identity(request.ClientRequestId, 256) || !Identity(request.SessionId, 256) || !Identity(request.Text, 32768, trim: false))
                return new("invalid_request", _epoch, null);
            cancellationToken.ThrowIfCancellationRequested();
            try { return Retain(_send!(new(request.ClientRequestId, request.SessionId, request.Text), cancellationToken)); }
            catch (ArgumentException) { return new("invalid_request", _epoch, null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return new("admission_failed", _epoch, null); }
        }
    }

    [NeoRpcMethod("abort")]
    public SessionAdmission Abort(SessionAbortRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, null);
            if (!Identity(request.ClientRequestId, 256) || !CanonicalGuid(request.TargetOperationId, out var target) || target == Guid.Empty)
                return new("invalid_request", _epoch, null);
            cancellationToken.ThrowIfCancellationRequested();
            try { return Retain(_abort!(new(request.ClientRequestId, target), cancellationToken)); }
            catch (ArgumentException) { return new("invalid_request", _epoch, null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return new("admission_failed", _epoch, null); }
        }
    }

    [NeoRpcMethod("steer")]
    public SessionAdmission Steer(SessionSteerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, null);
            if (!Identity(request.ClientRequestId, 256) || !Identity(request.SessionId, 256)
                || !CanonicalGuid(request.ExpectedRuntimeInstanceId, out var runtime) || runtime == Guid.Empty
                || !long.TryParse(request.ExpectedAttachmentGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out var attachment)
                || attachment <= 0 || attachment.ToString(CultureInfo.InvariantCulture) != request.ExpectedAttachmentGeneration
                || !Identity(request.ExpectedRunId, 256) || !Identity(request.Text, 32768, trim: false))
                return new("invalid_request", _epoch, null);
            cancellationToken.ThrowIfCancellationRequested();
            try { return Retain(_steer!(new(request.ClientRequestId, request.SessionId, runtime, attachment, request.ExpectedRunId, request.Text), cancellationToken)); }
            catch (ArgumentException) { return new("invalid_request", _epoch, null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return new("admission_failed", _epoch, null); }
        }
    }

    [NeoRpcMethod("compact")]
    public SessionAdmission Compact(SessionCompactRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, null);
            if (!Identity(request.ClientRequestId, 256) || !Identity(request.SessionId, 256)
                || !CanonicalGuid(request.ExpectedRuntimeInstanceId, out var runtime) || runtime == Guid.Empty
                || !long.TryParse(request.ExpectedAttachmentGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out var attachment)
                || attachment <= 0 || attachment.ToString(CultureInfo.InvariantCulture) != request.ExpectedAttachmentGeneration)
                return new("invalid_request", _epoch, null);
            cancellationToken.ThrowIfCancellationRequested();
            try { return Retain(_compact!(new(request.ClientRequestId, request.SessionId, runtime, attachment), cancellationToken)); }
            catch (ArgumentException) { return new("invalid_request", _epoch, null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return new("admission_failed", _epoch, null); }
        }
    }

    [NeoRpcMethod("abortRun")]
    public SessionAdmission AbortRun(SessionAbortRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, null);
            if (!Identity(request.ClientRequestId, 256) || !Identity(request.SessionId, 256) || !Identity(request.ExpectedRunId, 256)
                || !CanonicalGuid(request.ExpectedRuntimeInstanceId, out var runtime) || runtime == Guid.Empty
                || !long.TryParse(request.ExpectedAttachmentGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out var attachment)
                || attachment <= 0 || attachment.ToString(CultureInfo.InvariantCulture) != request.ExpectedAttachmentGeneration)
                return new("invalid_request", _epoch, null);
            cancellationToken.ThrowIfCancellationRequested();
            try { return Retain(_abortRun!(new(request.ClientRequestId, request.SessionId, runtime, attachment, request.ExpectedRunId), cancellationToken)); }
            catch (ArgumentException) { return new("invalid_request", _epoch, null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return new("admission_failed", _epoch, null); }
        }
    }

    [NeoRpcMethod("queue")]
    public SessionAdmission Queue(SessionQueueRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, null);
            if (!Identity(request.ClientRequestId, 256) || !Identity(request.SessionId, 256)
                || !CanonicalGuid(request.ExpectedRuntimeInstanceId, out var runtime) || runtime == Guid.Empty
                || !long.TryParse(request.ExpectedAttachmentGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out var attachment)
                || attachment <= 0 || attachment.ToString(CultureInfo.InvariantCulture) != request.ExpectedAttachmentGeneration
                || !Identity(request.Text, 32768, trim: false))
                return new("invalid_request", _epoch, null);
            cancellationToken.ThrowIfCancellationRequested();
            try { return Retain(_queue!(new(request.ClientRequestId, request.SessionId, runtime, attachment, request.Text), cancellationToken)); }
            catch (ArgumentException) { return new("invalid_request", _epoch, null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return new("admission_failed", _epoch, null); }
        }
    }

    [NeoRpcMethod("cancelQueue")]
    public SessionAdmission CancelQueue(SessionCancelQueueRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, null);
            if (!Identity(request.ClientRequestId, 256) || !CanonicalGuid(request.TargetOperationId, out var target) || target == Guid.Empty)
                return new("invalid_request", _epoch, null);
            cancellationToken.ThrowIfCancellationRequested();
            try { return Retain(_cancelQueue!(new(request.ClientRequestId, target), cancellationToken)); }
            catch (ArgumentException) { return new("invalid_request", _epoch, null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return new("admission_failed", _epoch, null); }
        }
    }

    [NeoRpcMethod("receipts")]
    public SessionReceiptPage Receipts(SessionReceiptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_epoch is null) return new("unconfigured", null, [], null);
            if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", _epoch, [], null);
            if (request.Offset < 0 || request.Offset > _receipts.Count || request.Offset % 64 != 0)
                return new("invalid_cursor", _epoch, [], null);
            // Insertion order is stable for this epoch: references are never removed or replaced.
            var rows = _receipts.Values.Skip(request.Offset).Take(64).Select(ProjectReceipt).ToArray();
            return ProjectPage(_epoch, rows, request.Offset + rows.Length < _receipts.Count ? request.Offset + rows.Length : null);
        }
    }

    internal void CloseAdmission() { lock (_gate) _closed = true; }
    private string? CheckEpoch(string expected) => _epoch is null ? "unconfigured"
        : !string.Equals(expected, _epoch, StringComparison.Ordinal) ? "stale_epoch" : _closed ? "closed" : null;

    private SessionAdmission Retain(OwnedSessionCommandAdmission admission)
    {
        if (admission.Receipt is { } receipt)
        {
            // Production host fixes owner capacity at 256; every admission goes through this index.
            _receipts.TryAdd(receipt.OperationId, receipt);
            var projected = ProjectReceipt(receipt);
            return ValidRow(projected) ? new(admission.Kind.ToString().ToLowerInvariant(), _epoch, projected)
                : new("wire_limit", _epoch, null);
        }
        return new(admission.Kind.ToString().ToLowerInvariant(), _epoch, null);
    }

    private static SessionReceiptView ProjectReceipt(OwnedSessionCommandReceipt receipt)
    {
        var result = receipt.Completion.IsCompletedSuccessfully ? receipt.Completion.GetAwaiter().GetResult() : null;
        // Completion must be sampled first: insertion settles before completion. A pending execution
        // with a settled (even refused) insertion is a valid racing snapshot, never the reverse.
        var insertion = receipt.QueueInsertion is { IsCompletedSuccessfully: true } task ? task.GetAwaiter().GetResult() : null;
        return new(receipt.ClientRequestId, receipt.SessionId, receipt.OperationId.ToString("D"), receipt.TargetOperationId?.ToString("D"),
            receipt.Kind.ToString(), result is null ? "pending" : "terminal", result?.Outcome.ToString(), result?.Code, result?.RunId?.ToString())
        {
            QueueInsertion = receipt.Kind != OwnedSessionCommandKind.Queue ? null
                : insertion is null ? new("pending", null, null) : new("terminal", insertion.Accepted, insertion.Code),
        };
    }

    internal static SessionReceiptPage ProjectPage(string epoch, SessionReceiptView[] rows, int? next)
    {
        // <=64 * 6400 + 8192 = 417792 bytes, below the 448 KiB response budget.
        // Worst-case escaping is six bytes per UTF-16 unit; each row includes property overhead.
        // The 8192 allowance covers page properties, escaped epoch, cursor and the RPC envelope.
        // NeoAstra does not enforce a per-response byte cap: this is validated payload accounting.
        if (rows.Length > 64 || !Identity(epoch, 64) || rows.Any(row => !ValidRow(row))) return new("wire_limit", epoch, [], null);
        return new("ok", epoch, rows, next);
    }

    private static bool ValidRow(SessionReceiptView row) => Identity(row.ClientRequestId, 256, trim: false)
        && Identity(row.SessionId, 256, trim: false) && CanonicalGuid(row.OperationId, out _)
        && (row.TargetOperationId is null || CanonicalGuid(row.TargetOperationId, out _))
        && row.State is "pending" or "terminal"
        && (row.Outcome is null or "Completed" or "Cancelled" or "Failed")
        && (row.Code is null || Identity(row.Code, 64, trim: false))
        && (row.RunId is null || Identity(row.RunId, 256, trim: false))
        && (row.Kind is "Queue" or "CancelQueue" ? ValidQueueRow(row)
            : row.Kind is "Send" or "Abort" or "Steer" or "Compact" or "AbortRun" && row.QueueInsertion is null);

    private static bool ValidQueueRow(SessionReceiptView row)
    {
        if (!CanonicalGuid(row.OperationId, out var operation) || operation == Guid.Empty) return false;
        if (row.State == "pending" && (row.Outcome is not null || row.Code is not null || row.RunId is not null)) return false;
        if (row.State == "terminal" && (row.Outcome is null || !Identity(row.Code, 64, trim: false))) return false;
        if (row.Kind == "CancelQueue")
            return row.QueueInsertion is null && row.RunId is null
                && CanonicalGuid(row.TargetOperationId, out var target) && target != Guid.Empty && target != operation
                && (row.State == "pending" || row.Outcome == "Failed"
                    || row.Outcome == "Completed" && row.Code is "queue_cancellation_signalled" or "already_terminal");
        if (row.TargetOperationId is not null || row.QueueInsertion is not { } insertion) return false;
        if (insertion.State == "pending")
            return row.State == "pending" && insertion.Accepted is null && insertion.Code is null;
        if (insertion.State != "terminal" || insertion.Accepted is null || !Identity(insertion.Code, 64, trim: false)
            || (insertion.Accepted.Value ? insertion.Code != "queue_accepted" : insertion.Code == "queue_accepted")) return false;
        if (row.State == "pending") return true;
        return row.Outcome == "Completed"
            ? insertion.Accepted == true && row.Code == "queue_dispatched" && row.RunId is not null
            : row.RunId is null && (row.Outcome == "Failed" || row.Outcome == "Cancelled" && row.Code == "queue_cancelled");
    }

    // Lowercase D format only. Guid parsing alone accepts padding and is not a wire bound.
    private static bool CanonicalGuid(string? value, out Guid parsed)
    {
        parsed = default;
        return value is { Length: 36 } && Guid.TryParseExact(value, "D", out parsed)
            && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);
    }

    private static bool Identity(string? value, int maximum, bool trim = true)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || (trim && value != value.Trim())) return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}

internal sealed record SessionSendRequest(string ExpectedEpoch, string ClientRequestId, string SessionId, string Text);
internal sealed record SessionAbortRequest(string ExpectedEpoch, string ClientRequestId, string TargetOperationId);
internal sealed record SessionSteerRequest(string ExpectedEpoch, string ClientRequestId, string SessionId,
    string ExpectedRuntimeInstanceId, string ExpectedAttachmentGeneration, string ExpectedRunId, string Text);
internal sealed record SessionCompactRequest(string ExpectedEpoch, string ClientRequestId, string SessionId,
    string ExpectedRuntimeInstanceId, string ExpectedAttachmentGeneration);
internal sealed record SessionAbortRunRequest(string ExpectedEpoch, string ClientRequestId, string SessionId,
    string ExpectedRuntimeInstanceId, string ExpectedAttachmentGeneration, string ExpectedRunId);
internal sealed record SessionQueueRequest(string ExpectedEpoch, string ClientRequestId, string SessionId,
    string ExpectedRuntimeInstanceId, string ExpectedAttachmentGeneration, string Text);
internal sealed record SessionCancelQueueRequest(string ExpectedEpoch, string ClientRequestId, string TargetOperationId);
internal sealed record SessionReceiptRequest(string ExpectedEpoch, int Offset);
internal sealed record SessionAdmission(string Status, string? Epoch, SessionReceiptView? Receipt);
internal sealed record SessionReceiptPage(string Status, string? Epoch, SessionReceiptView[] Rows, int? Next);
internal sealed record SessionReceiptView(string ClientRequestId, string SessionId, string OperationId, string? TargetOperationId,
    string Kind, string State, string? Outcome, string? Code, string? RunId)
{
    public SessionQueueInsertionView? QueueInsertion { get; init; }
}
internal sealed record SessionQueueInsertionView(string State, bool? Accepted, string? Code);
