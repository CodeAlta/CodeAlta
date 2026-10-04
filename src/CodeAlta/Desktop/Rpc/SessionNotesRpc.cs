using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

[NeoRpcService("sessionNotes", Version = 1)]
internal sealed class SessionNotesService
{
    // Agents write notes of any length through the alta tool; a plan or a report runs to tens of thousands
    // of characters. The response stays far below the frame limit of the window even when every unit is
    // escaped as six bytes.
    internal const int MaximumMarkdownUnits = 262_144;
    internal const int MaximumResponseBytes = 2 * 1024 * 1024;
    private readonly OwnedSessionWorkspace _reads;
    private readonly Func<string, CancellationToken, Task>? _clear;
    private readonly string _epoch;

    internal SessionNotesService(OwnedSessionWorkspace reads, string epoch, Func<string, CancellationToken, Task>? clear = null)
    {
        ArgumentNullException.ThrowIfNull(reads);
        if (!Epoch(epoch)) throw new ArgumentException("Canonical host epoch required.", nameof(epoch));
        _reads = reads;
        _epoch = epoch;
        _clear = clear;
    }

    internal SessionNotesService(OwnedSessionWorkspace reads, SessionRuntimeService runtime, string epoch)
        : this(reads, epoch, (sessionId, token) => runtime.UpdateNotesAsync(sessionId, "", AgentNotesUpdateKind.Cleared, static _ => { }, token))
    {
        ArgumentNullException.ThrowIfNull(runtime);
    }

    [NeoRpcMethod("current")]
    public async Task<SessionNotesResponse> CurrentAsync(SessionNotesRequest request, CancellationToken cancellationToken)
    {
        // Even error identity is validated before reflecting it into the bounded envelope.
        var sessionId = Identity(request?.SessionId) ? request!.SessionId : null;
        if (sessionId is null || !Epoch(request?.ExpectedHostEpoch)) return Error("invalid_request", sessionId);
        if (request!.ExpectedHostEpoch != _epoch) return Error("stale_epoch", sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        Task<string> original;
        try
        {
            // Only the synchronous shared gate refusal is capacity. An admitted callback is always
            // launched behind its retained owner task and reports any exception asynchronously.
            original = _reads.ReadNotesMarkdownAsync(sessionId, cancellationToken);
        }
        catch (ObjectDisposedException) { return Error("closed", sessionId); }
        catch (InvalidOperationException) { return Error("capacity", sessionId); }

        try
        {
            var markdown = await original.ConfigureAwait(false);
            if (!Text(markdown, MaximumMarkdownUnits)) return Error("wire_limit", sessionId);
            return new("ok", _epoch, sessionId, markdown);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SessionNotesSessionNotFoundException) { return Error("missing_session", sessionId); }
        catch (ObjectDisposedException) { return Error("closed", sessionId); }
        catch (Exception) { return Error("read_failed", sessionId); }
    }

    [NeoRpcMethod("clear")]
    public async Task<SessionNotesClearResponse> ClearAsync(SessionNotesRequest request, CancellationToken cancellationToken)
    {
        var sessionId = Identity(request?.SessionId) ? request!.SessionId : null;
        SessionNotesClearResponse Error(string status) => new(status, _epoch, sessionId);
        if (sessionId is null || !Epoch(request?.ExpectedHostEpoch)) return Error("invalid_request");
        if (request!.ExpectedHostEpoch != _epoch) return Error("stale_epoch");
        if (_clear is null) return Error("closed");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _clear(sessionId, cancellationToken).ConfigureAwait(false);
            return Error("ok");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // Even a recognizable exception can arise from post-commit feedback or a plugin.
        // Never describe an admitted write as unsuccessful or retry it automatically.
        catch (Exception) { return Error("clear_unconfirmed"); }
    }

    private SessionNotesResponse Error(string status, string? sessionId) => new(status, _epoch, sessionId, null);
    private static bool Epoch(string? value) => value is { Length: 36 } && Guid.TryParseExact(value, "D", out var guid)
        && guid != Guid.Empty && guid.ToString("D") == value;
    private static bool Identity(string? value) => Text(value, 256) && !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && !value.Any(char.IsControl);
    private static bool Text([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, int maximum)
    {
        if (value is null || value.Length > maximum) return false;
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
}

internal sealed record SessionNotesRequest(string ExpectedHostEpoch, string SessionId);
internal sealed record SessionNotesResponse(string Status, string HostEpoch, string? SessionId, string? Markdown);
internal sealed record SessionNotesClearResponse(string Status, string HostEpoch, string? SessionId);
