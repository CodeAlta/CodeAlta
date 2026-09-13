using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Inert shared read-gate regressions; no host, journals, discovery or providers.</summary>
[TestClass]
public sealed class OwnedSessionWorkspaceNotesTests
{
    [TestMethod]
    public Task NotesAndHistory_ShareEightActualReadSlots() => Fixture.Run(async f =>
    {
        var notes = Enumerable.Range(0, 7).Select(_ => f.Keep(() => f.Owner.ReadNotesMarkdownAsync("session", default))).ToArray();
        var history = f.Keep(() => f.Owner.ReadHistoryPageAsync("session", null, default));
        Assert.ThrowsExactly<InvalidOperationException>(() => f.Owner.ReadSnapshotAsync(default));
        Assert.ThrowsExactly<InvalidOperationException>(() => f.Owner.ReadNotesMarkdownAsync("session", default));
        f.Release.TrySetResult("exact");
        foreach (var note in notes) Assert.AreEqual("exact", await note);
        await history;
    });

    [TestMethod]
    public Task CancelledWait_DoesNotFreeOriginalSlot() => Fixture.Run(async f =>
    {
        var cancelled = f.Keep(() => f.Owner.ReadNotesMarkdownAsync("session", f.Caller.Token));
        var others = Enumerable.Range(0, 7).Select(_ => f.Keep(() => f.Owner.ReadNotesMarkdownAsync("session", default))).ToArray();
        var cancellation = f.Keep(() => f.Caller.CancelAsync());
        await cancellation;
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        Assert.ThrowsExactly<InvalidOperationException>(() => f.Owner.ReadNotesMarkdownAsync("session", default));
        f.Release.TrySetResult("retained");
        foreach (var other in others) Assert.AreEqual("retained", await other);
    });

    [TestMethod]
    public Task Closure_JoinsActualReadAndObserver() => Fixture.Run(async f =>
    {
        var read = f.Keep(() => f.Owner.ReadNotesMarkdownAsync("session", default));
        await f.Entered.Task;
        var close = f.Keep(() => f.Owner.DisposeAsync().AsTask());
        Assert.IsFalse(close.IsCompleted);
        Assert.ThrowsExactly<ObjectDisposedException>(() => f.Owner.ReadNotesMarkdownAsync("session", default));
        f.Release.TrySetResult("last");
        Assert.AreEqual("last", await read);
        await close;
    });

    [TestMethod]
    public Task LateReadFailure_IsRetainedByAlreadyStartedDrain() => Fixture.Run(async f =>
    {
        var read = f.Keep(() => f.Owner.ReadNotesMarkdownAsync("session", f.Caller.Token));
        await f.Entered.Task;
        var cancellation = f.Keep(() => f.Caller.CancelAsync());
        await cancellation;
        await Assert.ThrowsAsync<OperationCanceledException>(() => read);
        f.ExpectedDrainFailure = true;
        var drain = f.Keep(() => f.Owner.DisposeAsync().AsTask());
        var failure = new InvalidOperationException("downstream, not capacity");
        f.Release.TrySetException(failure);
        Assert.AreSame(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => drain));
    });

    [TestMethod]
    public Task PrecancelAndInvalidIdentity_DoNotEnterRead() => Fixture.Run(async f =>
    {
        var cancellation = f.Keep(() => f.Caller.CancelAsync());
        await cancellation;
        Assert.ThrowsExactly<OperationCanceledException>(() => f.Owner.ReadNotesMarkdownAsync("session", f.Caller.Token));
        Assert.ThrowsExactly<ArgumentException>(() => f.Owner.ReadNotesMarkdownAsync(" ", default));
        Assert.IsFalse(f.Entered.Task.IsCompleted);
    });

    private sealed class Fixture
    {
        internal TaskCompletionSource<string> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource Caller { get; } = new();
        internal OwnedSessionWorkspace Owner { get; }
        internal bool ExpectedDrainFailure;
        private readonly List<Task> _originals = [];
        private readonly TaskCompletionSource _stopLaunch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _stop = Task.CompletedTask;
        private Task _stopObserver = Task.CompletedTask;
        private Task _lifetimeObserver = Task.CompletedTask;
        private Task? _cancellation;
        private Task? _drain;
        private Task? _lifetime;
        internal Task Keep(Func<Task> start) => Keep(async () => { await start().ConfigureAwait(false); return true; });
        internal Task<T> Keep<T>(Func<Task<T>> start)
        {
            var acquisition = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = Observe(acquisition.Task);
            _originals.Add(observer); // Publish before launch, including synchronous failure and late acquisition.
            try { acquisition.SetResult(start()); } catch (Exception ex) { acquisition.SetException(ex); }
            return observer;
        }
        private static async Task<T> Observe<T>(Task<Task<T>> acquisition) => await (await acquisition.ConfigureAwait(false)).ConfigureAwait(false);

        private Fixture()
        {
            Owner = new(static _ => Task.FromResult<IReadOnlyList<CodeAlta.Catalog.ProjectDescriptor>>([]), Empty,
                async (_, _, token) => { Assert.AreEqual(CancellationToken.None, token); await Release.Task; return null!; },
                (_, token) => { Assert.AreEqual(CancellationToken.None, token); Entered.TrySetResult(); return Release.Task; });
        }

        internal static async Task Run(Func<Fixture, Task> body)
        {
            var f = new Fixture();
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f._stop = f.StopCore(); // Own independent shutdown even if the body never completes.
            f._stopObserver = Settle(f._stop);
            f._lifetime = f.RunCore(body, launch.Task);
            f._lifetimeObserver = Settle(f._lifetime);
            launch.TrySetResult();
            try
            {
                await f._lifetime.WaitAsync(TimeSpan.FromSeconds(10));
                await f._lifetimeObserver;
                f.Caller.Dispose(); // Only after the original body, cancellation and drain have actually joined.
            }
            catch (Exception ex) { ex.Data["RetainedFixture"] = f; ex.Data["OriginalLifetime"] = f._lifetime; throw; }
            finally { f.Release.TrySetResult(""); f._stopLaunch.TrySetResult(); }
        }

        private async Task RunCore(Func<Fixture, Task> body, Task launch)
        {
            await launch;
            Exception? primary = null;
            try { var originalBody = Keep(() => body(this)); await originalBody; }
            catch (Exception ex) { primary = ex; throw; }
            finally
            {
                Release.TrySetResult("");
                _stopLaunch.TrySetResult();
                // Begin all independent obligations before joining any dependent work.
                await Task.WhenAll(_originals.Select(Settle));
                try { await _stopObserver; await _stop; }
                catch (Exception ex) { if (primary is not null) throw new AggregateException(primary, ex); throw; }
            }
        }

        private async Task StopCore()
        {
            await _stopLaunch.Task;
            Release.TrySetResult("");
            _cancellation = Caller.CancelAsync();
            _drain = Owner.DisposeAsync().AsTask();
            await Task.WhenAll(Settle(_cancellation), Settle(_drain));
            await _cancellation;
            if (!ExpectedDrainFailure) await _drain;
        }

        private static async Task Settle(Task original) { try { await original; } catch { /* Inspected by body/drain assertions. */ } }
        private static async IAsyncEnumerable<AgentSessionMetadata> Empty([EnumeratorCancellation] CancellationToken token)
        { await Task.CompletedTask; token.ThrowIfCancellationRequested(); yield break; }
    }
}
