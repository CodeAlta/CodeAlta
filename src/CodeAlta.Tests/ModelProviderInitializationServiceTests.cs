using CodeAlta.Agent;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelProviderInitializationServiceTests
{
    [TestMethod]
    public async Task CanceledModelWaiterRetainsOriginalProviderPreparationClaim()
    {
        await using var registry = new ModelProviderRegistry();
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["fixture"]);
        Assert.IsTrue(protection.Publish("fixture", [], true));
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("held"), "Held", "test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.RegisterOrReplace(descriptor, () =>
        {
            Assert.IsNull(protection.TryReserveRetirement("old-session"), "Claim must precede provider construction.");
            return new HeldProbeRuntime(descriptor, entered, release);
        });
        var service = new ModelProviderInitializationService(registry, null, protection);
        using var cancellation = new CancellationTokenSource();
        var waiter = service.GetModelsAsync(descriptor.ProviderId, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
            Assert.IsNull(protection.TryReserveRetirement("old-session"), "Canceled waiter is not the original refresh.");
            var original = service.RefreshProviderAsync(descriptor.ProviderId);
            release.TrySetResult();
            await original.WaitAsync(TimeSpan.FromSeconds(5));
            using var retirement = protection.TryReserveRetirement("old-session");
            Assert.IsNotNull(retirement);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task HostShutdownJoinsModelPreparationBeforeDisposingProvider()
    {
        using var temp = TestTempDirectory.Create();
        string DirectoryAt(string name) => Directory.CreateDirectory(Path.Combine(temp.Path, name)).FullName;
        var project = DirectoryAt("project");
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("host-drain"), "Host drain", "test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new HeldProbeRuntime(descriptor, entered, release, disposed);
        await using var host = await CodeAltaHost.CreateAsync(new()
        {
            GlobalRoot = DirectoryAt("global"),
            StateRoot = DirectoryAt("state"),
            CurrentProjectPath = project,
            DiscoveryScope = new(DirectoryAt("home"), project),
            BuiltInSkillRoot = DirectoryAt("skills"),
            StartPlugins = false,
            OwnsLogging = false,
            ConfigureModelProviders = registry => registry.RegisterOrReplace(descriptor, () => runtime),
        });
        var refresh = host.ModelProviderInitializationService.RefreshProviderAsync(descriptor.ProviderId);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shutdown = host.DisposeAsync().AsTask();
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.IsFalse(disposed.Task.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(refresh, shutdown).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(disposed.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ProviderReconfigurationCannotDisposeActiveOrRetainedPreparation(bool remove, bool retained)
    {
        var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("reconfigure"), "Reconfigure", "test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new HeldProbeRuntime(descriptor, entered, release, disposed);
        registry.RegisterOrReplace(descriptor, () => runtime);
        var service = new ModelProviderInitializationService(registry);
        using var cancellation = new CancellationTokenSource();
        var observer = service.RefreshProviderAsync(descriptor.ProviderId, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => observer);
            if (retained)
            {
                release.SetException(new AgentDependencyRetentionException("fixture", "probe", [new IOException("held")], new object()));
                await Assert.ThrowsExactlyAsync<AgentDependencyRetentionException>(() => service.RefreshProviderAsync(descriptor.ProviderId));
            }
            var version = registry.GetRegistrationVersion(descriptor.ProviderId);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                if (remove) registry.Unregister(descriptor.ProviderId);
                else registry.RegisterOrReplace(descriptor, () => new TestRuntime(descriptor));
            });
            Assert.AreEqual(version, registry.GetRegistrationVersion(descriptor.ProviderId));
            Assert.IsFalse(disposed.Task.IsCompleted, "Neither a canceled observer nor a retained failure releases the original runtime.");
            if (!retained)
            {
                release.TrySetResult();
                await service.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (remove) Assert.IsTrue(registry.Unregister(descriptor.ProviderId));
                else registry.RegisterOrReplace(descriptor, () => new TestRuntime(descriptor));
                Assert.IsTrue(disposed.Task.IsCompleted);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<AgentDependencyRetentionException>(() => registry.DisposeAsync().AsTask());
                Assert.IsFalse(disposed.Task.IsCompleted);
            }
        }
        finally
        {
            release.TrySetResult();
            if (!retained) { await service.DrainAsync(); await registry.DisposeAsync(); }
        }
    }

    [TestMethod]
    public async Task DrainJoinsOriginalRefreshAfterCallerCancelsAndClosesAdmission()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("drain"), "Drain", "test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.RegisterOrReplace(descriptor, () => new HeldProbeRuntime(descriptor, entered, release));
        var service = new ModelProviderInitializationService(registry);
        using var cancellation = new CancellationTokenSource();
        var waiter = service.GetModelsAsync(descriptor.ProviderId, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
            var drain = service.DrainAsync();
            Assert.IsFalse(drain.IsCompleted);
            Assert.AreSame(drain, service.DrainAsync());
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => service.RefreshProviderAsync(descriptor.ProviderId));
            release.TrySetResult();
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task DrainSeesPublishedOriginalWhileRegistryPinWaitsForReentrantFactory()
    {
        await using var registry = new ModelProviderRegistry();
        var target = new ModelProviderDescriptor(new ModelProviderId("target"), "Target", "test");
        var blocker = new ModelProviderDescriptor(new ModelProviderId("blocker"), "Blocker", "test");
        var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseLock = new ManualResetEventSlim();
        ModelProviderInitializationService? service = null;
        Task? reentrantDrain = null;
        Task<IModelProviderRuntime>? registryWorker = null;
        registry.RegisterOrReplace(target, () => new TestRuntime(target));
        registry.RegisterOrReplace(blocker, () =>
        {
            lockHeld.TrySetResult();
            Assert.IsTrue(releaseLock.Wait(TimeSpan.FromSeconds(10)));
            reentrantDrain = service!.DrainAsync();
            return new TestRuntime(blocker);
        });
        var protection = new SessionRetentionProtection();
        service = new ModelProviderInitializationService(registry, null, new CallbackAdmission(() =>
        {
            registryWorker = Task.Run(async () => await registry.GetOrCreateRuntimeAsync(blocker.ProviderId));
            lockHeld.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            admitted.TrySetResult();
            return protection.TryProtectAll();
        }));
        var refresh = Task.Run(() => service.RefreshProviderAsync(target.ProviderId));
        try
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Drain must acquire _gate without needing the registry lock, and see the published original.
            var drainCall = Task.Run(() => new[] { service.DrainAsync() });
            var drain = (await drainCall.WaitAsync(TimeSpan.FromSeconds(5)))[0];
            Assert.IsFalse(drain.IsCompleted);
            releaseLock.Set();
            await Task.WhenAll(refresh, drain, registryWorker!).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(drain, reentrantDrain);
        }
        finally { releaseLock.Set(); await refresh; if (registryWorker is not null) await registryWorker; }
    }

    [TestMethod]
    public async Task RegistryShutdownBeforePinAcquisitionReleasesAdmissionWithoutCallingFactory()
    {
        var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("closed"), "Closed", "test");
        var constructions = 0;
        registry.RegisterOrReplace(descriptor, () => { constructions++; return new TestRuntime(descriptor); });
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["fixture"]);
        protection.Publish("fixture", [], true);
        var service = new ModelProviderInitializationService(registry, null, new CallbackAdmission(() =>
        {
            registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return protection.TryProtectAll();
        }));
        await service.RefreshProviderAsync(descriptor.ProviderId);
        await service.DrainAsync();
        Assert.AreEqual(0, constructions);
        using var retirement = protection.TryReserveRetirement("old-session");
        Assert.IsNotNull(retirement);
        Assert.ThrowsExactly<ObjectDisposedException>(() => registry.RegisterOrReplace(descriptor, () => new TestRuntime(descriptor)));
    }

    [TestMethod]
    public async Task RegistryShutdownWithActivePreparationDisposesNoRuntimeAndClosesAdmission()
    {
        var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("active"), "Active", "test");
        var other = new ModelProviderDescriptor(new ModelProviderId("other"), "Other", "test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.RegisterOrReplace(descriptor, () => new HeldProbeRuntime(descriptor, entered, release, disposed));
        registry.RegisterOrReplace(other, () => new HeldProbeRuntime(other, entered, release, otherDisposed));
        await registry.GetOrCreateRuntimeAsync(other.ProviderId);
        var service = new ModelProviderInitializationService(registry);
        var refresh = service.RefreshProviderAsync(descriptor.ProviderId);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failure = await Assert.ThrowsExactlyAsync<AgentDependencyRetentionException>(() => registry.DisposeAsync().AsTask());
            Assert.AreSame(registry, failure.Dependencies);
            Assert.IsFalse(disposed.Task.IsCompleted);
            Assert.IsFalse(otherDisposed.Task.IsCompleted);
            Assert.ThrowsExactly<ObjectDisposedException>(() => registry.ProtectPreparation(other.ProviderId));
            release.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            await service.DrainAsync();
            await registry.DisposeAsync();
            Assert.IsTrue(disposed.Task.IsCompleted);
            Assert.IsTrue(otherDisposed.Task.IsCompleted);
        }
        finally { release.TrySetResult(); await refresh; await registry.DisposeAsync(); }
    }

    private sealed class CallbackAdmission(Func<IDisposable?> protect) : ISessionRetentionAdmission
    {
        public IDisposable? TryProtectAll() => protect();
        public IDisposable? TryProtectSession(string sessionId) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task ReentrantDrainIncludesRefreshBeforeSynchronousProviderCallback()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("reentrant"), "Reentrant", "test");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ModelProviderInitializationService? service = null;
        Task? drain = null;
        registry.RegisterOrReplace(descriptor, () => new ReentrantStartRuntime(descriptor, () =>
        {
            drain = service!.DrainAsync();
            return release.Task;
        }));
        service = new ModelProviderInitializationService(registry);
        var refresh = service.RefreshProviderAsync(descriptor.ProviderId);
        try
        {
            Assert.IsNotNull(drain);
            Assert.IsFalse(drain.IsCompleted, "Provider callbacks run only after the original task is published.");
            release.TrySetResult();
            await Task.WhenAll(refresh, drain).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); }
    }

    private sealed class ReentrantStartRuntime(ModelProviderDescriptor descriptor, Func<Task> start) : IModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor => descriptor;
        public Task StartAsync(CancellationToken cancellationToken = default) => start();
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [TestMethod]
    public async Task SynchronouslyCompletedRefreshCanBeExplicitlyRefreshedAgain()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("repeat"), "Repeat", "test");
        var runtime = new TestRuntime(descriptor);
        registry.RegisterOrReplace(descriptor, () => runtime);
        var service = new ModelProviderInitializationService(registry);
        await service.RefreshProviderAsync(descriptor.ProviderId);
        await service.RefreshProviderAsync(descriptor.ProviderId);
        Assert.AreEqual(2, runtime.ProbeCount);
        await service.DrainAsync();
    }

    [TestMethod]
    public async Task RetirementRejectsModelPreparationBeforeProviderFactory()
    {
        await using var registry = new ModelProviderRegistry();
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["fixture"]);
        Assert.IsTrue(protection.Publish("fixture", [], true));
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("refused"), "Refused", "test");
        var constructions = 0;
        registry.RegisterOrReplace(descriptor, () => { constructions++; return new TestRuntime(descriptor); });
        var service = new ModelProviderInitializationService(registry, null, protection);
        using var retirement = protection.TryReserveRetirement("old-session");
        Assert.IsNotNull(retirement);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.GetModelsAsync(descriptor.ProviderId));
        Assert.AreEqual(0, constructions);
    }

    [TestMethod]
    public async Task UnconfirmedProviderDependencyReleaseKeepsPreparationProtected()
    {
        var registry = new ModelProviderRegistry();
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["fixture"]);
        Assert.IsTrue(protection.Publish("fixture", [], true));
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("retained"), "Retained", "test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.RegisterOrReplace(descriptor, () => new HeldProbeRuntime(descriptor, entered, release));
        var service = new ModelProviderInitializationService(registry, null, protection);
        var original = service.GetModelsAsync(descriptor.ProviderId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetException(new AggregateException(new InvalidOperationException("outer",
            new AgentDependencyRetentionException("fixture", "probe", [new IOException("held")], new object()))));
        await Assert.ThrowsExactlyAsync<AgentDependencyRetentionException>(() => original);
        Assert.IsNull(protection.TryReserveRetirement("old-session"), "Unknown dependency termination cannot release protection.");
        await Assert.ThrowsExactlyAsync<AgentDependencyRetentionException>(() => registry.DisposeAsync().AsTask());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HiddenSiblingProviderFailureKeepsPreparationProtected(bool failDuringStart)
    {
        var registry = new ModelProviderRegistry();
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["fixture"]);
        Assert.IsTrue(protection.Publish("fixture", [], true));
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("aggregate"), "Aggregate", "test");
        var probe = new TaskCompletionSource<ModelProviderProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new OriginalProbeRuntime(descriptor, probe.Task, failDuringStart);
        registry.RegisterOrReplace(descriptor, () => runtime);
        var service = new ModelProviderInitializationService(registry, null, protection);
        var original = service.GetModelsAsync(descriptor.ProviderId);
        probe.SetException([new IOException("ordinary first fault"),
            new AgentDependencyRetentionException("fixture", "probe", [new IOException("held")], new object())]);
        var failure = await Assert.ThrowsExactlyAsync<AgentDependencyRetentionException>(() => original);
        var retained = (ModelProviderInitializationService.RetainedPreparation)failure.Dependencies;
        Assert.AreSame(runtime, retained.Runtime);
        Assert.AreSame(probe.Task, retained.Original);
        Assert.IsNotNull(retained.Preparation);
        Assert.IsNotNull(retained.Admission);
        Assert.IsNotNull(retained.Cancellation);
        _ = retained.Cancellation.Token; // The source itself must not have been disposed on the retained path.
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Unregister(descriptor.ProviderId));
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.RegisterOrReplace(descriptor, () => runtime));
        Assert.IsNull(protection.TryReserveRetirement("old-session"), "Await's first exception is not the entire original outcome.");
        await Assert.ThrowsExactlyAsync<AgentDependencyRetentionException>(() => service.RefreshProviderAsync(descriptor.ProviderId));
        await Assert.ThrowsExactlyAsync<AgentDependencyRetentionException>(() => service.DrainAsync());
        Assert.IsNull(protection.TryReserveRetirement("old-session"), "Shutdown cannot turn a retained failure into successful drainage.");
        await Assert.ThrowsExactlyAsync<AgentDependencyRetentionException>(() => registry.DisposeAsync().AsTask());
    }

    private sealed class OriginalProbeRuntime(ModelProviderDescriptor descriptor, Task<ModelProviderProbeResult> probe,
        bool failDuringStart) : IModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor => descriptor;
        public Task StartAsync(CancellationToken cancellationToken = default) => failDuringStart ? probe : Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => probe;
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [TestMethod]
    public async Task RefreshAfterChange_DoesNotTakeTheResultOfAProbeThatStartedBefore()
    {
        // A probe reads the account when it starts: one that started before a sign-in reports it signed out, even when
        // it ends after. What asks for the provider after the sign-in must get a probe that started after it.
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("account"), "Account", "test");
        var runtime = new AccountProbeRuntime(descriptor);
        registry.RegisterOrReplace(descriptor, () => runtime);
        var service = new ModelProviderInitializationService(registry);

        var before = service.RefreshProviderAsync(descriptor.ProviderId);
        await runtime.FirstProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.SignedIn = true;
        var after = service.RefreshProviderAfterChangeAsync(descriptor.ProviderId);
        runtime.ReleaseFirstProbe.TrySetResult();
        await Task.WhenAll(before, after).WaitAsync(TimeSpan.FromSeconds(5));

        var state = service.CurrentStates.Single(value => value.ProviderId == descriptor.ProviderId);
        Assert.AreEqual(ModelProviderAvailability.Ready, state.Availability, state.StatusMessage);
        CollectionAssert.AreEqual(new[] { "model" }, state.Models.Select(static model => model.Id).ToArray());
        Assert.AreEqual(2, runtime.ProbeCount, "the probe that started before is not taken for the one after");
    }

    [TestMethod]
    public async Task RefreshAfterChange_StartsOneProbe_WhenNoneRuns()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("idle"), "Idle", "test");
        var runtime = new TestRuntime(descriptor, [new AgentModelInfo("idle-model")]);
        registry.RegisterOrReplace(descriptor, () => runtime);
        var service = new ModelProviderInitializationService(registry);

        await service.RefreshProviderAfterChangeAsync(descriptor.ProviderId).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, runtime.ProbeCount);
        Assert.AreEqual(ModelProviderAvailability.Ready, service.CurrentStates.Single(value => value.ProviderId == descriptor.ProviderId).Availability);
    }

    // Reads its "account" when a probe starts; the first probe waits until it is released.
    private sealed class AccountProbeRuntime(ModelProviderDescriptor descriptor) : IModelProviderRuntime
    {
        private int _probes;

        public volatile bool SignedIn;

        public TaskCompletionSource FirstProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProbeCount => Volatile.Read(ref _probes);

        public ModelProviderDescriptor Descriptor => descriptor;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            var signedIn = SignedIn;
            if (Interlocked.Increment(ref _probes) == 1)
            {
                FirstProbeStarted.TrySetResult();
                await ReleaseFirstProbe.Task.WaitAsync(cancellationToken);
            }

            return signedIn
                ? new() { ProviderId = descriptor.ProviderId, Availability = ModelProviderAvailability.Ready, Models = [new AgentModelInfo("model")] }
                : new() { ProviderId = descriptor.ProviderId, Availability = ModelProviderAvailability.Failed, StatusMessage = "Not signed in.", ErrorCategory = "signed-out" };
        }

        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HeldProbeRuntime(ModelProviderDescriptor descriptor, TaskCompletionSource entered,
        TaskCompletionSource release, TaskCompletionSource? disposed = null) : IModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor => descriptor;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task;
            return new() { ProviderId = descriptor.ProviderId, Availability = ModelProviderAvailability.Ready, Models = [] };
        }
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();
        public ValueTask DisposeAsync() { disposed?.TrySetResult(); return ValueTask.CompletedTask; }
    }

    [TestMethod]
    public async Task InitializeAllAsync_IsolatesHangingProviderFromReadyProviderAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var readyDescriptor = new ModelProviderDescriptor(new ModelProviderId("ready"), "Ready", "test");
        var hangingDescriptor = new ModelProviderDescriptor(new ModelProviderId("hanging"), "Hanging", "test");
        var readyRuntime = new TestRuntime(readyDescriptor, [new AgentModelInfo("ready-model")]);
        var hangingRuntime = new TestRuntime(hangingDescriptor, hangDuringProbe: true);
        registry.RegisterOrReplace(readyDescriptor, () => readyRuntime);
        registry.RegisterOrReplace(hangingDescriptor, () => hangingRuntime);
        var service = new ModelProviderInitializationService(
            registry,
            new ModelProviderInitializationOptions { DefaultProbeTimeout = TimeSpan.FromMilliseconds(50) });

        var initializeTask = service.InitializeAllAsync(CancellationToken.None);
        await readyRuntime.ProbeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var readyState = service.CurrentStates.Single(state => state.ProviderId == readyDescriptor.ProviderId);
        Assert.AreEqual(ModelProviderAvailability.Ready, readyState.Availability);
        CollectionAssert.AreEqual(new[] { "ready-model" }, readyState.Models.Select(static model => model.Id).ToArray());

        await initializeTask.WaitAsync(TimeSpan.FromSeconds(5));
        var hangingState = service.CurrentStates.Single(state => state.ProviderId == hangingDescriptor.ProviderId);
        Assert.AreEqual(ModelProviderAvailability.Failed, hangingState.Availability);
        Assert.AreEqual("Timeout", hangingState.ErrorCategory);
    }

    [TestMethod]
    public async Task InitializeAllAsync_AProviderThatAsksForMoreTime_IsProbedThatLong()
    {
        // An ACP provider starts an agent, which may be slow to answer, or downloaded first by npx: cut at the default
        // time, its probe never ends, and the provider fails with no word of the agent (#252).
        await using var registry = new ModelProviderRegistry();
        var slowDescriptor = new ModelProviderDescriptor(new ModelProviderId("slow"), "Slow", "test");
        var hangingDescriptor = new ModelProviderDescriptor(new ModelProviderId("hanging"), "Hanging", "test");
        registry.RegisterOrReplace(slowDescriptor, () => new SlowProbeRuntime(slowDescriptor, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10)));
        registry.RegisterOrReplace(hangingDescriptor, () => new SlowProbeRuntime(hangingDescriptor, Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(200)));
        var service = new ModelProviderInitializationService(
            registry,
            new ModelProviderInitializationOptions { DefaultProbeTimeout = TimeSpan.FromMilliseconds(50) });

        await service.InitializeAllAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var slow = service.CurrentStates.Single(state => state.ProviderId == slowDescriptor.ProviderId);
        Assert.AreEqual(ModelProviderAvailability.Ready, slow.Availability, slow.StatusMessage);
        var hanging = service.CurrentStates.Single(state => state.ProviderId == hangingDescriptor.ProviderId);
        Assert.AreEqual((ModelProviderAvailability.Failed, "Timeout"), (hanging.Availability, hanging.ErrorCategory));
        StringAssert.Contains(hanging.StatusMessage, TimeSpan.FromMilliseconds(200).ToString());
    }

    [TestMethod]
    public async Task InitializeAllAsync_AProbeLongerThanTheDefaultTime_LetsTheNextProvidersStart()
    {
        // The probes of agents that take their time do not keep the providers after them waiting.
        await using var registry = new ModelProviderRegistry();
        // Named so that the slow one is prepared first: the providers are prepared in the order of their names.
        var slowDescriptor = new ModelProviderDescriptor(new ModelProviderId("slow"), "A slow agent", "test");
        var readyDescriptor = new ModelProviderDescriptor(new ModelProviderId("ready"), "B ready", "test");
        var slow = new SlowProbeRuntime(slowDescriptor, Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(30));
        var ready = new TestRuntime(readyDescriptor, [new AgentModelInfo("ready-model")]);
        registry.RegisterOrReplace(slowDescriptor, () => slow);
        registry.RegisterOrReplace(readyDescriptor, () => ready);
        var service = new ModelProviderInitializationService(
            registry,
            new ModelProviderInitializationOptions { PreparationPlaceTime = TimeSpan.FromMilliseconds(50) });

        var initialization = service.InitializeAllAsync(maxConcurrency: 1, CancellationToken.None);
        await ready.ProbeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(initialization.IsCompleted);
        Assert.AreEqual(ModelProviderAvailability.Probing, service.CurrentStates.Single(state => state.ProviderId == slowDescriptor.ProviderId).Availability);
        slow.Release.TrySetResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(ModelProviderAvailability.Ready, service.CurrentStates.Single(state => state.ProviderId == slowDescriptor.ProviderId).Availability);
    }

    private sealed class SlowProbeRuntime(ModelProviderDescriptor descriptor, TimeSpan probeDuration, TimeSpan probeTimeout)
        : IModelProviderRuntime, IModelProviderProbeTimeout
    {
        public ModelProviderDescriptor Descriptor => descriptor;
        public TimeSpan ProbeTimeout => probeTimeout;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            await Task.WhenAny(Task.Delay(probeDuration, cancellationToken), Release.Task).Unwrap();
            return new() { ProviderId = descriptor.ProviderId, Availability = ModelProviderAvailability.Ready, Models = [new AgentModelInfo("model")] };
        }
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [TestMethod]
    public async Task InitializeAllAsync_WithAConcurrency_PreparesThatManyProvidersAtATime()
    {
        await using var registry = new ModelProviderRegistry();
        var entered = Enumerable.Range(0, 3).Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var index = 0; index < 3; index++)
        {
            var descriptor = new ModelProviderDescriptor(new ModelProviderId($"held-{index}"), $"Held {index}", "test");
            var started = entered[index];
            registry.RegisterOrReplace(descriptor, () => new HeldProbeRuntime(descriptor, started, release));
        }

        var service = new ModelProviderInitializationService(registry);
        var initializing = service.InitializeAllAsync(maxConcurrency: 2, CancellationToken.None);
        await Task.WhenAll(entered.Take(2).Select(static value => value.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.IsFalse(entered[2].Task.IsCompleted, "The third provider waits for one of the first two.");

        release.SetResult();
        await initializing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(entered[2].Task.IsCompleted);
        Assert.IsTrue(service.CurrentStates.All(static state => state.Availability == ModelProviderAvailability.Ready));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.InitializeAllAsync(maxConcurrency: 0, CancellationToken.None));
    }

    [TestMethod]
    public async Task GetModelsAsync_ReusesModelCatalogLoadedDuringInitializationAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("cached"), "Cached", "test");
        var runtime = new TestRuntime(descriptor, [new AgentModelInfo("cached-model")]);
        registry.RegisterOrReplace(descriptor, () => runtime);
        var service = new ModelProviderInitializationService(registry);

        await service.InitializeAllAsync(CancellationToken.None);
        var first = await service.GetModelsAsync(descriptor.ProviderId, CancellationToken.None);
        var second = await service.GetModelsAsync(new ModelProviderId("CACHED"), CancellationToken.None);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, runtime.ProbeCount);
        CollectionAssert.AreEqual(new[] { "cached-model" }, second.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task GetModelsAsync_PreservesProviderOrderByDefaultAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("ordered"), "Ordered", "test");
        var runtime = new TestRuntime(descriptor, CreateUnsortedModels());
        registry.RegisterOrReplace(descriptor, () => runtime);
        var service = new ModelProviderInitializationService(registry);

        var models = await service.GetModelsAsync(descriptor.ProviderId, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "z-model", "a-model", "b-model" }, models.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task GetModelsAsync_SortsModelsByDisplayNameWhenConfiguredAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("sorted"), "Sorted", "test") { ModelOrder = ModelOrder.Name };
        var runtime = new TestRuntime(descriptor, CreateUnsortedModels());
        registry.RegisterOrReplace(descriptor, () => runtime);
        var service = new ModelProviderInitializationService(registry);

        var models = await service.GetModelsAsync(descriptor.ProviderId, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "a-model", "b-model", "z-model" }, models.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task GetModelsAsync_DoesNotReturnCatalogForUnregisteredProviderAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("removed"), "Removed", "test");
        registry.RegisterOrReplace(descriptor, () => new TestRuntime(descriptor, [new AgentModelInfo("removed-model")]));
        var service = new ModelProviderInitializationService(registry);
        await service.InitializeAllAsync(CancellationToken.None);

        Assert.IsTrue(registry.Unregister(descriptor.ProviderId));

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            () => service.GetModelsAsync(descriptor.ProviderId, CancellationToken.None));
        Assert.AreEqual(0, service.CurrentStates.Count);
    }

    [TestMethod]
    public async Task SetModelOptionAsync_PublishesTheModelAsItIsNow_ForAProviderThatOffersOptions()
    {
        await using var registry = new ModelProviderRegistry();
        var withOptions = new ModelProviderDescriptor(new ModelProviderId("options"), "Options", "test");
        var plain = new ModelProviderDescriptor(new ModelProviderId("plain"), "Plain", "test");
        var context = new AgentModelOption("context", "Context", [new("300k"), new("1m")], "300k");
        var runtime = new OptionsRuntime(withOptions, [new AgentModelInfo("first") { Options = [context] }, new AgentModelInfo("second")]);
        registry.RegisterOrReplace(withOptions, () => runtime);
        registry.RegisterOrReplace(plain, () => new TestRuntime(plain, [new AgentModelInfo("plain-model")]));
        var service = new ModelProviderInitializationService(registry);
        await service.InitializeAllAsync(CancellationToken.None);

        var model = await service.SetModelOptionAsync(withOptions.ProviderId, "first", "context", "1m");

        Assert.AreEqual("1m", model!.Options!.Single().Value);
        var models = await service.GetModelsAsync(withOptions.ProviderId);
        CollectionAssert.AreEqual(new[] { "first", "second" }, models.Select(static listed => listed.Id).ToArray(), "the model keeps its place");
        Assert.AreEqual("1m", models[0].Options!.Single().Value);
        Assert.IsNull(await service.SetModelOptionAsync(plain.ProviderId, "plain-model", "context", "1m"), "a provider that offers no option");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.SetModelOptionAsync(withOptions.ProviderId, "first", "context", "2m"));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => service.SetModelOptionAsync(new ModelProviderId("unknown"), "first", "context", "1m"));
    }

    private sealed class OptionsRuntime(ModelProviderDescriptor descriptor, IReadOnlyList<AgentModelInfo> models) : IModelProviderRuntime, IModelProviderModelOptions
    {
        private IReadOnlyList<AgentModelInfo> _models = models;

        public ModelProviderDescriptor Descriptor { get; } = descriptor;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready, Models = _models });

        public Task<AgentModelInfo?> SetModelOptionAsync(string modelId, string optionId, string value, CancellationToken cancellationToken = default)
        {
            if (_models.FirstOrDefault(model => model.Id == modelId) is not { } model) return Task.FromResult<AgentModelInfo?>(null);
            var option = model.Options?.FirstOrDefault(option => option.Id == optionId) ?? throw new ArgumentException("No such option.", nameof(optionId));
            if (!option.Values.Any(offered => offered.Value == value)) throw new ArgumentException("No such value.", nameof(value));
            var changed = model with { Options = [.. model.Options!.Select(listed => listed.Id == optionId ? listed with { Value = value } : listed)] };
            _models = [.. _models.Select(listed => listed.Id == modelId ? changed : listed)];
            return Task.FromResult<AgentModelInfo?>(changed);
        }

        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static AgentModelInfo[] CreateUnsortedModels()
        =>
        [
            new AgentModelInfo("z-model", DisplayName: "Zulu"),
            new AgentModelInfo("a-model", DisplayName: "alpha"),
            new AgentModelInfo("b-model", DisplayName: "Bravo"),
        ];

    private sealed class TestRuntime : IModelProviderRuntime
    {
        private readonly IReadOnlyList<AgentModelInfo> _models;
        private readonly bool _hangDuringProbe;

        public TestRuntime(
            ModelProviderDescriptor descriptor,
            IReadOnlyList<AgentModelInfo>? models = null,
            bool hangDuringProbe = false)
        {
            Descriptor = descriptor;
            _models = models ?? [];
            _hangDuringProbe = hangDuringProbe;
        }

        public ModelProviderDescriptor Descriptor { get; }

        public int ProbeCount { get; private set; }

        public TaskCompletionSource ProbeCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            if (_hangDuringProbe)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var result = new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = _models,
            };
            ProbeCompleted.TrySetResult();
            return result;
        }

        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
