using System.Globalization;
using System.Text.Json;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// Owned-only, unary, read-only. All durable scope checks occur in the runtime owner before projection.
[NeoRpcService("sessionUsage", Version = 1)]
internal sealed class SessionUsageService
{
    internal const int MaximumResponseBytes = 32 * 1024;
    private readonly Func<string, string, string?, string?, CancellationToken, Task<OwnedUsageReadResult>> _read;
    private readonly string _epoch;

    internal SessionUsageService(SessionRuntimeService runtime, string epoch)
        : this(runtime.ReadOwnedUsageAsync, epoch) { }

    internal SessionUsageService(Func<string, string, string?, string?, CancellationToken, Task<OwnedUsageReadResult>> read, string epoch)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!Guid.TryParseExact(epoch, "D", out var parsed) || parsed == Guid.Empty || parsed.ToString("D") != epoch)
            throw new ArgumentException("Canonical host epoch required.", nameof(epoch));
        _read = read;
        _epoch = epoch;
    }

    [NeoRpcMethod("read")]
    public async Task<SessionUsageResponse> ReadAsync(SessionUsageRequest request, CancellationToken cancellationToken)
    {
        var id = Identity(request?.SessionId) ? request!.SessionId : null;
        if (id is null || request!.Scope is not ("global" or "project") || request.Scope == "global" &&
            (request.ProjectId is not null || request.ExpectedProjectPath is not null) || request.Scope == "project" &&
            (!ProjectId(request.ProjectId) || !NormalizedPath(request.ExpectedProjectPath))) return Error("invalid_request", id);
        if (request.ExpectedHostEpoch != _epoch) return Error("stale_epoch", id);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var state = await _read(id, request.Scope, request.ProjectId, request.ExpectedProjectPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (state is null || state.SessionId != id || state.RuntimeInstanceId == Guid.Empty)
                return Error("read_failed", id);
            if (state.Status is not ("ok" or "no_observation"))
                return AllowedFailure(state.Status) ? Error(state.Status, id) : Error("read_failed", id);
            if (state.AttachmentGeneration is not > 0 || state.OmittedUsageEvents < 0
                || state.Status == "ok" && state.Usage is null || state.Status == "no_observation" && state.Usage is not null)
                return Error("read_failed", id);
            var usage = state.Usage;
            if (usage is not null && (usage.Sequence <= 0 || !Enum.IsDefined(usage.Scope) || !Enum.IsDefined(usage.Source)
                || usage.Window is { } window && (window.CurrentTokens is < 0 || window.TokenLimit is <= 0 || window.MessageCount is < 0)
                || usage.LastOperation is { } operation && (operation.InputTokens is < 0 || operation.OutputTokens is < 0
                    || operation.CacheReadTokens is < 0 || operation.CacheWriteTokens is < 0 || operation.CachedInputTokens is < 0
                    || operation.ReasoningTokens is < 0 || operation.Cost is { } cost && (!double.IsFinite(cost) || cost < 0)
                    || operation.DurationMs is { } duration && (!double.IsFinite(duration) || duration < 0))
                || usage.Window is { } envelope && (envelope.TotalContextEnvelope is <= 0 || envelope.MaxOutputTokens is <= 0 || !Label(envelope.Label))
                || usage.LastOperation is { } labels && !(Label(labels.Model) && Label(labels.ReasoningEffort) && Label(labels.Initiator) && Label(labels.Label) && Label(labels.CostUnit))
                || usage.RateLimits is { } rates && !(Label(rates.Name) && Label(rates.PlanType) && ValidRate(rates.Primary) && ValidRate(rates.Secondary))
                || usage.SessionTotal is { } totals && (totals.TotalTokens < 0 || totals.InputTokens < 0 || totals.OutputTokens < 0
                    || totals.CachedInputTokens < 0 || totals.ReasoningTokens < 0)))
                return Error("read_failed", id);
            SessionUsageObservation? observation = usage is null ? null : new(
                Decimal(usage.Sequence), usage.SourceUpdatedAt?.ToString("O", CultureInfo.InvariantCulture),
                usage.EventTimestamp?.ToString("O", CultureInfo.InvariantCulture), usage.Scope.ToString(), usage.Source.ToString(),
                usage.Window is { } w ? new SessionUsageWindow(NullableDecimal(w.CurrentTokens), NullableDecimal(w.TokenLimit), w.MessageCount,
                    w.Label, NullableDecimal(w.TotalContextEnvelope), NullableDecimal(w.MaxOutputTokens)) : null,
                usage.LastOperation is { } o ? new SessionUsageOperation(NullableDecimal(o.InputTokens), NullableDecimal(o.OutputTokens),
                    NullableDecimal(o.CacheReadTokens), NullableDecimal(o.CacheWriteTokens), NullableDecimal(o.CachedInputTokens),
                    NullableDecimal(o.ReasoningTokens), o.Cost?.ToString("R", CultureInfo.InvariantCulture),
                    o.DurationMs?.ToString("R", CultureInfo.InvariantCulture), o.Model, o.ReasoningEffort, o.Initiator, o.Label, o.CostUnit) : null,
                usage.HadInvalidValues, usage.HadOmittedData,
                usage.RateLimits is { } limits ? new SessionUsageRateLimits(limits.Name, limits.PlanType, RateWindow(limits.Primary), RateWindow(limits.Secondary)) : null,
                usage.SessionTotal is { } total ? new SessionUsageTotals(Decimal(total.TotalTokens), Decimal(total.InputTokens),
                    Decimal(total.OutputTokens), Decimal(total.CachedInputTokens), Decimal(total.ReasoningTokens)) : null);
            var response = new SessionUsageResponse(state.Status, _epoch, id, state.RuntimeInstanceId.ToString("D"),
                Decimal(state.AttachmentGeneration.Value), Decimal(state.OmittedUsageEvents), observation);
            return JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.SessionUsageResponse).Length <= MaximumResponseBytes
                ? response : Error("wire_limit", id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) { return Error("closed", id); }
        catch (Exception) { return Error("read_failed", id); }
    }

    private SessionUsageResponse Error(string status, string? id) => new(status, _epoch, id, null, null, null, null);
    private static bool AllowedFailure(string status) => status is "invalid_request" or "missing_session" or "scope_mismatch"
        or "transition" or "stale_attachment" or "metadata_missing" or "metadata_incomplete" or "metadata_invalid"
        or "metadata_mismatch" or "missing_project" or "ambiguous_project" or "incomplete_project"
        or "invalid_project" or "archived_project" or "read_failed";
    private static SessionUsageRateWindow? RateWindow(SessionRuntimeUsageRateWindow? window) => window is { } value
        ? new(value.UsedPercent, value.ResetsAt?.ToString("O", CultureInfo.InvariantCulture), NullableDecimal(value.WindowDurationMinutes)) : null;
    private static bool ValidRate(SessionRuntimeUsageRateWindow? window) => window is not { } value
        || value.UsedPercent is null or (>= 0 and <= 100) && value.WindowDurationMinutes is null or >= 0;
    // Provider text is bounded by the runtime owner; refuse anything else instead of forwarding it.
    private static bool Label(string? value) => value is null
        || value.Length is > 0 and <= SessionRuntimeUsageObservation.MaximumTextLength && Text(value) && !value.Any(char.IsControl);
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string? NullableDecimal(long? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static bool ProjectId(string? value) => value is { Length: 36 } && Guid.TryParseExact(value, "D", out var id)
        && id != Guid.Empty && id.ToString("D") == value;
    private static bool NormalizedPath(string? value)
    {
        if (value is not { Length: > 0 and <= 4096 } || !Path.IsPathFullyQualified(value) || !Text(value)) return false;
        try
        {
            var full = Path.GetFullPath(value);
            if (full != Path.GetPathRoot(full)) full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, value, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static bool Identity(string? value) => value is { Length: > 0 and <= 256 } && value == value.Trim()
        && value is not ("." or "..") && Text(value) && !value.Any(static c => char.IsControl(c)
            || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|');
    private static bool Text(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
}

internal sealed record SessionUsageRequest(string ExpectedHostEpoch, string SessionId, string Scope,
    string? ProjectId, string? ExpectedProjectPath);
internal sealed record SessionUsageResponse(string Status, string HostEpoch, string? SessionId, string? RuntimeInstanceId,
    string? AttachmentGeneration, string? OmittedUsageEvents, SessionUsageObservation? Observation);
internal sealed record SessionUsageObservation(string Sequence, string? SourceUpdatedAt, string? EventTimestamp,
    string Scope, string Source, SessionUsageWindow? Window, SessionUsageOperation? LastOperation,
    bool HadInvalidValues, bool HadOmittedData, SessionUsageRateLimits? RateLimits = null, SessionUsageTotals? SessionTotal = null);
internal sealed record SessionUsageWindow(string? CurrentTokens, string? TokenLimit, int? MessageCount,
    string? Label = null, string? TotalContextEnvelope = null, string? MaxOutputTokens = null);
internal sealed record SessionUsageOperation(string? InputTokens, string? OutputTokens, string? CacheReadTokens,
    string? CacheWriteTokens, string? CachedInputTokens, string? ReasoningTokens, string? Cost, string? DurationMs,
    string? Model = null, string? ReasoningEffort = null, string? Initiator = null, string? Label = null, string? CostUnit = null);
internal sealed record SessionUsageRateLimits(string? Name, string? PlanType, SessionUsageRateWindow? Primary, SessionUsageRateWindow? Secondary);
internal sealed record SessionUsageRateWindow(int? UsedPercent, string? ResetsAt, string? WindowDurationMinutes);
internal sealed record SessionUsageTotals(string TotalTokens, string InputTokens, string OutputTokens, string CachedInputTokens, string ReasoningTokens);
