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
    private readonly Dictionary<Guid, OwnedSessionCommandReceipt> _receipts = [];
    private bool _closed;

    internal SessionOperationsService() { }
    internal SessionOperationsService(OwnedSessionCommandService commands, string epoch)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _epoch = epoch;
        _send = commands.AdmitSend;
        _abort = commands.AdmitAbort;
    }
    // Mandatory rejection-route seam: tests use throwing literal callbacks, never fabricate receipts.
    internal SessionOperationsService(string epoch, Func<OwnedTextSendRequest, OwnedSessionCommandAdmission> send,
        Func<OwnedAbortRequest, OwnedSessionCommandAdmission> abort)
    {
        _epoch = epoch;
        _send = (request, _) => send(request);
        _abort = (request, _) => abort(request);
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
        return new(receipt.ClientRequestId, receipt.SessionId, receipt.OperationId.ToString("D"), receipt.TargetOperationId?.ToString("D"),
            receipt.Kind.ToString(), result is null ? "pending" : "terminal", result?.Outcome.ToString(), result?.Code, result?.RunId?.ToString());
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
        && row.Kind is "Send" or "Abort" && row.State is "pending" or "terminal"
        && (row.Outcome is null or "Completed" or "Cancelled" or "Failed")
        && (row.Code is null || Identity(row.Code, 64, trim: false))
        && (row.RunId is null || Identity(row.RunId, 256, trim: false));

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
internal sealed record SessionReceiptRequest(string ExpectedEpoch, int Offset);
internal sealed record SessionAdmission(string Status, string? Epoch, SessionReceiptView? Receipt);
internal sealed record SessionReceiptPage(string Status, string? Epoch, SessionReceiptView[] Rows, int? Next);
internal sealed record SessionReceiptView(string ClientRequestId, string SessionId, string OperationId, string? TargetOperationId,
    string Kind, string State, string? Outcome, string? Code, string? RunId);
