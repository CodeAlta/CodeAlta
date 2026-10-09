using System.Globalization;
using System.Text.Json.Serialization;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

// The host owns every operation. This application-scoped transport index retains receipt references
// only, never prompt text, executable workers, cancellation sources or another retry policy. It keeps as
// many references as the host keeps receipts, and makes room the same way: with the oldest settled one.
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
    private readonly Dictionary<Guid, LinkedListNode<OwnedSessionCommandReceipt>> _receipts = [];
    // In the order of their admission, the oldest first.
    private readonly LinkedList<OwnedSessionCommandReceipt> _kept = new();
    private readonly int _capacity = 256;
    private bool _closed;
    private readonly Func<string, CancellationToken, Task<OwnedSelectionChoices?>>? _choices;
    private readonly OwnedSessionCommandService? _commands;

    internal SessionOperationsService() { }
    internal SessionOperationsService(OwnedSessionCommandService commands, string epoch)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _epoch = epoch;
        _commands = commands;
        _capacity = commands.ReceiptCapacity;
        _send = commands.AdmitSend;
        _abort = commands.AdmitAbort;
        _steer = commands.AdmitSteer;
        _compact = commands.AdmitCompact;
        _abortRun = commands.AdmitAbortRun;
        _queue = commands.AdmitQueue;
        _cancelQueue = commands.AdmitCancelQueue;
        _choices = commands.GetObservedSelectionChoicesAsync;
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
        // Match the browser's opaque key without recording prompts, paths or provider data.
        // Diagnostic only: an uninitialized process logger must never turn admission into a failure.
        if (LogManager.IsInitialized)
        {
            var diagnosticId = Guid.TryParseExact(request.ClientRequestId, "D", out var id) ? id.ToString("D") : "non-uuid";
            LogManager.GetLogger("CodeAlta.Desktop.Rpc").Info($"Send reached backend ({diagnosticId})");
        }
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, null);
            if (!Identity(request.ClientRequestId, 256) || !Identity(request.SessionId, 256)
                || !(Identity(request.Text, 32768, trim: false) || request.Text == "" && request.Images is { Count: > 0 }))
                return new("invalid_request", _epoch, null);
            if (request.References is { } references && (!Identity(references.ProjectId, 256) || !Identity(references.ProjectPath, 4096)))
                return new("invalid_request", _epoch, null);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Selection is { } selection && (!Identity(selection.ProviderKey, 256) || !Identity(selection.AgentPromptId, 256)
                || selection.ModelId is not null && !Identity(selection.ModelId, 256)
                || selection.PermissionMode is not null && !Identity(selection.PermissionMode, 256)
                || selection.ReasoningEffort is not null && (!Enum.TryParse<AgentReasoningEffort>(selection.ReasoningEffort, out var effort)
                    || !Enum.IsDefined(effort) || effort.ToString() != selection.ReasoningEffort)))
                return new("invalid_request", _epoch, null);
            try { return Retain(_send!(new(request.ClientRequestId, request.SessionId, request.Text)
            {
                Images = request.Images?.Select(image => new OwnedPromptImage(image.Title, image.MediaType, image.Base64)).ToArray(),
                References = request.References is { } scope ? new(scope.ProjectId, scope.ProjectPath) : null,
                // The mode is checked against the provider by the host: null keeps the session's, "provider" leaves it to the provider.
                Selection = request.Selection is { } value ? new(value.ProviderKey, value.AgentPromptId, value.ModelId,
                    value.ReasoningEffort is null ? null : Enum.Parse<AgentReasoningEffort>(value.ReasoningEffort)) { PermissionMode = value.PermissionMode } : null,
            }, cancellationToken)); }
            catch (ArgumentException) { return new("invalid_request", _epoch, null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception failure)
            {
                // The page only sees an uncertain admission; record why, without prompt text.
                if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Desktop.Rpc").Error(failure, "Send admission failed");
                return new("admission_failed", _epoch, null);
            }
        }
    }

    [NeoRpcMethod("searchReferences")]
    public async Task<SessionReferenceSearchResponse> SearchReferencesAsync(SessionReferenceSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, [], false);
            if (!Identity(request.ProjectId, 256) || !Identity(request.ProjectPath, 4096) || request.Query is null || request.Query.Length > 256
                || request.SessionId is not null && !Identity(request.SessionId, 256)) return new("invalid_request", _epoch, [], false);
        }
        if (_commands is null) return new("unavailable", _epoch, [], false);
        try
        {
            var result = await _commands.SearchReferencesAsync(new(request.ProjectId, request.ProjectPath), request.SessionId, request.Query, cancellationToken).ConfigureAwait(false);
            return new(result.Status, _epoch, result.Items.Select(item => new SessionReferenceMatch(item.Path, item.Directory, item.Recent)).ToArray(), result.Omitted, result.Indexed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new("read_error", _epoch, [], true); }
    }

    [NeoRpcMethod("observeReferences")]
    public async Task<SessionReferenceObservationResponse> ObserveReferencesAsync(SessionReferenceObservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, [], false);
            if (!Identity(request.ProjectId, 256) || !Identity(request.ProjectPath, 4096) || request.Text is null || request.Text.Length > 32768
                || request.SessionId is not null && !Identity(request.SessionId, 256)) return new("invalid_request", _epoch, [], false);
        }
        if (_commands is null) return new("unavailable", _epoch, [], false);
        try
        {
            var result = await _commands.ObserveReferencesAsync(new(request.ProjectId, request.ProjectPath), request.SessionId, request.Text, cancellationToken).ConfigureAwait(false);
            return new(result.Status, _epoch, result.Items.Select(item => new SessionReferenceSpan(item.Start, item.Length, item.Status)).ToArray(), result.Omitted);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new("read_error", _epoch, [], true); }
    }

    [NeoRpcMethod("choices")]
    public async Task<SessionChoicesResponse> Choices(SessionChoicesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, request.SessionId, null, [], []);
            if (!Identity(request.SessionId, 256)) return new("invalid_request", _epoch, request.SessionId, null, [], []);
        }
        try
        {
            var choices = _choices is null ? null : await _choices(request.SessionId, cancellationToken).ConfigureAwait(false);
            if (choices is null) return new("unavailable", _epoch, request.SessionId, null, [], []);
            return new("ok", _epoch, request.SessionId,
                new(choices.Current.ProviderKey, choices.Current.AgentPromptId, choices.Current.ModelId, choices.Current.ReasoningEffort?.ToString())
                    { PermissionMode = choices.Current.PermissionMode },
                choices.Prompts.Select(p => new SessionPromptChoice(p.Id, p.Name)).ToArray(),
                choices.Models.Select(m => new SessionModelChoice(m.Id, m.Name, m.Efforts.Select(e => e.ToString()).ToArray())
                    { ImageInput = m.ImageInput, StartEffort = m.StartEffort?.ToString() }).ToArray())
            {
                PermissionModes = choices.PermissionModes.Select(mode => new SessionPermissionModeChoice(mode)).ToArray(),
                DefaultPermissionMode = choices.DefaultPermissionMode,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new("unavailable", _epoch, request.SessionId, null, [], []); }
    }

    [NeoRpcMethod("providerChoices")]
    public async Task<SessionProviderChoices> ProviderChoicesAsync(SessionChoicesRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, request.SessionId, null, null, null, null, []);
            if (!Identity(request.SessionId, 256)) return new("invalid_request", _epoch, request.SessionId, null, null, null, null, []);
        }
        var context = _commands is null ? null : await _commands.GetProviderSelectionAsync(request.SessionId).WaitAsync(cancellationToken).ConfigureAwait(false);
        return context is null ? new("unavailable", _epoch, request.SessionId, null, null, null, null, [])
            : new("ok", _epoch, request.SessionId, context.RuntimeInstanceId.ToString("D"), context.AttachmentGeneration?.ToString(CultureInfo.InvariantCulture),
                context.ProviderKey, context.Revision, context.Providers.Take(32).Select(provider => new SessionProviderChoice(provider.ProviderId.Value, provider.DisplayName)).ToArray());
    }

    [NeoRpcMethod("selectProvider")]
    public async Task<SessionProviderResult> SelectProviderAsync(SessionProviderRequest request, CancellationToken cancellationToken)
    {
        Task<string> work;
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, request.SessionId);
            if (!Identity(request.SessionId, 256) || !Identity(request.ProviderKey, 256) || !Identity(request.ExpectedProviderKey, 256)
                || !CanonicalGuid(request.RuntimeInstanceId, out var runtime) || runtime == Guid.Empty
                || !long.TryParse(request.Revision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0
                || request.AttachmentGeneration is not null && (!long.TryParse(request.AttachmentGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) || generation < 0))
                return new("invalid_request", _epoch, request.SessionId);
            if (_commands is null) return new("unavailable", _epoch, request.SessionId);
            cancellationToken.ThrowIfCancellationRequested();
            work = _commands.SelectProviderAsync(new(request.SessionId, runtime,
                request.AttachmentGeneration is null ? null : long.Parse(request.AttachmentGeneration, CultureInfo.InvariantCulture),
                request.ExpectedProviderKey, request.Revision, []), request.ProviderKey);
        }
        return new(await work.WaitAsync(cancellationToken).ConfigureAwait(false), _epoch, request.SessionId);
    }

    // A task of the provider goes on outside the runs: stopping it is not a command of a run and leaves no receipt.
    // Its effect is read where the task was listed, in the runtime state of the session.
    [NeoRpcMethod("stopBackgroundTask")]
    public async Task<SessionStopBackgroundTaskResult> StopBackgroundTaskAsync(SessionStopBackgroundTaskRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var denied = CheckEpoch(request.ExpectedEpoch);
            if (denied is not null) return new(denied, _epoch, request.SessionId);
            if (!Identity(request.SessionId, 256) || !Identity(request.TaskId, 256)) return new("invalid_request", _epoch, request.SessionId);
            if (_commands is null) return new("unavailable", _epoch, request.SessionId);
        }
        return new(await _commands.StopBackgroundTaskAsync(request.SessionId, request.TaskId, cancellationToken).ConfigureAwait(false), _epoch, request.SessionId);
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
            // What can still be acted on comes first: the pending commands, then the settled ones, each the most
            // recent first. The first page is what the window reads; an offset names a place in this order as it
            // is now, which moves when a command is admitted or settles.
            var ordered = new List<OwnedSessionCommandReceipt>(_kept.Count);
            for (var node = _kept.Last; node is not null; node = node.Previous)
                if (!node.Value.Completion.IsCompleted) ordered.Add(node.Value);
            for (var node = _kept.Last; node is not null; node = node.Previous)
                if (node.Value.Completion.IsCompleted) ordered.Add(node.Value);
            var rows = ordered.Skip(request.Offset).Take(64).Select(ProjectReceipt).ToArray();
            return ProjectPage(_epoch, rows, request.Offset + rows.Length < ordered.Count ? request.Offset + rows.Length : null);
        }
    }

    internal void CloseAdmission() { lock (_gate) _closed = true; }
    private string? CheckEpoch(string expected) => _epoch is null ? "unconfigured"
        : !string.Equals(expected, _epoch, StringComparison.Ordinal) ? "stale_epoch" : _closed ? "closed" : null;

    private SessionAdmission Retain(OwnedSessionCommandAdmission admission)
    {
        if (admission.Receipt is { } receipt)
        {
            // Every admission of the page goes through this index.
            Keep(receipt);
            var projected = ProjectReceipt(receipt);
            return ValidRow(projected) ? new(admission.Kind.ToString().ToLowerInvariant(), _epoch, projected)
                : new("wire_limit", _epoch, null);
        }
        return new(admission.Kind.ToString().ToLowerInvariant(), _epoch, null);
    }

    // Called under _gate. A replay names a receipt that is already here; a new one takes the place of the
    // oldest settled one when the index is full, as it did in the host. The host admits a command only while it
    // keeps a settled receipt or has room, so a full index has a settled one too.
    private void Keep(OwnedSessionCommandReceipt receipt)
    {
        if (_receipts.ContainsKey(receipt.OperationId)) return;
        if (_receipts.Count >= _capacity)
        {
            var oldest = _kept.First;
            while (oldest is not null && !oldest.Value.Completion.IsCompleted) oldest = oldest.Next;
            oldest ??= _kept.First!;
            _kept.Remove(oldest);
            _receipts.Remove(oldest.Value.OperationId);
        }

        _receipts.Add(receipt.OperationId, _kept.AddLast(receipt));
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

internal sealed record SessionSendRequest(string ExpectedEpoch, string ClientRequestId, string SessionId, string Text)
{
    public IReadOnlyList<SessionPromptImage>? Images { get; init; }
    public SessionReferenceScope? References { get; init; }
    public SessionSelection? Selection { get; init; }
}
internal sealed record SessionReferenceScope(string ProjectId, string ProjectPath);
internal sealed record SessionReferenceSearchRequest(string ExpectedEpoch, string ProjectId, string ProjectPath, string? SessionId, string Query);
internal sealed record SessionReferenceMatch(string Path, bool Directory, bool Recent);
internal sealed record SessionReferenceSearchResponse(string Status, string? Epoch, IReadOnlyList<SessionReferenceMatch> Items, bool Omitted, int Indexed = 0);
internal sealed record SessionReferenceObservationRequest(string ExpectedEpoch, string ProjectId, string ProjectPath, string? SessionId, string Text);
internal sealed record SessionReferenceSpan(int Start, int Length, string Status);
internal sealed record SessionReferenceObservationResponse(string Status, string? Epoch, IReadOnlyList<SessionReferenceSpan> Items, bool Omitted);
internal sealed record SessionSelection(string ProviderKey, string AgentPromptId, string? ModelId, string? ReasoningEffort)
{
    // The mode chosen for the session, or null: in a send, null keeps the session's and "provider" goes back to the
    // provider's; in choices, null means the session runs in the provider's.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PermissionMode { get; init; }
}
internal sealed record SessionChoicesRequest(string ExpectedEpoch, string SessionId);
internal sealed record SessionPromptChoice(string Id, string Name);
internal sealed record SessionPromptImage(string Title, string MediaType, string Base64);
internal sealed record SessionModelChoice(string Id, string Name, IReadOnlyList<string> Efforts)
{
    public bool? ImageInput { get; init; }
    // The effort a session starts this model with: one of Efforts, null when the model reports none.
    public string? StartEffort { get; init; }
}
internal sealed record SessionChoicesResponse(string Status, string? Epoch, string SessionId, SessionSelection? Current,
    IReadOnlyList<SessionPromptChoice> Prompts, IReadOnlyList<SessionModelChoice> Models)
{
    // The modes a session of the provider can be given, empty when it has none; the page names them. A response that
    // is not "ok" has none. Optional on the page, as the other fields of the permission mode.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SessionPermissionModeChoice>? PermissionModes { get; init; }
    // The mode the provider is configured with, or null when it leaves it to the CLI's own setting.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultPermissionMode { get; init; }
}
// A mode a session can be given, by its identifier: the page names it.
internal sealed record SessionPermissionModeChoice(string Id);
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
