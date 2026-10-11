using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginTaskRetentionTests
{
    [TestMethod]
    public async Task RuntimeActivation_PassesAdmissionToOriginalTaskOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-task-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var admission = new Admission();
        var plugin = new TaskPlugin();
        await using var runtime = new PluginRuntimeManager();
        try
        {
            Assert.IsNull(runtime.SessionRetentionAdmission);
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = root,
                SessionRetentionAdmission = admission,
                BuiltIns = [new BuiltInPluginDefinition { Id = "held", DisplayName = "Held", PluginType = typeof(TaskPlugin), Factory = () => plugin }],
            });
            await plugin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreSame(admission, runtime.SessionRetentionAdmission);
            Assert.IsTrue(runtime.IsStarted);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = root,
                SessionRetentionAdmission = new Admission(),
            }).AsTask());
            Assert.AreSame(admission, runtime.SessionRetentionAdmission, "Rejected startup cannot replace the original admission identity.");
            Assert.AreEqual(1, admission.Claims);
            plugin.Finish.SetResult();
            await runtime.DisposeAsync();
            Assert.AreEqual(0, admission.Claims);
        }
        finally
        {
            plugin.Finish.TrySetResult();
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A synthetic activation-owned task.</summary>
    public sealed class TaskPlugin : PluginBase
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
        {
            Tasks.Run("held", async _ =>
            {
                Entered.SetResult();
                await Finish.Task;
            });
            return ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    public async Task OriginalTaskAndCancellationCleanup_OwnClaimDespiteCanceledWaiter()
    {
        var admission = new Admission();
        using var lifetime = new CancellationTokenSource();
        var tasks = new PluginRuntimeTaskService(lifetime.Token, admission);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = tasks.Run("held", async _ =>
        {
            Assert.AreEqual(1, admission.Claims);
            entered.SetResult();
            await finish.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        handle.RequestCancellation();
        lifetime.Cancel();
        using var waiter = new CancellationTokenSource();
        waiter.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => tasks.WhenIdleAsync(waiter.Token).AsTask());
        Assert.AreEqual(1, tasks.RunningTaskCount);
        Assert.IsTrue(tasks.HasRunningTasks);
        Assert.AreEqual(1, admission.Claims, "Cancellation and presentation reads cannot release original work.");
        finish.SetResult();
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await tasks.WhenIdleAsync();
        Assert.AreEqual(0, admission.Claims);
    }

    [TestMethod]
    public async Task CancellationCallback_StillOwnsClaimAfterOriginalWorkEnds()
    {
        var admission = new Admission();
        var tasks = new PluginRuntimeTaskService(CancellationToken.None, admission);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        var task = tasks.Run("held cancellation", async token =>
        {
            // Deliberately leave the registration to the CTS owner: cleanup must join this callback too.
            token.Register(() => { callbackEntered.SetResult(); callbackRelease.Wait(TimeSpan.FromSeconds(10)); });
            entered.SetResult();
            await finish.Task;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            task.RequestCancellation();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            finish.SetResult();
            await task.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, admission.Claims);
            Assert.IsTrue(tasks.HasRunningTasks);
        }
        finally
        {
            finish.TrySetResult();
            callbackRelease.Set();
        }
        await tasks.WhenIdleAsync();
        Assert.AreEqual(0, admission.Claims);
    }

    [TestMethod]
    public async Task RefusedAdmission_DoesNotPublishOrStartTask_ExplicitIndependentTaskCanRun()
    {
        var admission = new Admission { Retiring = true };
        var tasks = new PluginRuntimeTaskService(CancellationToken.None, admission);
        var calls = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() => tasks.Run("refused", _ =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.CompletedTask;
        }));
        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, tasks.RunningTaskCount);
        var independent = tasks.Run("declared independent", _ =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.CompletedTask;
        }, new PluginTaskOptions { SessionRetentionIndependent = true });
        await independent.Completion;
        await tasks.WhenIdleAsync();
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, admission.Attempts, "Only an explicit task contract opts out, regardless of the task/plugin name.");
    }

    [TestMethod]
    public async Task FaultedOriginalTask_ReleasesClaimAfterCleanup()
    {
        var admission = new Admission();
        var tasks = new PluginRuntimeTaskService(CancellationToken.None, admission);
        var task = tasks.Run("fault", _ => throw new InvalidOperationException("synthetic"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => task.Completion);
        await tasks.WhenIdleAsync();
        Assert.AreEqual(0, admission.Claims);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NestedRetainedDependencyFault_DoesNotAuthorizeTaskCleanup(bool pluginFailure)
    {
        var admission = new Admission();
        var tasks = new PluginRuntimeTaskService(CancellationToken.None, admission);
        Exception retained = pluginFailure
            ? new PluginEventDependencyException(new Exception("creation"), new Exception("barrier"), new object())
            : new AgentDependencyRetentionException("test", "release", [new Exception("release")], new object());
        var failure = new AggregateException(new Exception("ordinary"), new InvalidOperationException("wrapper", retained));
        var task = tasks.Run("retained", _ => throw failure);
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<AggregateException>(() => task.Completion));
        await Assert.ThrowsExactlyAsync<AggregateException>(() => tasks.WhenIdleAsync().AsTask());
        Assert.AreEqual(1, admission.Claims);
        Assert.IsTrue(tasks.HasRunningTasks, "Failed dependency cleanup remains owned rather than being removed as history.");
        Assert.ThrowsExactly<ObjectDisposedException>(() => tasks.Run("after failure", _ => ValueTask.CompletedTask));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MultiplyFaultedOriginalTask_RetainsNonFirstDependencyFailure(bool pluginFailure)
    {
        var admission = new Admission();
        var tasks = new PluginRuntimeTaskService(CancellationToken.None, admission);
        var ordinary = new InvalidOperationException("first ordinary failure");
        Exception retained = pluginFailure
            ? new PluginEventDependencyException(new Exception("creation"), new Exception("barrier"), new object())
            : new AgentDependencyRetentionException("test", "release", [new Exception("release")], new object());
        var original = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original.SetException([ordinary, retained]);
        var task = tasks.Run("multiple faults", _ => new ValueTask(original.Task));
        var outcome = await Assert.ThrowsExactlyAsync<AggregateException>(() => task.Completion);
        Assert.AreSame(ordinary, outcome.InnerExceptions[0]);
        Assert.AreSame(retained, outcome.InnerExceptions[1]);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => tasks.WhenIdleAsync().AsTask());
        Assert.AreEqual(1, admission.Claims);
        Assert.IsTrue(tasks.HasRunningTasks);
    }

    [TestMethod]
    public async Task Startup_OwnsClaimBeforeActivation_AndInProgressIdentityIsNotStarted()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-start-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var admission = new Admission();
        var plugin = new HeldActivationPlugin();
        var runtime = new PluginRuntimeManager();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var startup = runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = root,
                SessionRetentionAdmission = admission,
                BuiltIns = [new BuiltInPluginDefinition { Id = "held", DisplayName = "Held", PluginType = typeof(HeldActivationPlugin), Factory = () => plugin }],
            }).AsTask();
            await plugin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, admission.Claims);
            Assert.AreSame(admission, runtime.SessionRetentionAdmission);
            Assert.IsFalse(runtime.IsStarted);
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => startup.WaitAsync(cancellation.Token));
            Assert.AreEqual(1, admission.Claims);
            Assert.IsFalse(runtime.IsStarted);
            plugin.Finish.SetResult();
            await startup;
            Assert.IsTrue(runtime.IsStarted);
            Assert.AreEqual(0, admission.Claims);
        }
        finally
        {
            plugin.Finish.TrySetResult();
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A synthetic original activation held independently of caller cancellation.</summary>
    public sealed class HeldActivationPlugin : PluginBase
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; init; }
        public Task? OriginalFailure { get; init; }
        public bool FailAtStartup { get; init; }
        public bool FailAtInitialization { get; init; }
        public bool FailAtDisposal { get; init; }
        public Task? InitialFailure { get; init; }
        public override ValueTask InitializeAsync(CancellationToken cancellationToken = default)
            => FailAtDisposal ? new ValueTask(InitialFailure ?? Task.FromException(new InvalidOperationException("ordinary initialization failure")))
                : FailAtInitialization ? RunOriginal() : ValueTask.CompletedTask;

        public override ValueTask DisposeAsync()
            => FailAtDisposal ? RunOriginal() : base.DisposeAsync();

        public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
            => FailAtStartup ? ValueTask.CompletedTask : RunOriginal();

        public override IEnumerable<PluginStartupContribution> GetStartupContributions()
        {
            if (FailAtStartup) yield return new PluginStartupContribution { Name = "held", Handler = (_, _) => RunOriginal() };
        }

        private ValueTask RunOriginal()
        {
            Entered.SetResult();
            return new ValueTask(OriginalFailure ?? CompleteAsync());
        }

        private async Task CompleteAsync()
        {
            await Finish.Task;
            if (Failure is not null) throw Failure;
        }
    }

    [TestMethod]
    [DataRow(true, false, false, "activation")]
    [DataRow(false, false, false, "activation")]
    [DataRow(false, true, false, "activation")]
    [DataRow(false, false, true, "activation")]
    [DataRow(false, true, true, "activation")]
    [DataRow(false, false, true, "startup")]
    [DataRow(false, true, true, "startup")]
    [DataRow(false, false, true, "initialization")]
    [DataRow(false, true, true, "initialization")]
    [DataRow(false, false, true, "disposal")]
    [DataRow(false, true, true, "disposal")]
    public async Task Startup_RefusesBeforeActivation_OrRetainsDependencyFailure(bool refuse, bool pluginFailure, bool multiple, string stage)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-start-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var admission = new Admission { Retiring = refuse };
        Exception retained = pluginFailure
            ? new PluginEventDependencyException(new Exception("creation"), new Exception("barrier"), new object())
            : new AgentDependencyRetentionException("activation", "release", [new Exception("release")], new object());
        var failure = new InvalidOperationException("wrapper", retained);
        var original = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original.SetException([new Exception("first ordinary failure"), failure]);
        var firstCreationFailure = new InvalidOperationException("ordinary initialization failure");
        var secondCreationFailure = new InvalidOperationException("another initialization failure");
        var initialFailure = stage == "disposal" ? Task.WhenAll(Task.FromException(firstCreationFailure), Task.FromException(secondCreationFailure)) : null;
        var plugin = new HeldActivationPlugin
        {
            Failure = failure,
            OriginalFailure = multiple ? original.Task : null,
            FailAtStartup = stage == "startup",
            FailAtInitialization = stage == "initialization",
            FailAtDisposal = stage == "disposal",
            InitialFailure = initialFailure,
        };
        plugin.Finish.SetResult();
        var runtime = new PluginRuntimeManager();
        try
        {
            var startup = runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = root,
                SessionRetentionAdmission = admission,
                BuiltIns = [new BuiltInPluginDefinition { Id = "held", DisplayName = "Held", PluginType = typeof(HeldActivationPlugin), Factory = () => plugin }],
            }).AsTask();
            if (refuse) await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => startup);
            else
            {
                AggregateException outcome;
                if (stage == "startup") outcome = await Assert.ThrowsExactlyAsync<AggregateException>(() => startup);
                else
                {
                    var retainedActivation = await Assert.ThrowsExactlyAsync<AgentDependencyRetentionException>(() => startup);
                    Assert.IsInstanceOfType<IReadOnlyList<object>>(retainedActivation.Dependencies);
                    var inventory = (IReadOnlyList<object>)retainedActivation.Dependencies;
                    Assert.IsTrue(inventory.Contains(plugin), "An arbitrary plugin marker does not root its own activation.");
                    var context = inventory.OfType<PluginRuntimeContext>().Single();
                    Assert.IsTrue(inventory.Contains(context.Services));
                    Assert.IsTrue(inventory.Contains(context.Services.Tasks));
                    Assert.AreEqual(1, inventory.OfType<PluginActivationLifetime>().Count());
                    if (multiple) Assert.IsTrue(inventory.Contains(original.Task), "Keep the actual original, not only its selected await exception.");
                    if (initialFailure is not null) Assert.IsTrue(inventory.Contains(initialFailure));
                    Assert.IsTrue(inventory.OfType<PluginContributionRegistry>().Any());
                    // Callback task failures stay complete and unflattened under the owning marker.
                    outcome = stage == "disposal" ? retainedActivation : (AggregateException)retainedActivation.InnerExceptions.Single();
                }
                Assert.IsTrue(PluginDependencyRetention.Contains(outcome));
                if (multiple && stage != "disposal") Assert.AreSame(failure, outcome.InnerExceptions[1]);
                if (stage == "disposal")
                {
                    var creationFailures = (AggregateException)outcome.InnerExceptions[0];
                    Assert.AreSame(firstCreationFailure, creationFailures.InnerExceptions[0]);
                    Assert.AreSame(secondCreationFailure, creationFailures.InnerExceptions[1]);
                    Assert.AreSame(failure, ((AggregateException)outcome.InnerExceptions[1]).InnerExceptions[1]);
                }
            }
            Assert.AreEqual(!refuse, plugin.Entered.Task.IsCompleted);
            Assert.IsFalse(runtime.IsStarted);
            Assert.AreEqual(refuse ? 0 : 1, admission.Claims);
        }
        finally
        {
            // A failed original startup prevents manager dependency disposal by design.
            if (refuse) await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.DisposeAsync().AsTask());
            else if (stage == "startup") await Assert.ThrowsExactlyAsync<AggregateException>(() => runtime.DisposeAsync().AsTask());
            else await Assert.ThrowsExactlyAsync<AgentDependencyRetentionException>(() => runtime.DisposeAsync().AsTask());
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Admission : ISessionRetentionAdmission
    {
        private int _claims;
        public int Claims => Volatile.Read(ref _claims);
        public int Attempts { get; private set; }
        public bool Retiring { get; init; }
        public IDisposable? TryProtectSession(string sessionId) => TryProtectAll();
        public IDisposable? TryProtectAll()
        {
            Attempts++;
            if (Retiring) return null;
            Interlocked.Increment(ref _claims);
            return new Claim(this);
        }
        private sealed class Claim(Admission owner) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._claims);
            }
        }
    }
}
