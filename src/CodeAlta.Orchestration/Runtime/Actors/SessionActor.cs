namespace CodeAlta.Orchestration.Runtime.Actors;

internal sealed record SessionActorCommandResult
{
    public required bool Succeeded { get; init; }

    public string? Message { get; init; }

    public Exception? Exception { get; init; }

    public static SessionActorCommandResult Success(string? message = null)
        => new() { Succeeded = true, Message = message };

    public static SessionActorCommandResult Failure(Exception exception, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new SessionActorCommandResult
        {
            Succeeded = false,
            Message = message ?? exception.Message,
            Exception = exception,
        };
    }
}

internal enum SessionActorSupervisorDecision
{
    Complete,
    FailCommand,
    StopActor,
}

internal sealed record SessionActorSupervisorEvent(
    string SessionId,
    SessionActorSupervisorDecision Decision,
    Exception? Exception = null,
    string? Message = null);

/// <summary>
/// The single writer of the runtime state of one session: its commands and its provider events are applied in the order
/// they are posted, without locks, while other sessions run in parallel. Long work (provider calls, tools, decisions of
/// the user, joins) runs outside the mailbox, and its result comes back as a command.
/// </summary>
internal sealed class SessionActor : IAsyncDisposable
{
    private readonly OrchestrationMailboxActor _mailbox;
    private readonly Func<Exception, SessionActorSupervisorDecision> _superviseException;
    private bool _stopped;

    public SessionActor(
        string sessionId,
        int mailboxCapacity = 128,
        Func<Exception, SessionActorSupervisorDecision>? superviseException = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        _mailbox = new OrchestrationMailboxActor(mailboxCapacity);
        _superviseException = superviseException ?? (_ => SessionActorSupervisorDecision.FailCommand);
    }

    public string SessionId { get; }

    public event EventHandler<SessionActorSupervisorEvent>? SupervisorEventEmitted;

    public ValueTask PostAsync(Func<CancellationToken, ValueTask> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfStopped();
        return _mailbox.PostAsync(command, cancellationToken);
    }

    public async ValueTask<SessionActorCommandResult> ExecuteAsync(
        Func<CancellationToken, ValueTask> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfStopped();

        return await ExecuteCoreAsync(command, reserved: false, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SessionActorCommandResult> ExecuteReservedAsync(
        Func<CancellationToken, ValueTask> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfStopped();

        return await ExecuteCoreAsync(command, reserved: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SessionActorCommandResult> ExecuteCoreAsync(
        Func<CancellationToken, ValueTask> command,
        bool reserved,
        CancellationToken cancellationToken)
    {
        ValueTask<SessionActorCommandResult> Execute(Func<CancellationToken, ValueTask<SessionActorCommandResult>> query)
            => reserved
                ? _mailbox.AskReservedAsync(query, cancellationToken)
                : _mailbox.AskAsync(query, cancellationToken);

        return await Execute(
            async actorCancellationToken =>
            {
                try
                {
                    await command(actorCancellationToken).ConfigureAwait(false);
                    Emit(SessionActorSupervisorDecision.Complete);
                    return SessionActorCommandResult.Success();
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !actorCancellationToken.IsCancellationRequested)
                {
                    var decision = _superviseException(exception);
                    Emit(decision, exception, exception.Message);
                    if (decision == SessionActorSupervisorDecision.StopActor)
                    {
                        _ = StopAsync(cancelPending: true).AsTask();
                    }

                    return SessionActorCommandResult.Failure(exception);
                }
            }).ConfigureAwait(false);
    }

    public async ValueTask<TReply> QueryAsync<TReply>(
        Func<CancellationToken, ValueTask<TReply>> query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfStopped();
        return await _mailbox.AskAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(bool cancelPending = false)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        await _mailbox.StopAsync(cancelPending).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
        => await StopAsync(cancelPending: true).ConfigureAwait(false);

    private void Emit(
        SessionActorSupervisorDecision decision,
        Exception? exception = null,
        string? message = null)
        => SupervisorEventEmitted?.Invoke(this, new SessionActorSupervisorEvent(SessionId, decision, exception, message));

    private void ThrowIfStopped()
    {
        if (_stopped)
        {
            throw new ObjectDisposedException(nameof(SessionActor));
        }
    }
}
