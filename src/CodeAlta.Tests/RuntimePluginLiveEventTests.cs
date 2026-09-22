extern alias TuiApp;

using CodeAlta.Agent;
using CodeAlta.Hosting;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Views;

namespace CodeAlta.Tests;

/// <summary>Inert regressions for live-event release decisions; no runtime or filesystem startup.</summary>
[TestClass]
public sealed class RuntimePluginLiveEventTests
{
    [TestMethod]
    [DataRow(false, false)] [DataRow(false, true)] [DataRow(true, false)] [DataRow(true, true)]
    public async Task ConnectedShutdown_ClosureJoinOwnsAllOriginalsAndOrderedGraphs(bool pluginMarker, bool retained)
    {
        var selected = new IOException("selected closure");
        var shared = new OperationCanceledException("shared closure");
        var sibling = retained ? CreateDeferredMarker(pluginMarker, shared) : (Exception)shared;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.FromException(shared);
        var firstOutcome = ObserveAsync(first.Task);
        var secondOutcome = ObserveAsync(second);
        var enumerated = 0;
        IEnumerable<Task> Originals() { enumerated++; yield return first.Task; enumerated++; yield return second; }
        var join = SessionPermissionService.JoinDeliveryOriginalsAsync(Originals());
        var outcome = ObserveAsync(join);
        var launchedAll = enumerated == 2 && !join.IsCompleted;
        first.SetException([selected, sibling, shared, shared]);
        var failure = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(selected, await firstOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(shared, await secondOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(launchedAll);
        Assert.IsInstanceOfType<AggregateException>(failure);
        Assert.AreEqual(retained, OwnedProviderEventForwarding.HasRetention(failure));
        var errors = ((AggregateException)failure).InnerExceptions;
        Assert.AreEqual(2, errors.Count);
        CollectionAssert.AreEqual(new Exception[] { selected, sibling, shared, shared }, ((AggregateException)errors[0]).InnerExceptions.ToArray());
        Assert.AreSame(shared, errors[1]);
        if (retained)
        {
            var receipts = (SessionPermissionService.DeliveryStage[])((AgentDependencyRetentionException)failure).Dependencies;
            Assert.AreSame(first.Task, receipts[0].Original);
            Assert.AreSame(second, receipts[1].Original);
            Assert.AreSame(selected, await receipts[0].Outcome);
            Assert.AreSame(receipts[0].OriginalFaults, errors[0]);
        }
    }

    [TestMethod]
    [DataRow(0, false)] [DataRow(0, true)] [DataRow(1, false)] [DataRow(1, true)]
    [DataRow(2, false)] [DataRow(2, true)]
    public async Task ConnectedShutdown_QueueClosureStartsControlsAndKeepsQueryPermissionDrainOrder(int queryKind, bool pluginMarker)
    {
        var queryError = new IOException("query");
        var permissionError = new OperationCanceledException("permission");
        var drainError = new IOException("drain");
        var retained = CreateDeferredMarker(pluginMarker, permissionError);
        var permission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new TaskCompletionSource<Task?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permissionOutcome = ObserveAsync(permission.Task);
        var drainOutcome = ObserveAsync(drain.Task);
        var queryOutcome = ObserveAsync(query.Task);
        var calls = new List<string>();
        var owner = new SessionRuntimeService.AttachmentClosureJoin(new object());
        var operation = owner.RunAsync(() => { calls.Add("permissions"); return permission.Task; }, () =>
        {
            calls.Add("query/stop");
            if (queryKind == 2) throw queryError;
            return query.Task;
        });
        var outcome = ObserveAsync(operation);
        var controlsStarted = calls.SequenceEqual(new[] { "permissions", "query/stop" }) && !operation.IsCompleted;
        if (queryKind == 1) query.SetException([queryError, retained, queryError]);
        else query.SetResult(drain.Task);
        permission.SetException([permissionError, retained, permissionError]);
        drain.SetException([drainError, retained, drainError]);
        var actual = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        await queryOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        await permissionOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        await drainOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(controlsStarted);
        Assert.IsInstanceOfType<AgentDependencyRetentionException>(actual);
        var failures = ((AgentDependencyRetentionException)actual).InnerExceptions;
        Assert.AreEqual(2, failures.Count);
        Assert.AreSame(permission.Task, owner.Permissions.Original);
        Assert.AreSame(permissionError, owner.Permissions.AwaitedFailure);
        Assert.AreSame(owner.Permissions.OriginalFaults, failures[queryKind == 0 ? 0 : 1]);
        if (queryKind == 0)
        {
            Assert.AreSame(query.Task, owner.QueryOriginal);
            Assert.AreSame(drain.Task, owner.QueueDrain!.Original);
            Assert.AreSame(owner.QueueDrain.OriginalFaults, failures[1]);
        }
        else
        {
            Assert.AreSame(queryError, owner.QueryAwaitedFailure);
            Assert.IsNull(owner.QueueDrain);
            if (queryKind == 2) { Assert.IsNull(owner.QueryOriginal); Assert.AreSame(queryError, failures[0]); }
            else { Assert.AreSame(query.Task, owner.QueryOriginal); Assert.AreSame(owner.QueryOriginalFaults, failures[0]); }
        }
    }

    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)]
    public async Task ConnectedShutdown_QueueClosureOrdinaryAndAbsentDrainAreNotRetention(int queryKind)
    {
        var error = new OperationCanceledException("ordinary");
        var original = Task.FromException(error);
        var originalOutcome = ObserveAsync(original);
        var owner = new SessionRuntimeService.AttachmentClosureJoin(new object());
        Task<Task?>? query = queryKind == 2 ? null : Task.FromResult<Task?>(queryKind == 0 ? original : null);
        var actual = await ObserveAsync(owner.RunAsync(() => queryKind == 0 ? Task.CompletedTask : original, () => query)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(error, await originalOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(error, actual);
        Assert.IsFalse(OwnedProviderEventForwarding.HasRetention(actual!));
        Assert.AreSame(query, owner.QueryOriginal);
        if (queryKind == 0) Assert.AreSame(original, owner.QueueDrain!.Original); else Assert.IsNull(owner.QueueDrain);
    }

    [TestMethod]
    [DataRow(0, 0)] [DataRow(0, 1)] [DataRow(0, 2)] [DataRow(0, 3)]
    [DataRow(1, 0)] [DataRow(1, 1)] [DataRow(1, 2)] [DataRow(1, 3)]
    public async Task ConnectedShutdown_AbortPrerequisitesRetainOnlyUnconfirmedDependencies(int stage, int kind)
    {
        var selected = new OperationCanceledException("abort selected");
        var marker = CreateDeferredMarker(kind == 2, selected);
        var original = kind == 3 ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalOutcome = original is null ? null : ObserveAsync(original.Task);
        if (kind == 0) original!.SetException(selected);
        else original?.SetException([selected, marker, selected]);
        Task Invoke() { if (original is null) throw selected; return original.Task; }
        var owner = new SessionRuntimeService.RuntimeAbortLifetime(new object());
        var aborts = 0;
        var token = new CancellationToken(canceled: true);
        var actual = await ObserveAsync(owner.RunAsync(() => stage == 0 ? Invoke() : Task.CompletedTask,
            execution => { aborts++; Assert.IsTrue(execution.IsCancellationRequested); return Invoke(); }, token, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
        if (originalOutcome is not null) Assert.AreSame(selected, await originalOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNotNull(actual);
        Assert.AreEqual(kind != 0, OwnedProviderEventForwarding.HasRetention(actual));
        Assert.AreEqual(stage, aborts);
        Assert.AreEqual(stage == 1 && kind == 0, owner.SourceReleased);
        if (stage == 0) Assert.IsNull(owner.Source); else Assert.IsNotNull(owner.Source);
        var receipt = stage == 0 ? owner.Permissions : owner.Abort;
        Assert.AreSame(original?.Task, receipt.Original);
        Assert.AreSame(selected, receipt.AwaitedFailure);
        if (kind == 0) Assert.AreSame(selected, actual);
        else if (original is not null)
            Assert.AreSame(receipt.OriginalFaults, ((AgentDependencyRetentionException)actual).InnerExceptions[0]);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ConnectedShutdown_MissingPermissionOriginalStillStartsQueueQuery(bool nullReturn)
    {
        var error = new IOException("permission invocation");
        var query = Task.FromResult<Task?>(null);
        var owner = new SessionRuntimeService.AttachmentClosureJoin(new object());
        var calls = new List<string>();
        var actual = await ObserveAsync(owner.RunAsync(() =>
        {
            calls.Add("permissions");
            if (nullReturn) return null!;
            throw error;
        }, () => { calls.Add("query"); return query; })).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<AgentDependencyRetentionException>(actual);
        CollectionAssert.AreEqual(new[] { "permissions", "query" }, calls);
        Assert.IsNull(owner.Permissions.Original);
        Assert.AreSame(query, owner.QueryOriginal);
        Assert.IsNull(owner.QueueDrain);
        if (!nullReturn) Assert.AreSame(error, owner.Permissions.AwaitedFailure);
        Assert.AreSame(owner.Permissions.AwaitedFailure, ((AgentDependencyRetentionException)actual).InnerExceptions[0]);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ConnectedShutdown_QueryFailureIsUnconfirmedButCanceledPermissionOriginalIsOrdinary(bool failQuery)
    {
        var token = new CancellationToken(canceled: true);
        var permissions = Task.FromCanceled(token);
        var permissionsOutcome = ObserveAsync(permissions);
        var error = new IOException("unknown queue drainage");
        var query = new TaskCompletionSource<Task?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryOutcome = ObserveAsync(query.Task);
        if (failQuery) query.SetException([error, error]); else query.SetResult(null);
        var owner = new SessionRuntimeService.AttachmentClosureJoin(new object());
        var actual = await ObserveAsync(owner.RunAsync(() => permissions, () => query.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        await permissionsOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        await queryOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(actual);
        Assert.AreEqual(failQuery, OwnedProviderEventForwarding.HasRetention(actual));
        Assert.IsTrue(permissions.IsCanceled);
        Assert.AreSame(permissions, owner.Permissions.Original);
        Assert.IsNull(owner.Permissions.OriginalFaults);
        Assert.IsNull(owner.QueueDrain);
        if (failQuery)
        {
            var failures = ((AgentDependencyRetentionException)actual).InnerExceptions;
            Assert.AreEqual(2, failures.Count);
            Assert.AreSame(owner.QueryOriginalFaults, failures[0]);
            CollectionAssert.AreEqual(new Exception[] { error, error }, ((AggregateException)failures[0]).InnerExceptions.ToArray());
            Assert.AreSame(owner.Permissions.AwaitedFailure, failures[1]);
        }
        else Assert.AreSame(owner.Permissions.AwaitedFailure, actual);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ConnectedShutdown_AbortPendingOriginalNeverAuthorizesEarlyRelease(bool permissionStage)
    {
        var original = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalOutcome = ObserveAsync(original.Task);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = new OperationCanceledException("terminal ordinary graph");
        Task Invoke() { entered.TrySetResult(); return original.Task; }
        var owner = new SessionRuntimeService.RuntimeAbortLifetime(new object());
        var operation = owner.RunAsync(() => permissionStage ? Invoke() : Task.CompletedTask,
            _ => Invoke(), CancellationToken.None, CancellationToken.None);
        var outcome = ObserveAsync(operation);
        Exception? deadline = null;
        var pending = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pending = !operation.IsCompleted && !owner.SourceReleased;
        }
        catch (Exception failure) { deadline = failure; }
        finally { original.TrySetException([shared, shared]); }
        var actual = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        await originalOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        if (deadline is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(deadline);
        Assert.IsTrue(pending);
        Assert.IsInstanceOfType<AggregateException>(actual);
        Assert.IsFalse(OwnedProviderEventForwarding.HasRetention(actual));
        CollectionAssert.AreEqual(new Exception[] { shared, shared }, ((AggregateException)actual).InnerExceptions.ToArray());
        Assert.AreEqual(!permissionStage, owner.SourceReleased);
        Assert.AreSame(original.Task, (permissionStage ? owner.Permissions : owner.Abort).Original);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OuterCorrection_SynchronousAndTaskFailuresRemainOrdinary(bool synchronous)
    {
        var error = new OperationCanceledException("cleanup");
        var original = synchronous ? null : Task.FromException(error);
        var originalOutcome = original is null ? null : ObserveAsync(original);
        ValueTask Cleanup() { if (synchronous) throw error; return new ValueTask(original!); }
        var hostCalls = new List<string>();
        var host = CodeAltaHost.CreateHostDisposal(Cleanup,
            () => { hostCalls.Add("hub"); return ValueTask.CompletedTask; },
            () => { hostCalls.Add("registry"); return ValueTask.CompletedTask; },
            () => { hostCalls.Add("plugins"); return ValueTask.CompletedTask; }, () => hostCalls.Add("logging"), true, true);
        var hostOutcome = ObserveAsync(host.Value);
        var hostFailure = await hostOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(hostFailure);
        Assert.IsFalse(OwnedProviderEventForwarding.HasRetention(hostFailure));
        CollectionAssert.AreEqual(new[] { "hub", "registry", "plugins", "logging" }, hostCalls);
        var outerCalls = new List<string>();
        var outer = CodeAltaOwnedServices.CreateOwnedServicesDisposal(Cleanup,
            () => { outerCalls.Add("metadata"); return ValueTask.CompletedTask; }, () => outerCalls.Add("logging"), true);
        var outerOutcome = ObserveAsync(outer.Value);
        var outerFailure = await outerOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(outerFailure);
        Assert.IsFalse(OwnedProviderEventForwarding.HasRetention(outerFailure));
        CollectionAssert.AreEqual(new[] { "metadata", "logging" }, outerCalls);
        Assert.AreSame(error, hostFailure);
        Assert.AreSame(error, outerFailure);
        if (originalOutcome is not null) Assert.AreSame(error, await originalOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public async Task OuterCorrection_LoggingFailureIsOrdinaryAndControlsLaunchBeforeJoins()
    {
        var shared = new OperationCanceledException("logging");
        var logging = new AggregateException(shared, shared);
        var host = CodeAltaHost.CreateHostDisposal(() => ValueTask.CompletedTask, () => ValueTask.CompletedTask,
            () => ValueTask.CompletedTask, () => ValueTask.CompletedTask, () => throw logging, true, true);
        var outer = CodeAltaOwnedServices.CreateOwnedServicesDisposal(() => ValueTask.CompletedTask,
            () => ValueTask.CompletedTask, () => throw logging, true);
        Assert.AreSame(logging, await ObserveAsync(host.Value).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(logging, await ObserveAsync(outer.Value).WaitAsync(TimeSpan.FromSeconds(10)));
        var reads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readsOutcome = ObserveAsync(reads.Task);
        var commandsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var commandFailure = new IOException("command invocation");
        var operation = CodeAltaHost.DisposeOwnedWorkAsync(
            () => { calls.Add("commands"); commandsEntered.TrySetResult(); throw commandFailure; },
            () => { calls.Add("reads"); return reads.Task; },
            () => { calls.Add("runtime release"); return ValueTask.CompletedTask; },
            _ => { calls.Add("retained drain"); return ValueTask.CompletedTask; },
            () => { calls.Add("plugins"); return Task.CompletedTask; }).AsTask();
        var outcome = ObserveAsync(operation);
        Exception? deadline = null;
        try { await commandsEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) { deadline = error; }
        finally { reads.TrySetResult(); }
        var actual = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        await readsOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        if (deadline is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(deadline);
        Assert.IsNotNull(actual);
        Assert.IsFalse(OwnedProviderEventForwarding.HasRetention(actual));
        CollectionAssert.AreEqual(new[] { "reads", "commands", "plugins", "runtime release" }, calls);
    }

    [TestMethod]
    [DataRow(0, 0)] [DataRow(0, 1)] [DataRow(0, 2)] [DataRow(0, 3)]
    [DataRow(1, 0)] [DataRow(1, 1)] [DataRow(1, 2)] [DataRow(1, 3)]
    [DataRow(2, 0)] [DataRow(2, 1)] [DataRow(2, 2)] [DataRow(2, 3)]
    public async Task OuterCorrection_CoordinatorOwnsSendAbortAndCleanupGraphs(int stage, int kind)
    {
        var selected = new IOException("selected");
        var shared = new OperationCanceledException("shared");
        var sibling = kind >= 2 ? CreateDeferredMarker(kind == 3, shared) : (Exception)shared;
        var source = kind == 0 ? null : new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceOutcome = source is null ? null : ObserveAsync(source.Task);
        source?.SetException([selected, sibling, shared, shared]);
        Task<int> Invoke() { if (source is null) throw selected; return source.Task; }
        var owner = new AgentHub.CoordinatorFailureOwner(new object());
        var invocation = stage == 2 ? owner.SessionDisposal : new OwnedSessionCommandService.OriginalInvocation();
        var gates = 0;
        Task operation = stage switch
        {
            0 => owner.RunAsync(invocation, Invoke),
            1 => owner.AbortAsync(invocation, () => Invoke()),
            _ => owner.DisposeAsync(() => new ValueTask(Invoke()), () => gates++),
        };
        var actual = await ObserveAsync(operation).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(actual);
        Assert.AreSame(selected, invocation.AwaitedFailure);
        if (source is null) Assert.IsNull(invocation.Original);
        else
        {
            Assert.AreSame(source.Task, invocation.Original);
            Assert.AreSame(selected, await sourceOutcome!.WaitAsync(TimeSpan.FromSeconds(10)));
            CollectionAssert.AreEqual(new Exception[] { selected, sibling, shared, shared }, invocation.OriginalFaults!.InnerExceptions.ToArray());
        }
        var retained = kind >= 2 || kind == 0 && stage != 0;
        Assert.AreEqual(retained, owner.Retained);
        Assert.AreEqual(retained, OwnedProviderEventForwarding.HasRetention(actual));
        if (source is not null)
            Assert.AreSame(invocation.OriginalFaults, stage == 2 && retained
                ? ((AgentDependencyRetentionException)actual).InnerExceptions[0] : actual);
        if (retained)
        {
            var newInvocation = new OwnedSessionCommandService.OriginalInvocation();
            var next = ObserveAsync(owner.RunAsync<int>(newInvocation, () => throw new InvalidOperationException("must not enter")));
            Assert.IsTrue(OwnedProviderEventForwarding.HasRetention((await next.WaitAsync(TimeSpan.FromSeconds(10)))!));
            Assert.IsNull(newInvocation.Original);
        }
        if (stage == 2)
        {
            Assert.AreEqual(retained ? 0 : 1, gates);
            Assert.AreSame(operation, owner.DisposeAsync(() => throw new InvalidOperationException("retry"), () => gates++));
        }
    }

    [TestMethod]
    [DataRow(0, false)] [DataRow(0, true)]
    [DataRow(1, false)] [DataRow(1, true)]
    [DataRow(2, false)] [DataRow(2, true)]
    public async Task OuterCorrection_EntryMissingCleanupOriginalNeverReleasesDependencies(int stage, bool synchronous)
    {
        var error = new OperationCanceledException("cleanup original");
        var original = synchronous ? null : Task.FromException(error);
        var originalOutcome = original is null ? null : ObserveAsync(original);
        Task Cleanup() { if (synchronous) throw error; return original!; }
        var calls = new List<string>();
        var entry = new AgentHub.SessionEntryLifetime(new object(),
            () => { calls.Add("abort"); return stage == 0 ? Cleanup() : Task.CompletedTask; },
            () => { calls.Add("session"); return stage == 1 ? new ValueTask(Cleanup()) : ValueTask.CompletedTask; },
            () => { calls.Add("provider"); return stage == 2 ? new ValueTask(Cleanup()) : ValueTask.CompletedTask; });
        Assert.IsTrue(entry.TryAddReference());
        var disposal = entry.DisposeAsync().AsTask();
        var outcome = ObserveAsync(disposal);
        Assert.IsFalse(disposal.IsCompleted, "An actual active reference must drain first.");
        entry.CompleteReference(null, null);
        var failure = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(failure);
        Assert.AreEqual(synchronous, OwnedProviderEventForwarding.HasRetention(failure));
        Assert.AreEqual(!synchronous, entry.DependenciesReleased);
        Assert.AreEqual(0, entry.ActiveReferences);
        CollectionAssert.AreEqual(synchronous ? new[] { "abort", "session", "provider" }[..(stage + 1)] : new[] { "abort", "session", "provider" }, calls);
        if (originalOutcome is not null) Assert.AreSame(error, await originalOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        var receipt = stage switch { 0 => entry.Abort, 1 => entry.SessionDisposal, _ => entry.ProviderDisposal };
        if (synchronous) Assert.IsNull(receipt!.Original); else Assert.AreSame(original, receipt!.Original);
        Assert.AreSame(disposal, entry.DisposeAsync().AsTask());
    }

    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)]
    public async Task OuterCorrection_ForwarderPermissionPrerequisiteGatesActors(int kind)
    {
        var shared = new OperationCanceledException("permission");
        var source = kind == 0 ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceOutcome = source is null ? null : ObserveAsync(source.Task);
        source?.SetException(kind >= 2 ? [shared, CreateDeferredMarker(kind == 3, shared), shared] : [shared, shared]);
        var calls = new List<string>();
        var owner = new OwnedProviderEventForwarding();
        var close = owner.CloseAsync(() => { if (source is null) throw shared; return source.Task; },
            () => { calls.Add("actors"); return Task.CompletedTask; }, () => calls.Add("events"));
        var failure = await ObserveAsync(close).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(failure);
        Assert.AreEqual(kind != 1, OwnedProviderEventForwarding.HasRetention(failure));
        CollectionAssert.AreEqual(kind == 1 ? new[] { "actors", "events" } : Array.Empty<string>(), calls);
        Assert.AreEqual(0, owner.ActiveWorkCount);
        if (sourceOutcome is not null) Assert.AreSame(shared, await sourceOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(close, owner.CloseAsync(() => throw new InvalidOperationException("retry"), () => Task.CompletedTask, () => { }));
    }

    [TestMethod]
    [DataRow(false, false)] [DataRow(false, true)] [DataRow(true, false)] [DataRow(true, true)]
    public async Task OuterCorrection_HostAndFrontendKeepReturnedOrderedGraphs(bool pluginMarker, bool retained)
    {
        var selected = new IOException("selected");
        var shared = new OperationCanceledException("shared");
        var sibling = retained ? CreateDeferredMarker(pluginMarker, shared) : (Exception)shared;
        var original = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalOutcome = ObserveAsync(original.Task);
        original.SetException([selected, sibling, shared, shared]);
        var hostReleases = 0;
        var outerReleases = 0;
        var host = CodeAltaHost.CreateHostDisposal(() => new ValueTask(original.Task),
            () => { hostReleases++; return ValueTask.CompletedTask; }, () => ValueTask.CompletedTask,
            () => ValueTask.CompletedTask, () => { }, true, true);
        var outer = CodeAltaOwnedServices.CreateOwnedServicesDisposal(() => new ValueTask(original.Task),
            () => { outerReleases++; return ValueTask.CompletedTask; }, () => { }, true);
        var hostFailure = await ObserveAsync(host.Value).WaitAsync(TimeSpan.FromSeconds(10));
        var outerFailure = await ObserveAsync(outer.Value).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(selected, await originalOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        foreach (var failure in new[] { hostFailure, outerFailure })
        {
            Assert.IsNotNull(failure);
            Assert.AreEqual(retained, OwnedProviderEventForwarding.HasRetention(failure));
            var graph = retained ? ((AgentDependencyRetentionException)failure).InnerExceptions[0] : failure;
            Assert.IsInstanceOfType<AggregateException>(graph);
            CollectionAssert.AreEqual(new Exception[] { selected, sibling, shared, shared }, ((AggregateException)graph).InnerExceptions.ToArray());
        }
        Assert.AreEqual(retained ? 0 : 1, hostReleases);
        Assert.AreEqual(retained ? 0 : 1, outerReleases);
    }

    [TestMethod]
    public async Task OuterCorrection_NullCleanupTasksRemainOrdinaryAtHostBoundary()
    {
        var stage = new CodeAltaHost.HostDisposalStage(() => null!);
        stage.Launch();
        var hostFailure = await stage.ReportedOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNull(stage.Original);
        Assert.IsNotNull(stage.InvocationFailure);
        Assert.IsFalse(OwnedProviderEventForwarding.HasRetention(hostFailure!));
        var owner = new AgentHub.CoordinatorFailureOwner(new object());
        var abort = new OwnedSessionCommandService.OriginalInvocation();
        var abortFailure = await ObserveAsync(owner.AbortAsync(abort, () => null!)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNull(abort.Original);
        Assert.IsTrue(owner.Retained);
        Assert.IsTrue(OwnedProviderEventForwarding.HasRetention(abortFailure!));
        var releases = 0;
        var entry = new AgentHub.SessionEntryLifetime(new object(), () => null!,
            () => { releases++; return ValueTask.CompletedTask; }, () => { releases++; return ValueTask.CompletedTask; });
        Assert.IsTrue(entry.TryAddReference());
        var entryOutcome = ObserveAsync(entry.DisposeAsync().AsTask());
        entry.CompleteReference(null, null);
        Assert.IsTrue(OwnedProviderEventForwarding.HasRetention((await entryOutcome.WaitAsync(TimeSpan.FromSeconds(10)))!));
        Assert.IsNull(entry.Abort!.Original);
        Assert.IsFalse(entry.DependenciesReleased);
        var forwarding = new OwnedProviderEventForwarding();
        var close = forwarding.CloseAsync(() => null!, () => { releases++; return Task.CompletedTask; }, () => releases++);
        Assert.IsTrue(OwnedProviderEventForwarding.HasRetention((await ObserveAsync(close).WaitAsync(TimeSpan.FromSeconds(10)))!));
        Assert.AreEqual(0, releases);
    }

    [TestMethod]
    [DataRow(0, false)] [DataRow(0, true)]
    [DataRow(1, false)] [DataRow(1, true)]
    [DataRow(2, false)] [DataRow(2, true)]
    public async Task OuterCorrection_CoordinatorSoleFaultAndCanceledOriginalRemainOrdinary(int stage, bool canceled)
    {
        var token = new CancellationToken(canceled: true);
        var error = new OperationCanceledException("sole", token);
        var original = canceled ? Task.FromCanceled<int>(token) : Task.FromException<int>(error);
        var originalOutcome = ObserveAsync(original);
        var owner = new AgentHub.CoordinatorFailureOwner(new object());
        var invocation = stage == 2 ? owner.SessionDisposal : new OwnedSessionCommandService.OriginalInvocation();
        var gates = 0;
        Task operation = stage switch
        {
            0 => owner.RunAsync(invocation, () => original),
            1 => owner.AbortAsync(invocation, () => original),
            _ => owner.DisposeAsync(() => new ValueTask(original), () => gates++),
        };
        var actual = await ObserveAsync(operation).WaitAsync(TimeSpan.FromSeconds(10));
        await originalOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(original, invocation.Original);
        Assert.AreSame(invocation.AwaitedFailure, actual);
        Assert.IsInstanceOfType<OperationCanceledException>(actual);
        Assert.AreEqual(token, ((OperationCanceledException)actual).CancellationToken);
        Assert.AreEqual(canceled, original.IsCanceled);
        Assert.IsFalse(owner.Retained);
        Assert.IsFalse(OwnedProviderEventForwarding.HasRetention(actual));
        Assert.AreEqual(stage == 2 ? 1 : 0, gates);
        if (!canceled) Assert.AreSame(error, actual);
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    [DataRow(3, true)]
    public async Task LifecycleCorrection_ClosingWrappersPreserveMissingOriginalContrast(int route, bool synchronous)
    {
        var failure = new IOException("closing invocation");
        var calls = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = new CorrectionLifecycle(() => Task.CompletedTask, () =>
        {
            calls.Add("failing");
            if (synchronous) throw failure;
            return Task.FromException(failure);
        });
        var independent = new CorrectionLifecycle(() => Task.CompletedTask, () =>
        {
            calls.Add("independent"); entered.TrySetResult(); return other.Task;
        });
        AgentRunLifecycle lifecycle = route switch
        {
            0 => OwnedSessionAskExecution.Combine(failing, independent),
            1 => OwnedSessionAskExecution.Combine(independent, failing),
            2 => new OwnedSessionAskExecution.Lifecycle(independent, failing),
            _ => new OwnedSessionAskExecution.Lifecycle(failing, independent),
        };
        var closing = lifecycle.ClosingAsync(new AgentRunId("inert"));
        var outcome = ObserveAsync(closing);
        Exception? deadline = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) { deadline = error; }
        finally { other.TrySetResult(); }
        var actual = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        if (deadline is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(deadline);
        Assert.IsNotNull(actual);
        var retained = synchronous || route == 3;
        Assert.AreEqual(retained, AgentDependencyRetentionException.Contains(actual));
        Assert.AreEqual(1, calls.Count(value => value == "failing"));
        Assert.AreEqual(1, calls.Count(value => value == "independent"));
        Assert.AreSame(closing, lifecycle.ClosingAsync(new AgentRunId("inert")));
        if (retained)
        {
            var marker = (AgentDependencyRetentionException)actual;
            Assert.AreSame(failure, marker.InnerExceptions.Single());
            var pair = (OwnedSessionAskExecution.ClosingPair)marker.Dependencies;
            var failedOriginal = route is 0 or 3 ? pair.First.Original : pair.Second.Original;
            if (synchronous) Assert.IsNull(failedOriginal); else Assert.IsNotNull(failedOriginal);
            Assert.AreSame(other.Task, route is 0 or 3 ? pair.Second.Original : pair.First.Original);
        }
        else Assert.AreSame(failure, actual);
        var release = new AgentSession.RunReleaseDecision(lifecycle);
        var finish = ObserveAsync(release.FinishAsync(() => closing, () => ValueTask.CompletedTask,
            _ => Task.CompletedTask, () => { }, () => Task.CompletedTask, null));
        var finishFailure = await finish.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(finishFailure);
        if (retained) Assert.IsTrue(AgentDependencyRetentionException.Contains(finishFailure));
        else Assert.AreSame(failure, finishFailure);
        Assert.AreEqual(retained, release.Retained);
        Assert.AreEqual(!retained, release.SourceReleased);
        Assert.AreEqual(!retained, release.SlotReleased);
    }

    [TestMethod]
    public async Task LifecycleCorrection_MissingFirstStillOwnsSecondCompleteFaultGraph()
    {
        var first = new IOException("synchronous first");
        var shared = new OperationCanceledException("shared second");
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOutcome = ObserveAsync(second.Task);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = new OwnedSessionAskExecution.ClosingPair(() => throw first,
            () => { entered.TrySetResult(); return second.Task; });
        var outcome = ObserveAsync(pair.Completion);
        pair.Launch();
        Exception? deadline = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) { deadline = error; }
        finally { second.TrySetException([shared, first, shared]); }
        var failure = (AgentDependencyRetentionException)(await outcome.WaitAsync(TimeSpan.FromSeconds(10)))!;
        Assert.AreSame(shared, await secondOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        if (deadline is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(deadline);
        Assert.IsNull(pair.First.Original);
        Assert.AreSame(second.Task, pair.Second.Original);
        Assert.AreSame(first, await pair.First.Outcome);
        Assert.AreSame(shared, await pair.Second.Outcome);
        Assert.AreSame(first, failure.InnerExceptions[0]);
        Assert.AreSame(pair.Second.OriginalFaults, failure.InnerExceptions[1]);
        CollectionAssert.AreEqual(new Exception[] { shared, first, shared }, pair.Second.OriginalFaults!.InnerExceptions.ToArray());
    }

    [TestMethod]
    [DataRow(0, false, false)]
    [DataRow(0, false, true)]
    [DataRow(1, false, false)]
    [DataRow(1, false, true)]
    [DataRow(1, true, true)]
    [DataRow(2, false, false)]
    [DataRow(2, false, true)]
    [DataRow(2, true, true)]
    [DataRow(3, false, false)]
    [DataRow(3, true, true)]
    [DataRow(4, false, false)]
    [DataRow(4, false, true)]
    [DataRow(4, true, true)]
    [DataRow(5, false, false)]
    [DataRow(5, false, true)]
    [DataRow(5, true, true)]
    public async Task LifecycleCorrection_DirectAndNestedStartKeepFullGraphsAndSelectedPublicationError(int route, bool pluginMarker, bool retained)
    {
        Exception selected = route >= 4 ? new OperationCanceledException("selected start cancellation") : new IOException("selected start");
        var shared = new OperationCanceledException("shared start");
        var sibling = retained ? CreateDeferredMarker(pluginMarker, shared) : (Exception)shared;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startOutcome = ObserveAsync(start.Task);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterStarts = 0;
        var failing = new CorrectionLifecycle(() => { entered.TrySetResult(); return start.Task; }, () => Task.CompletedTask);
        var later = new CorrectionLifecycle(() => { laterStarts++; return Task.CompletedTask; }, () => Task.CompletedTask);
        AgentRunLifecycle lifecycle = route switch
        {
            0 => failing,
            1 => OwnedSessionAskExecution.Combine(failing, later),
            2 => new OwnedSessionAskExecution.Lifecycle(later, OwnedSessionAskExecution.Combine(failing, later)),
            3 => new OwnedSessionAskExecution.Lifecycle(later, failing),
            4 => OwnedSessionAskExecution.Combine(later, failing),
            _ => new OwnedSessionAskExecution.Lifecycle(failing, later),
        };
        var invocation = new AgentSession.RunStartInvocation();
        var completion = invocation.RunAsync(lifecycle, new AgentRunId("inert"), CancellationToken.None);
        var outcome = ObserveAsync(completion);
        Exception? deadline = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) { deadline = error; }
        finally { start.TrySetException([selected, sibling, shared, shared]); }
        var actual = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(selected, await startOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        if (deadline is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(deadline);
        Assert.IsNotNull(actual);
        Assert.AreSame(selected, invocation.PublicationFailure);
        Assert.AreSame(invocation.Failure, actual);
        Assert.AreEqual(route >= 4 ? 1 : 0, laterStarts);
        var leaf = invocation;
        while (leaf.Invocation is AgentSession.IRunStartEvidence evidence && evidence.FailedStart is { } nested) leaf = nested;
        Assert.AreSame(start.Task, leaf.Original);
        Assert.AreSame(selected, leaf.AwaitedFailure);
        CollectionAssert.AreEqual(new Exception[] { selected, sibling, shared, shared }, leaf.OriginalFaults!.InnerExceptions.ToArray());
        var release = new AgentSession.RunReleaseDecision(invocation);
        var finish = ObserveAsync(release.FinishAsync(() => lifecycle.ClosingAsync(new AgentRunId("inert")),
            () => ValueTask.CompletedTask, _ => Task.CompletedTask, () => { }, () => Task.CompletedTask, actual));
        var cleanup = await finish.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(retained, cleanup is not null);
        Assert.AreEqual(retained, release.Retained);
        Assert.AreEqual(!retained, release.SourceReleased);
        Assert.AreEqual(!retained, release.SlotReleased);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LifecycleCorrection_StartSuccessOrderAndSynchronousOceOriginal(bool synchronous)
    {
        var selected = new OperationCanceledException("ordinary start cancellation");
        // A synchronous invocation failure has no original, even in the fixture inventory.
        var original = synchronous ? null : Task.FromException(selected);
        var originalOutcome = original is null ? null : ObserveAsync(original);
        var calls = new List<string>();
        var first = new CorrectionLifecycle(() => { calls.Add("first"); return Task.CompletedTask; }, () => Task.CompletedTask);
        var second = new CorrectionLifecycle(() => { calls.Add("second"); if (synchronous) throw selected; return original!; }, () => Task.CompletedTask);
        var ask = new CorrectionLifecycle(() => { calls.Add("ask"); return Task.CompletedTask; }, () => Task.CompletedTask);
        var lifecycle = new OwnedSessionAskExecution.Lifecycle(ask, OwnedSessionAskExecution.Combine(first, second));
        var invocation = new AgentSession.RunStartInvocation();
        var outcome = ObserveAsync(invocation.RunAsync(lifecycle, new AgentRunId("inert"), CancellationToken.None));
        Assert.AreSame(selected, await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        if (originalOutcome is not null) Assert.AreSame(selected, await originalOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(selected, invocation.PublicationFailure);
        var leaf = invocation;
        while (leaf.Invocation is AgentSession.IRunStartEvidence evidence && evidence.FailedStart is { } nested) leaf = nested;
        if (synchronous) Assert.IsNull(leaf.Original); else Assert.AreSame(original, leaf.Original);
        Assert.AreSame(selected, leaf.AwaitedFailure);
        CollectionAssert.AreEqual(new[] { "first", "second" }, calls);
        var release = new AgentSession.RunReleaseDecision(invocation);
        var finish = ObserveAsync(release.FinishAsync(() => lifecycle.ClosingAsync(new AgentRunId("inert")),
            () => ValueTask.CompletedTask, _ => Task.CompletedTask, () => { }, () => Task.CompletedTask, selected));
        Assert.IsNull(await finish.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(release.SourceReleased);
        Assert.IsTrue(release.SlotReleased);
    }

    private sealed class CorrectionLifecycle(Func<Task> start, Func<Task> close) : AgentRunLifecycle
    {
        public override Task StartedAsync(AgentRunId runId, CancellationToken executionToken) => start();
        public override Task ClosingAsync(AgentRunId runId) => close();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LifecycleCorrection_NullPreviousClosingOriginalIsNotReplaced(bool previousWrapper)
    {
        // Deliberately violate the hook contract: no substitute Task may hide this missing receipt.
        var invalid = new CorrectionLifecycle(() => Task.CompletedTask, () => null!);
        var independent = new CorrectionLifecycle(() => Task.CompletedTask, () => Task.CompletedTask);
        AgentRunLifecycle lifecycle = previousWrapper
            ? new OwnedSessionAskExecution.Lifecycle(independent, invalid)
            : OwnedSessionAskExecution.Combine(independent, invalid);
        var outcome = ObserveAsync(lifecycle.ClosingAsync(new AgentRunId("inert")));
        var failure = (AgentDependencyRetentionException)(await outcome.WaitAsync(TimeSpan.FromSeconds(10)))!;
        var pair = (OwnedSessionAskExecution.ClosingPair)failure.Dependencies;
        Assert.IsNotNull(pair.First.Original);
        Assert.IsNull(pair.Second.Original);
        Assert.IsNotNull(await pair.Second.Outcome);
    }

    [TestMethod]
    public async Task LifecycleCorrection_AllSuccessfulStartsPreserveOrderAndActualInvocation()
    {
        var calls = new List<string>();
        var first = new CorrectionLifecycle(() => { calls.Add("first"); return Task.CompletedTask; }, () => Task.CompletedTask);
        var second = new CorrectionLifecycle(() => { calls.Add("second"); return Task.CompletedTask; }, () => Task.CompletedTask);
        var ask = new CorrectionLifecycle(() => { calls.Add("ask"); return Task.CompletedTask; }, () => Task.CompletedTask);
        var lifecycle = new OwnedSessionAskExecution.Lifecycle(ask, OwnedSessionAskExecution.Combine(first, second));
        var invocation = new AgentSession.RunStartInvocation();
        var completion = invocation.RunAsync(lifecycle, new AgentRunId("inert"), CancellationToken.None);
        var outcome = ObserveAsync(completion);
        Assert.IsNull(await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(completion, invocation.Completion);
        Assert.AreSame(lifecycle, invocation.Invocation);
        Assert.IsNotNull(invocation.Original);
        Assert.IsNull(invocation.Failure);
        CollectionAssert.AreEqual(new[] { "first", "second", "ask" }, calls);
    }

    [TestMethod]
    public async Task LifecycleCorrection_CanceledOriginalStaysOwnedWithoutBecomingRetention()
    {
        var canceled = Task.FromCanceled(new CancellationToken(canceled: true));
        var inner = new CorrectionLifecycle(() => canceled, () => Task.CompletedTask);
        var skipped = new CorrectionLifecycle(() => throw new InvalidOperationException("must not start"), () => Task.CompletedTask);
        var lifecycle = new OwnedSessionAskExecution.Lifecycle(skipped, inner);
        var invocation = new AgentSession.RunStartInvocation();
        var outcome = ObserveAsync(invocation.RunAsync(lifecycle, new AgentRunId("inert"), CancellationToken.None));
        var actual = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<OperationCanceledException>(actual);
        var leaf = ((AgentSession.IRunStartEvidence)lifecycle).FailedStart!;
        Assert.AreSame(canceled, leaf.Original);
        Assert.IsNull(leaf.OriginalFaults);
        Assert.AreSame(leaf.AwaitedFailure, actual);
        Assert.AreSame(invocation.PublicationFailure, actual);
        var release = new AgentSession.RunReleaseDecision(invocation);
        var finish = ObserveAsync(release.FinishAsync(() => lifecycle.ClosingAsync(new AgentRunId("inert")),
            () => ValueTask.CompletedTask, _ => Task.CompletedTask, () => { }, () => Task.CompletedTask, actual));
        Assert.IsNull(await finish.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(release.SourceReleased);
        Assert.IsTrue(release.SlotReleased);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Correction_PublicationGraphRetainsForwardingUseBeforeReleaseAndObservation(bool pluginMarker, bool failRetentionControl)
    {
        var shared = new OperationCanceledException("shared publication evidence");
        var marker = CreateDeferredMarker(pluginMarker, shared);
        var controlFailure = new AggregateException(shared, new IOException("retention control"), shared);
        var controlCalls = 0;
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new OwnedProviderEventForwarding();
        var attachment = owner.RegisterAttachment("session", "handle", () => Task.CompletedTask, () => Task.CompletedTask);
        attachment.CompleteSetup();
        var receipt = new SessionRuntimeService.LiveEventPublication(owner);
        var envelope = CorrectionEnvelope(shared);
        var calls = new List<string>();
        OwnedProviderEventForwarding.Use? capturedUse = null;
        var work = owner.Forward(attachment, use =>
        {
            capturedUse = use;
            return receipt.CompleteAsync(mark => { mark(envelope); entered.TrySetResult(); return source.Task; },
                () => { calls.Add("release"); use.Dispose(); },
                _ => { calls.Add("observe"); return Task.CompletedTask; },
                () => { calls.Add("independent"); return Task.CompletedTask; },
                (failure, dependencies) =>
                {
                    controlCalls++;
                    if (failRetentionControl) throw controlFailure;
                    use.Retain(failure);
                    owner.RetainDependencies(failure, dependencies);
                });
        });
        var outcome = ObserveAsync(work);
        Exception? deadlineFailure = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception failure) { deadlineFailure = failure; }
        finally { source.TrySetException([shared, marker, shared]); }
        var failureResult = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        var close = owner.CloseAsync(() => Task.CompletedTask,
            () => { calls.Add("actors"); return Task.CompletedTask; }, () => calls.Add("events"));
        var closeOutcome = ObserveAsync(close);
        var closeFailure = await closeOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        if (deadlineFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(deadlineFailure);
        Assert.IsNotNull(failureResult);
        Assert.IsNotNull(closeFailure);
        Assert.IsTrue(OwnedProviderEventForwarding.HasRetention(failureResult));
        Assert.AreSame(source.Task, receipt.PublicationOriginal);
        var graph = (AggregateException)receipt.PublicationFailure!;
        CollectionAssert.AreEqual(new Exception[] { shared, marker, shared }, graph.InnerExceptions.ToArray());
        Assert.AreEqual(1, controlCalls);
        if (failRetentionControl)
            CollectionAssert.AreEqual(new Exception[] { graph, controlFailure }, ((AgentDependencyRetentionException)failureResult).InnerExceptions.ToArray());
        Assert.AreSame(envelope, receipt.Published);
        Assert.IsFalse(capturedUse!.IsReleased);
        Assert.AreEqual(0, attachment.ProjectionUses);
        Assert.AreEqual(1, attachment.RetainedUses.Count);
        Assert.AreEqual(0, owner.ActiveWorkCount);
        CollectionAssert.AreEqual(new[] { "independent" }, calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Correction_RetentionControlInvocationFailureKeepsGraphAndNoOriginal(bool pluginMarker)
    {
        var shared = new OperationCanceledException("shared");
        var marker = CreateDeferredMarker(pluginMarker, shared);
        var control = new AggregateException(shared, new IOException("control"), shared);
        var receipt = new SessionRuntimeService.LiveEventPublication(new object());
        var releases = 0;
        var observations = 0;
        var controls = 0;
        var outcome = ObserveAsync(receipt.CompleteAsync(mark => { mark(CorrectionEnvelope(shared)); throw marker; },
            () => releases++, _ => { observations++; return Task.CompletedTask; }, () => Task.CompletedTask,
            (_, _) => { controls++; throw control; }));
        var failure = (AgentDependencyRetentionException)(await outcome.WaitAsync(TimeSpan.FromSeconds(10)))!;
        Assert.IsNull(receipt.PublicationOriginal);
        Assert.AreSame(marker, receipt.PublicationFailure);
        CollectionAssert.AreEqual(new Exception[] { marker, control }, failure.InnerExceptions.ToArray());
        Assert.AreEqual(1, controls);
        Assert.AreEqual(0, releases);
        Assert.AreEqual(0, observations);
    }

    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, false, false)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    public async Task Correction_SyntheticPrerequisiteSuppressesOnlyDependentObservation(bool pluginMarker, bool released, bool retainedFailure)
    {
        var original = new IOException("original run failure");
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupOutcome = ObserveAsync(cleanup.Task);
        var owner = new OwnedProviderEventForwarding();
        var attachment = owner.RegisterAttachment("session", "handle", () => Task.CompletedTask, () => Task.CompletedTask);
        attachment.CompleteSetup();
        var use = attachment.TryAcquireHandleUse()!;
        var evidence = retainedFailure ? new AggregateException(original, CreateDeferredMarker(pluginMarker, original)) : (Exception)original;
        cleanup.SetException(evidence);
        Assert.AreSame(evidence, await cleanupOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        if (released) use.Dispose(); else if (retainedFailure) use.Retain(evidence);
        var prerequisite = new SessionRuntimeService.LiveEventObservationPrerequisite(
            new { Original = cleanup.Task, Outcome = cleanupOutcome, Use = use }, use.IsReleased, evidence);
        var receipt = new SessionRuntimeService.LiveEventPublication(owner) { ObservationPrerequisite = prerequisite };
        var envelope = CorrectionEnvelope(original);
        var calls = new List<string>();
        RuntimePluginAgentEventEnvelope? observed = null;
        var outcome = ObserveAsync(receipt.CompleteAsync(mark => { calls.Add("publish"); mark(envelope); return Task.CompletedTask; },
            () => { }, value => { calls.Add("observe"); observed = value; return Task.CompletedTask; },
            () => { calls.Add("lifecycle"); return Task.CompletedTask; }, owner.RetainDependencies));
        Assert.IsNull(await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(envelope, receipt.Published);
        Assert.AreSame(original, ((AgentErrorEvent)receipt.Published!.Event).Exception);
        var mayObserve = released && !retainedFailure;
        if (mayObserve) Assert.AreSame(envelope, observed); else Assert.IsNull(observed);
        CollectionAssert.AreEqual(mayObserve ? new[] { "publish", "observe", "lifecycle" } : new[] { "publish", "lifecycle" }, calls);
        // The unconfirmed ordinary case deliberately keeps a real use active through publication,
        // then completes that fixture-owned use; it is not a retained release retry.
        if (!released && !retainedFailure) use.Dispose();
        var closeOutcome = ObserveAsync(owner.CloseAsync(() => Task.CompletedTask, () => Task.CompletedTask, () => { }));
        var closeFailure = await closeOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(retainedFailure, closeFailure is not null);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Correction_IndependentStagesKeepOrderedGraphsAfterObserverAndEarlierStageFailures(bool observerFails)
    {
        var shared = new OperationCanceledException("shared");
        var observer = new IOException("observer");
        var cache = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.SetException([new InvalidOperationException("cache"), shared]);
        var parent = new AggregateException(shared, new IOException("parent"), shared);
        var calls = new List<string>();
        var effects = new SessionRuntimeService.LiveEventIndependentWork([
            () => { calls.Add("cache"); return cache.Task; },
            () => { calls.Add("parent1"); throw parent; },
            () => { calls.Add("parent2"); return Task.FromException(shared); },
            () => { calls.Add("queue"); return Task.CompletedTask; },
        ]);
        var receipt = new SessionRuntimeService.LiveEventPublication(effects);
        var outcome = ObserveAsync(receipt.CompleteAsync(mark => { mark(CorrectionEnvelope(shared)); return Task.CompletedTask; },
            () => { }, _ => { calls.Add("observe"); return observerFails ? Task.FromException(observer) : Task.CompletedTask; },
            effects.RunAsync, (_, _) => { }));
        var failure = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(failure);
        Assert.IsFalse(receipt.CanToleratePrepublicationFailure);
        var independent = (AggregateException)receipt.IndependentFailure!;
        var cacheGraph = (AggregateException)independent.InnerExceptions[0];
        Assert.AreSame(shared, cacheGraph.InnerExceptions[1]);
        Assert.AreSame(parent, independent.InnerExceptions[1]);
        Assert.AreSame(shared, independent.InnerExceptions[2]);
        Assert.AreSame(cache.Task, effects.Stages[0].Original);
        Assert.IsNull(effects.Stages[1].Original);
        Assert.AreSame(parent, await effects.Stages[1].Outcome);
        if (observerFails)
            CollectionAssert.AreEqual(new Exception[] { observer, independent }, ((AggregateException)failure).InnerExceptions.ToArray());
        else Assert.AreSame(independent, failure);
        CollectionAssert.AreEqual(new[] { "observe", "cache", "parent1", "parent2", "queue" }, calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Correction_ProviderToleranceRequiresGenuinelyPrepublicationFailure(bool published)
    {
        var actorFailure = new InvalidOperationException("actor chores");
        var receipt = new SessionRuntimeService.LiveEventPublication(new object());
        var observations = 0;
        var outcome = ObserveAsync(receipt.CompleteAsync(mark =>
        {
            if (published) mark(CorrectionEnvelope(actorFailure));
            return Task.FromException(actorFailure);
        }, () => { }, _ => { observations++; return Task.CompletedTask; }, () => Task.CompletedTask, (_, _) => { }));
        Assert.AreSame(actorFailure, await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(!published, receipt.CanToleratePrepublicationFailure);
        Assert.AreEqual(published ? 1 : 0, observations);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task Correction_ActorCleanupGraphGatesOnlyDependentEventCompletion(bool pluginMarker, bool ordinary)
    {
        var shared = new OperationCanceledException("actor");
        var marker = CreateDeferredMarker(pluginMarker, shared);
        var actor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var owner = new OwnedProviderEventForwarding();
        var close = owner.CloseAsync(() => Task.CompletedTask,
            () => { calls.Add("actors"); entered.TrySetResult(); return actor.Task; }, () => calls.Add("events"));
        var outcome = ObserveAsync(close);
        Exception? deadlineFailure = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception failure) { deadlineFailure = failure; }
        finally { actor.TrySetException(ordinary ? [shared, shared] : [shared, marker, shared]); }
        var failureResult = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        if (deadlineFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(deadlineFailure);
        Assert.IsNotNull(failureResult);
        Assert.AreEqual(!ordinary, OwnedProviderEventForwarding.HasRetention(failureResult));
        var graph = (AggregateException)((AggregateException)failureResult).InnerExceptions[0];
        CollectionAssert.AreEqual(ordinary ? new Exception[] { shared, shared } : new Exception[] { shared, marker, shared }, graph.InnerExceptions.ToArray());
        Assert.AreEqual(0, owner.ActiveWorkCount);
        CollectionAssert.AreEqual(ordinary ? new[] { "actors", "events" } : new[] { "actors" }, calls);
        Assert.AreSame(close, owner.CloseAsync(() => throw new InvalidOperationException("retry"), () => throw new InvalidOperationException("retry"), () => Assert.Fail("retry")));
    }

    private static RuntimePluginAgentEventEnvelope CorrectionEnvelope(Exception error)
        => RuntimePluginAgentEventEnvelope.Capture(new AgentErrorEvent(ModelProviderIds.Codex, "session", DateTimeOffset.UnixEpoch, "original", error),
            "session", null, "inert", null, null);

    [TestMethod]
    public async Task Correction_ActorCleanupInvocationFailureHasNoReleaseAuthorityOrRetry()
    {
        var owner = new OwnedProviderEventForwarding();
        var invocationFailure = new IOException("actor invocation");
        var actorCalls = 0;
        var events = 0;
        var close = owner.CloseAsync(() => Task.CompletedTask, () => { actorCalls++; throw invocationFailure; }, () => events++);
        var outcome = ObserveAsync(close);
        var failure = (AgentDependencyRetentionException)(await outcome.WaitAsync(TimeSpan.FromSeconds(10)))!;
        Assert.AreSame(invocationFailure, failure.InnerExceptions.Single());
        Assert.AreSame(close, owner.CloseAsync(() => Task.CompletedTask, () => Task.CompletedTask, () => events++));
        Assert.AreEqual(1, actorCalls);
        Assert.AreEqual(0, events);
        Assert.AreEqual(0, owner.ActiveWorkCount);
    }

    [TestMethod]
    public async Task LivePublication_LossyFalseStillObservesExactReferenceAndCapturedProject()
    {
        var error = new InvalidOperationException("original");
        var published = new AgentErrorEvent(ModelProviderIds.Codex, "origin", DateTimeOffset.UnixEpoch, "event", error);
        var envelope = RuntimePluginAgentEventEnvelope.Capture(published, "origin", "PROJECT", "working", "project", "host-project");
        var receipt = new SessionRuntimeService.LiveEventPublication(new object());
        RuntimePluginAgentEventEnvelope? observed = null;
        var admitted = true;
        var original = receipt.CompleteAsync(mark =>
        {
            admitted = false; // The actual lossy publisher's bool is not observation authority.
            mark(envelope);
            return Task.CompletedTask;
        }, static () => { }, value => { observed = value; return Task.CompletedTask; }, static () => Task.CompletedTask, (_, _) => { });
        var outcome = ObserveAsync(original);
        Assert.IsNull(await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(admitted);
        Assert.AreSame(published, observed!.Event);
        Assert.AreEqual("origin", observed.SessionId);
        Assert.AreEqual("PROJECT", observed.ProjectId);
        Assert.AreEqual("host-project", observed.ProjectPath);
        Assert.IsNull(observed.Event.RunId);
    }

    [TestMethod]
    public async Task FailurePolicy_SuccessfulLoggingHandlesAndReturningReporterDoesNotHandleItsFailure()
    {
        var original = new InvalidOperationException("observer");
        var reportingFailure = new AggregateException(new OperationCanceledException("reporting"));
        var messages = new List<string>();
        var reports = new List<AggregateException>();
        var success = RuntimePluginAgentEventFailurePolicy.ReportAsync("origin", original,
            (message, _) => messages.Add(message), (_, failure) => reports.Add(failure)).AsTask();
        var successOutcome = ObserveAsync(success);
        Assert.IsNull(await successOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        CollectionAssert.AreEqual(new[] { "Plugin agent event observer failed for session origin" }, messages);
        Assert.AreEqual(0, reports.Count);
        var failed = RuntimePluginAgentEventFailurePolicy.ReportAsync("origin", original,
            (_, _) => throw reportingFailure, (_, failure) => reports.Add(failure)).AsTask();
        var failedOutcome = ObserveAsync(failed);
        Assert.AreSame(reportingFailure, await failedOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, reports.Count);
        Assert.AreSame(reportingFailure.InnerExceptions[0], reports[0].InnerExceptions.Single());
    }

    [TestMethod]
    public async Task RecoveredNotes_GlobalPrecedenceAndLazyProjectMatchPreserveScalars()
    {
        var loads = 0;
        var normalized = new List<string>();
        var globalOriginal = SessionRuntimeService.ResolveRecoveredPluginEventContextAsync("session", " provider ", "global", "ignored", "global",
            Normalize, () => { loads++; return Task.FromResult<IEnumerable<(string, string)>>([("first", "project"), ("later", "invalid")]); });
        var globalOutcome = ObserveAsync(globalOriginal);
        Assert.IsNull(await globalOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        var global = await globalOriginal;
        Assert.AreEqual(0, loads);
        Assert.IsNull(global.ProjectId);
        Assert.AreEqual("provider", global.ProviderId.Value);
        normalized.Clear();
        var projectOriginal = SessionRuntimeService.ResolveRecoveredPluginEventContextAsync("session", " provider ", null, "PROJECT", "global",
            Normalize, () => { loads++; return Task.FromResult<IEnumerable<(string, string)>>([("first", "project"), ("later", "invalid")]); });
        var projectOutcome = ObserveAsync(projectOriginal);
        Assert.IsNull(await projectOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        var project = await projectOriginal;
        Assert.AreEqual("first", project.ProjectId);
        Assert.AreEqual("project", project.WorkingDirectory);
        Assert.IsFalse(normalized.Contains("invalid"));
        var blank = SessionRuntimeService.ResolveRecoveredPluginEventContextAsync("session", "provider", " ", "project", "global", Normalize,
            () => throw new InvalidOperationException("blank cwd must not load projects"));
        var blankOutcome = ObserveAsync(blank);
        Assert.IsInstanceOfType<SessionNotesSessionNotFoundException>(await blankOutcome.WaitAsync(TimeSpan.FromSeconds(10)));

        string Normalize(string path)
        {
            normalized.Add(path);
            if (path == "invalid") throw new InvalidOperationException("must remain lazy");
            return path.ToLowerInvariant();
        }
    }

    [TestMethod]
    public async Task RuntimeCommand_RetainedBodyJoinsRegistrationsWithoutReleasingSource()
    {
        var marker = new AgentDependencyRetentionException("command", "provider", [new InvalidOperationException("cleanup")], new object());
        var owner = new SessionRuntimeService.RuntimeCommandLifetime(new object());
        var original = owner.RunAsync<int>(_ => Task.FromException<int>(marker), CancellationToken.None, CancellationToken.None);
        var outcome = ObserveAsync(original);
        var failure = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(failure);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(failure));
        Assert.IsFalse(owner.SourceReleased);
        Assert.IsFalse(owner.ReleaseDecision.Active);
        Assert.IsFalse(owner.ReleaseDecision.Released);
        Assert.IsNotNull(owner.OwnerRegistrationClose.Original);
        Assert.IsNotNull(owner.AttachmentRegistrationClose.Original);
    }

    [TestMethod]
    public async Task CommandOriginal_PreservesCanceledPrimaryAndRetainedSibling()
    {
        var primary = new OperationCanceledException("primary");
        var marker = new AgentDependencyRetentionException("command", "closing", [new InvalidOperationException("cleanup")], new object());
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = new OwnedSessionCommandService.OriginalInvocation();
        invocation.Launch(() => source.Task);
        source.TrySetException([primary, marker]);
        var failure = await invocation.Outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(source.Task, invocation.Original);
        Assert.AreSame(primary, invocation.AwaitedFailure);
        Assert.IsInstanceOfType<AggregateException>(failure);
        var graph = (AggregateException)failure!;
        Assert.AreSame(primary, graph.InnerExceptions[0]);
        Assert.AreSame(marker, graph.InnerExceptions[1]);
    }

    [TestMethod]
    public void CommandRelease_RetainedTerminalIsNeitherActiveNorReleased()
    {
        var dependencies = new object();
        var failure = new AgentDependencyRetentionException("command", "permission", [new InvalidOperationException("close")], dependencies);
        var retained = new OwnedSessionCommandService.DependencyReleaseDecision();
        Assert.IsTrue(retained.Active);
        retained.Observe(failure);
        retained.Complete();
        Assert.IsFalse(retained.Active);
        Assert.IsFalse(retained.Released);
        Assert.AreSame(failure, retained.Failures.Single());
        var ordinary = new OwnedSessionCommandService.DependencyReleaseDecision();
        ordinary.Observe(new OperationCanceledException("settled ordinary failure"));
        ordinary.Complete();
        Assert.IsTrue(ordinary.Released);
    }

    [TestMethod]
    public void RetentionRecognition_PreservesNestedAndSharedOriginals()
    {
        var original = new OperationCanceledException("original");
        var dependencies = new object();
        var retained = new AgentDependencyRetentionException("run", "closing", [original, original], dependencies);
        var outer = new AggregateException(new InvalidOperationException("ordinary"), retained);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(outer));
        Assert.IsFalse(AgentDependencyRetentionException.Contains(new AggregateException(original)));
        Assert.AreSame(dependencies, retained.Dependencies);
        Assert.AreSame(original, retained.InnerExceptions[0]);
        Assert.AreSame(original, retained.InnerExceptions[1]);
    }

    [TestMethod]
    public void GuardAcquisition_PublishesCandidateAndPreservesFailedRollback()
    {
        var primary = new InvalidOperationException("initialize");
        var cleanup = new OperationCanceledException("rollback");
        var candidate = new object();
        var evidence = new CodeAltaSingleInstanceGuard.AcquisitionEvidence<object>();
        var calls = new List<string>();
        try
        {
            CodeAltaSingleInstanceGuard.AcquireCandidate(evidence,
                () => { calls.Add("acquire"); return candidate; },
                value =>
                {
                    Assert.AreSame(candidate, value);
                    Assert.AreSame(candidate, evidence.Candidate);
                    calls.Add("initialize");
                    throw primary;
                },
                value => { Assert.AreSame(candidate, value); calls.Add("rollback"); throw cleanup; });
            Assert.Fail("Failed rollback must be reported.");
        }
        catch (AggregateException failure)
        {
            Assert.AreSame(primary, failure.InnerExceptions[0]);
            Assert.AreSame(cleanup, failure.InnerExceptions[1]);
        }
        CollectionAssert.AreEqual(new[] { "acquire", "initialize", "rollback" }, calls);
        Assert.AreSame(candidate, evidence.Candidate);
        Assert.AreSame(primary, evidence.InitializationFailure);
        Assert.AreSame(cleanup, evidence.RollbackFailure);
        Assert.IsTrue(evidence.RollbackAttempted);
        Assert.IsFalse(evidence.RollbackCompleted);
    }

    [TestMethod]
    public void GuardAcquisition_OrdinaryFailureWithConfirmedRollbackKeepsPrimary()
    {
        var primary = new InvalidOperationException("initialize");
        var evidence = new CodeAltaSingleInstanceGuard.AcquisitionEvidence<object>();
        var candidate = new object();
        var releases = 0;
        try
        {
            CodeAltaSingleInstanceGuard.AcquireCandidate(evidence, () => candidate,
                _ => throw primary, _ => releases++);
            Assert.Fail("Initialization must fail.");
        }
        catch (InvalidOperationException failure) { Assert.AreSame(primary, failure); }
        Assert.AreEqual(1, releases);
        Assert.IsTrue(evidence.RollbackCompleted);
        Assert.AreSame(candidate, evidence.Candidate);
    }

    [TestMethod]
    public async Task FailurePolicy_LoggerCancellationDoesNotReportFatal()
    {
        var original = new InvalidOperationException("observer");
        var logging = new OperationCanceledException("logger");
        var reports = 0;
        var operation = RuntimePluginAgentEventFailurePolicy.ReportAsync("session", original,
            (_, failure) => { Assert.AreSame(original, failure); throw logging; },
            (_, _) => reports++).AsTask();
        var outcome = ObserveAsync(operation);
        Assert.AreSame(logging, await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(0, reports);
    }

    [TestMethod]
    public async Task FailurePolicy_ReportingFailureAndFatalFailureKeepIdentity()
    {
        var original = new InvalidOperationException("observer");
        var logging = new AggregateException(new InvalidOperationException("logger"));
        var fatal = new OperationCanceledException("reporter");
        var operation = RuntimePluginAgentEventFailurePolicy.ReportAsync("session", original,
            (_, _) => throw logging,
            (source, failure) =>
            {
                Assert.AreEqual("Plugin agent event observer for session session", source);
                Assert.AreSame(logging.InnerExceptions[0], failure.InnerExceptions[0]);
                throw fatal;
            }).AsTask();
        var outcome = ObserveAsync(operation);
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<AggregateException>(result);
        var aggregate = (AggregateException)result!;
        Assert.AreSame(logging, aggregate.InnerExceptions[0]);
        Assert.AreSame(fatal, aggregate.InnerExceptions[1]);
    }

    private static async Task<Exception?> ObserveAsync(Task original)
    {
        try { await original.ConfigureAwait(false); return null; }
        catch (Exception failure) { return failure; }
    }

    [TestMethod]
    public async Task LivePublication_StoreUnwindsBeforeObservationAndBothFailuresSurvive()
    {
        var storeFailure = new OperationCanceledException("committed callback");
        var observerFailure = new InvalidOperationException("observer");
        var published = new AgentErrorEvent(ModelProviderIds.Codex, "session", DateTimeOffset.UnixEpoch, "event", storeFailure);
        var envelope = new RuntimePluginAgentEventEnvelope(published, "session", null, "cwd");
        var store = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeOutcome = ObserveAsync(store.Task);
        var observer = Task.FromException(observerFailure);
        var observerOutcome = ObserveAsync(observer);
        var gateReleased = false;
        var calls = new List<string>();
        RuntimePluginAgentEventEnvelope? observed = null;
        var receipt = new SessionRuntimeService.LiveEventPublication(new object());
        var original = receipt.CompleteAsync(mark =>
            {
                mark(envelope);
                entered.TrySetResult();
                return store.Task;
            }, () => { gateReleased = true; calls.Add("release"); }, captured =>
            {
                observed = captured;
                calls.Add(gateReleased ? "observe-after-release" : "observe-before-release");
                return observer;
            }, () => { calls.Add("independent"); return Task.CompletedTask; }, (_, _) => { });
        var outcome = ObserveAsync(original);
        Exception? entryDeadline = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception deadline) { entryDeadline = deadline; }
        finally { store.TrySetException(storeFailure); }
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(storeFailure, await storeOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(observerFailure, await observerOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        if (entryDeadline is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(entryDeadline);
        Assert.AreSame(envelope, observed);
        Assert.AreSame(published, observed!.Event);
        Assert.AreSame(store.Task, receipt.PublicationOriginal);
        Assert.AreSame(observer, receipt.ObservationOriginal);
        Assert.IsInstanceOfType<AggregateException>(result);
        var graph = (AggregateException)result!;
        Assert.AreSame(storeFailure, graph.InnerExceptions[0]);
        Assert.AreSame(observerFailure, graph.InnerExceptions[1]);
        CollectionAssert.AreEqual(new[] { "release", "observe-after-release", "independent" }, calls);
    }

    [TestMethod]
    public async Task LivePublication_UnpublishedEventDoesNotObserve()
    {
        var failure = new InvalidOperationException("store before publication");
        var store = Task.FromException(failure);
        var observations = 0;
        var receipt = new SessionRuntimeService.LiveEventPublication(new object());
        var original = receipt.CompleteAsync(_ => store, () => { },
            _ => { observations++; return Task.CompletedTask; }, () => Task.CompletedTask, (_, _) => { });
        var outcome = ObserveAsync(original);
        Assert.AreSame(failure, await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(0, observations);
        Assert.IsNull(receipt.ObservationOriginal);
    }

    [TestMethod]
    public void StartupOwner_OrdinaryUnwindReleasesActualLeaseThenAnchorOnce()
    {
        var calls = new List<string>();
        var lease = new InertLease(() => calls.Add("lease-release"));
        var owner = new TuiApp::Program.StartupOwner(_ => calls.Add("anchor-acquire"), _ => calls.Add("anchor-release"));
        var wrapper = owner.AcquireAdmission(() => { calls.Add("lease-acquire"); return lease; });
        owner.Capture(new OperationCanceledException("ordinary"));
        owner.MarkStartupSettled();
        wrapper.Dispose();
        owner.FinishAfterAdmissionUnwind();
        owner.FinishAfterAdmissionUnwind();
        Assert.AreSame(lease, owner.AdmissionLease);
        CollectionAssert.AreEqual(new[] { "anchor-acquire", "lease-acquire", "lease-release", "anchor-release" }, calls);
    }

    [TestMethod]
    public void StartupOwner_RetainedSignalPermanentlyKeepsActualLeaseAndInertAnchor()
    {
        var acquisitions = 0;
        var releases = 0;
        var lease = new InertLease(() => releases++);
        var owner = new TuiApp::Program.StartupOwner(_ => acquisitions++, _ => releases++);
        var wrapper = owner.AcquireAdmission(() => lease);
        var original = new InvalidOperationException("required cleanup");
        var marker = new AgentDependencyRetentionException("run", "closing", [original], new object());
        owner.Capture(new AggregateException(marker));
        owner.MarkStartupSettled();
        wrapper.Dispose();
        owner.FinishAfterAdmissionUnwind();
        owner.MarkStartupSettled();
        owner.FinishAfterAdmissionUnwind();
        Assert.AreEqual(1, acquisitions);
        Assert.AreEqual(0, releases);
        Assert.AreSame(lease, owner.AdmissionLease);
        Assert.IsTrue(owner.HasRetainedEvidence);
    }

    [TestMethod]
    public void StartupOwner_AnchorFailurePrecedesLeaseAndRequiresNoRelease()
    {
        var failure = new InvalidOperationException("anchor allocation");
        var effects = 0;
        var owner = new TuiApp::Program.StartupOwner(_ => throw failure, _ => effects++);
        try
        {
            owner.AcquireAdmission(() => { effects++; return new InertLease(() => effects++); });
            Assert.Fail("Allocation must fail.");
        }
        catch (InvalidOperationException actual) { Assert.AreSame(failure, actual); }
        owner.FinishAfterAdmissionUnwind();
        Assert.AreEqual(0, effects);
    }

    [TestMethod]
    public void StartupOwner_MissingAcquisitionReleaseReceiptRetainsOuterLeaseAndAnchor()
    {
        var releases = new List<string>();
        var lease = new InertLease(() => releases.Add("lease"));
        var owner = new TuiApp::Program.StartupOwner(_ => { }, _ => releases.Add("anchor"));
        var wrapper = owner.AcquireAdmission(() => lease);
        owner.Terminal = new object();
        owner.ReleaseTerminal = () => releases.Add("terminal");
        owner.MarkStartupSettled();
        Exception? failure = null;
        try { wrapper.Dispose(); owner.FinishAfterAdmissionUnwind(); }
        catch (Exception actual) { failure = actual; }

        Assert.IsNotNull(failure, "Settlement alone must not substitute for the acquired terminal's release receipt.");
        Assert.IsTrue(AgentDependencyRetentionException.Contains(failure));
        Assert.IsTrue(owner.HasRetainedEvidence);
        owner.ReleaseStartupIfEligible();
        wrapper.Dispose();
        owner.FinishAfterAdmissionUnwind();
        Assert.AreEqual(0, releases.Count);
        Assert.AreSame(lease, owner.AdmissionLease);
    }

    [TestMethod]
    public async Task StartupOwner_PrematureFinalizationLatchesBeforeLateOriginalSettlement()
    {
        var releases = 0;
        var leaseReleases = 0;
        var owner = new TuiApp::Program.StartupOwner(_ => { }, _ => releases++);
        var wrapper = owner.AcquireAdmission(() => new InertLease(() => leaseReleases++));
        var original = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalOutcome = ObserveAsync(original.Task);
        var operation = owner.Start("controlled original", () => original.Task);
        var completionOutcome = ObserveAsync(operation.Completion);
        owner.MarkStartupSettled();
        Exception? failure = null;
        try { wrapper.Dispose(); owner.FinishAfterAdmissionUnwind(); }
        catch (Exception actual) { failure = actual; }
        finally { original.TrySetResult(); }

        Assert.IsNull(await originalOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNull(await completionOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(original.Task, operation.Original);
        Assert.IsNotNull(failure);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(failure));
        Assert.IsTrue(owner.HasRetainedEvidence);
        owner.FinishAfterAdmissionUnwind();
        Assert.AreEqual(0, leaseReleases);
        Assert.AreEqual(0, releases, "Late completion must not restore release eligibility.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StartupOwner_RetentionDuringPrerequisiteInspectionPreventsReleaseAuthorization(bool anchor)
    {
        var releases = 0;
        var inspections = 0;
        var owner = new TuiApp::Program.StartupOwner(_ => { }, _ => releases++);
        var wrapper = owner.AcquireAdmission(() => new InertLease(() => releases++));
        var retained = new AgentDependencyRetentionException("startup", "settled original", [new InvalidOperationException("cleanup")], new object());
        owner.MarkStartupSettled();
        var authorized = owner.TryAuthorizeRelease(anchor, () =>
        {
            inspections++;
            // Models the original capturing its graph before its outcome becomes settled, after
            // the release caller's initial latch read but before its final release decision.
            owner.Capture(retained);
            return true;
        });
        Assert.IsFalse(authorized);
        Assert.AreEqual(1, inspections);
        Assert.IsTrue(owner.HasRetainedEvidence);
        wrapper.Dispose();
        owner.FinishAfterAdmissionUnwind();
        Assert.AreEqual(0, releases);
    }

    [TestMethod]
    public async Task Forwarding_RetainedTerminalWorkDrainsWithoutReleasingActorsOrEvents()
    {
        var failure = new AgentDependencyRetentionException("observer", "closing", [new InvalidOperationException("cleanup")], new object());
        var body = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new OwnedProviderEventForwarding();
        var calls = new List<string>();
        var work = owner.RunAsync(() => { entered.TrySetResult(); return body.Task; });
        var workOutcome = ObserveAsync(work);
        Exception? entryDeadline = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception deadline) { entryDeadline = deadline; }
        finally { body.TrySetException(failure); }
        var actual = await workOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        var close = owner.CloseAsync(() => { calls.Add("permissions"); return Task.CompletedTask; },
            () => { calls.Add("actors"); return Task.CompletedTask; }, () => calls.Add("events"));
        var closeOutcome = ObserveAsync(close);
        var closeFailure = await closeOutcome.WaitAsync(TimeSpan.FromSeconds(10));
        if (entryDeadline is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(entryDeadline);
        Assert.AreSame(failure, actual);
        Assert.IsNotNull(closeFailure);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(closeFailure));
        Assert.AreEqual(0, owner.ActiveWorkCount);
        Assert.AreEqual(1, owner.RetainedWorkCount);
        CollectionAssert.AreEqual(new[] { "permissions" }, calls);
        Assert.AreSame(close, owner.CloseAsync(() => Task.CompletedTask, () => Task.CompletedTask, () => { }));
    }

    [TestMethod]
    public async Task Forwarding_OrdinaryTerminalErrorDoesNotRetainDependencies()
    {
        var failure = new OperationCanceledException("ordinary body");
        var body = Task.FromException(failure);
        var owner = new OwnedProviderEventForwarding();
        var calls = new List<string>();
        var original = owner.RunAsync(() => body);
        var outcome = ObserveAsync(original);
        Assert.AreSame(failure, await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        var close = owner.CloseAsync(() => { calls.Add("permissions"); return Task.CompletedTask; },
            () => { calls.Add("actors"); return Task.CompletedTask; }, () => calls.Add("events"));
        var closeOutcome = ObserveAsync(close);
        Assert.IsNull(await closeOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(0, owner.RetainedWorkCount);
        CollectionAssert.AreEqual(new[] { "permissions", "actors", "events" }, calls);
    }

    private sealed class InertLease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    [TestMethod]
    public async Task DeferredRetentionInsideExpectedCancellation_IsNeverSuppressed()
    {
        var marker = new AgentDependencyRetentionException("permission", "closing", [new InvalidOperationException("cleanup")], new object());
        var token = new CancellationToken(canceled: true);
        var canceled = new OperationCanceledException("startup", marker, token);
        var startup = CancelStartupAsync();
        var startupOutcome = ObserveAsync(startup);
        var releases = 0;
        var original = DeferredCodeAltaApp.DisposeDeferredStartupAsync<InertAsyncLease>(null, startup, canceled, token,
            () => { }, () => { releases++; return ValueTask.CompletedTask; }, () => releases++, () => releases++);
        var outcome = ObserveAsync(original);
        var failure = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(canceled, await startupOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNotNull(failure);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(failure));
        Assert.AreEqual(0, releases);

        async Task<InertAsyncLease> CancelStartupAsync() { await Task.CompletedTask; throw canceled; }
    }

    [TestMethod]
    public async Task DeferredShutdown_StartsIndependentControlsBeforeJoiningApp()
    {
        var appDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appOutcome = ObserveAsync(appDisposal.Task);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var app = new InertAsyncLease(() => { calls.Add("app"); entered.TrySetResult(); return new ValueTask(appDisposal.Task); });
        var original = DeferredCodeAltaApp.DisposeDeferredStartupAsync<InertAsyncLease>(app, null, null, CancellationToken.None,
            () => calls.Add("cancel"), () => { calls.Add("update"); return ValueTask.CompletedTask; },
            () => calls.Add("presenter"), () => calls.Add("source"), () => calls.Add("owned-controls"));
        var outcome = ObserveAsync(original);
        Exception? entryDeadline = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception deadline) { entryDeadline = deadline; }
        finally { appDisposal.TrySetResult(); }
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNull(await appOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        if (entryDeadline is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(entryDeadline);
        Assert.IsNull(result);
        CollectionAssert.AreEqual(new[] { "cancel", "owned-controls", "app", "update", "presenter", "source" }, calls);
    }

    private sealed class InertAsyncLease(Func<ValueTask> release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => release();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeferredStartup_ReportedOrdinaryFailureCannotHideRetainedSibling(bool pluginMarker)
    {
        var primary = new InvalidOperationException("reported startup failure");
        var marker = CreateDeferredMarker(pluginMarker, primary);
        var startup = new TaskCompletionSource<InertAsyncLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupOutcome = ObserveAsync(startup.Task);
        var releases = 0;
        var original = DeferredCodeAltaApp.DisposeDeferredStartupAsync<InertAsyncLease>(null, startup.Task, primary, CancellationToken.None,
            () => { }, () => { releases++; return ValueTask.CompletedTask; }, () => releases++, () => releases++);
        var outcome = ObserveAsync(original);
        startup.SetException([primary, marker, primary]);
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(primary, await startupOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNotNull(result);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(result));
        AssertDeferredOriginal(result, startup.Task, primary, marker);
        Assert.AreEqual(0, releases);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task DeferredDisposal_OrdinaryFailureCannotHideRetainedSibling(bool returnedServices, bool pluginMarker)
    {
        var primary = new InvalidOperationException("ordinary disposal failure");
        var marker = CreateDeferredMarker(pluginMarker, primary);
        var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalOutcome = ObserveAsync(disposal.Task);
        var lease = new InertAsyncLease(() => new ValueTask(disposal.Task));
        var startup = returnedServices ? Task.FromResult(lease) : null;
        var startupOutcome = startup is null ? null : ObserveAsync(startup);
        var releases = 0;
        var original = DeferredCodeAltaApp.DisposeDeferredStartupAsync<InertAsyncLease>(returnedServices ? null : lease, startup, null, CancellationToken.None,
            () => { }, () => { releases++; return ValueTask.CompletedTask; }, () => releases++, () => releases++);
        var outcome = ObserveAsync(original);
        disposal.SetException([primary, marker, primary]);
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(primary, await disposalOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNotNull(result);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(result));
        AssertDeferredOriginal(result, disposal.Task, primary, marker);
        if (startupOutcome is not null) Assert.IsNull(await startupOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(0, releases);
    }

    private static Exception CreateDeferredMarker(bool plugin, Exception shared)
        => plugin ? new CodeAlta.Plugins.PluginEventDependencyException(shared, shared, new object())
            : new AgentDependencyRetentionException("deferred", "required cleanup", [shared, shared], new object());

    private static void AssertDeferredOriginal(Exception failure, Task original, Exception primary, Exception marker)
    {
        Assert.IsInstanceOfType<AgentDependencyRetentionException>(failure);
        var retained = (AgentDependencyRetentionException)failure;
        Assert.IsInstanceOfType<DeferredCodeAltaApp.DeferredRetainedDependencies>(retained.Dependencies);
        var evidence = ((DeferredCodeAltaApp.DeferredRetainedDependencies)retained.Dependencies).OriginalFailures.Single();
        Assert.AreSame(original, evidence.Original);
        Assert.AreSame(primary, evidence.AwaitedFailure);
        Assert.IsNotNull(evidence.OriginalFaults);
        Assert.AreSame(evidence.OriginalFaults, retained.InnerExceptions.Single());
        Assert.AreEqual(3, evidence.OriginalFaults.InnerExceptions.Count);
        Assert.AreSame(primary, evidence.OriginalFaults.InnerExceptions[0]);
        Assert.AreSame(marker, evidence.OriginalFaults.InnerExceptions[1]);
        Assert.AreSame(primary, evidence.OriginalFaults.InnerExceptions[2]);
    }

    [TestMethod]
    public async Task DeferredStartup_AlreadyPresentedOrdinaryGraphRemainsSuppressed()
    {
        var primary = new InvalidOperationException("already presented");
        var startup = new TaskCompletionSource<InertAsyncLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupOutcome = ObserveAsync(startup.Task);
        var releases = 0;
        var original = DeferredCodeAltaApp.DisposeDeferredStartupAsync<InertAsyncLease>(null, startup.Task, primary, CancellationToken.None,
            () => { }, () => { releases++; return ValueTask.CompletedTask; }, () => releases++, () => releases++);
        var outcome = ObserveAsync(original);
        startup.SetException([primary, new OperationCanceledException("ordinary sibling")]);
        Assert.IsNull(await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(primary, await startupOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(3, releases);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task DeferredDisposal_OrdinaryReturnedFailureKeepsExactExceptionAndCleanup(bool returnedServices, bool cancellation)
    {
        Exception failure = cancellation ? new OperationCanceledException("ordinary cancellation") : new InvalidOperationException("ordinary failure");
        var disposal = Task.FromException(failure);
        var disposalOutcome = ObserveAsync(disposal);
        var lease = new InertAsyncLease(() => new ValueTask(disposal));
        var startup = returnedServices ? Task.FromResult(lease) : null;
        var startupOutcome = startup is null ? null : ObserveAsync(startup);
        var releases = 0;
        var original = DeferredCodeAltaApp.DisposeDeferredStartupAsync<InertAsyncLease>(returnedServices ? null : lease, startup, null, CancellationToken.None,
            () => { }, () => { releases++; return ValueTask.CompletedTask; }, () => releases++, () => releases++);
        var outcome = ObserveAsync(original);
        Assert.AreSame(failure, await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreSame(failure, await disposalOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        if (startupOutcome is not null) Assert.IsNull(await startupOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(3, releases);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeferredDisposal_SynchronousRetainedFailureDoesNotInventAnOriginal(bool pluginMarker)
    {
        var failure = CreateDeferredMarker(pluginMarker, new InvalidOperationException("synchronous failure"));
        var launches = 0;
        var releases = 0;
        var lease = new InertAsyncLease(() => { launches++; throw failure; });
        var original = DeferredCodeAltaApp.DisposeDeferredStartupAsync<InertAsyncLease>(lease, null, null, CancellationToken.None,
            () => { }, () => { releases++; return ValueTask.CompletedTask; }, () => releases++, () => releases++);
        var outcome = ObserveAsync(original);
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<AgentDependencyRetentionException>(result);
        var retained = (AgentDependencyRetentionException)result!;
        var evidence = ((DeferredCodeAltaApp.DeferredRetainedDependencies)retained.Dependencies).OriginalFailures.Single();
        Assert.IsNull(evidence.Original);
        Assert.IsNull(evidence.OriginalFaults);
        Assert.AreSame(failure, evidence.AwaitedFailure);
        Assert.AreSame(failure, retained.InnerExceptions.Single());
        Assert.AreEqual(1, launches);
        Assert.AreEqual(0, releases);
    }

    [TestMethod]
    public async Task DeferredPluginBarrierFailure_RetainsAppPresenterAndStartupSource()
    {
        var failure = new OperationCanceledException("plugin quiescence control");
        var barrier = Task.FromException(failure);
        var barrierOutcome = ObserveAsync(barrier);
        var calls = new List<string>();
        var app = new InertAsyncLease(() => { calls.Add("app"); return ValueTask.CompletedTask; });
        var original = DeferredCodeAltaApp.DisposeDeferredStartupAsync<InertAsyncLease>(app, null, null, CancellationToken.None,
            () => calls.Add("cancel"), () => { calls.Add("update"); return ValueTask.CompletedTask; },
            () => calls.Add("presenter"), () => calls.Add("source"), () => calls.Add("owned-controls"),
            () => { calls.Add("plugin-drain"); return barrier; });
        var outcome = ObserveAsync(original);
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(failure, await barrierOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNotNull(result);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(result));
        CollectionAssert.AreEqual(new[] { "cancel", "owned-controls", "plugin-drain" }, calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task HostRelease_RetainedStageProhibitsLaterDependentReleases(int retainedStage)
    {
        var retained = new AgentDependencyRetentionException("inner", "release", [new OperationCanceledException("cleanup")], new object());
        var failure = Task.FromException(retained);
        var failureOutcome = ObserveAsync(failure);
        var calls = new List<int>();
        var disposal = CodeAltaHost.CreateHostDisposal(() => Invoke(0), () => Invoke(1), () => Invoke(2),
            () => Invoke(3), () => calls.Add(4), true, true);
        var original = disposal.Value;
        var outcome = ObserveAsync(original);
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(retained, await failureOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNotNull(result);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(result));
        CollectionAssert.AreEqual(Enumerable.Range(0, retainedStage + 1).ToArray(), calls);

        ValueTask Invoke(int stage)
        {
            calls.Add(stage);
            return stage == retainedStage ? new ValueTask(failure) : ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    public async Task HostOwnedWork_RetainedFailureStartsBothControlsAndOnlyDrainsRuntime()
    {
        var retained = new AgentDependencyRetentionException("command", "closing", [new InvalidOperationException("cleanup")], new object());
        var command = Task.FromException(retained);
        var commandOutcome = ObserveAsync(command);
        var reads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readsOutcome = ObserveAsync(reads.Task);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var original = CodeAltaHost.DisposeOwnedWorkAsync(
            () => { calls.Add("commands"); entered.TrySetResult(); return command; },
            () => { calls.Add("reads"); return reads.Task; },
            () => { calls.Add("release-runtime"); return ValueTask.CompletedTask; },
            _ => { calls.Add("drain-runtime"); return ValueTask.CompletedTask; }, () => Task.CompletedTask).AsTask();
        var outcome = ObserveAsync(original);
        Exception? entryDeadline = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception deadline) { entryDeadline = deadline; }
        finally { reads.TrySetResult(); }
        var failure = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(retained, await commandOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNull(await readsOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        if (entryDeadline is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(entryDeadline);
        Assert.IsNotNull(failure);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(failure));
        CollectionAssert.AreEqual(new[] { "reads", "commands", "drain-runtime" }, calls);
    }

    [TestMethod]
    public async Task AgentRelease_RetainedClosingReportsWithoutReleasingSourceOrSlot()
    {
        var failure = new InvalidOperationException("permission registration cleanup");
        var retained = new AgentDependencyRetentionException("permission", "registration", [failure], new object());
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controls = new List<string>();
        var releases = 0;
        var decision = new AgentSession.RunReleaseDecision(new object());
        var original = decision.FinishAsync(
            () => { entered.TrySetResult(); return closing.Task; },
            () => { controls.Add("unregister"); return ValueTask.CompletedTask; },
            retain => { Assert.IsTrue(retain); controls.Add("cancel"); return Task.CompletedTask; },
            () => releases++, () => { releases++; return Task.CompletedTask; }, null);
        var outcome = ObserveAsync(original);
        Exception? deadline = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception failureAtEntry) { deadline = failureAtEntry; }
        finally { closing.TrySetException(retained); }
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        if (deadline is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(deadline);
        Assert.IsNotNull(result);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(result));
        Assert.AreSame(closing.Task, decision.Closing!.Original);
        Assert.AreSame(retained, await decision.Closing.Outcome);
        CollectionAssert.AreEqual(new[] { "unregister", "cancel" }, controls);
        Assert.AreEqual(0, releases);
        Assert.IsTrue(decision.Retained);
        Assert.IsFalse(decision.SourceReleased);
        Assert.IsFalse(decision.SlotReleased);
    }

    [TestMethod]
    public async Task AgentRelease_ConfirmedOrdinaryCallbackFailureReleasesDependencies()
    {
        var failure = new OperationCanceledException("ordinary callback");
        var decision = new AgentSession.RunReleaseDecision(new object());
        var releases = new List<string>();
        var original = decision.FinishAsync(() => Task.FromException(failure),
            () => ValueTask.CompletedTask, retain => { Assert.IsFalse(retain); return Task.CompletedTask; },
            () => releases.Add("source"), () => { releases.Add("slot"); return Task.CompletedTask; }, null);
        var outcome = ObserveAsync(original);
        Assert.AreSame(failure, await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        CollectionAssert.AreEqual(new[] { "source", "slot" }, releases);
        Assert.IsFalse(decision.Retained);
        Assert.IsTrue(decision.SourceReleased);
        Assert.IsTrue(decision.SlotReleased);
    }

    [TestMethod]
    public async Task PermissionDelivery_MissingIndexReceiptIsExplicitRetention()
    {
        var failure = new InvalidOperationException("mailbox cleanup");
        var lifetime = new SessionPermissionService.OwnedDeliveryLifetime(new object());
        var original = lifetime.RunAsync(_ => Task.FromResult(7), () => Task.CompletedTask,
            () => Task.FromException(failure), []);
        var outcome = ObserveAsync(original);
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(result);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(result));
        Assert.AreSame(failure, await lifetime.IndexCleanup!.Outcome);
        Assert.IsTrue(lifetime.SourceReleased);
        Assert.IsFalse(lifetime.IndexReleased);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AgentRelease_MissingClosingOrCancellationOriginalRetainsDependencies(bool closingLaunch)
    {
        var failure = new InvalidOperationException("synchronous launch failure");
        var decision = new AgentSession.RunReleaseDecision(new object());
        var releases = 0;
        var original = decision.FinishAsync(
            () => closingLaunch ? throw failure : Task.CompletedTask,
            () => ValueTask.CompletedTask,
            _ => closingLaunch ? Task.CompletedTask : throw failure,
            () => releases++, () => { releases++; return Task.CompletedTask; }, null);
        var outcome = ObserveAsync(original);
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<AgentDependencyRetentionException>(result);
        var marker = (AgentDependencyRetentionException)result!;
        Assert.AreSame(failure, marker.InnerExceptions.Single());
        Assert.IsNull((closingLaunch ? decision.Closing : decision.Cancellation)!.Original);
        Assert.IsTrue(decision.Retained);
        Assert.AreEqual(0, releases);
    }

    [TestMethod]
    public async Task PermissionDelivery_OrdinaryBodyFailureWithConfirmedCleanupIsUnchanged()
    {
        var failure = new OperationCanceledException("ordinary body");
        var lifetime = new SessionPermissionService.OwnedDeliveryLifetime(new object());
        var controls = new List<string>();
        var original = lifetime.RunAsync<int>(_ => Task.FromException<int>(failure),
            () => { controls.Add("cancel"); return Task.CompletedTask; },
            () => { controls.Add("index"); return Task.CompletedTask; }, []);
        var outcome = ObserveAsync(original);
        Assert.AreSame(failure, await outcome.WaitAsync(TimeSpan.FromSeconds(10)));
        CollectionAssert.AreEqual(new[] { "cancel", "index" }, controls);
        Assert.IsTrue(lifetime.SourceReleased);
        Assert.IsTrue(lifetime.IndexReleased);
    }

    [TestMethod]
    public async Task LifecycleClosing_RetainedFaultAndCanceledOriginalBothSurvive()
    {
        var canceled = new OperationCanceledException("second closing");
        var retained = new AgentDependencyRetentionException("permission", "closing", [new InvalidOperationException("cleanup")], new object());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.FromException(retained);
        var second = SecondAsync();
        var secondOutcome = ObserveAsync(second);
        var pair = new OwnedSessionAskExecution.ClosingPair(() => first,
            () => { entered.TrySetResult(); return second; });
        var outcome = ObserveAsync(pair.Completion);
        pair.Launch();
        Exception? deadline = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception failure) { deadline = failure; }
        finally { gate.TrySetResult(); }
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(canceled, await secondOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
        if (deadline is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(deadline);
        Assert.IsInstanceOfType<AgentDependencyRetentionException>(result);
        var graph = (AgentDependencyRetentionException)result!;
        Assert.AreSame(retained, graph.InnerExceptions[0]);
        Assert.AreSame(canceled, graph.InnerExceptions[1]);
        Assert.AreSame(first, pair.First.Original);
        Assert.AreSame(second, pair.Second.Original);

        async Task SecondAsync() { await gate.Task; throw canceled; }
    }

    [TestMethod]
    public async Task HubRetirement_RetainedUseSettlesButDoesNotReleaseDependencies()
    {
        var calls = new List<string>();
        var failure = new AgentDependencyRetentionException("run", "closing", [new InvalidOperationException("cleanup")], new object());
        var owner = new AgentHub.SessionEntryLifetime(new object(),
            () => { calls.Add("abort"); return Task.CompletedTask; },
            () => { calls.Add("session"); return ValueTask.CompletedTask; },
            () => { calls.Add("provider"); return ValueTask.CompletedTask; });
        Assert.IsTrue(owner.TryAddReference());
        owner.CompleteReference(failure, failure.Dependencies);
        Assert.AreEqual(0, owner.ActiveReferences);
        Assert.AreEqual(1, owner.RetainedReferences);
        Assert.AreEqual(0, owner.ReleasedReferences);
        Assert.IsFalse(owner.TryAddReference());
        owner.BeginShutdown();
        var original = owner.DisposeAsync().AsTask();
        var outcome = ObserveAsync(original);
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(result);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(result));
        CollectionAssert.AreEqual(new[] { "abort" }, calls);
        Assert.IsFalse(owner.DependenciesReleased);
        var repeated = owner.DisposeAsync().AsTask();
        var repeatedOutcome = ObserveAsync(repeated);
        Assert.AreSame(result, await repeatedOutcome.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
