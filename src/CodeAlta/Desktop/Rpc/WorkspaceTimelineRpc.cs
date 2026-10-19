using System.Globalization;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    private readonly Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? _readTimeline;
    private readonly Func<AgentHistoryRevision, long, long, long, CancellationToken, Task<AgentHistorySourceChunk>>? _readHistorySource;

    private readonly Func<string, string?, CancellationToken, Task<OwnedTimelineTail>>? _readTimelineTail;

    [NeoRpcMethod("historyTimeline")]
    public Task<TimelineHistoryResponse> HistoryTimelineAsync(HistoryRequest request, CancellationToken token)
        => request?.Cursor is null && _readTimelineTail is not null
            ? ReadTimelineTailAsync(request!, _readTimelineTail, token)
            : ReadTimelineAsync(request!, _readTimeline, token);

    // The newest page, read only when the journal no longer has the stamp the request knows. An unchanged journal is
    // answered `unchanged`, with no rows: the page keeps the ones it has. Any other answer is the one of a whole read.
    internal static async Task<TimelineHistoryResponse> ReadTimelineTailAsync(HistoryRequest request,
        Func<string, string?, CancellationToken, Task<OwnedTimelineTail>> read, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A stamp longer than any this host returns is not one of them.
        var known = request.Known is { Length: <= MaximumStampLength } value ? value : null;
        OwnedTimelineTail? tail = null;
        var response = await ReadTimelineAsync(request, async (id, cursor, cancellation) =>
        {
            tail = await read(id, known, cancellation).ConfigureAwait(false);
            return tail.Page ?? new([], null, false);
        }, token).ConfigureAwait(false);
        if (response.Page.Status != "ok" || tail is null) return response;
        if (tail.Unchanged) return new(new("unchanged", [], null, false), null, []) { Stamp = tail.Stamp };
        return tail.Stamp is { Length: <= MaximumStampLength } stamp ? response with { Stamp = stamp } : response;
    }

    // Stamps name the host, the session (up to 256 units) and three numbers.
    private const int MaximumStampLength = 512;

    internal static async Task<TimelineHistoryResponse> ReadTimelineAsync(HistoryRequest request,
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? read, CancellationToken token)
    {
        AgentSessionHistoryPage? actual = null;
        var page = await ReadHistoryCoreAsync("historyTimeline", request, read is null ? null : async (id, cursor, cancellation) =>
        {
            actual = await read(id, cursor, cancellation).ConfigureAwait(false);
            return actual;
        }, 2, token, 16 * 1024).ConfigureAwait(false);
        if (page.Status == "record_too_large") page = page with { Status = "timeline_record_too_large" };
        if (page.Status != "ok" || actual?.Revision is not { } revision) return new(page, null, []);
        // Reserve 16 KiB inside the unchanged 700 KiB response budget for at most 100
        // decimal source ranges plus escaped selected identity and revision/property overhead.
        if (revision.SessionId != request.SessionId || revision.Length < 0 || revision.LastWriteUtcTicks < 0
            || revision.LastWriteUtcTicks > DateTime.MaxValue.Ticks || actual.Entries.Any(entry => entry.SourceEnd is not { } end
                || entry.Offset < 0 || end <= entry.Offset || end > revision.Length || end - entry.Offset > 8 * 1024 * 1024))
            return new(Failure("invalid_cursor"), null, []);
        var ranges = actual.Entries.Where(entry => entry.SourceEnd is not null)
            .Select(entry => new HistorySourceRange(Number(entry.Offset), Number(entry.SourceEnd!.Value))).ToArray();
        return new(page, new(revision.SessionId, Number(revision.Length), Number(revision.LastWriteUtcTicks)), ranges);
    }

    [NeoRpcMethod("historySource")]
    public Task<HistorySourceResponse> HistorySourceAsync(HistorySourceRequest request, CancellationToken token)
        => ReadSourceAsync(request, _readHistorySource, token);

    internal static async Task<HistorySourceResponse> ReadSourceAsync(HistorySourceRequest request,
        Func<AgentHistoryRevision, long, long, long, CancellationToken, Task<AgentHistorySourceChunk>>? read, CancellationToken token)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Revision);
            token.ThrowIfCancellationRequested();
            ValidateIdentity(request.Revision.SessionId, 256, required: true);
            var revision = new AgentHistoryRevision(request.Revision.SessionId, Decimal(request.Revision.Length), Decimal(request.Revision.LastWriteUtcTicks));
            var start = Decimal(request.Start); var end = Decimal(request.End); var offset = Decimal(request.Offset);
            if (revision.Length <= 0 || revision.LastWriteUtcTicks > DateTime.MaxValue.Ticks || end <= start || end > revision.Length
                || end - start > 8 * 1024 * 1024 || offset < start || offset >= end) return new("invalid_cursor", null, null);
            if (read is null) return new("unconfigured", null, null);
            var chunk = await read(revision, start, end, offset, token).ConfigureAwait(false);
            ValidateUnicode(chunk.Text);
            // 16 Ki UTF-16 units * six-byte worst-case escaping + bounded envelope < 104 KiB.
            if (chunk.Text.Length > 16 * 1024 || chunk.NextOffset is { } next && (next <= offset || next >= end))
                return new("wire_limit", null, null);
            return new("ok", chunk.Text, chunk.NextOffset is { } position ? Number(position) : null);
        }
        catch (OperationCanceledException) { throw; }
        catch (OwnedWorkspaceBusyException) { return new("busy", null, null); }
        catch (AgentSessionHistoryException error)
        {
            return SourceFailure(request, error.Code is "history_changed" or "invalid_cursor" or "unsupported_format" or "missing_session" or "outside_root"
                ? error.Code : "read_failed", error);
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidDataException or ArgumentException)
        { return SourceFailure(request, "invalid_cursor", error); }
        catch (Exception error) { return SourceFailure(request, "read_failed", error); }
    }

    private static HistorySourceResponse SourceFailure(HistorySourceRequest? request, string code, Exception exception)
    {
        LogHistoryFailure("historySource", request?.Revision?.SessionId ?? "(none)", code, exception);
        return new(code, null, null);
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

internal sealed record HistoryRevision(string SessionId, string Length, string LastWriteUtcTicks);
internal sealed record HistorySourceRange(string Start, string End);
internal sealed record TimelineHistoryResponse(HistoryResponse Page, HistoryRevision? Revision, HistorySourceRange[] Sources)
{
    /// <summary>
    /// The stamp of the journal a newest page was read from, which the next request of the newest page sends back as
    /// <c>known</c>; absent when the host is not sure the page is the journal as it has this stamp.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Stamp { get; init; }
}
internal sealed record HistorySourceRequest(HistoryRevision Revision, string Start, string End, string Offset);
internal sealed record HistorySourceResponse(string Status, string? Text, string? NextOffset);
