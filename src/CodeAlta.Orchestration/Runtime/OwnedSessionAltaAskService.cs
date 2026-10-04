using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Lets a session's <c>alta ask</c> command reach the owner's ask flow: the ask is queued through the send of
/// that session that is running, and answered through <see cref="OwnedSessionAskService"/> like any owned ask.
/// </summary>
/// <remarks>
/// Only <see cref="QueueAsync"/> does something. The pending asks are read and answered through the owner
/// (<see cref="OwnedSessionAskService.List"/>, <see cref="OwnedSessionAskService.AnswerAsync"/>), never through
/// this adapter, so its queue views are empty and its response operations refuse.
/// </remarks>
public sealed class OwnedSessionAltaAskService : IAltaAskService
{
    private readonly OwnedSessionAskService _asks;

    /// <summary>Creates the adapter over a host's ask owner.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="asks"/> is null.</exception>
    public OwnedSessionAltaAskService(OwnedSessionAskService asks)
    {
        ArgumentNullException.ThrowIfNull(asks);
        _asks = asks;
    }

    /// <inheritdoc />
    public event EventHandler<AltaAskQueueChangedEventArgs>? QueueChanged { add { } remove { } }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The session has no running send that can ask (asks are disabled, the run was not started by the owner or
    /// already asked, or capacity is exhausted).
    /// </exception>
    public Task<AltaAskQueueResult> QueueAsync(AltaAskRequest request, string sessionId, AltaCallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        var queued = _asks.QueueFromSession(sessionId.Trim(), request, cancellationToken)
            ?? throw new InvalidOperationException("This session cannot ask right now: only a run started from the application can, once per run.");
        return Task.FromResult(queued);
    }

    /// <inheritdoc />
    public AltaQueuedAsk? Peek(string sessionId) => null;

    /// <inheritdoc />
    public IReadOnlyList<AltaQueuedAsk> GetPending(string sessionId) => [];

    /// <inheritdoc />
    public AltaAskRemovalResult TryRemoveHead(string sessionId, string askId)
        => throw new NotSupportedException("Owned asks are answered through the session's owner.");

    /// <inheritdoc />
    public Task<AltaAskResponseResult> RespondAsync(AltaAskResponseHandle handle, Func<Task<SessionPromptResponseResult>> dispatch)
        => throw new NotSupportedException("Owned asks are answered through the session's owner.");

    /// <inheritdoc />
    public AltaAskRemovalResult TryCancelResponse(AltaAskResponseHandle handle)
        => throw new NotSupportedException("Owned asks are answered through the session's owner.");
}
