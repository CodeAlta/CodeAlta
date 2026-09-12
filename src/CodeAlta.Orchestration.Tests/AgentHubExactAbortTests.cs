using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class AgentHubExactAbortTests
{
    [TestMethod]
    public Task AbortRun_HeldSendAndTrustedJoinAllowCallbackControlReadAndStopJoins() => Fixture.Run(true, async f =>
    {
        var send = f.Keep(f.Hub.RunAsync(f.Handle, new() { Input = AgentInput.Text("inert") }));
        await f.Wait(f.Session.SendEntered.Task);
        f.Session.Read = () => f.Keep(f.Hub.GetSessionHistoryAsync(f.Handle));
        var exact = f.Keep(f.Hub.AbortRunAsync(f.Handle, new("run")));
        await f.Wait(f.Session.CallbackEntered.Task);
        var trusted = f.Keep(f.Hub.AbortAsync(f.Handle));
        await f.Wait(f.Session.TrustedEntered.Task);
        f.Session.AllowRead.TrySetResult();
        await f.Wait(f.Session.ReadCompleted.Task);
        Assert.AreEqual(1, f.Session.HistoryCalls);
        var stop = f.Keep(f.Hub.StopSessionAsync(f.Handle));
        Assert.IsFalse(stop.IsCompleted);
        Assert.IsFalse(exact.IsCompleted);
        Assert.IsFalse(trusted.IsCompleted);
        Assert.IsFalse(send.IsCompleted);
        Assert.IsFalse(f.Session.Disposed);
        var removed = f.Keep(f.Hub.AbortRunAsync(f.Handle, new("run")));
        await f.Expect<InvalidOperationException>(removed);
        f.Session.ReleaseAll();
        Assert.AreEqual(AgentTargetedAbortOutcome.CancellationSignalled, await f.Wait(exact));
        await f.Wait(trusted);
        await f.Wait(send);
        await f.Wait(stop);
        Assert.IsTrue(f.Session.Disposed);
        Assert.AreEqual(1, f.Session.Traversals);
    });

    [TestMethod]
    public Task AbortRun_UnsupportedHasNoFallbackAndLegacyTrustedRemainsSerialized() => Fixture.Run(false, async f =>
    {
        await f.Expect<InvalidOperationException>(f.Keep(f.Hub.AbortRunAsync(AgentSessionHandleId.NewVersion7(), new("run"))));
        var unsupported = f.Keep(f.Hub.AbortRunAsync(f.Handle, new("run")));
        await f.Expect<NotSupportedException>(unsupported);
        Assert.AreEqual(0, f.Session.TrustedCalls);
        var trusted = f.Keep(f.Hub.AbortAsync(f.Handle));
        await f.Wait(f.Session.TrustedEntered.Task);
        var read = f.Keep(f.Hub.GetSessionHistoryAsync(f.Handle));
        Assert.AreEqual(0, f.Session.HistoryCalls);
        Assert.IsFalse(read.IsCompleted);
        var stillUnsupported = f.Keep(f.Hub.AbortRunAsync(f.Handle, new("run")));
        await f.Expect<NotSupportedException>(stillUnsupported);
        Assert.AreEqual(1, f.Session.TrustedCalls);
        f.Session.ReleaseAll();
        await f.Wait(trusted);
        await f.Wait(read);
        await f.Wait(f.Keep(f.Hub.StopSessionAsync(f.Handle)));
        await f.Expect<InvalidOperationException>(f.Keep(f.Hub.AbortRunAsync(f.Handle, new("run"))));
    });

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _work = [];
        private readonly HashSet<Task> _expected = [];
        private readonly List<Exception> _failures = [];
        private Task? _lifetime;
        private Task? _deadline;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-hub-exact-" + Guid.NewGuid().ToString("N"));
        internal AgentHub Hub { get; private set; } = null!;
        internal Session Session { get; private set; } = null!;
        internal AgentSessionHandleId Handle { get; private set; }
        internal Task Keep(Task work) { lock (_gate) _work.Add(work); return work; }
        internal Task<T> Keep<T>(Task<T> work) { Keep((Task)work); return work; }
        internal Task Wait(Task work) => Keep(Keep(work).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<T> Wait<T>(Task<T> work) => Keep(Keep(work).WaitAsync(TimeSpan.FromSeconds(5)));
        internal async Task Expect<T>(Task work) where T : Exception
        {
            lock (_gate) _expected.Add(work);
            await Wait(Keep(Assert.ThrowsAsync<T>(() => work)));
        }
        internal static async Task Run(bool exact, Func<Fixture, Task> body)
        {
            var f = new Fixture();
            f._lifetime = f.Lifetime(exact, body);
            f._deadline = f._lifetime.WaitAsync(TimeSpan.FromSeconds(10));
            try { await f._deadline; }
            catch (Exception ex) { lock (f._gate) f._failures.Add(ex); }
            Exception[] failures;
            lock (f._gate) failures = [.. f._failures];
            if (failures.Length != 0)
            {
                var error = new AggregateException("Hub exact fixture retained at " + f._root, failures);
                error.Data["RetainedFixture"] = f;
                throw error;
            }
            // Explicit roots are intentionally retained for audit, even after success.
        }
        private async Task Lifetime(bool exact, Func<Fixture, Task> body)
        {
            try
            {
                for (var node = new DirectoryInfo(Path.GetDirectoryName(_root)!); node is not null; node = node.Parent)
                    if ((node.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse fixture ancestry.");
                if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root already exists.");
                Directory.CreateDirectory(_root);
                Session = exact ? new ExactSession() : new Session();
                Session.Root = _root;
                var runtime = new Runtime(Session);
                var registry = new ModelProviderRegistry();
                registry.RegisterOrReplace(runtime.Descriptor, () => runtime);
                Hub = new AgentHub(registry, _root);
                var handle = await Keep(Hub.StartSessionAsync(new()
                {
                    SessionId = "isolated-session", ProviderKey = "exact-fixture", WorkingDirectory = _root,
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
                }));
                Handle = handle.HandleId;
                await Keep(body(this));
            }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
            finally
            {
                Session?.ReleaseAll();
                if (Hub is not null) _ = Keep(Hub.DisposeAsync().AsTask());
                Task[] tasks;
                lock (_gate) tasks = [.. _work];
                foreach (var task in tasks) await Join(task);
                Task[] late;
                lock (_gate) late = _work.Except(tasks).ToArray();
                foreach (var task in late) await Join(task);
            }
        }
        private async Task Join(Task task)
        {
            try { await task; }
            catch (Exception ex) { lock (_gate) if (!_expected.Contains(task)) _failures.Add(ex); }
        }
    }

    private sealed class Runtime(Session session) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new("exact-fixture"), "Inert exact fixture");
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No probe.");
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("No raw provider route.");
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default) => Task.FromResult<IAgentSession>(session);
        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No resume.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class Session : IAgentSession
    {
        internal string Root { get; set; } = "";
        internal Func<Task>? Read { get; set; }
        internal int HistoryCalls, TrustedCalls, Traversals;
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TrustedEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseTrusted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ReleaseAll() { ReleaseSend.TrySetResult(); ReleaseTrusted.TrySetResult(); AllowRead.TrySetResult(); ReleaseCallback.TrySetResult(); }
        public ModelProviderId ProviderId => new("exact-fixture");
        public string SessionId => "isolated-session";
        public string? WorkspacePath => Root;
        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();
        public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
        { SendEntered.TrySetResult(); await ReleaseSend.Task; return new("run"); }
        public virtual async Task AbortAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref TrustedCalls); TrustedEntered.TrySetResult(); await ReleaseTrusted.Task; }
        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No steering.");
        public Task CompactAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No compaction.");
        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref HistoryCalls); return Task.FromResult<IReadOnlyList<AgentEvent>>([]); }
        public virtual ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }

    private sealed class ExactSession : Session, IAgentTargetedAbortProvider
    {
        private readonly CancellationTokenSource _source = new();
        private readonly TaskCompletionSource _launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _callback;
        private readonly Task _work;
        internal ExactSession()
        {
            _callback = _source.Token.Register(() =>
            {
                CallbackEntered.TrySetResult();
                AllowRead.Task.GetAwaiter().GetResult();
                Read?.Invoke().GetAwaiter().GetResult();
                ReadCompleted.TrySetResult();
                ReleaseCallback.Task.GetAwaiter().GetResult();
            });
            _work = CancelAsync();
        }
        private async Task CancelAsync()
        { await _launch.Task; Interlocked.Increment(ref Traversals); var traversal = _source.CancelAsync(); await traversal; }
        public async Task<AgentTargetedAbortOutcome> AbortRunAsync(AgentRunId expectedRunId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedRunId.Value != "run") return AgentTargetedAbortOutcome.TargetNotActive;
            _launch.TrySetResult();
            await _work;
            return AgentTargetedAbortOutcome.CancellationSignalled;
        }
        public override async Task AbortAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref TrustedCalls); _launch.TrySetResult(); TrustedEntered.TrySetResult(); await _work; }
        public override async ValueTask DisposeAsync()
        {
            _launch.TrySetResult();
            await _work;
            await _callback.DisposeAsync();
            _source.Dispose();
            await base.DisposeAsync();
        }
    }
}
