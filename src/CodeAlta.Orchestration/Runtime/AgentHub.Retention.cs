using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class AgentHub
{
    private readonly ISessionRetentionAdmission? _retentionAdmission;
    private readonly List<Exception> _retainedPreparations = [];
    private int _preparations;
    private TaskCompletionSource? _preparationsIdle;

    private static async Task<T> InvokeOriginalAsync<T>(Func<Task<T>> invoke)
    {
        Task<T>? result = null;
        await InvokeOriginalAsync(() => (Task)(result = invoke())).ConfigureAwait(false);
        return result!.GetAwaiter().GetResult();
    }

    private static async Task InvokeOriginalAsync(Func<Task> invoke)
    {
        var original = new OwnedSessionCommandService.OriginalInvocation();
        original.Launch(invoke);
        if (await original.Outcome.ConfigureAwait(false) is not { } failure) return;
        // Await alone unwraps a multiply-faulted task to its first exception. The invocation keeps
        // the complete original aggregate, including sibling retention signals and their owners.
        if (OwnedProviderEventForwarding.HasRetention(failure))
            throw new AgentDependencyRetentionException("hub", "original operation", [failure], original);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
    }

    private static async Task<T> ExecuteReferencedAsync<T>(SessionEntry entry, Func<Task<T>> invoke)
    {
        var original = entry.Lifetime.RecordRun(() => invoke());
        try
        {
            original.Launch();
            if (await original.Outcome.ConfigureAwait(false) is { } failure)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            return ((Task<T>)original.Original!).GetAwaiter().GetResult();
        }
        finally { entry.Lifetime.CompleteReference(original.Failure, original); }
    }

    private async Task<AgentSessionHandle> PrepareSessionAsync(AgentSessionCreateOptions options,
        string? resumeId, CancellationToken cancellationToken)
    {
        var providerId = ResolveProviderId(options);
        lock (_disposalGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preparations++ == 0) _preparationsIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var protection = new PreparationProtection(_retentionAdmission);
        ProviderSessionRuntimeLease? runtime = null;
        IAgentSession? session = null;
        SessionEntry? entry = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // No provider factory, Start, Create, Resume, or callback registration precedes this claim.
            protection.Acquire(resumeId ?? options.SessionId, options.ParentSessionId, options.CreatedBySessionId);
            if (resumeId is not null && _retentionAdmission is not null)
            {
                // Do not trust a cached catalog generation: legacy providers can otherwise recreate a
                // missing journal while resuming their own history. Admission pins this existence check.
                var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(_stateRootPath!));
                if (await store.GetSessionSummaryAsync(resumeId, cancellationToken).ConfigureAwait(false) is null)
                    throw new KeyNotFoundException($"The session '{resumeId}' no longer has an owned journal.");
            }
            runtime = await InvokeOriginalAsync(() => CreateStartedProviderRuntimeAsync(providerId, cancellationToken)).ConfigureAwait(false);
            session = resumeId is null
                ? await InvokeOriginalAsync(() => runtime.CreateSessionAsync(options, cancellationToken)).ConfigureAwait(false)
                : await InvokeOriginalAsync(() => runtime.ResumeSessionAsync(resumeId, (AgentSessionResumeOptions)options, cancellationToken)).ConfigureAwait(false);
            EnsureRuntimePreservedRequestedSessionId(resumeId ?? options.SessionId, session.SessionId);
            protection.Narrow(session.SessionId);
            entry = new SessionEntry(new AgentSessionCoordinator(session), runtime, protection);
            return await AttachSessionAsync(providerId, entry, session, options.ParentSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            try
            {
                if (OwnedProviderEventForwarding.HasRetention(failure))
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
                if (entry is not null)
                    await InvokeOriginalAsync(() => entry.DisposeAsync().AsTask()).ConfigureAwait(false);
                else
                {
                    if (session is not null) await InvokeOriginalAsync(() => session.DisposeAsync().AsTask()).ConfigureAwait(false);
                    if (runtime is not null) await InvokeOriginalAsync(() => DisposeProviderRuntimeAsync(runtime).AsTask()).ConfigureAwait(false);
                    protection.Dispose();
                }
            }
            catch (Exception cleanupFailure)
            {
                // An uncertain original/disposal is not permission to retire its journal. Keep the
                // lease and dependency graph, including failures before an entry could be published.
                var retained = new AgentDependencyRetentionException("hub", "preparation cleanup",
                    [failure, cleanupFailure], new { protection, runtime, session, entry });
                lock (_disposalGate) _retainedPreparations.Add(retained);
                throw retained;
            }
            throw;
        }
        finally
        {
            lock (_disposalGate)
                if (--_preparations == 0) _preparationsIdle!.TrySetResult();
        }
    }

    // The preparation owns this object until the entry is constructed; thereafter the original
    // entry disposal owns it. A cancelled caller never independently disposes the claims.
    private sealed class PreparationProtection(ISessionRetentionAdmission? admission) : IDisposable
    {
        private readonly List<IDisposable> _claims = [];
        private IDisposable? _unscoped;
        private string? _sessionId;

        internal void Acquire(string? sessionId, string? parentId, string? createdById)
        {
            if (admission is null) return;
            _sessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
            if (_sessionId is null)
                _unscoped = admission.TryProtectAll() ?? throw Refused();
            else
                Protect(_sessionId);
            if (!string.IsNullOrWhiteSpace(parentId)) Protect(parentId.Trim());
            if (!string.IsNullOrWhiteSpace(createdById)) Protect(createdById.Trim());
        }

        internal void Narrow(string actualId)
        {
            if (admission is null) return;
            ArgumentException.ThrowIfNullOrWhiteSpace(actualId);
            if (!string.Equals(actualId, actualId.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("A provider returned a noncanonical session identity.");
            if (_sessionId is null) Protect(actualId);
            // Acquire the narrower claim before releasing all-session protection.
            Interlocked.Exchange(ref _unscoped, null)?.Dispose();
        }

        private void Protect(string id) => _claims.Add(admission!.TryProtectSession(id) ?? throw Refused());
        private static InvalidOperationException Refused() => new("Automatic retirement already owns the requested session.");

        public void Dispose()
        {
            foreach (var claim in _claims) claim.Dispose();
            _claims.Clear();
            Interlocked.Exchange(ref _unscoped, null)?.Dispose();
        }
    }
}
