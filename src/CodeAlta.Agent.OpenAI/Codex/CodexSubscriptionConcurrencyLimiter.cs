using System.Collections.Concurrent;

namespace CodeAlta.Agent.OpenAI.Codex;

// Bounds the requests of the ChatGPT subscription: one at a time per session, and per account at most the limit each
// request comes with. Waiters get their turn in the order they came: a release hands its slot to the oldest waiter its
// limit admits, and wakes no other.
internal sealed class CodexSubscriptionConcurrencyLimiter
{
    private readonly object _sessionSync = new();
    // The gate of a session exists while a request of the session holds it or waits for it.
    private readonly Dictionary<string, SessionGate> _sessionGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AccountConcurrencyGate> _accountGates = new(StringComparer.Ordinal);

    internal int SessionGateCount
    {
        get
        {
            lock (_sessionSync)
            {
                return _sessionGates.Count;
            }
        }
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string providerKey,
        string sessionId,
        string? accountId,
        int maxConcurrentRequests,
        CancellationToken cancellationToken)
        => await AcquireAsync(
                providerKey,
                sessionId,
                accountId,
                maxConcurrentRequests,
                onAccountLimitWaitAsync: null,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string providerKey,
        string sessionId,
        string? accountId,
        int maxConcurrentRequests,
        Func<CodexSubscriptionConcurrencyWaitInfo, CancellationToken, ValueTask>? onAccountLimitWaitAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var normalizedMaxConcurrentRequests = Math.Max(1, maxConcurrentRequests);
        var sessionKey = providerKey + "\n" + sessionId;
        var accountKey = ResolveAccountKey(accountId);
        var accountGate = _accountGates.GetOrAdd(accountKey, static _ => new AccountConcurrencyGate());
        var sessionGate = RentSessionGate(sessionKey);
        try
        {
            await sessionGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReturnSessionGate(sessionKey, sessionGate);
            throw;
        }

        try
        {
            var accountLease = await accountGate.AcquireAsync(
                    normalizedMaxConcurrentRequests,
                    accountKey,
                    onAccountLimitWaitAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            return new Releaser(this, sessionKey, sessionGate, accountLease);
        }
        catch
        {
            sessionGate.Semaphore.Release();
            ReturnSessionGate(sessionKey, sessionGate);
            throw;
        }
    }

    internal int GetAccountWaiterCount(string? accountId)
        => _accountGates.TryGetValue(ResolveAccountKey(accountId), out var gate) ? gate.WaiterCount : 0;

    private static string ResolveAccountKey(string? accountId)
        => string.IsNullOrWhiteSpace(accountId) ? "<default>" : accountId.Trim();

    private SessionGate RentSessionGate(string sessionKey)
    {
        lock (_sessionSync)
        {
            if (!_sessionGates.TryGetValue(sessionKey, out var gate))
            {
                gate = new SessionGate();
                _sessionGates.Add(sessionKey, gate);
            }

            gate.Users++;
            return gate;
        }
    }

    private void ReturnSessionGate(string sessionKey, SessionGate gate)
    {
        lock (_sessionSync)
        {
            gate.Users--;
            if (gate.Users == 0)
            {
                _sessionGates.Remove(sessionKey);
            }
        }
    }

    private sealed class SessionGate
    {
        // SemaphoreSlim hands a release to its oldest asynchronous waiter, and a cancelled wait never takes the slot.
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        // The requests that hold the gate or wait for it, under the lock of the limiter.
        public int Users { get; set; }
    }

    private sealed class AccountConcurrencyGate
    {
        private readonly object _sync = new();
        private readonly LinkedList<AccountWaiter> _waiters = new();
        private int _activeCount;
        // No waiter is admitted while this many requests run: the highest limit among the waiters (exact when they share
        // one limit), back to zero when none waits.
        private int _waiterLimitBound;

        public int WaiterCount
        {
            get
            {
                lock (_sync)
                {
                    return _waiters.Count;
                }
            }
        }

        public async ValueTask<IAsyncDisposable> AcquireAsync(
            int maxConcurrentRequests,
            string accountKey,
            Func<CodexSubscriptionConcurrencyWaitInfo, CancellationToken, ValueTask>? onLimitWaitAsync,
            CancellationToken cancellationToken)
        {
            if (TryAcquire(maxConcurrentRequests))
            {
                return new AccountReleaser(this);
            }

            if (onLimitWaitAsync is not null)
            {
                await onLimitWaitAsync(
                        new CodexSubscriptionConcurrencyWaitInfo(accountKey, maxConcurrentRequests),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AccountWaiter waiter;
            lock (_sync)
            {
                if (_activeCount < maxConcurrentRequests)
                {
                    _activeCount++;
                    return new AccountReleaser(this);
                }

                waiter = new AccountWaiter(this, maxConcurrentRequests);
                waiter.Node = _waiters.AddLast(waiter);
                _waiterLimitBound = Math.Max(_waiterLimitBound, maxConcurrentRequests);
            }

            // A cancelled waiter leaves the queue; one already admitted keeps its slot and gives it back with its lease.
            using (cancellationToken.UnsafeRegister(
                static (state, token) => ((AccountWaiter)state!).Gate.Cancel((AccountWaiter)state!, token),
                waiter))
            {
                await waiter.Completion.Task.ConfigureAwait(false);
            }

            return new AccountReleaser(this);
        }

        private bool TryAcquire(int maxConcurrentRequests)
        {
            lock (_sync)
            {
                if (_activeCount >= maxConcurrentRequests)
                {
                    return false;
                }

                _activeCount++;
                return true;
            }
        }

        private void Cancel(AccountWaiter waiter, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (waiter.Node is null)
                {
                    // Admitted: the slot is the waiter's.
                    return;
                }

                RemoveLocked(waiter);
            }

            waiter.Completion.TrySetCanceled(cancellationToken);
        }

        private void Release()
        {
            AccountWaiter? admitted;
            lock (_sync)
            {
                if (_activeCount <= 0)
                {
                    throw new InvalidOperationException("Codex subscription concurrency lease was released too many times.");
                }

                _activeCount--;
                admitted = AdmitNextWaiterLocked();
            }

            admitted?.Completion.TrySetResult();
        }

        // Hands the freed slot to the oldest waiter whose limit admits it. Every waiter came while at least as many
        // requests as its limit ran, so one release admits one waiter at most: with one limit for all, the first.
        private AccountWaiter? AdmitNextWaiterLocked()
        {
            for (var node = _waiters.First; node is not null && _activeCount < _waiterLimitBound; node = node.Next)
            {
                var waiter = node.Value;
                if (_activeCount < waiter.Limit)
                {
                    RemoveLocked(waiter);
                    _activeCount++;
                    return waiter;
                }
            }

            return null;
        }

        private void RemoveLocked(AccountWaiter waiter)
        {
            _waiters.Remove(waiter.Node!);
            waiter.Node = null;
            if (_waiters.Count == 0)
            {
                _waiterLimitBound = 0;
            }
        }

        private sealed class AccountWaiter(AccountConcurrencyGate gate, int limit)
        {
            public AccountConcurrencyGate Gate { get; } = gate;

            public int Limit { get; } = limit;

            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            // The place of the waiter in the queue, null once it was admitted or cancelled; under the lock of the gate.
            public LinkedListNode<AccountWaiter>? Node { get; set; }
        }

        private sealed class AccountReleaser(AccountConcurrencyGate owner) : IAsyncDisposable
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.Release();
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Releaser(
        CodexSubscriptionConcurrencyLimiter owner,
        string sessionKey,
        SessionGate sessionGate,
        IAsyncDisposable accountLease) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await accountLease.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                sessionGate.Semaphore.Release();
                owner.ReturnSessionGate(sessionKey, sessionGate);
            }
        }
    }
}

internal sealed record CodexSubscriptionConcurrencyWaitInfo(
    string AccountKey,
    int MaxConcurrentRequests);
