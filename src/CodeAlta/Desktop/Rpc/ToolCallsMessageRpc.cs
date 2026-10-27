using CodeAlta.Agent;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class ToolCallsService
{
    /// <summary>
    /// Returns the text of a message of a session, whole, in parts: what a history row shows only the start of when the
    /// text is longer than its share of a page (<see cref="WorkspaceService.ProjectHistory"/>). A user message loses the
    /// lines that name its images and its files, as its row does.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    [NeoRpcMethod("readMessage")]
    public async Task<MessageTextResponse> ReadMessageAsync(MessageTextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_read is null) return new("unavailable", null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null);
        if (!Identity(request.SessionId) || !Offset(request.Offset, out var offset) || request.Position < 0) return new("invalid", null);
        cancellationToken.ThrowIfCancellationRequested();
        AgentEvent? record;
        try
        {
            record = await ReadRecordAsync(request.SessionId, offset, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ToolCallRefusal refusal) { return new(refusal.Status, null); }
        if (record is not AgentContentCompletedEvent { Content: { } content } message) return new("missing_record", null);
        string? text = content, details = null;
        HistoryImageProjection.Project(message, ref text, ref details, out _, out _);
        text ??= string.Empty;
        return request.Position > text.Length ? new("invalid", null) : new("ok", Part(text, request.Position));
    }
}

/// <summary>Asks for the text of a message of a session, from a position.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Offset">The journal offset of the message, as its history row reported it.</param>
/// <param name="Position">Where the part starts in the text, in UTF-16 code units.</param>
internal sealed record MessageTextRequest(string ExpectedEpoch, string SessionId, string Offset, int Position);

/// <summary>A part of the text of a message, or the reason none is served.</summary>
/// <param name="Status">
/// <c>ok</c>, or <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid</c>, <c>capacity</c>, <c>closed</c>,
/// <c>missing_session</c>, <c>missing_record</c>, <c>too_large</c> or <c>read_failed</c>.
/// </param>
/// <param name="Text">The part, when the status is <c>ok</c>.</param>
internal sealed record MessageTextResponse(string Status, ToolCallText? Text);
