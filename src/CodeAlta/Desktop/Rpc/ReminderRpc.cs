using System.Globalization;
using System.Text.Json;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// The reminder worker is host-owned. This transport only exposes bounded rows for one exact
// persisted session; callback reads use the host's admitted and drained workspace reader.
[NeoRpcService("reminders", Version = 1)]
internal sealed class ReminderService : IAsyncDisposable, IAltaReminderDelivery
{
    internal const int MaximumDetailResponseBytes = 96 * 1024;
    private readonly object _gate = new();
    private readonly string _epoch;
    private readonly Func<string, CancellationToken, Task<bool>> _exists;
    private readonly Func<OwnedTextSendRequest, OwnedSessionCommandAdmission> _send;
    private readonly AltaReminderService _reminders;
    private bool _closed;

    internal ReminderService(OwnedSessionWorkspace reads, OwnedSessionCommandService commands, string epoch)
        : this(epoch, async (id, token) => (await reads.ReadSnapshotAsync(token).ConfigureAwait(false)).Sessions
            .Any(session => string.Equals(session.SessionId, id, StringComparison.Ordinal)),
            request => commands.AdmitSend(request)) { }

    // Literal callbacks and clock let tests exercise admission and firing without a provider or profile writes.
    internal ReminderService(string epoch, Func<string, CancellationToken, Task<bool>> exists,
        Func<OwnedTextSendRequest, OwnedSessionCommandAdmission> send, TimeProvider? clock = null)
    {
        if (!Identity(epoch)) throw new ArgumentException("A bounded host epoch is required.", nameof(epoch));
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(send);
        _epoch = epoch;
        _exists = exists;
        _send = send;
        _reminders = new AltaReminderService(new AltaServiceCollection(), clock ?? TimeProvider.System, this);
    }

    [NeoRpcMethod("list")]
    public async Task<ReminderListResponse> List(ReminderListRequest request, CancellationToken cancellationToken)
    {
        var error = Check(request?.ExpectedEpoch, request?.SessionId);
        if (error is not null) return new(error, _epoch, Identity(request?.SessionId) ? request!.SessionId : null, [], 0, 0);
        try
        {
            if (!await _exists(request!.SessionId, cancellationToken).ConfigureAwait(false))
                return new("missing_session", _epoch, request.SessionId, [], 0, 0);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_closed) return new("closed", _epoch, request.SessionId, [], 0, 0);
                var all = _reminders.List(request.SessionId, includeCompleted: true);
                var active = all.Count(row => row.State == AltaReminderStates.Active);
                // Total is bounded by create admission (32 retained rows per session).
                var rows = all.Select(row => new ReminderRow(row.ReminderId, row.State, row.ContentPreview,
                    (int)row.Duration.TotalSeconds, row.RepeatCount, row.FiredCount, row.DueAt,
                    row.LastExitCode, row.LastError is null ? null : row.LastError[..Math.Min(128, row.LastError.Length)])).ToArray();
                return new("ok", _epoch, request.SessionId, rows, active, rows.Length - active);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new("read_failed", _epoch, request!.SessionId, [], 0, 0); }
    }

    [NeoRpcMethod("detail")]
    public async Task<ReminderDetailResponse> Detail(ReminderDetailRequest request, CancellationToken cancellationToken)
    {
        var sessionId = Identity(request?.SessionId) ? request!.SessionId : null;
        var reminderId = Identity(request?.ReminderId) ? request!.ReminderId : null;
        ReminderDetailResponse Error(string status) => new(status, _epoch, sessionId, reminderId, null, null, null, null);
        var error = Check(request?.ExpectedEpoch, request?.SessionId);
        if (error is not null || sessionId is null || reminderId is null) return Error(error ?? "invalid_request");
        try
        {
            lock (_gate) if (_closed) return Error("closed");
            if (!await _exists(sessionId, cancellationToken).ConfigureAwait(false)) return Error("missing_session");
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_closed) return Error("closed");
                // One shared-state snapshot avoids pairing a descriptor with another edit's content/revision.
                if (!_reminders.TryGetEditSnapshot(reminderId, out var row, out var content, out var revision) ||
                    !string.Equals(row!.TargetSessionId, sessionId, StringComparison.Ordinal)) return Error("missing_reminder");
                if (!Text(content, 4096, multiline: true)) return Error("read_failed");
                var response = new ReminderDetailResponse("ok", _epoch, sessionId, reminderId, content,
                    (int)row.Duration.TotalSeconds, row.RepeatCount, revision);
                return JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.ReminderDetailResponse).Length <= MaximumDetailResponseBytes
                    ? response : Error("wire_limit");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Error("read_failed"); }
    }

    [NeoRpcMethod("save")]
    public async Task<ReminderMutationResponse> Save(ReminderSaveRequest request, CancellationToken cancellationToken)
    {
        var error = Check(request?.ExpectedEpoch, request?.SessionId);
        if (error is not null) return new(error, _epoch, Identity(request?.SessionId) ? request!.SessionId : null, null);
        if (!Identity(request!.ReminderId) || !long.TryParse(request.EditRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision < 0 || revision.ToString(CultureInfo.InvariantCulture) != request.EditRevision ||
            !Text(request.Content, 4096, multiline: true) || string.IsNullOrWhiteSpace(request.Content))
            return new("invalid_request", _epoch, request.SessionId, null);
        try
        {
            if (!await _exists(request.SessionId, cancellationToken).ConfigureAwait(false))
                return new("missing_session", _epoch, request.SessionId, null);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_closed) return new("closed", _epoch, request.SessionId, null);
                // Owner, id, active state and revision are compared with the replacement under one shared gate.
                var result = _reminders.TryUpdateContent(request.ReminderId, request.SessionId, request.EditRevision,
                    request.Content, out _, out _);
                return result switch
                {
                    AltaReminderContentUpdateResult.Updated => new("ok", _epoch, request.SessionId, request.ReminderId),
                    AltaReminderContentUpdateResult.Missing => new("missing_reminder", _epoch, request.SessionId, null),
                    AltaReminderContentUpdateResult.Conflict => new("conflict", _epoch, request.SessionId, null),
                    _ => new("completed", _epoch, request.SessionId, null),
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // A committed edit may have lost its response. Never invite an automatic retry.
        catch (Exception) { return new("unconfirmed", _epoch, request.SessionId, request.ReminderId); }
    }

    [NeoRpcMethod("create")]
    public async Task<ReminderMutationResponse> Create(ReminderCreateRequest request, CancellationToken cancellationToken)
    {
        var error = Check(request?.ExpectedEpoch, request?.SessionId);
        if (error is not null) return new(error, _epoch, Identity(request?.SessionId) ? request!.SessionId : null, null);
        if (!Text(request!.Content, 4096, multiline: true) || string.IsNullOrWhiteSpace(request.Content) ||
            request.DelaySeconds is < 1 or > 86400 || request.RepeatCount is < 1 or > 20)
            return new("invalid_request", _epoch, request.SessionId, null);
        try
        {
            if (!await _exists(request.SessionId, cancellationToken).ConfigureAwait(false))
                return new("missing_session", _epoch, request.SessionId, null);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_closed) return new("closed", _epoch, request.SessionId, null);
                if (_reminders.List(request.SessionId, includeCompleted: true).Count >= 32 ||
                    _reminders.List(null, includeCompleted: true).Count >= 256)
                    return new("capacity", _epoch, request.SessionId, null);
                var reminder = _reminders.Create(new AltaReminderCreateRequest
                {
                    TargetSessionId = request.SessionId, SourceSessionId = request.SessionId,
                    Content = request.Content, Duration = TimeSpan.FromSeconds(request.DelaySeconds),
                    RepeatCount = request.RepeatCount,
                });
                return new("ok", _epoch, request.SessionId, reminder.ReminderId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // A committed creation may have lost its response. Never invite an automatic retry.
        catch (Exception) { return new("unconfirmed", _epoch, request.SessionId, null); }
    }

    [NeoRpcMethod("delete")]
    public ReminderMutationResponse Delete(ReminderDeleteRequest request, CancellationToken cancellationToken)
    {
        var error = Check(request?.ExpectedEpoch, request?.SessionId);
        if (error is not null) return new(error, _epoch, Identity(request?.SessionId) ? request!.SessionId : null, null);
        if (!Identity(request!.ReminderId) || request.Confirmation != request.ReminderId)
            return new("invalid_request", _epoch, request.SessionId, null);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_closed) return new("closed", _epoch, request.SessionId, null);
            // Never delete another session's reminder, even when supplied its exact id.
            if (!_reminders.List(request.SessionId, includeCompleted: true).Any(row => row.ReminderId == request.ReminderId))
                return new("missing_reminder", _epoch, request.SessionId, null);
            try
            {
                return _reminders.TryDelete(request.ReminderId, out _)
                    ? new("ok", _epoch, request.SessionId, request.ReminderId)
                    : new("missing_reminder", _epoch, request.SessionId, null);
            }
            catch (Exception) { return new("unconfirmed", _epoch, request.SessionId, request.ReminderId); }
        }
    }

    public async Task<AltaReminderDeliveryResult> DeliverAsync(AltaReminderDescriptor reminder, string content)
    {
        // A unique key per firing prevents retries from targeting later reminders/runs.
        OwnedSessionCommandAdmission admission;
        lock (_gate)
        {
            if (_closed) return new(AltaExitCodes.Failure, "closed", string.Empty);
            try
            {
                admission = _send(new OwnedTextSendRequest($"reminder:{reminder.ReminderId}:{reminder.FiredCount}",
                    reminder.TargetSessionId, content));
            }
            catch (Exception) { return new(AltaExitCodes.Failure, "send_failed", string.Empty); }
        }
        if (admission.Kind != OwnedSessionCommandAdmissionKind.Accepted || admission.Receipt is null)
            return new(AltaExitCodes.Failure, $"send_{admission.Kind.ToString().ToLowerInvariant()}", string.Empty);
        try
        {
            var result = await admission.Receipt.Completion.ConfigureAwait(false);
            return result.Outcome == OwnedSessionCommandOutcome.Completed
                ? new(AltaExitCodes.Success, null, string.Empty)
                : new(AltaExitCodes.Failure, result.Code ?? "send_failed", string.Empty);
        }
        catch (Exception) { return new(AltaExitCodes.Failure, "send_failed", string.Empty); }
    }

    internal void CloseAdmission() { lock (_gate) _closed = true; }
    public async ValueTask DisposeAsync() { CloseAdmission(); await _reminders.DisposeAsync().ConfigureAwait(false); }

    private string? Check(string? epoch, string? session)
    {
        if (!Identity(epoch) || !Identity(session)) return "invalid_request";
        return epoch == _epoch ? null : "stale_epoch";
    }

    private static bool Identity(string? value) => Text(value, 256) && !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && !value.Any(char.IsControl);
    private static bool Text(string? value, int max, bool multiline = false)
    {
        if (value is not { Length: > 0 } || value.Length > max) return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]) && !(multiline && value[i] is '\n' or '\r' or '\t')) return false;
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i]))) return false;
        }
        return true;
    }
}

internal sealed record ReminderListRequest(string ExpectedEpoch, string SessionId);
internal sealed record ReminderDetailRequest(string ExpectedEpoch, string SessionId, string ReminderId);
internal sealed record ReminderDetailResponse(string Status, string Epoch, string? SessionId, string? ReminderId,
    string? Content, int? DelaySeconds, int? RepeatCount, string? EditRevision);
internal sealed record ReminderCreateRequest(string ExpectedEpoch, string SessionId, string Content, int DelaySeconds, int RepeatCount);
internal sealed record ReminderSaveRequest(string ExpectedEpoch, string SessionId, string ReminderId, string EditRevision, string Content);
internal sealed record ReminderDeleteRequest(string ExpectedEpoch, string SessionId, string ReminderId, string Confirmation);
internal sealed record ReminderMutationResponse(string Status, string Epoch, string? SessionId, string? ReminderId);
internal sealed record ReminderListResponse(string Status, string Epoch, string? SessionId, IReadOnlyList<ReminderRow> Reminders, int ActiveCount, int CompletedCount);
internal sealed record ReminderRow(string Id, string State, string Preview, int DelaySeconds, int RepeatCount, int FiredCount,
    DateTimeOffset? DueAt, int? LastExitCode, string? LastError);
