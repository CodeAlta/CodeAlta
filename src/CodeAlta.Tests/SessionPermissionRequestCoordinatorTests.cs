using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionPermissionRequestCoordinatorTests
{
    [TestMethod]
    public void CancellationBeforeQueuedShow_DoesNotPresentAndJoinsQueuedWork()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var response = fixture.Coordinator.HandleAsync("session", SessionPermissionServiceTests.Request(), cancellation.Token);
        var show = fixture.Dispatcher.Take();
        cancellation.Cancel();
        Assert.IsFalse(response.IsCompleted, "Queued presentation must be joined, not abandoned.");
        show.Run();
        fixture.Dispatcher.Take().Run(); // Timeline, even without a tab.
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, response.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult().Kind);
        Assert.AreEqual(0, fixture.Terminal.DialogCount);
        Assert.AreEqual(0, fixture.Service.ListAsync().AsTask().GetAwaiter().GetResult().Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void VisibleDialog_CallerCancellationOrOwnerDisposalClosesBeforeReturning(bool shutdown)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var response = fixture.Coordinator.HandleAsync("session", SessionPermissionServiceTests.Request(), cancellation.Token);
        fixture.Dispatcher.Take().Run();
        fixture.Terminal.Tick();
        Assert.AreEqual(1, fixture.Terminal.DialogCount);
        if (shutdown)
        {
            fixture.Service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else
        {
            cancellation.Cancel();
        }

        fixture.Dispatcher.Take().Run(); // Close is marshaled and joined.
        fixture.Dispatcher.Take().Run(); // Timeline.
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, response.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult().Kind);
        Assert.AreEqual(0, fixture.Terminal.DialogCount);
        Assert.AreEqual(0, fixture.Service.ListAsync().AsTask().GetAwaiter().GetResult().Count);
    }

    [TestMethod]
    public void ResolutionWithoutOpenTab_CompletesAndClosesVisibleDialog()
    {
        using var fixture = new Fixture();
        var response = fixture.Coordinator.HandleAsync("session", SessionPermissionServiceTests.Request(), CancellationToken.None);
        fixture.Dispatcher.Take().Run();
        Assert.AreEqual(1, fixture.Terminal.DialogCount);
        var snapshot = fixture.Service.ListAsync().AsTask().GetAwaiter().GetResult().Single();
        Assert.IsTrue(fixture.Service.ResolveAsync(snapshot.Handle, AgentPermissionDecisionKind.AllowForSession).AsTask().GetAwaiter().GetResult());
        fixture.Dispatcher.Take().Run();
        fixture.Dispatcher.Take().Run();
        Assert.AreEqual(AgentPermissionDecisionKind.AllowForSession, response.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult().Kind);
        Assert.AreEqual(0, fixture.Terminal.DialogCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PresentationFailure_CancelsPendingAndPropagatesObservedException(bool afterShow)
    {
        using var fixture = new Fixture();
        var response = fixture.Coordinator.HandleAsync("session", SessionPermissionServiceTests.Request(), CancellationToken.None);
        var show = fixture.Dispatcher.Take();
        var snapshot = fixture.Service.ListAsync().AsTask().GetAwaiter().GetResult().Single();
        if (afterShow)
        {
            show.RunAndFail();
            Assert.AreEqual(1, fixture.Terminal.DialogCount);
            fixture.Dispatcher.Take().Run(); // Even a partially successful presentation is closed.
        }
        else
        {
            show.Fail();
        }
        Assert.ThrowsExactly<InvalidOperationException>(() => response.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
        Assert.AreEqual(0, fixture.Service.ListAsync().AsTask().GetAwaiter().GetResult().Count);
        Assert.IsFalse(fixture.Service.ResolveAsync(snapshot.Handle, AgentPermissionDecisionKind.AllowOnce).AsTask().GetAwaiter().GetResult());
        Assert.AreEqual(0, fixture.Terminal.DialogCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestTempDirectory _root = TestTempDirectory.Create();
        public PermissionTerminalFixture Terminal { get; } = new();
        public SessionPermissionService Service { get; } = new();
        public ControlledDispatcher Dispatcher { get; } = new();
        public SessionPermissionRequestCoordinator Coordinator { get; }

        public Fixture()
        {
            // Catalog constructors only, explicit owned root; no loads, runtime, providers or app startup.
            var options = new CatalogOptions { GlobalRoot = _root.Path };
            var state = TestSessionStateServices.CreateCoordinator(new ProjectCatalog(options), new SessionViewCatalog(options),
                Dispatcher, new ShellStateStore(Dispatcher));
            var selection = new SessionSelectionContext(state, static (_, _) => Task.CompletedTask, static _ => false);
            var context = new ShellSessionCommandContext(
                new DelegatingSessionLifecycleCommandPort(static _ => Task.FromResult<SessionViewDescriptor?>(null),
                    static _ => Task.FromResult<SessionViewDescriptor?>(null), static () => Task.CompletedTask),
                new SessionCommandUiPort(Dispatcher, static () => false, static () => false,
                    static () => { }, static () => { }, static () => { }, static () => { }, static (_, action, _) => action()),
                new PromptSessionPort(Dispatcher, static () => true, static () => { }, static _ => { }, static () => [], static _ => { }),
                static () => new PromptSessionId("test"),
                new ShellStatusPort(Dispatcher, static (_, _, _) => { }, static (_, _, _, _) => { }));
            Coordinator = new(selection, context, Dispatcher, Service);
        }

        public void Dispose()
        {
            Service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Terminal.Dispose();
            _root.Dispose();
        }
    }

    private sealed class ControlledDispatcher : IUiDispatcher
    {
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>();
        public bool CheckAccess() => Environment.CurrentManagedThreadId == _owner;
        public void Post(Action action) => throw new AssertFailedException("No unjoined UI posts are allowed.");
        public Task InvokeAsync(Action action)
        {
            var work = new Work(action);
            Assert.IsTrue(_work.Writer.TryWrite(work));
            return work.Completion.Task;
        }

        public Task<T> InvokeAsync<T>(Func<T> action) => throw new AssertFailedException("Unexpected background UI read.");
        public Work Take() => _work.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    private sealed class Work(Action action)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Run()
        {
            try { action(); Completion.SetResult(); }
            catch (Exception exception) { Completion.SetException(exception); }
        }
        public void Fail() => Completion.SetException(new InvalidOperationException("Controlled presentation failure."));
        public void RunAndFail()
        {
            action();
            Fail();
        }
    }
}
