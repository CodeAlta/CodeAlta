using System.Threading.Channels;

namespace CodeAlta.Orchestration.Runtime.Actors;

internal enum OrchestrationActorDiagnosticKind
{
    Started,
    CommandCompleted,
    CommandFailed,
    Stopped,
}

internal sealed record OrchestrationActorDiagnostic(
    OrchestrationActorDiagnosticKind Kind,
    Exception? Exception = null);

internal sealed class OrchestrationMailboxActor : IAsyncDisposable
{
    private readonly Channel<Func<CancellationToken, ValueTask>> _mailbox;
    // Normal commands are admitted in the order they are posted: a command posted while all the slots are taken waits in
    // _waiters, and the start of a command admits the first ones that wait, writing them to the mailbox in that order.
    // (A semaphore would resume its waiters on the thread pool, each writing when it runs: in any order.)
    private readonly object _admission = new();
    private readonly LinkedList<Waiter> _waiters = [];
    private int _freeSlots;
    private bool _admissionClosed;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _runner;
    private bool _disposed;

    public OrchestrationMailboxActor(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Mailbox capacity must be greater than zero.");
        }

        _freeSlots = capacity;
        _mailbox = Channel.CreateBounded<Func<CancellationToken, ValueTask>>(new BoundedChannelOptions(capacity + 1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _runner = Task.Run(RunAsync);
    }

    public event EventHandler<OrchestrationActorDiagnostic>? DiagnosticEmitted;

    public ValueTask PostAsync(
        Func<CancellationToken, ValueTask> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfDisposed();
        return EnqueueAsync(command, reserveNormalSlot: true, cancellationToken);
    }

    public ValueTask PostReservedAsync(
        Func<CancellationToken, ValueTask> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfDisposed();
        return EnqueueAsync(command, reserveNormalSlot: false, cancellationToken);
    }

    public async ValueTask<TReply> AskAsync<TReply>(
        Func<CancellationToken, ValueTask<TReply>> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfDisposed();

        return await AskCoreAsync(command, reserveNormalSlot: true, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TReply> AskReservedAsync<TReply>(
        Func<CancellationToken, ValueTask<TReply>> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfDisposed();

        return await AskCoreAsync(command, reserveNormalSlot: false, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TReply> AskCoreAsync<TReply>(
        Func<CancellationToken, ValueTask<TReply>> command,
        bool reserveNormalSlot,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<TReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueAsync(
                async actorCancellationToken =>
                {
                    try
                    {
                        completion.SetResult(await command(actorCancellationToken).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (actorCancellationToken.IsCancellationRequested)
                    {
                        completion.SetCanceled(actorCancellationToken);
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                        throw;
                    }
                },
                reserveNormalSlot,
                cancellationToken)
            .ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask EnqueueAsync(
        Func<CancellationToken, ValueTask> command,
        bool reserveNormalSlot,
        CancellationToken cancellationToken)
    {
        if (!reserveNormalSlot)
        {
            return _mailbox.Writer.WriteAsync(Wrap(command, releaseNormalSlotOnStart: false), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_admission)
        {
            if (_admissionClosed)
            {
                throw new ChannelClosedException();
            }

            // Nothing waits before it and a slot is free: it is written now, in its turn.
            if (_waiters.Count == 0 && _freeSlots > 0 && _mailbox.Writer.TryWrite(Wrap(command, releaseNormalSlotOnStart: true)))
            {
                _freeSlots--;
                return ValueTask.CompletedTask;
            }

            waiter = new Waiter(command);
            waiter.Node = _waiters.AddLast(waiter);
        }

        return WaitAdmissionAsync(waiter, cancellationToken);
    }

    private async ValueTask WaitAdmissionAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        // A waiter canceled before its turn leaves the line; once admitted, its command runs as any other.
        using var registration = cancellationToken.Register(static state =>
        {
            var (actor, waiter, token) = ((OrchestrationMailboxActor, Waiter, CancellationToken))state!;
            lock (actor._admission)
            {
                if (waiter.Node is null) return;
                actor._waiters.Remove(waiter.Node);
                waiter.Node = null;
            }

            waiter.Admitted.TrySetCanceled(token);
        }, (this, waiter, cancellationToken));
        await waiter.Admitted.Task.ConfigureAwait(false);
    }

    // At the start of each command, from the mailbox runner: a normal command gives back its slot, and the waiters that
    // the free slots and the room of the mailbox admit are written to it, first come first.
    private void AdmitWaiters(bool releaseSlot)
    {
        List<Waiter>? admitted = null;
        lock (_admission)
        {
            if (releaseSlot) _freeSlots++;
            while (_freeSlots > 0 && _waiters.First is { } first)
            {
                var waiter = first.Value;
                if (!_mailbox.Writer.TryWrite(Wrap(waiter.Command, releaseNormalSlotOnStart: true))) break;
                _freeSlots--;
                _waiters.RemoveFirst();
                waiter.Node = null;
                (admitted ??= []).Add(waiter);
            }
        }

        if (admitted is not null)
        {
            foreach (var waiter in admitted) waiter.Admitted.TrySetResult();
        }
    }

    // When the mailbox no longer takes commands: what still waits is told so, as a write to a completed mailbox is.
    private void CloseAdmission()
    {
        List<Waiter> waiting;
        lock (_admission)
        {
            _admissionClosed = true;
            waiting = [.. _waiters];
            _waiters.Clear();
            foreach (var waiter in waiting) waiter.Node = null;
        }

        foreach (var waiter in waiting) waiter.Admitted.TrySetException(new ChannelClosedException());
    }

    private Func<CancellationToken, ValueTask> Wrap(Func<CancellationToken, ValueTask> command, bool releaseNormalSlotOnStart)
        => async cancellationToken =>
        {
            // A reserved command frees no slot, but it frees room in the mailbox for a waiter.
            AdmitWaiters(releaseNormalSlotOnStart);
            await command(cancellationToken).ConfigureAwait(false);
        };

    public async ValueTask StopAsync(bool cancelPending = false)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (cancelPending)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }

        CloseAdmission();
        _mailbox.Writer.TryComplete();
        await _runner.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    public async ValueTask DisposeAsync()
        => await StopAsync(cancelPending: true).ConfigureAwait(false);

    private async Task RunAsync()
    {
        Emit(new OrchestrationActorDiagnostic(OrchestrationActorDiagnosticKind.Started));
        try
        {
            await foreach (var command in _mailbox.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    await command(_lifetime.Token).ConfigureAwait(false);
                    Emit(new OrchestrationActorDiagnostic(OrchestrationActorDiagnosticKind.CommandCompleted));
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Emit(new OrchestrationActorDiagnostic(OrchestrationActorDiagnosticKind.CommandFailed, exception));
                }
            }
        }
        finally
        {
            Emit(new OrchestrationActorDiagnostic(OrchestrationActorDiagnosticKind.Stopped));
        }
    }

    private void Emit(OrchestrationActorDiagnostic diagnostic)
        => DiagnosticEmitted?.Invoke(this, diagnostic);

    private sealed class Waiter(Func<CancellationToken, ValueTask> command)
    {
        public Func<CancellationToken, ValueTask> Command { get; } = command;

        // Under _admission: the place of the waiter in the line; null once it left it.
        public LinkedListNode<Waiter>? Node { get; set; }

        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OrchestrationMailboxActor));
        }
    }
}
