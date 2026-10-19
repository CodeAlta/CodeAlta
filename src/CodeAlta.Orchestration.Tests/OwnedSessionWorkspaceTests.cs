using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Literal callbacks and retained gates only; no source, store, configuration or native reads.</summary>
[TestClass]
public sealed class OwnedSessionWorkspaceTests
{
    [TestMethod]
    public Task Snapshot_FullyJoinsDirectReadsAfterCallerCancellation() => RunAsync(async f =>
    {
        var caller = f.CreateCaller();
        var reads = f.CreateReads();
        var read = f.Keep(reads.ReadSnapshotAsync(caller.Token), typeof(TaskCanceledException));
        var cancellation = f.Keep(caller.CancelAsync());
        await f.Wait(cancellation);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => f.Wait(read));
        var disposal = f.Keep(reads.DisposeAsync().AsTask());
        Assert.IsFalse(disposal.IsCompleted);
        f.Release.TrySetResult();
        await f.Wait(disposal);
        Assert.AreEqual(1, f.Enumerations);
        Assert.AreEqual(CancellationToken.None, f.ActualToken);
    });

    [TestMethod]
    public Task SnapshotCapacity_WaitsBoundedThenRefusesBusyWithoutLaunchingAdditionalWork() => RunAsync(async f =>
    {
        var reads = f.CreateReads(wait: TimeSpan.FromMilliseconds(50));
        var ready = f.Keep(f.FourStarted.Task);
        for (var i = 0; i < OwnedSessionWorkspace.SnapshotSlots; i++) _ = f.Keep(reads.ReadSnapshotAsync(CancellationToken.None));
        // No synchronous refusal: the fifth snapshot waits for a slot, then is refused as busy.
        var refused = f.Keep(reads.ReadSnapshotAsync(CancellationToken.None), typeof(OwnedWorkspaceBusyException));
        await Assert.ThrowsExactlyAsync<OwnedWorkspaceBusyException>(() => f.Wait(refused));
        await f.Wait(ready);
        Assert.AreEqual(4, f.Starts);
        f.Release.TrySetResult();
    });

    [TestMethod]
    public Task ReadCapacity_IsNotTakenBySnapshots_AndRefusesBusyOnlyAfterItsWait() => RunAsync(async f =>
    {
        var reads = f.CreateReads(wait: TimeSpan.FromMilliseconds(50));
        var ready = f.Keep(f.EightHistoryStarted.Task);
        for (var i = 0; i < OwnedSessionWorkspace.ReadSlots; i++) _ = f.Keep(reads.ReadHistoryPageAsync("literal", null, CancellationToken.None));
        await f.Wait(ready);
        // Eight history reads hold every read slot: a snapshot still starts, a ninth read is refused, unstarted.
        _ = f.Keep(reads.ReadSnapshotAsync(CancellationToken.None));
        var refused = f.Keep(reads.ReadHistoryPageAsync("literal", null, CancellationToken.None), typeof(OwnedWorkspaceBusyException));
        await Assert.ThrowsExactlyAsync<OwnedWorkspaceBusyException>(() => f.Wait(refused));
        Assert.AreEqual(8, f.HistoryStarts);
        Assert.AreEqual(1, f.Starts);
        f.Release.TrySetResult();
    });

    [TestMethod]
    public Task WaitingRead_IsAdmittedOnceAnActualReadEnds() => RunAsync(async f =>
    {
        var reads = f.CreateReads(wait: TimeSpan.FromSeconds(30));
        var ready = f.Keep(f.EightHistoryStarted.Task);
        var originals = Enumerable.Range(0, OwnedSessionWorkspace.ReadSlots).Select(_ => f.Keep(reads.ReadHistoryPageAsync("literal", null, CancellationToken.None))).ToArray();
        await f.Wait(ready);
        var waiting = f.Keep(reads.ReadHistoryPageAsync("literal", null, CancellationToken.None));
        Assert.IsFalse(waiting.IsCompleted);
        Assert.AreEqual(8, f.HistoryStarts);
        f.Release.TrySetResult();
        await f.Wait(waiting);
        foreach (var original in originals) await f.Wait(original);
        Assert.AreEqual(9, f.HistoryStarts);
    });

    [TestMethod]
    public Task CanceledWaitForASlot_HoldsNoSlotAndStartsNothing() => RunAsync(async f =>
    {
        var reads = f.CreateReads(wait: TimeSpan.FromSeconds(30));
        var ready = f.Keep(f.EightHistoryStarted.Task);
        for (var i = 0; i < OwnedSessionWorkspace.ReadSlots; i++) _ = f.Keep(reads.ReadHistoryPageAsync("literal", null, CancellationToken.None));
        await f.Wait(ready);
        var caller = f.CreateCaller();
        var waiting = f.Keep(reads.ReadHistoryPageAsync("literal", null, caller.Token), typeof(OperationCanceledException));
        await f.Wait(f.Keep(caller.CancelAsync()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Wait(waiting));
        f.Release.TrySetResult();
        // The slots the eight reads give back are all free again: eight more are admitted at once.
        var later = Enumerable.Range(0, OwnedSessionWorkspace.ReadSlots).Select(_ => f.Keep(reads.ReadHistoryPageAsync("literal", null, CancellationToken.None))).ToArray();
        foreach (var read in later) await f.Wait(read);
        Assert.AreEqual(16, f.HistoryStarts);
    });

    [TestMethod]
    public Task WaitingRead_IsRefusedAsClosedWhenDisposalStarts_AndTheDrainJoinsTheOthers() => RunAsync(async f =>
    {
        var reads = f.CreateReads(wait: TimeSpan.FromSeconds(30));
        var ready = f.Keep(f.EightHistoryStarted.Task);
        for (var i = 0; i < OwnedSessionWorkspace.ReadSlots; i++) _ = f.Keep(reads.ReadHistoryPageAsync("literal", null, CancellationToken.None));
        await f.Wait(ready);
        var waiting = f.Keep(reads.ReadHistoryPageAsync("literal", null, CancellationToken.None), typeof(ObjectDisposedException));
        var disposal = f.Keep(reads.DisposeAsync().AsTask());
        Assert.IsFalse(disposal.IsCompleted);
        f.Release.TrySetResult();
        await f.Wait(disposal);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => f.Wait(waiting));
        Assert.AreEqual(8, f.HistoryStarts);
    });

    [TestMethod]
    public Task Disposal_AttemptsAllReadsAndObservesFailures() => RunAsync(async f =>
    {
        var reads = f.CreateReads(fail: true);
        var first = f.Keep(reads.ReadSnapshotAsync(CancellationToken.None), typeof(InvalidDataException));
        var second = f.Keep(reads.ReadSnapshotAsync(CancellationToken.None), typeof(InvalidDataException));
        var disposal = f.Keep(reads.DisposeAsync().AsTask(), typeof(AggregateException));
        f.Release.TrySetResult();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Wait(first));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Wait(second));
        await Assert.ThrowsExactlyAsync<AggregateException>(() => f.Wait(disposal));
        Assert.AreEqual(2, f.Starts);
    });

    [TestMethod]
    public Task History_RetainsActualReadThroughWaiterCancellation() => RunAsync(async f =>
    {
        var caller = f.CreateCaller();
        var reads = f.CreateReads();
        var read = f.Keep(reads.ReadHistoryPageAsync("literal", null, caller.Token), typeof(TaskCanceledException));
        var cancellation = f.Keep(caller.CancelAsync());
        await f.Wait(cancellation);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => f.Wait(read));
        var disposal = f.Keep(reads.DisposeAsync().AsTask());
        Assert.IsFalse(disposal.IsCompleted);
        f.Release.TrySetResult();
        await f.Wait(disposal);
    });

    [TestMethod]
    public Task HostDisposal_ClosesReadAdmissionAndStartsBothDrains() => RunAsync(async f =>
    {
        var reads = f.CreateReads();
        _ = f.Keep(reads.ReadSnapshotAsync(CancellationToken.None));
        var commands = f.CreateGate();
        var disposal = f.Keep(CodeAltaHost.DisposeOwnedWorkAsync(
            () => commands.Task, () => reads.DisposeAsync().AsTask(), () => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask, () => Task.CompletedTask).AsTask());
        Assert.ThrowsExactly<ObjectDisposedException>(() => f.Keep(reads.ReadSnapshotAsync(CancellationToken.None)));
        Assert.IsFalse(disposal.IsCompleted);
        commands.TrySetResult();
        f.Release.TrySetResult();
        await f.Wait(disposal);
    });

    [TestMethod]
    public Task HostDisposal_JoinsReadsBeforeRuntimeDependencies() => RunAsync(async f =>
    {
        var reads = f.CreateReads();
        _ = f.Keep(reads.ReadSnapshotAsync(CancellationToken.None));
        var runtimeDisposed = false;
        var disposal = f.Keep(CodeAltaHost.DisposeOwnedWorkAsync(
            () => Task.CompletedTask, () => reads.DisposeAsync().AsTask(), () =>
            {
                runtimeDisposed = true;
                return ValueTask.CompletedTask;
            }, _ => ValueTask.CompletedTask, () => Task.CompletedTask).AsTask());
        Assert.IsFalse(runtimeDisposed);
        f.Release.TrySetResult();
        await f.Wait(disposal);
        Assert.IsTrue(runtimeDisposed);
    });

    [TestMethod]
    public Task HostDisposal_AttemptsRuntimeAfterSettledReadFailure() => RunAsync(async f =>
    {
        var commandFailure = new InvalidOperationException("literal command");
        var readFailure = new InvalidDataException("literal read");
        var runtimeFailure = new IOException("literal runtime");
        var command = f.Keep(Task.FromException(commandFailure), typeof(InvalidOperationException));
        var read = f.Keep(Task.FromException(readFailure), typeof(InvalidDataException));
        var runtime = f.Keep(Task.FromException(runtimeFailure), typeof(IOException));
        var disposal = f.Keep(CodeAltaHost.DisposeOwnedWorkAsync(
            () => command, () => read, () => new ValueTask(runtime),
            _ => ValueTask.CompletedTask, () => Task.CompletedTask).AsTask(), typeof(AggregateException));
        var error = await Assert.ThrowsExactlyAsync<AggregateException>(() => f.Wait(disposal));
        CollectionAssert.AreEqual(new Exception[] { commandFailure, readFailure, runtimeFailure }, error.InnerExceptions.ToArray());
    });

    private static async Task RunAsync(Func<Fixture, Task> body)
    {
        var f = new Fixture();
        Task? bodyTask = null;
        var failures = new List<Exception>();
        try
        {
            bodyTask = f.Keep(body(f));
            await f.Wait(bodyTask);
        }
        catch (Exception ex) { failures.Add(ex); }
        finally
        {
            // Close the fixture under its gate: release all prepared gates and start every
            // reader disposal before any cleanup await. Body/read/observer joins are independent.
            f.BeginClosing();
            var confirmed = await f.DrainAsync(f.Snapshot(), failures);
            // A still-running body can add work. Never claim a final snapshot in that case.
            if (bodyTask is null || bodyTask.IsCompleted)
            {
                confirmed &= await f.DrainAsync(f.Snapshot(), failures);
                if (confirmed && failures.Count == 0) f.DisposeCallers();
            }
            else failures.Add(new TimeoutException("Literal body remains unconfirmed; available readers were still drained."));
        }
        if (failures.Count != 0) throw new AggregateException("Literal fixture failed or work remains unconfirmed.", failures);
    }

    private sealed class Fixture
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FourStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource EightHistoryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<Task> _tasks = [], _observers = [], _cleanup = [];
        private readonly List<OwnedSessionWorkspace> _readers = [];
        private readonly List<CancellationTokenSource> _callers = [];
        private bool _closing;
        internal int Starts, Enumerations, HistoryStarts;
        internal CancellationToken ActualToken;
        private readonly Dictionary<Task, Type> _expected = [];

        internal Task Keep(Task task, Type? expected = null)
        {
            lock (_gate)
            {
                if (expected is not null) _expected.TryAdd(task, expected);
                if (!_tasks.Contains(task)) { _tasks.Add(task); _observers.Add(ObserveAsync(task)); }
            }
            return task;
        }
        // Faults are retained as data; cleanup checks each original against its explicit expectation.
        private static async Task<Exception?> ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); return null; }
            catch (Exception ex) { return ex; }
        }
        private bool IsExpected(Task task, Exception ex)
        {
            lock (_gate) return ex is not TimeoutException
                && _expected.TryGetValue(task, out var type) && ex.GetType() == type;
        }
        internal Task Wait(Task task)
        {
            lock (_gate) return Keep(task.WaitAsync(TimeSpan.FromSeconds(5)), _expected.GetValueOrDefault(task));
        }
        internal CancellationTokenSource CreateCaller()
        {
            lock (_gate)
            {
                var caller = new CancellationTokenSource();
                _callers.Add(caller);
                return caller;
            }
        }
        internal TaskCompletionSource CreateGate()
        {
            lock (_gate)
            {
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates.Add(gate);
                if (_closing) gate.TrySetResult();
                return gate;
            }
        }
        internal void BeginClosing()
        {
            lock (_gate)
            {
                _closing = true;
                Release.TrySetResult();
                foreach (var gate in _gates) gate.TrySetResult();
                foreach (var reads in _readers) Keep(reads.DisposeAsync().AsTask());
            }
        }
        internal Task[] Snapshot()
        {
            lock (_gate) return _tasks.Concat(_observers).Distinct().ToArray();
        }
        internal async Task<bool> DrainAsync(Task[] snapshot, List<Exception> failures)
        {
            var drains = snapshot.Select(task => (Original: task, Wait: task.WaitAsync(TimeSpan.FromSeconds(5)))).ToArray();
            lock (_gate) _cleanup.AddRange(drains.Select(drain => drain.Wait));
            var confirmed = true;
            foreach (var drain in drains)
            {
                try { await drain.Wait.ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (!IsExpected(drain.Original, ex)) { failures.Add(ex); confirmed = false; }
                }
            }
            return confirmed;
        }
        internal void DisposeCallers()
        {
            lock (_gate) foreach (var caller in _callers) caller.Dispose();
        }

        internal OwnedSessionWorkspace CreateReads(bool fail = false, TimeSpan? wait = null)
        {
            async Task<IReadOnlyList<ProjectDescriptor>> Projects(CancellationToken token)
            {
                if (Interlocked.Increment(ref Starts) == 4) FourStarted.TrySetResult();
                ActualToken = token;
                await Release.Task.ConfigureAwait(false);
                if (fail) throw new InvalidDataException("literal read failure");
                return [];
            }
            async IAsyncEnumerable<AgentSessionMetadata> Sessions([EnumeratorCancellation] CancellationToken token)
            {
                ActualToken = token;
                await Release.Task.ConfigureAwait(false);
                Interlocked.Increment(ref Enumerations);
                yield return new AgentSessionMetadata("literal", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            }
            async Task<AgentSessionHistoryPage> History(string id, AgentSessionHistoryCursor? cursor, CancellationToken token)
            {
                if (Interlocked.Increment(ref HistoryStarts) == 8) EightHistoryStarted.TrySetResult();
                ActualToken = token;
                await Release.Task.ConfigureAwait(false);
                return new([], null, false);
            }
            var reads = new OwnedSessionWorkspace(Projects, Sessions, History)
            {
                ReadWait = wait ?? OwnedSessionWorkspace.ReadAdmissionWait,
                SnapshotWait = wait ?? OwnedSessionWorkspace.SnapshotAdmissionWait,
            };
            lock (_gate)
            {
                _readers.Add(reads);
                if (_closing) Keep(reads.DisposeAsync().AsTask());
            }
            return reads;
        }
    }
}
