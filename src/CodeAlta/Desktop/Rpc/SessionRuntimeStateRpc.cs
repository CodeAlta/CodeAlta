using System.Globalization;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// Owned-only, unary observation. No discovery, command dispatch, original-event reader or Display dependency.
[NeoRpcService("runtimeState", Version = 1)]
internal sealed class SessionRuntimeStateService
{
    internal const int MaximumResponseBytes = 32 * 1024;
    private readonly Func<string, CancellationToken, Task<SessionRuntimeCurrentState>> _read;
    private readonly string _hostEpoch;
    private readonly Func<string, DateTimeOffset, string, string?, string?, CancellationToken, Task<OwnedRuntimeObservation>>? _observe;

    internal SessionRuntimeStateService(SessionRuntimeService runtime, string hostEpoch)
        : this(runtime.GetCurrentStateAsync, hostEpoch) { _observe = runtime.ObserveOwnedStateAsync; }

    internal SessionRuntimeStateService(Func<string, DateTimeOffset, string, string?, string?, CancellationToken, Task<OwnedRuntimeObservation>> observe, string hostEpoch)
        : this((_, _) => throw new InvalidOperationException("Only scoped observation is configured."), hostEpoch) { _observe = observe; }

    [NeoRpcMethod("observe")]
    public async Task<SessionRuntimeScopedResponse> ObserveAsync(SessionRuntimeScopedRequest request, CancellationToken cancellationToken)
    {
        SessionRuntimeScopedResponse Reply(string status, SessionRuntimeStateResponse? state = null) => new(status, _hostEpoch,
            request?.SessionId, request?.Scope, request?.ProjectId, request?.ProjectPath, state);
        if (request is null || !Identity(request.SessionId) || request.SessionId.Any(ch => char.IsControl(ch) || "/\\:*?\"<>|".Contains(ch))
            || request.CreatedAt is not { Length: > 0 and <= 64 } || !DateTimeOffset.TryParse(request.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt)
            || request.Scope is not ("global" or "project") || request.Scope == "global" && (request.ProjectId is not null || request.ProjectPath is not null)
            || request.Scope == "project" && (!Guid.TryParseExact(request.ProjectId, "D", out var projectId) || projectId == Guid.Empty
                || request.ProjectPath is not { Length: > 0 and <= 4096 } || !Path.IsPathFullyQualified(request.ProjectPath)))
            return new("invalid_request", _hostEpoch, null, null, null, null, null);
        if (request.ExpectedHostEpoch != _hostEpoch) return Reply("stale_epoch");
        if (_observe is null) return Reply("unconfigured");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _observe(request.SessionId, createdAt, request.Scope, request.ProjectId, request.ProjectPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Status != "ok" || result.State is null) return Reply(result.Status);
            // Reuse the unary wire validator/projection without another actor query.
            var projection = ProjectState(result.State, request.SessionId);
            var response = Reply(projection.Status, projection.Status == "ok" ? projection : null);
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.SessionRuntimeScopedResponse).Length <= 64 * 1024
                ? response : Reply("wire_limit");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { return Reply("closed"); }
        catch (Exception) { return Reply("read_failed"); }
    }

    // Literal transport-test seam; runtime transition tests use the actual isolated runtime.
    internal SessionRuntimeStateService(Func<string, CancellationToken, Task<SessionRuntimeCurrentState>> read, string hostEpoch)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!Guid.TryParseExact(hostEpoch, "D", out var epoch) || epoch == Guid.Empty || epoch.ToString("D") != hostEpoch)
            throw new ArgumentException("A canonical host epoch is required.", nameof(hostEpoch));
        _read = read;
        _hostEpoch = hostEpoch;
    }

    [NeoRpcMethod("current")]
    public async Task<SessionRuntimeStateResponse> CurrentAsync(SessionRuntimeStateRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !Identity(request.SessionId)) return Error("invalid_request", null);
        if (!string.Equals(request.ExpectedHostEpoch, _hostEpoch, StringComparison.Ordinal)) return Error("stale_epoch", request.SessionId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var state = await _read(request.SessionId, cancellationToken).ConfigureAwait(false);
            return ProjectState(state, request.SessionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { return Error("closed", request.SessionId); }
        catch (Exception) { return Error("read_failed", request.SessionId); }
    }

    private SessionRuntimeStateResponse ProjectState(SessionRuntimeCurrentState state, string sessionId)
    {
        if (state is null || state.RuntimeInstanceId == Guid.Empty || state.SessionId != sessionId || !Identity(state.SessionId))
            return Error("wire_limit", sessionId);
        SessionRuntimeStateEntry? projected = null;
        if (state.Entry is { } entry)
        {
            if (entry.AttachmentGeneration <= 0 || !Identity(entry.ProviderId) || !Identity(entry.ProviderKey) ||
                (entry.ActiveRunId is not null && !Identity(entry.ActiveRunId)) || !Text(entry.ModelId) ||
                !Text(entry.AgentPromptId) || !Text(entry.PendingAgentPromptId) ||
                (entry.ReasoningEffort is { } effort && !Enum.IsDefined(effort)))
                return Error("wire_limit", sessionId);
            SessionRuntimeActivityResponse? activity = null;
            if (entry.Activity is { } observed)
            {
                if (observed.AdmittedEvents < 0 || observed.OmittedEvents < 0 || observed.Timestamp?.Year <= 1
                    || (observed.AdmittedEvents == 0) != (observed.Timestamp is null)) return Error("wire_limit", sessionId);
                activity = new(observed.Timestamp?.ToString("O", CultureInfo.InvariantCulture), "admitted_agent_event",
                    observed.AdmittedEvents.ToString(CultureInfo.InvariantCulture), observed.OmittedEvents.ToString(CultureInfo.InvariantCulture));
            }
            projected = new(entry.AttachmentGeneration.ToString(CultureInfo.InvariantCulture), entry.IsTerminated, entry.IsRetiring,
                entry.ActiveRunId, entry.QueueDrainInProgress, entry.ProviderId, entry.ProviderKey, entry.ModelId,
                entry.ReasoningEffort?.ToString(), entry.AgentPromptId, entry.PendingAgentPromptId) { Activity = activity };
        }
        // Seven strings <=256 UTF-16 units at six-byte worst-case JSON escaping, fixed GUIDs,
        // ordinal/enums/keys plus 4 KiB framing fit below 32 KiB. No values are truncated.
        return new("ok", _hostEpoch, state.SessionId, state.RuntimeInstanceId.ToString("D"), state.CoordinatorTransitionInProgress, projected);
    }

    private SessionRuntimeStateResponse Error(string status, string? sessionId) => new(status, _hostEpoch, sessionId, null, null, null);
    private static bool Identity(string? value) => value is { Length: > 0 and <= 256 }
        && !string.IsNullOrWhiteSpace(value) && value == value.Trim() && Text(value);
    private static bool Text(string? value)
    {
        if (value is null) return true;
        if (value.Length > 256) return false;
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
}

internal sealed record SessionRuntimeStateRequest(string ExpectedHostEpoch, string SessionId);
internal sealed record SessionRuntimeScopedRequest(string ExpectedHostEpoch, string SessionId, string CreatedAt, string Scope, string? ProjectId, string? ProjectPath);
internal sealed record SessionRuntimeScopedResponse(string Status, string HostEpoch, string? SessionId, string? Scope, string? ProjectId,
    string? ProjectPath, SessionRuntimeStateResponse? Observation);
internal sealed record SessionRuntimeStateResponse(string Status, string HostEpoch, string? SessionId, string? RuntimeInstanceId,
    bool? CoordinatorTransitionInProgress, SessionRuntimeStateEntry? Entry);
internal sealed record SessionRuntimeStateEntry(string AttachmentGeneration, bool IsTerminated, bool IsRetiring,
    string? ActiveRunId, bool QueueDrainInProgress, string ProviderId, string ProviderKey, string? ModelId,
    string? ReasoningEffort, string? AgentPromptId, string? PendingAgentPromptId)
{
    public SessionRuntimeActivityResponse? Activity { get; init; }
}
internal sealed record SessionRuntimeActivityResponse(string? Timestamp, string Source, string AdmittedEvents, string OmittedEvents);
