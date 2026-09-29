using System.Globalization;
using System.Runtime.CompilerServices;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// Owned-mode renderer data only. No catalog lookup, provider work, commands or original event reader.
[NeoRpcService("display", Version = 1)]
internal sealed class SessionDisplayService(RuntimeDisplayProjection display, string hostEpoch)
{
    internal const int MaximumItemBytes = 256 * 1024;
    internal const int LabelCharacters = 256;

    [NeoRpcMethod("observe")]
    public NeoRpcChannel<SessionDisplayItem> Observe(SessionDisplayRequest request, CancellationToken cancellationToken)
        => new(EnumerateAsync(request, cancellationToken), DesktopJsonContext.Default.SessionDisplayItem);

    private async IAsyncEnumerable<SessionDisplayItem> EnumerateAsync(SessionDisplayRequest? request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Validate before even creating a runtime observation. Missing retained state is not an invalid identity.
        if (request is null || !ValidIdentity(request.SessionId))
        {
            yield return Error("invalid_request", null);
            yield break;
        }
        if (!string.Equals(request.ExpectedHostEpoch, hostEpoch, StringComparison.Ordinal))
        {
            yield return Error("stale_epoch", request.SessionId);
            yield break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await using var observation = display.ObserveAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        var first = true;
        while (true)
        {
            bool moved = false;
            string? error = null;
            try { moved = await observation.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (InvalidOperationException) when (first) { error = "capacity"; }
            catch (Exception) { error = "observation_failed"; }
            if (error is not null) { yield return Error(error, request.SessionId); yield break; }
            if (!moved) yield break;
            first = false;
            yield return Project(hostEpoch, request.SessionId, observation.Current);
        }
    }

    private SessionDisplayItem Error(string code, string? sessionId)
        => new(code, hostEpoch, sessionId, null, null, null, false, false, false, true, "0", "0", null);

    internal static SessionDisplayItem Project(string epoch, string selectedSessionId, RuntimeDisplayReplacement replacement)
    {
        var snapshot = replacement.Snapshot;
        SessionDisplayView? selected = null;
        foreach (var session in snapshot.Sessions)
        {
            if (!string.Equals(session.SessionId, selectedSessionId, StringComparison.OrdinalIgnoreCase)) continue;
            var shortened = false;
            string? Label(string? value)
            {
                if (value is null || value.Length <= LabelCharacters) return value;
                shortened = true;
                var count = LabelCharacters;
                if (char.IsHighSurrogate(value[count - 1]) && char.IsLowSurrogate(value[count])) count--;
                return value[..count];
            }
            var lifecycle = session.Lifecycle is { } life
                ? new SessionDisplayLifecycle(life.Kind.ToString(), Label(life.RunId), Label(life.Message)) : null;
            var config = session.Configuration is { } configuration
                ? new SessionDisplayConfiguration(Label(configuration.ProviderId), Label(configuration.ProviderKey), Label(configuration.ModelId),
                    configuration.ReasoningEffort?.ToString(), Label(configuration.AgentPromptId)) : null;
            var message = Label(session.StatusMessage);
            selected = new(session.SessionId, Decimal(session.Revision), lifecycle, session.QueuedPromptCount, config,
                session.StatusKind?.ToString(), message,
                session.Text.Select(text => new SessionDisplayText(text.RunId, text.ContentId, text.Kind.ToString(),
                    text.Text, text.IsComplete, text.IsTruncated, text.StartedWithDelta) { Timestamp = text.Timestamp, Sequence = Decimal(text.Sequence) }).ToArray(),
                session.MetadataTruncated, shortened, Decimal(session.EvictedTextItems), Decimal(session.UnsupportedEvents),
                session.ToolActivities.Select(activity => new SessionDisplayToolActivity(activity.ProviderId, activity.RunId,
                    activity.ActivityId, activity.Phase.ToString(), activity.Name, activity.IsNameTruncated) { Timestamp = activity.Timestamp, Sequence = Decimal(activity.Sequence) }).ToArray(),
                Decimal(session.EvictedToolActivities));
            break;
        }
        // One selected session: 8*(4096+2*256) text/key + 8*256 label + 2*256 session identity
        // + 2*(3*256+128) tool identity/name = 41,216 UTF-16 units, at most 247,296 escaped bytes.
        // JSON overhead (NOT framing): <=103 properties * (32 ASCII key bytes + 4 syntax bytes),
        // <=57 non-payload scalars * 64 bytes (canonical epochs, fixed codes/enums, Int32/Int64, bool/null),
        // plus 256 container/separator/nullable-string quote bytes = 7,612, rounded up to 8 KiB.
        // With a SEPARATE 4 KiB bridge framing allowance: 247,296 + 8,192 + 4,096 = 259,584 < 262,144.
        // Ten row timestamps/sequences add at most 1,280 bytes: 260,864 < 262,144.
        // Actual generated JsonTypeInfo serialization remains covered by the worst-escaping budget assertion.
        return new("ok", epoch, selectedSessionId, snapshot.Epoch.ToString("D"), Decimal(snapshot.Revision),
            replacement.PreviousRevision is { } previous ? Decimal(previous) : null, replacement.IsInitial,
            replacement.HasGap, snapshot.IsClosed, snapshot.IsPartial, Decimal(snapshot.EvictedSessions),
            Decimal(snapshot.OmittedSessionEvents), selected);
    }

    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool ValidIdentity(string? value)
    {
        if (value is null || value.Length is 0 or > RuntimeDisplayProjection.MaxIdentifierCharacters || value != value.Trim()) return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}

internal sealed record SessionDisplayRequest(string ExpectedHostEpoch, string SessionId);
// Revision/counter values are decimal strings: generated JS must not order Int64 values as Number.
internal sealed record SessionDisplayItem(string Status, string HostEpoch, string? SessionId, string? ProjectionEpoch,
    string? Revision, string? PreviousRevision, bool IsInitial, bool HasGap, bool IsClosed, bool IsPartial,
    string EvictedSessions, string OmittedSessionEvents, SessionDisplayView? Session);
internal sealed record SessionDisplayView(string SessionId, string Revision, SessionDisplayLifecycle? Lifecycle,
    int? QueuedPromptCount, SessionDisplayConfiguration? Configuration, string? StatusKind, string? StatusMessage,
    SessionDisplayText[] Text, bool MetadataTruncated, bool TransportTruncated, string EvictedTextItems, string UnsupportedEvents,
    SessionDisplayToolActivity[] ToolActivities, string EvictedToolActivities);
internal sealed record SessionDisplayLifecycle(string Kind, string? RunId, string? Message);
internal sealed record SessionDisplayConfiguration(string? ProviderId, string? ProviderKey, string? ModelId,
    string? ReasoningEffort, string? AgentPromptId);
internal sealed record SessionDisplayText(string? RunId, string ContentId, string Kind, string Text,
    bool IsComplete, bool IsTruncated, bool StartedWithDelta)
{
    public DateTimeOffset? Timestamp { get; init; }
    public string? Sequence { get; init; }
}
internal sealed record SessionDisplayToolActivity(string ProviderId, string? RunId, string ActivityId, string Phase,
    string? Name, bool IsNameTruncated)
{
    public DateTimeOffset? Timestamp { get; init; }
    public string? Sequence { get; init; }
}
