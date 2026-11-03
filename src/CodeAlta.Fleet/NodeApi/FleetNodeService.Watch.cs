using System.Diagnostics;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.NodeApi;

public sealed partial class FleetNodeService
{
    /// <summary>The shortest time between two display events of one watch: replacements in between are coalesced.</summary>
    internal static readonly TimeSpan MinimumDisplayInterval = TimeSpan.FromMilliseconds(100);

    private async Task<FleetWatchResult> WatchAsync(FleetNodeCall call, FleetWatchParams parameters, CancellationToken cancellationToken)
    {
        if (parameters.WatchId is { } named && (named.Length is 0 or > 64 || !named.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "A watch id has 1 to 64 letters, digits, '-' or '_'.");
        var session = await RequireViewedSessionAsync(parameters.SessionId, cancellationToken).ConfigureAwait(false);
        var watch = new FleetNodeWatch(parameters.WatchId ?? Guid.NewGuid().ToString("N"), session.SessionId, call.Caller.Connection);
        if (!call.Caller.TryAddWatch(watch))
            throw new FleetException(FleetErrorCodes.ServerBusy, null, $"A channel watches at most {FleetNodeCaller.MaxWatches} sessions at a time, each with its own id.");
        watch.Start(_host.RuntimeService.Display, () => call.Caller.RemoveWatch(watch.WatchId));
        return new FleetWatchResult(watch.WatchId);
    }

    private async Task<FleetEmpty> UnwatchAsync(FleetNodeCall call, FleetUnwatchParams parameters, CancellationToken cancellationToken)
    {
        if (call.Caller.RemoveWatch(parameters.WatchId ?? "") is { } watch) await watch.StopAsync().ConfigureAwait(false);
        return new FleetEmpty();
    }
}

/// <summary>
/// The live display of one session for one channel: full replacements of <see cref="RuntimeDisplayProjection"/> sent as
/// <c>session.display</c> events, at most one per <see cref="FleetNodeService.MinimumDisplayInterval"/>. The projection
/// gives a slow observer the latest replacement only, and the send queue of the connection is bounded: a slow reader
/// costs updates, never memory.
/// </summary>
internal sealed class FleetNodeWatch(string watchId, string sessionId, FleetConnection connection)
{
    private readonly CancellationTokenSource _stop = new();
    private Task _work = Task.CompletedTask;

    public string WatchId { get; } = watchId;

    public string SessionId { get; } = sessionId;

    public void Start(RuntimeDisplayProjection display, Action ended) => _work = Task.Run(() => RunAsync(display, ended));

    public async Task StopAsync()
    {
        if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
        try { await _work.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(RuntimeDisplayProjection display, Action ended)
    {
        var token = _stop.Token;
        var reason = "closed";
        try
        {
            long? sentRevision = null, sentSession = null;
            long sentAt = 0;
            await foreach (var replacement in display.ObserveSessionAsync(SessionId, token).ConfigureAwait(false))
            {
                var snapshot = replacement.Snapshot;
                var session = Select(snapshot);
                // A replacement for the other sessions of the projection holds nothing new for this one.
                if (sentRevision is not null && !snapshot.IsClosed && session?.Revision == sentSession) continue;
                var wait = FleetNodeService.MinimumDisplayInterval - Stopwatch.GetElapsedTime(sentAt);
                if (sentRevision is not null && wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                var payload = Project(snapshot, session, sentRevision);
                await connection.SendEventAsync(new FleetEvent(FleetNodeMethods.SessionDisplayEvent,
                    JsonSerializer.SerializeToElement(payload, FleetNodeJsonContext.Default.FleetSessionDisplay)), token).ConfigureAwait(false);
                (sentRevision, sentSession, sentAt) = (snapshot.Revision, session?.Revision, Stopwatch.GetTimestamp());
                if (snapshot.IsClosed) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            reason = "unwatched";
        }
        catch (Exception exception) when (exception is FleetException or InvalidOperationException or IOException or ObjectDisposedException)
        {
            reason = "failed";
        }
        finally
        {
            ended();
        }

        if (reason == "unwatched" || connection.State != FleetConnectionState.Open) return;
        try
        {
            await connection.SendEventAsync(new FleetEvent(FleetNodeMethods.SessionWatchEndedEvent,
                JsonSerializer.SerializeToElement(new FleetWatchEnded(WatchId, reason), FleetNodeJsonContext.Default.FleetWatchEnded))).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetException or InvalidOperationException or IOException or ObjectDisposedException)
        {
            // The channel closed meanwhile: its reader learns it from the channel.
        }
    }

    private RuntimeDisplaySession? Select(RuntimeDisplaySnapshot snapshot)
    {
        foreach (var session in snapshot.Sessions)
            if (string.Equals(session.SessionId, SessionId, StringComparison.OrdinalIgnoreCase)) return session;
        return null;
    }

    private FleetSessionDisplay Project(RuntimeDisplaySnapshot snapshot, RuntimeDisplaySession? session, long? previous)
        => new(WatchId, SessionId, snapshot.Revision, previous, previous is { } sent && snapshot.Revision > sent + 1, snapshot.IsClosed,
            session?.Lifecycle is { } life ? new FleetDisplayLifecycle(life.Kind.ToString(), life.RunId, life.Message) : null,
            session?.QueuedPromptCount, session?.StatusKind?.ToString(), session?.StatusMessage,
            session is { } value ? [.. value.Text.Select(static text => new FleetDisplayText(text.RunId, text.ContentId, text.Kind.ToString(), text.Text, text.IsComplete, text.IsTruncated) { ClientRequestId = text.ClientRequestId })] : [],
            session is { } tools ? [.. tools.ToolActivities.Select(static tool => new FleetDisplayTool(tool.ActivityId, tool.RunId, tool.Phase.ToString(), tool.Name))] : []);
}
