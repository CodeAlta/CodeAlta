using System.Collections.Concurrent;

namespace CodeAlta.Agent.OpenAI.CodexSubscription;

internal sealed class CodexSubscriptionConcurrencyLimiter
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionSemaphores = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AccountConcurrencyGate> _accountGates = new(StringComparer.Ordinal);

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
        var sessionSemaphore = _sessionSemaphores.GetOrAdd(
            providerKey + "\n" + sessionId,
            static _ => new SemaphoreSlim(1, 1));
        var accountKey = string.IsNullOrWhiteSpace(accountId) ? "<default>" : accountId.Trim();
        var accountGate = _accountGates.GetOrAdd(accountKey, static _ => new AccountConcurrencyGate());

        await sessionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accountLease = await accountGate.AcquireAsync(
                    normalizedMaxConcurrentRequests,
                    accountKey,
                    onAccountLimitWaitAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            return new Releaser(sessionSemaphore, accountLease);
        }
        catch
        {
            sessionSemaphore.Release();
            throw;
        }
    }

    private sealed class AccountConcurrencyGate
    {
        private readonly object _sync = new();
        private int _activeCount;
        private TaskCompletionSource _releaseSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Task releaseTask;
                lock (_sync)
                {
                    if (_activeCount < maxConcurrentRequests)
                    {
                        _activeCount++;
                        return new AccountReleaser(this);
                    }

                    releaseTask = _releaseSignal.Task;
                }

                await releaseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
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

        private void Release()
        {
            TaskCompletionSource releaseSignal;
            lock (_sync)
            {
                if (_activeCount <= 0)
                {
                    throw new InvalidOperationException("Codex subscription concurrency lease was released too many times.");
                }

                _activeCount--;
                releaseSignal = _releaseSignal;
                _releaseSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            releaseSignal.TrySetResult();
        }

        private sealed class AccountReleaser(AccountConcurrencyGate owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Releaser(
        SemaphoreSlim sessionSemaphore,
        IAsyncDisposable accountLease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await accountLease.DisposeAsync().ConfigureAwait(false);
            sessionSemaphore.Release();
        }
    }
}

internal sealed record CodexSubscriptionConcurrencyWaitInfo(
    string AccountKey,
    int MaxConcurrentRequests);
