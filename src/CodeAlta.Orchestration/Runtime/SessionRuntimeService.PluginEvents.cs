using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.Plugins;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    internal RuntimePluginAgentEventObserver? PluginEventObserver { get; init; }
    internal string? PluginEventCurrentProjectId { get; init; }
    internal string? PluginEventCurrentProjectPath { get; init; }

    private RuntimePluginAgentEventEnvelope CapturePluginEvent(AgentEvent publishedEvent, string sessionId,
        string? projectId, string? workingDirectory)
        => RuntimePluginAgentEventEnvelope.Capture(publishedEvent, sessionId, projectId, workingDirectory,
            PluginEventCurrentProjectId, PluginEventCurrentProjectPath);

    private Task ObserveLivePluginEventAsync(RuntimePluginAgentEventEnvelope envelope)
        => PluginEventObserver?.ObserveAsync(envelope, CancellationToken.None).AsTask() ?? Task.CompletedTask;

    private async Task ObserveRuntimeFailureAsync(SessionViewDescriptor session, Exception original, Exception escaping,
        LiveEventObservationPrerequisite? prerequisite = null)
    {
        if (OwnedProviderEventForwarding.HasRetention(escaping)) _forwarding.RetainDependencies(escaping, this);
        if (original is OperationCanceledException) return;
        // Bind callers have already joined their cleanup owner. Send additionally supplies the actual
        // source/use release receipt: ordinary terminal errors alone are not missing prerequisites.
        prerequisite ??= new LiveEventObservationPrerequisite(this, true, escaping);
        try { await PublishRuntimeFailureEventAsync(session, original, prerequisite).ConfigureAwait(false); }
        catch (Exception observationFailure) { throw new AggregateException(escaping, observationFailure); }
    }

    internal sealed class LiveEventObservationPrerequisite
    {
        internal LiveEventObservationPrerequisite(object dependencies, bool cleanupReleased, Exception failure)
        {
            ArgumentNullException.ThrowIfNull(dependencies);
            ArgumentNullException.ThrowIfNull(failure);
            Dependencies = dependencies;
            CleanupReleased = cleanupReleased;
            Failure = failure;
        }

        internal object Dependencies { get; }
        internal bool CleanupReleased { get; }
        internal Exception Failure { get; }
        internal bool MayObserve => CleanupReleased && !OwnedProviderEventForwarding.HasRetention(Failure);
    }

    // Provider cache, each captured notification, and an eligible queue trigger are independent.
    // Own every stage before invoking any of them; a synchronous failure has no invented original.
    internal sealed class LiveEventIndependentWork
    {
        private readonly Func<Task>[] _effects;
        private int _started;

        internal LiveEventIndependentWork(IReadOnlyList<Func<Task>> effects)
        {
            ArgumentNullException.ThrowIfNull(effects);
            _effects = effects.ToArray();
            foreach (var effect in _effects) ArgumentNullException.ThrowIfNull(effect);
            Stages = _effects.Select(static _ => new OwnedSessionCommandService.OriginalInvocation()).ToArray();
        }

        internal IReadOnlyList<OwnedSessionCommandService.OriginalInvocation> Stages { get; }

        internal async Task RunAsync()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Independent event work already launched.");
            var failures = new List<Exception>();
            for (var index = 0; index < _effects.Length; index++)
            {
                Stages[index].Launch(_effects[index]);
                if (await Stages[index].Outcome.ConfigureAwait(false) is { } failure) failures.Add(failure);
            }
            if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }

    private sealed class RuntimeFailureCapture
    {
        internal Exception? Original { get; private set; }
        internal void Capture(Exception failure) => Original ??= failure;
    }

    internal static async Task<(string SessionId, ModelProviderId ProviderId, string? ProjectId, string WorkingDirectory)> ResolveRecoveredPluginEventContextAsync(
        string sessionId, string? providerKey, string? contextCwd, string? workspacePath, string globalRoot,
        Func<string, string> normalize, Func<Task<IEnumerable<(string ProjectId, string ProjectPath)>>> loadProjects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(globalRoot);
        ArgumentNullException.ThrowIfNull(normalize);
        ArgumentNullException.ThrowIfNull(loadProjects);
        var cwd = contextCwd ?? workspacePath;
        if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(cwd))
            throw new SessionNotesSessionNotFoundException(sessionId);
        var normalizedCwd = normalize(cwd);
        string? projectId = null;
        if (!string.Equals(normalizedCwd, normalize(globalRoot), StringComparison.OrdinalIgnoreCase))
        {
            // Enumerate/normalize only through the first match; later invalid paths stay untouched.
            foreach (var project in await loadProjects().ConfigureAwait(false))
            {
                if (!string.Equals(normalize(project.ProjectPath), normalizedCwd, StringComparison.OrdinalIgnoreCase)) continue;
                projectId = project.ProjectId;
                break;
            }
            if (projectId is null) throw new SessionNotesSessionNotFoundException(sessionId);
        }
        return (sessionId, new ModelProviderId(providerKey.Trim()), projectId, normalizedCwd);
    }

    // Permission invalidation and queue stop are independent controls. Query failure means queue
    // drainage was not confirmed; a missing actor/queue is instead an explicit ineligible result.
    internal sealed class AttachmentClosureJoin(object dependencies)
    {
        internal OwnedSessionCommandService.OriginalInvocation Permissions { get; } = new();
        internal Task<Task?>? QueryOriginal { get; private set; }
        internal Exception? QueryAwaitedFailure { get; private set; }
        internal AggregateException? QueryOriginalFaults { get; private set; }
        internal OwnedSessionCommandService.OriginalInvocation? QueueDrain { get; private set; }
        private int _started;

        internal async Task RunAsync(Func<Task> invalidatePermissions, Func<Task<Task?>?> queryDrain)
        {
            ArgumentNullException.ThrowIfNull(invalidatePermissions);
            ArgumentNullException.ThrowIfNull(queryDrain);
            if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Attachment closure already launched.");
            var failures = new List<Exception>();
            var unconfirmed = false;
            Permissions.Launch(invalidatePermissions);
            try
            {
                QueryOriginal = queryDrain();
                if (QueryOriginal is not null && await QueryOriginal.ConfigureAwait(false) is { } drainage)
                {
                    QueueDrain = new OwnedSessionCommandService.OriginalInvocation();
                    QueueDrain.Launch(() => drainage);
                }
            }
            catch (Exception failure)
            {
                QueryAwaitedFailure = failure;
                QueryOriginalFaults = QueryOriginal?.Exception;
                failures.Add(QueryOriginalFaults is { InnerExceptions.Count: > 1 } ? QueryOriginalFaults : failure);
                unconfirmed = true;
            }
            // Explicit stage order: query/stop, permission invalidation, returned queue drainage.
            if (await Permissions.Outcome.ConfigureAwait(false) is { } permissionFailure) failures.Add(permissionFailure);
            unconfirmed |= Permissions.Original is null;
            if (QueueDrain is not null && await QueueDrain.Outcome.ConfigureAwait(false) is { } queueFailure) failures.Add(queueFailure);
            if (unconfirmed || failures.Any(OwnedProviderEventForwarding.HasRetention))
                throw new AgentDependencyRetentionException("attachment closure", "required drainage", failures,
                    new { Owner = dependencies, Closure = this });
            if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }

    // The broad abort route retains its original linked-token semantics. Its source is acquired
    // only after permission closure and is released only after confirmed ordinary abort completion.
    internal sealed class RuntimeAbortLifetime(object dependencies)
    {
        internal OwnedSessionCommandService.OriginalInvocation Permissions { get; } = new();
        internal OwnedSessionCommandService.OriginalInvocation Abort { get; } = new();
        internal CancellationTokenSource? Source { get; private set; }
        internal bool SourceReleased { get; private set; }
        private int _started;

        internal async Task RunAsync(Func<Task> invalidatePermissions, Func<CancellationToken, Task> abort,
            CancellationToken callerToken, CancellationToken attachmentToken)
        {
            ArgumentNullException.ThrowIfNull(invalidatePermissions);
            ArgumentNullException.ThrowIfNull(abort);
            if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Abort lifetime already launched.");
            Permissions.Launch(invalidatePermissions);
            var failure = await Permissions.Outcome.ConfigureAwait(false);
            ThrowIfUnconfirmed(Permissions, failure);
            if (failure is not null) ExceptionDispatchInfo.Throw(failure);
            Source = CancellationTokenSource.CreateLinkedTokenSource(callerToken, attachmentToken);
            Abort.Launch(() => abort(Source.Token));
            failure = await Abort.Outcome.ConfigureAwait(false);
            ThrowIfUnconfirmed(Abort, failure);
            try { Source.Dispose(); SourceReleased = true; }
            catch (Exception releaseFailure)
            {
                throw new AgentDependencyRetentionException("runtime abort", "source release",
                    failure is null ? [releaseFailure] : [failure, releaseFailure], new { Owner = dependencies, Lifetime = this });
            }
            if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        }

        private void ThrowIfUnconfirmed(OwnedSessionCommandService.OriginalInvocation invocation, Exception? failure)
        {
            if (failure is not null && (invocation.Original is null || OwnedProviderEventForwarding.HasRetention(failure)))
                throw new AgentDependencyRetentionException("runtime abort", "retained prerequisite", [failure],
                    new { Owner = dependencies, Lifetime = this });
        }
    }

    // These three exact-target command routes share the same source/registration release obligation.
    internal sealed class RuntimeCommandLifetime(object dependencies)
    {
        private readonly CancellationTokenSource _execution = new();
        private readonly OwnedSessionCommandService.OriginalInvocation _body = new();
        private readonly OwnedSessionCommandService.OriginalInvocation _cancellation = new();
        private CancellationTokenRegistration _ownerRegistration;
        private CancellationTokenRegistration _attachmentRegistration;
        private int _cancellationStarted;
        private int _started;
        internal OwnedSessionCommandService.DependencyReleaseDecision ReleaseDecision { get; } = new();
        internal OwnedSessionCommandService.OriginalInvocation OwnerRegistrationClose { get; } = new();
        internal OwnedSessionCommandService.OriginalInvocation AttachmentRegistrationClose { get; } = new();
        internal bool SourceReleased { get; private set; }

        internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ownerToken, CancellationToken attachmentToken)
        {
            ArgumentNullException.ThrowIfNull(dependencies);
            ArgumentNullException.ThrowIfNull(body);
            if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Command lifetime already launched.");
            var ownerRegistered = false;
            var attachmentRegistered = false;
            T result = default!;
            try
            {
                _ownerRegistration = ownerToken.UnsafeRegister(static state => ((RuntimeCommandLifetime)state!).Cancel(), this);
                ownerRegistered = true;
                _attachmentRegistration = attachmentToken.UnsafeRegister(static state => ((RuntimeCommandLifetime)state!).Cancel(), this);
                attachmentRegistered = true;
                result = await _body.RunAsync(() => body(_execution.Token)).ConfigureAwait(false);
            }
            catch (Exception failure) { ReleaseDecision.Observe(failure); }
            if (ownerRegistered) OwnerRegistrationClose.Launch(() => _ownerRegistration.DisposeAsync().AsTask());
            if (attachmentRegistered) AttachmentRegistrationClose.Launch(() => _attachmentRegistration.DisposeAsync().AsTask());
            if (ownerRegistered) await Join(OwnerRegistrationClose, "owner registration", true).ConfigureAwait(false);
            if (attachmentRegistered) await Join(AttachmentRegistrationClose, "attachment registration", true).ConfigureAwait(false);
            if (Volatile.Read(ref _cancellationStarted) != 0) await Join(_cancellation, "cancellation", false).ConfigureAwait(false);
            if (!ReleaseDecision.Retained)
            {
                try { _execution.Dispose(); SourceReleased = true; }
                catch (Exception failure) { ReleaseDecision.Observe(new AgentDependencyRetentionException("runtime command", "source release", [failure], this)); }
            }
            ReleaseDecision.Complete();
            if (ReleaseDecision.Retained)
                throw new AgentDependencyRetentionException("runtime command", "retained terminal prerequisites", ReleaseDecision.Failures,
                    new { Dependencies = dependencies, Lifetime = this });
            if (ReleaseDecision.Failures.Count == 1) ExceptionDispatchInfo.Throw(ReleaseDecision.Failures[0]);
            if (ReleaseDecision.Failures.Count > 1) throw new AggregateException(ReleaseDecision.Failures);
            return result;

            async Task Join(OwnedSessionCommandService.OriginalInvocation invocation, string stage, bool release)
            {
                if (await invocation.Outcome.ConfigureAwait(false) is not { } failure) return;
                ReleaseDecision.Observe(release || invocation.Original is null
                    ? new AgentDependencyRetentionException("runtime command", stage, [failure], this) : failure);
            }
        }

        private void Cancel()
        {
            if (Interlocked.CompareExchange(ref _cancellationStarted, 1, 0) == 0)
                _cancellation.Launch(_execution.CancelAsync);
        }
    }

    internal ValueTask DrainRetainedDependenciesAsync(Exception failure)
    {
        _forwarding.RetainDependencies(failure, this);
        // CloseAsync starts the independent permission/cancellation/abort controls and joins real work.
        // Explicit retained evidence prevents attachment, actor and event-stream dependency releases.
        return DisposeAsync();
    }

    // The same owner-specific publication/unwind decision is used by journal, actor and synthetic origins.
    // It owns actual returned originals separately from outcomes; marking is not plugin execution.
    internal sealed class LiveEventPublication(object dependencies)
    {
        private readonly TaskCompletionSource _launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private readonly List<(Task? Original, Exception AwaitedFailure, AggregateException? OriginalFaults)> _outcomes = [];
        private readonly List<Exception> _retentionFailures = [];
        internal object? InvocationDependencies { get; private set; }
        internal RuntimePluginAgentEventEnvelope? Published { get; private set; }
        internal Task? Completion { get; private set; }
        internal Task? PublicationOriginal { get; private set; }
        internal Task? ObservationOriginal { get; private set; }
        internal Task? IndependentOriginal { get; private set; }
        internal LiveEventIndependentWork? IndependentWork { get; set; }
        internal Exception? PublicationFailure { get; private set; }
        internal Exception? ReleaseFailure { get; private set; }
        internal Exception? ObservationFailure { get; private set; }
        internal Exception? IndependentFailure { get; private set; }
        internal LiveEventObservationPrerequisite? ObservationPrerequisite { get; init; }
        internal bool CanToleratePrepublicationFailure => Published is null && PublicationFailure is not null
            && !OwnedProviderEventForwarding.HasRetention(PublicationFailure) && ReleaseFailure is null
            && ObservationFailure is null && IndependentFailure is null && _retentionFailures.Count == 0;

        internal Task CompleteAsync(Func<Action<RuntimePluginAgentEventEnvelope>, Task> publish,
            Action releasePublicationUse, Func<RuntimePluginAgentEventEnvelope, Task> observe,
            Func<Task> independent, Action<Exception, object> retainDependencies)
        {
            ArgumentNullException.ThrowIfNull(dependencies);
            ArgumentNullException.ThrowIfNull(publish);
            ArgumentNullException.ThrowIfNull(releasePublicationUse);
            ArgumentNullException.ThrowIfNull(observe);
            ArgumentNullException.ThrowIfNull(independent);
            ArgumentNullException.ThrowIfNull(retainDependencies);
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("A live publication invocation can only be launched once.");
            InvocationDependencies = new { Owner = dependencies, Publish = publish, Release = releasePublicationUse,
                Observe = observe, Independent = independent, Retain = retainDependencies };
            Completion = CoreAsync(publish, releasePublicationUse, observe, independent, retainDependencies);
            _launch.TrySetResult();
            return Completion;
        }

        private void MarkPublished(RuntimePluginAgentEventEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            if (Published is not null) throw new InvalidOperationException("A publication was already recorded.");
            Published = envelope;
        }

        private async Task CoreAsync(Func<Action<RuntimePluginAgentEventEnvelope>, Task> publish,
            Action releasePublicationUse, Func<RuntimePluginAgentEventEnvelope, Task> observe,
            Func<Task> independent, Action<Exception, object> retainDependencies)
        {
            await _launch.Task.ConfigureAwait(false);
            try
            {
                PublicationOriginal = publish(MarkPublished) ?? throw new InvalidOperationException("Publication returned no original.");
                await PublicationOriginal.ConfigureAwait(false);
            }
            catch (Exception failure) { PublicationFailure = CaptureFailure(PublicationOriginal, failure); }
            // Await selects only one fault. Capture/classify the actual complete graph before a
            // required use can be released or an observer can touch its dependent acquisitions.
            var publicationRetained = PublicationFailure is not null && OwnedProviderEventForwarding.HasRetention(PublicationFailure);
            RecordRetention(PublicationFailure);
            if (!publicationRetained)
            {
                try { releasePublicationUse(); }
                catch (Exception failure) { ReleaseFailure = failure; }
            }
            RecordRetention(ObservationPrerequisite?.Failure);

            // A failed required gate/use release is not permission to invoke a plugin under that gate.
            if (!publicationRetained && ReleaseFailure is null && _retentionFailures.Count == 0
                && ObservationPrerequisite?.MayObserve != false && Published is { } envelope)
            {
                try
                {
                    ObservationOriginal = observe(envelope) ?? throw new InvalidOperationException("Observation returned no original.");
                    await ObservationOriginal.ConfigureAwait(false);
                }
                catch (Exception failure) { ObservationFailure = CaptureFailure(ObservationOriginal, failure); }
            }

            RecordRetention(ObservationFailure);
            if (ReleaseFailure is not null)
                RecordRetention(new AgentDependencyRetentionException("live plugin event", "publication release", [ReleaseFailure], this));

            // Plugin failure must not skip independent cache, queue or parent-notification work.
            try
            {
                IndependentOriginal = independent() ?? throw new InvalidOperationException("Independent event work returned no original.");
                await IndependentOriginal.ConfigureAwait(false);
            }
            catch (Exception failure) { IndependentFailure = CaptureFailure(IndependentOriginal, failure); }
            var failures = new[] { PublicationFailure, ReleaseFailure, ObservationFailure }
                .Concat(_retentionFailures).Append(IndependentFailure)
                .Where(static failure => failure is not null).Select(static failure => failure!).ToArray();
            if (ReleaseFailure is not null || failures.Any(OwnedProviderEventForwarding.HasRetention))
                throw new AgentDependencyRetentionException("live plugin event", "retained prerequisite", failures,
                    new { Owner = dependencies, Publication = this });
            if (failures.Length == 1) ExceptionDispatchInfo.Throw(failures[0]);
            if (failures.Length > 1) throw new AggregateException(failures);

            void RecordRetention(Exception? failure)
            {
                if (failure is null || !OwnedProviderEventForwarding.HasRetention(failure)) return;
                try { retainDependencies(failure, this); }
                catch (Exception controlFailure) { _retentionFailures.Add(controlFailure); }
            }
        }

        private Exception CaptureFailure(Task? original, Exception failure)
        {
            var faults = original?.Exception;
            _outcomes.Add((original, failure, faults));
            return faults is { InnerExceptions.Count: > 1 } ? faults : failure;
        }
    }
}
