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
                entry.ReasoningEffort?.ToString(), entry.AgentPromptId, entry.PendingAgentPromptId)
            { Activity = activity, BackgroundTasks = [.. entry.BackgroundTasks.Select(ProjectTask).OfType<SessionRuntimeBackgroundTaskResponse>().Take(MaximumBackgroundTasks)],
              RemoteControl = entry.RemoteControl.Status == CodeAlta.Agent.AgentRemoteControlStatus.Off ? null : SessionRemoteControlResponse.From(entry.RemoteControl) };
        }
        // Seven strings <=256 UTF-16 units at six-byte worst-case JSON escaping, fixed GUIDs,
        // ordinal/enums/keys plus 4 KiB framing fit below 32 KiB. No values are truncated.
        return new("ok", _hostEpoch, state.SessionId, state.RuntimeInstanceId.ToString("D"), state.CoordinatorTransitionInProgress, projected);
    }

    // A task is what a provider says of itself: one that does not fit the wire is left out, and what it says of
    // itself is cut, so that the rest of the state is still read. The tool call is named as the timeline names it.
    // The runtime combines up to sixteen provider tasks with eight host jobs, running tasks first.
    private const int MaximumBackgroundTasks = 24;
    private static SessionRuntimeBackgroundTaskResponse? ProjectTask(SessionRuntimeBackgroundTask task)
        => task.TaskId is { Length: <= 64 } && Identity(task.TaskId) && Identity(Cut(task.Kind, 32))
            && (task.ToolCallId is null || Identity(RuntimeDisplayProjection.CompactIdentifier(task.ToolCallId)))
            ? new(task.TaskId, Cut(task.Kind, 32)!, Cut(task.Description, 160), task.ToolCallId is null ? null : RuntimeDisplayProjection.CompactIdentifier(task.ToolCallId),
                task.StartedAt?.ToString("O", CultureInfo.InvariantCulture),
                task.Outcome switch { null => "running", CodeAlta.Agent.AgentBackgroundTaskOutcome.Failed => "failed", CodeAlta.Agent.AgentBackgroundTaskOutcome.Stopped => "stopped", _ => "completed" })
            { IsJob = task.IsJob, ExitCode = task.ExitCode, EndedAt = task.EndedAt?.ToString("O", CultureInfo.InvariantCulture) }
            : null;

    // Cuts a text at a character, never inside a surrogate pair, and makes one line of it.
    private static string? Cut(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var line = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= maximum) return Text(line) ? line : null;
        var end = char.IsHighSurrogate(line[maximum - 2]) ? maximum - 2 : maximum - 1;
        var cut = line[..end] + "…";
        return Text(cut) ? cut : null;
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

    /// <summary>What the provider does in the background outside its runs: the tasks that go on, then the last that failed or were stopped.</summary>
    public IReadOnlyList<SessionRuntimeBackgroundTaskResponse> BackgroundTasks { get; init; } = [];

    /// <summary>The Remote Control of the session (Claude Code's, from claude.ai); absent while it is off.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SessionRemoteControlResponse? RemoteControl { get; init; }
}

/// <summary>A background task of a session.</summary>
/// <param name="TaskId">Its identity, by which it is stopped.</param>
/// <param name="Kind"><c>command</c>, <c>agent</c>, <c>workflow</c>, or the name its provider gives it.</param>
/// <param name="Description">What it does, in one line; null when its provider does not say.</param>
/// <param name="ToolCallId">The tool call that started it, as the timeline names that call; null when unknown.</param>
/// <param name="StartedAt">When it was first known, as a round-trip timestamp; null for a task that ended.</param>
/// <param name="State"><c>running</c>, or how it ended: <c>failed</c>, <c>stopped</c>, or <c>completed</c> for a background job that succeeded.</param>
internal sealed record SessionRuntimeBackgroundTaskResponse(string TaskId, string Kind, string? Description, string? ToolCallId, string? StartedAt, string State)
{
    /// <summary>Whether it is a background job the host runs for the session: what it writes can be followed (<c>jobs.observe</c>).</summary>
    public bool IsJob { get; init; }

    /// <summary>The exit code of the command of a job that ended by itself.</summary>
    public int? ExitCode { get; init; }

    /// <summary>When a job ended, as a round-trip timestamp; null while it runs, and for a task of the provider.</summary>
    public string? EndedAt { get; init; }
}
internal sealed record SessionRuntimeActivityResponse(string? Timestamp, string Source, string AdmittedEvents, string OmittedEvents);
