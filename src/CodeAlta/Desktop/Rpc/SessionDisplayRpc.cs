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
                    text.Text, text.IsComplete, text.IsTruncated, text.StartedWithDelta)).ToArray(),
                session.MetadataTruncated, shortened, Decimal(session.EvictedTextItems), Decimal(session.UnsupportedEvents));
            break;
        }
        // Only one session crosses the bridge. Bounds inherited from the runtime: 8*(4096+2*256)
        // text/key units, 8*256 label units, 2*256 session identifiers, plus fixed codes/counters/epochs.
        // Six-byte worst-case JSON escaping plus 16 KiB property/envelope allowance is <256 KiB.
        // The serialization-budget test uses the actual generated JsonTypeInfo, not an unescaped text estimate.
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
    SessionDisplayText[] Text, bool MetadataTruncated, bool TransportTruncated, string EvictedTextItems, string UnsupportedEvents);
internal sealed record SessionDisplayLifecycle(string Kind, string? RunId, string? Message);
internal sealed record SessionDisplayConfiguration(string? ProviderId, string? ProviderKey, string? ModelId,
    string? ReasoningEffort, string? AgentPromptId);
internal sealed record SessionDisplayText(string? RunId, string ContentId, string Kind, string Text,
    bool IsComplete, bool IsTruncated, bool StartedWithDelta);
