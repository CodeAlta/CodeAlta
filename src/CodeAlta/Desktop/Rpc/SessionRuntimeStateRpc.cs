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

    internal SessionRuntimeStateService(SessionRuntimeService runtime, string hostEpoch)
        : this(runtime.GetCurrentStateAsync, hostEpoch) { }

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
            if (state is null || state.RuntimeInstanceId == Guid.Empty || state.SessionId != request.SessionId || !Identity(state.SessionId))
                return Error("wire_limit", request.SessionId);
            SessionRuntimeStateEntry? projected = null;
            if (state.Entry is { } entry)
            {
                if (entry.AttachmentGeneration <= 0 || !Identity(entry.ProviderId) || !Identity(entry.ProviderKey) ||
                    (entry.ActiveRunId is not null && !Identity(entry.ActiveRunId)) || !Text(entry.ModelId) ||
                    !Text(entry.AgentPromptId) || !Text(entry.PendingAgentPromptId) ||
                    (entry.ReasoningEffort is { } effort && !Enum.IsDefined(effort)))
                    return Error("wire_limit", request.SessionId);
                projected = new(entry.AttachmentGeneration.ToString(CultureInfo.InvariantCulture), entry.IsTerminated, entry.IsRetiring,
                    entry.ActiveRunId, entry.QueueDrainInProgress, entry.ProviderId, entry.ProviderKey, entry.ModelId,
                    entry.ReasoningEffort?.ToString(), entry.AgentPromptId, entry.PendingAgentPromptId);
            }
            // Seven strings <=256 UTF-16 units at six-byte worst-case JSON escaping, fixed GUIDs,
            // ordinal/enums/keys plus 4 KiB framing fit below 32 KiB. No values are truncated.
            return new("ok", _hostEpoch, state.SessionId, state.RuntimeInstanceId.ToString("D"), state.CoordinatorTransitionInProgress, projected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { return Error("closed", request.SessionId); }
        catch (Exception) { return Error("read_failed", request.SessionId); }
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
internal sealed record SessionRuntimeStateResponse(string Status, string HostEpoch, string? SessionId, string? RuntimeInstanceId,
    bool? CoordinatorTransitionInProgress, SessionRuntimeStateEntry? Entry);
internal sealed record SessionRuntimeStateEntry(string AttachmentGeneration, bool IsTerminated, bool IsRetiring,
    string? ActiveRunId, bool QueueDrainInProgress, string ProviderId, string ProviderKey, string? ModelId,
    string? ReasoningEffort, string? AgentPromptId, string? PendingAgentPromptId);
