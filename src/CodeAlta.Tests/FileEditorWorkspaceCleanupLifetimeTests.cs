using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class FileEditorWorkspaceCleanupLifetimeTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(-1)]
    public async Task Core_ValidatesCallbacks(int missing)
    {
        var recording = new Recording();
        try
        {
            var calls = 0;
            Func<ValueTask> picker = () => { calls++; return ValueTask.CompletedTask; };
            Func<IAsyncDisposable[]> snapshot = () => { calls++; return []; };
            var failure = Assert.ThrowsExactly<ArgumentNullException>(() =>
            {
                // Deliberate nulls test synchronous mandatory validation, without real owners.
                var task = FileEditorWorkspaceCoordinator.DisposeWorkspaceAsync(
                    missing is -1 or 0 ? null! : picker,
                    missing is -1 or 1 ? null! : snapshot);
                recording.Track(task);
            });
            Assert.AreEqual(missing == 1 ? "snapshotTabs" : "disposePicker", failure.ParamName);
            Assert.AreEqual(0, calls);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Core_EmptySnapshotStartsInline()
    {
        var recording = new Recording();
        try
        {
            var workspace = recording.Core();
            Assert.IsTrue(workspace.IsCompletedSuccessfully);
            recording.AssertEvents("picker", "snapshot");
            Assert.AreEqual(1, recording.SnapshotCalls);
            Assert.IsNull(await recording.ObserveAsync(workspace));
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("sync-ordinary")]
    [DataRow("sync-oce")]
    [DataRow("sync-nested")]
    [DataRow("fault")]
    [DataRow("canceled")]
    [DataRow("faulted-oce")]
    [DataRow("nested")]
    public async Task Core_PickerTerminalFailureStillSnapshotsAndDisposesTabs(string outcome)
    {
        var recording = new Recording();
        try
        {
            var operation = recording.Operation(outcome, "picker failure");
            recording.Picker = operation.Invoke;
            var tabs = recording.ThreeTabs();
            recording.Snapshot = () => tabs;
            var workspace = recording.Core();
            var failures = await recording.ObserveManyAsync(operation.With(workspace));
            AssertOutcome(workspace, failures[0], outcome, operation.Expected);
            operation.AssertOriginal(failures);
            recording.AssertWorkspaceEvents();
            Assert.AreEqual(1, recording.SnapshotCalls);
            foreach (var tab in tabs) Assert.AreEqual(1, tab.Calls);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("canceled")]
    [DataRow("faulted-oce")]
    [DataRow("nested")]
    public async Task Core_PendingPickerDefersSnapshot(string outcome)
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            var operation = recording.Operation(outcome, "pending picker", gate.Task);
            recording.Picker = operation.Invoke;
            var tabs = recording.ThreeTabs();
            recording.Snapshot = () => tabs;
            var workspace = recording.Core();
            Assert.IsFalse(workspace.IsCompleted);
            Assert.IsFalse(operation.Original!.IsCompleted);
            Assert.AreEqual(0, recording.SnapshotCalls);
            recording.AssertEvents("picker");
            gate.TrySetResult(true);
            var failures = await recording.ObserveManyAsync(operation.With(workspace));
            AssertOutcome(workspace, failures[0], outcome, operation.Expected);
            operation.AssertOriginal(failures);
            recording.AssertWorkspaceEvents();
            Assert.AreEqual(1, recording.SnapshotCalls);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(false, "ordinary")]
    [DataRow(false, "oce")]
    [DataRow(false, "nested")]
    [DataRow(true, "ordinary")]
    [DataRow(true, "oce")]
    [DataRow(true, "nested")]
    public async Task Core_SnapshotFailureRetainsEarlierPickerError(bool pickerFails, string kind)
    {
        var recording = new Recording();
        try
        {
            var picker = recording.Operation(pickerFails ? "fault" : "success", "picker");
            var snapshotFailure = CreateFailure(kind, "snapshot");
            recording.Picker = picker.Invoke;
            recording.Snapshot = () => throw snapshotFailure;
            var workspace = recording.Core();
            var failures = await recording.ObserveManyAsync(picker.With(workspace));
            var failure = failures[0];
            picker.AssertOriginal(failures);
            if (pickerFails) AssertAggregate(failure, picker.Expected, snapshotFailure);
            else Assert.AreSame(snapshotFailure, failure);
            Assert.AreEqual(!pickerFails && kind == "oce", workspace.IsCanceled);
            Assert.AreEqual(pickerFails || kind != "oce", workspace.IsFaulted);
            recording.AssertEvents("picker", "snapshot");
            Assert.AreEqual(1, recording.SnapshotCalls);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Core_RejectsNullSnapshotAfterPicker(bool pickerFails)
    {
        var recording = new Recording();
        try
        {
            var picker = recording.Operation(pickerFails ? "fault" : "success", "picker");
            recording.Picker = picker.Invoke;
            // Invalid seam return, not an allegation about the real dictionary snapshot.
            recording.Snapshot = () => null!;
            var workspace = recording.Core();
            var failures = await recording.ObserveManyAsync(picker.With(workspace));
            var failure = failures[0];
            picker.AssertOriginal(failures);
            var snapshotFailure = pickerFails
                ? Assert.IsInstanceOfType<AggregateException>(failure).InnerExceptions[1]
                : failure;
            var invalid = Assert.IsInstanceOfType<InvalidOperationException>(snapshotFailure);
            Assert.AreEqual("The tab snapshot must not be null.", invalid.Message);
            if (pickerFails) AssertAggregate(failure, picker.Expected, invalid);
            Assert.IsTrue(workspace.IsFaulted);
            recording.AssertEvents("picker", "snapshot");
            Assert.AreEqual(1, recording.SnapshotCalls);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(0, "sync-ordinary")]
    [DataRow(0, "sync-oce")]
    [DataRow(0, "sync-nested")]
    [DataRow(0, "fault")]
    [DataRow(0, "canceled")]
    [DataRow(0, "faulted-oce")]
    [DataRow(0, "nested")]
    [DataRow(1, "sync-ordinary")]
    [DataRow(1, "sync-oce")]
    [DataRow(1, "sync-nested")]
    [DataRow(1, "fault")]
    [DataRow(1, "canceled")]
    [DataRow(1, "faulted-oce")]
    [DataRow(1, "nested")]
    [DataRow(2, "sync-ordinary")]
    [DataRow(2, "sync-oce")]
    [DataRow(2, "sync-nested")]
    [DataRow(2, "fault")]
    [DataRow(2, "canceled")]
    [DataRow(2, "faulted-oce")]
    [DataRow(2, "nested")]
    public async Task Core_TabFailureStillAttemptsLaterTabs(int position, string outcome)
    {
        var recording = new Recording();
        try
        {
            var operation = recording.Operation(outcome, "tab failure");
            var tabs = recording.ThreeTabs(position, operation.Invoke);
            recording.Snapshot = () => tabs;
            var workspace = recording.Core();
            var failures = await recording.ObserveManyAsync(operation.With(workspace));
            AssertOutcome(workspace, failures[0], outcome, operation.Expected);
            operation.AssertOriginal(failures);
            recording.AssertWorkspaceEvents();
            Assert.AreEqual(1, recording.SnapshotCalls);
            foreach (var tab in tabs) Assert.AreEqual(1, tab.Calls);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("canceled")]
    [DataRow("faulted-oce")]
    [DataRow("nested")]
    public async Task Core_PendingTabDefersLaterEntries(string outcome)
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            var operation = recording.Operation(outcome, "pending tab", gate.Task);
            var tabs = recording.ThreeTabs(1, operation.Invoke);
            recording.Snapshot = () => tabs;
            var workspace = recording.Core();
            Assert.IsFalse(workspace.IsCompleted);
            Assert.IsFalse(operation.Original!.IsCompleted);
            recording.AssertEvents("picker", "snapshot", "tab-0", "tab-1");
            Assert.AreEqual(0, tabs[2].Calls);
            gate.TrySetResult(true);
            var failures = await recording.ObserveManyAsync(operation.With(workspace));
            AssertOutcome(workspace, failures[0], outcome, operation.Expected);
            operation.AssertOriginal(failures);
            recording.AssertWorkspaceEvents();
            Assert.AreEqual(1, recording.SnapshotCalls);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("nested")]
    [DataRow("mixed-oce")]
    [DataRow("repeated")]
    public async Task Core_OrdersDirectFailures(string kind)
    {
        var recording = new Recording();
        try
        {
            var shared = CreateFailure("ordinary", "same reference");
            var expected = Enumerable.Range(0, 4).Select(index => kind == "repeated" ? shared
                : CreateFailure(kind == "mixed-oce" ? (index == 2 ? "nested" : "oce") : kind, $"failure-{index}")).ToArray();
            recording.Picker = () => throw expected[0];
            InertTab[] tabs =
            [
                new(recording, "tab-0", () => throw expected[1]),
                new(recording, "tab-1", () => throw expected[2]),
                new(recording, "tab-2", () => throw expected[3]),
            ];
            recording.Snapshot = () => tabs;
            var workspace = recording.Core();
            AssertAggregate(await recording.ObserveAsync(workspace), expected);
            Assert.IsTrue(workspace.IsFaulted);
            recording.AssertWorkspaceEvents();
            Assert.AreEqual(1, recording.SnapshotCalls);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Core_PreservesSnapshotEntryOrderAndDuplicateIdentity()
    {
        var recording = new Recording();
        try
        {
            var first = new InertTab(recording, "first", static () => ValueTask.CompletedTask);
            var repeated = new InertTab(recording, "repeated", static () => ValueTask.CompletedTask);
            IAsyncDisposable[] snapshot = [repeated, first, repeated];
            recording.Snapshot = () => snapshot;
            var workspace = recording.Core();
            Assert.IsNull(await recording.ObserveAsync(workspace));
            recording.AssertEvents("picker", "snapshot", "repeated", "first", "repeated");
            Assert.AreEqual(1, first.Calls);
            Assert.AreEqual(2, repeated.Calls);
            Assert.AreEqual(1, recording.SnapshotCalls);
            // Duplicate references test the seam's array order, not normal production entries.
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Core_NullEntryStillAttemptsLaterTabs(int position)
    {
        var recording = new Recording();
        try
        {
            var tabs = recording.ThreeTabs();
            IAsyncDisposable[] snapshot = [.. tabs];
            snapshot[position] = null!;
            recording.Snapshot = () => snapshot;
            var workspace = recording.Core();
            var failure = Assert.IsInstanceOfType<InvalidOperationException>(await recording.ObserveAsync(workspace));
            Assert.AreEqual("The tab snapshot contains a null entry.", failure.Message);
            Assert.IsTrue(workspace.IsFaulted);
            recording.AssertEvents(["picker", "snapshot", .. Enumerable.Range(0, 3).Where(index => index != position).Select(index => $"tab-{index}")]);
            for (var index = 0; index < tabs.Length; index++) Assert.AreEqual(index == position ? 0 : 1, tabs[index].Calls);
            Assert.AreEqual(1, recording.SnapshotCalls);
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Core_OnlyDisposesReturnedSnapshot()
    {
        var recording = new Recording();
        try
        {
            var gate = recording.Gate();
            var unselected = recording.Operation("fault", "unselected original", gate.Task);
            var outside = new InertTab(recording, "outside", unselected.Invoke);
            var selected = new InertTab(recording, "selected", static () => ValueTask.CompletedTask);
            recording.Snapshot = () => [selected];
            var workspace = recording.Core();
            Assert.IsTrue(workspace.IsCompletedSuccessfully);
            Assert.IsFalse(unselected.Original!.IsCompleted);
            Assert.AreEqual(0, outside.Calls);
            recording.AssertEvents("picker", "snapshot", "selected");
            gate.TrySetResult(true);
            var failures = await recording.ObserveManyAsync(unselected.With(workspace));
            Assert.IsNull(failures[0]);
            unselected.AssertOriginal(failures);
            Assert.AreEqual(1, selected.Calls);
            Assert.AreEqual(1, recording.SnapshotCalls);
            // Independently owned inert work only: no reproduction of runtime late publication.
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("oce")]
    [DataRow("nested")]
    public async Task Frontend_WorkspaceFailureStillAttemptsLaterStages(string kind)
    {
        var recording = new Recording();
        try
        {
            var expected = CreateFailure(kind, "picker");
            recording.Picker = () => throw expected;
            var tabs = recording.ThreeTabs();
            recording.Snapshot = () => tabs;
            var frontend = recording.Frontend();
            var workspace = recording.Workspace ?? throw new AssertFailedException("Workspace not reached.");
            var failures = await recording.ObserveManyAsync(frontend, workspace);
            Assert.AreSame(expected, failures[0]);
            Assert.AreSame(expected, failures[1]);
            Assert.AreEqual(kind == "oce", frontend.IsCanceled);
            Assert.AreEqual(kind == "oce", workspace.IsCanceled);
            recording.AssertFrontendEvents("tab-0", "tab-1", "tab-2");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Frontend_PreservesWorkspaceAggregateAsDirectStageError()
    {
        var recording = new Recording();
        try
        {
            var persistence = CreateFailure("ordinary", "persistence");
            var picker = CreateFailure("nested", "picker");
            var tab = CreateFailure("oce", "tab");
            var pump = CreateFailure("ordinary", "pump");
            recording.FrontendFailures[2] = persistence;
            recording.FrontendFailures[4] = pump;
            recording.Picker = () => throw picker;
            var tabs = recording.ThreeTabs(1, () => throw tab);
            recording.Snapshot = () => tabs;
            var frontend = recording.Frontend();
            var workspace = recording.Workspace ?? throw new AssertFailedException("Workspace not reached.");
            var failures = await recording.ObserveManyAsync(frontend, workspace);
            AssertAggregate(failures[1], picker, tab);
            AssertAggregate(failures[0], persistence, failures[1], pump);
            Assert.IsTrue(frontend.IsFaulted);
            Assert.IsTrue(workspace.IsFaulted);
            recording.AssertFrontendEvents("tab-0", "tab-1", "tab-2");
        }
        finally { await recording.FinishAsync(); }
    }

    [TestMethod]
    public async Task Frontend_PendingWorkspacePreservesContextBoundary()
    {
        var recording = new Recording();
        TaskCompletionSource<bool>? gate = null;
        ManualContext? context = null;
        Task? frontend = null;
        Task? dispatch = null;
        Task? completion = null;
        var previous = SynchronizationContext.Current;
        try
        {
            gate = recording.Gate();
            var operation = recording.Operation("success", "pending tab", gate.Task);
            var original = operation.Original ?? throw new AssertFailedException("Missing pending original.");
            InertTab[] tabs =
            [
                new(recording, "tab-0", operation.Invoke),
                new(recording, "tab-1", static () => ValueTask.CompletedTask),
            ];
            recording.Snapshot = () => tabs;
            var contexts = new ConcurrentQueue<(string Stage, SynchronizationContext? Context)>();
            recording.OnEntry = stage => contexts.Enqueue((stage, SynchronizationContext.Current));
            context = new ManualContext(recording);
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                frontend = recording.Frontend();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            // Both producer tasks and the initial signal are retained before assertions. Dispatch
            // waits for inert posts, not a real UI loop, and never silently drops additional posts.
            completion = recording.Track(context.CompleteAfterAsync(frontend));
            dispatch = recording.Track(context.DispatchAsync());
            context.StartDispatch();
            var workspace = recording.Workspace ?? throw new AssertFailedException("Workspace not reached.");
            Assert.IsFalse(frontend.IsCompleted);
            Assert.IsFalse(workspace.IsCompleted);
            Assert.IsFalse(original.IsCompleted);
            Assert.AreEqual(0, context.PostCount);
            recording.AssertEvents("projection", "reminder-ui", "persistence", "editors", "picker", "snapshot", "tab-0");
            gate.TrySetResult(true);
            // Launch every independent bound before the aggregate; never await dispatch first.
            var failures = await recording.ObserveManyAsync(dispatch, frontend, workspace, completion, original);
            foreach (var failure in failures) Assert.IsNull(failure);
            Assert.AreEqual(1, context.PostCount);
            recording.AssertFrontendEvents("tab-0", "tab-1");
            foreach (var observed in contexts) Assert.AreSame(context, observed.Context, observed.Stage);
            CollectionAssert.AreEqual(new[] { "tab-1", "pump", "controller", "drafts" },
                contexts.Where(item => item.Stage is "tab-1" or "pump" or "controller" or "drafts").Select(item => item.Stage).ToArray());
            Assert.AreEqual(1, tabs[1].Calls);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            gate?.TrySetResult(true);
            try
            {
                if (context is not null)
                {
                    if (frontend is null) context.Complete();
                    else completion ??= recording.Track(context.CompleteAfterAsync(frontend));
                    dispatch ??= recording.Track(context.DispatchAsync());
                    context.StartDispatch();
                }
            }
            finally
            {
                try { await recording.FinishAsync(); }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
            }
        }
    }

    private static Exception CreateFailure(string kind, string name) => kind switch
    {
        "ordinary" or "fault" or "sync-ordinary" => new InvalidOperationException(name),
        "oce" or "faulted-oce" or "sync-oce" => new OperationCanceledException(name),
        "nested" or "sync-nested" => new AggregateException(new InvalidOperationException(name), new AggregateException(new OperationCanceledException("nested"))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void AssertOutcome(Task task, Exception? actual, string outcome, Exception? expected)
    {
        if (outcome == "canceled")
        {
            var canceled = Assert.IsInstanceOfType<OperationCanceledException>(actual);
            Assert.AreEqual(new CancellationToken(canceled: true), canceled.CancellationToken);
        }
        else Assert.AreSame(expected, actual);
        Assert.AreEqual(outcome is "canceled" or "faulted-oce" or "sync-oce", task.IsCanceled);
        Assert.AreEqual(outcome is not ("success" or "canceled" or "faulted-oce" or "sync-oce"), task.IsFaulted);
    }

    private static void AssertAggregate(Exception? actual, params Exception?[] expected)
    {
        var aggregate = Assert.IsInstanceOfType<AggregateException>(actual);
        Assert.AreEqual(expected.Length, aggregate.InnerExceptions.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.IsNotNull(expected[index]);
            Assert.AreSame(expected[index], aggregate.InnerExceptions[index]);
        }
    }

    private static async Task CompleteOriginalAsync(Task gate, TaskCompletionSource<bool> completion, string outcome, Exception? expected)
    {
        await gate.ConfigureAwait(false);
        switch (outcome)
        {
            case "success": completion.TrySetResult(true); break;
            case "canceled": completion.TrySetCanceled(new CancellationToken(canceled: true)); break;
            case "fault" or "faulted-oce" or "nested":
                completion.TrySetException(expected ?? new AssertFailedException("Missing synthetic failure."));
                break;
            default: completion.TrySetException(new AssertFailedException("Unknown synthetic outcome.")); break;
        }
    }

    private sealed class Operation(Func<ValueTask> invoke, Task? original, Exception? expected, string outcome)
    {
        public Func<ValueTask> Invoke { get; } = invoke;
        public Task? Original { get; } = original;
        public Exception? Expected { get; } = expected;

        public Task[] With(Task caller) => Original is { } task ? [caller, task] : [caller];

        public void AssertOriginal(Exception?[] observed)
        {
            if (Original is not { } task) return;
            if (outcome == "canceled")
                Assert.AreEqual(new CancellationToken(canceled: true), Assert.IsInstanceOfType<OperationCanceledException>(observed[1]).CancellationToken);
            else Assert.AreSame(Expected, observed[1]);
            Assert.AreEqual(outcome == "canceled", task.IsCanceled);
            Assert.AreEqual(outcome is "fault" or "faulted-oce" or "nested", task.IsFaulted);
        }
    }

    private sealed class InertTab(Recording recording, string name, Func<ValueTask> operation) : IAsyncDisposable
    {
        public int Calls { get; private set; }

        public ValueTask DisposeAsync()
        {
            Calls++;
            return recording.Invoke(name, operation);
        }
    }

    // Instance-owned inert state only. No file/source reads, CTS, concrete acquisition, native UI,
    // codec, watcher, host, logger/version/monitor setup, auth records or HOME-isolation shortcuts.
    private sealed class Recording
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly ConcurrentQueue<Task> _tasks = new();
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> _gates = new();
        private readonly ConcurrentQueue<Exception> _permanentFailures = new();

        public Func<ValueTask> Picker { get; set; } = static () => ValueTask.CompletedTask;
        public Func<IAsyncDisposable[]> Snapshot { get; set; } = static () => [];
        public Exception?[] FrontendFailures { get; } = new Exception?[7];
        public Action<string>? OnEntry { get; set; }
        public int SnapshotCalls { get; private set; }
        public Task? Workspace { get; private set; }

        public T Track<T>(T task) where T : Task { _tasks.Enqueue(task); return task; }
        public void RecordFailure(Exception failure) => _permanentFailures.Enqueue(failure);
        public void AssertEvents(params string[] expected) => CollectionAssert.AreEqual(expected, _events.ToArray());
        public void AssertWorkspaceEvents() => AssertEvents("picker", "snapshot", "tab-0", "tab-1", "tab-2");
        public void AssertFrontendEvents(params string[] tabs)
            => AssertEvents(["projection", "reminder-ui", "persistence", "editors", "picker", "snapshot", .. tabs, "pump", "controller", "drafts"]);

        public TaskCompletionSource<bool> Signal()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Track(signal.Task);
            return signal;
        }

        public TaskCompletionSource<bool> Gate()
        {
            var gate = Signal();
            _gates.Enqueue(gate);
            return gate;
        }

        public Operation Operation(string outcome, string name, Task? gate = null)
        {
            var expected = outcome is "success" or "canceled" ? null : CreateFailure(outcome, name);
            if (outcome.StartsWith("sync-", StringComparison.Ordinal))
                return new Operation(() => throw expected!, null, expected, outcome);
            Task original;
            if (gate is not null)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                original = Track(completion.Task);
                Track(CompleteOriginalAsync(gate, completion, outcome, expected));
            }
            else
            {
                original = Track(outcome switch
                {
                    "success" => Task.CompletedTask,
                    "canceled" => Task.FromCanceled(new CancellationToken(canceled: true)),
                    _ => Task.FromException(expected ?? new AssertFailedException("Missing synthetic failure.")),
                });
            }
            // Every returned original and its producer is retained before exposing the callback.
            return new Operation(() => new ValueTask(original), original, expected, outcome);
        }

        public InertTab[] ThreeTabs(int failingPosition = -1, Func<ValueTask>? operation = null)
            => Enumerable.Range(0, 3).Select(index => new InertTab(this, $"tab-{index}",
                index == failingPosition ? operation ?? throw new AssertFailedException("Missing inert operation.")
                    : static () => ValueTask.CompletedTask)).ToArray();

        private void Enter(string name)
        {
            _events.Enqueue(name);
            OnEntry?.Invoke(name);
        }

        public ValueTask Invoke(string name, Func<ValueTask> operation)
        {
            Enter(name);
            // All delegates here return task-backed/completed values. Convert each returned value
            // exactly once and retain it before any later callback; sync throws start no task.
            var task = Track(operation().AsTask());
            return new ValueTask(task);
        }

        public Task Core()
        {
            var task = Track(FileEditorWorkspaceCoordinator.DisposeWorkspaceAsync(
                () => Invoke("picker", Picker),
                () => { Enter("snapshot"); SnapshotCalls++; return Snapshot(); }));
            Workspace = task;
            return task;
        }

        // Actual accepted static frontend core only; no App/Shell construction or disposal.
        public Task Frontend() => Track(ShellFrontendHost.DisposeFrontendResourcesAsync(
            () => FrontendStage(0, "projection"),
            () => FrontendStage(1, "reminder-ui"),
            () => { FrontendStage(2, "persistence"); return Track(Task.CompletedTask); },
            () => { FrontendStage(3, "editors"); return new ValueTask(Core()); },
            () => { FrontendStage(4, "pump"); return ValueTask.CompletedTask; },
            () => { FrontendStage(5, "controller"); return ValueTask.CompletedTask; },
            () => { FrontendStage(6, "drafts"); return ValueTask.CompletedTask; }));

        private void FrontendStage(int index, string name)
        {
            Enter(name);
            if (FrontendFailures[index] is { } failure) ExceptionDispatchInfo.Throw(failure);
        }

        public Task<Exception?> ObserveAsync(Task task) => Track(ObserveCoreAsync(task));

        private async Task<Exception?> ObserveCoreAsync(Task task)
        {
            try
            {
                var bounded = Track(task.WaitAsync(TimeSpan.FromSeconds(5)));
                await bounded.ConfigureAwait(false);
                return null;
            }
            catch (TimeoutException ex)
            {
                // Body, nested signal/dispatch and teardown timeouts are permanently fatal to
                // this fixture, even if a subsequent independent observation sees completion.
                RecordFailure(ex);
                if (task.IsCompleted)
                {
                    try { await task.ConfigureAwait(false); }
                    catch (Exception) { /* Observe original without rehabilitating timeout. */ }
                }
                throw;
            }
            catch (Exception) when (task.IsCompleted)
            {
                try { await task.ConfigureAwait(false); }
                catch (Exception original) { return original; }
                return null;
            }
        }

        public Task<Exception?[]> ObserveManyAsync(params Task[] tasks) => Track(ObserveManyCoreAsync(tasks));

        private async Task<Exception?[]> ObserveManyCoreAsync(Task[] tasks)
        {
            List<Task<Exception?>> observations = [];
            foreach (var task in tasks) observations.Add(ObserveAsync(task));
            var aggregate = Track(Task.WhenAll(observations));
            return await aggregate.ConfigureAwait(false);
        }

        public async Task FinishAsync()
        {
            foreach (var gate in _gates) gate.TrySetResult(true);
            var firstTasks = _tasks.ToArray();
            Exception? firstFailure = null;
            try { await ObserveManyAsync(firstTasks).ConfigureAwait(false); }
            catch (Exception ex) { firstFailure = ex; }
            // Finite second snapshot includes callback-produced work, independent observations
            // and their retained bounds/aggregates. Never serially observe a dispatch first.
            var finalTasks = _tasks.ToArray();
            Exception? finalFailure = null;
            try { await ObserveManyAsync(finalTasks).ConfigureAwait(false); }
            catch (Exception ex) { finalFailure = ex; }
            List<Exception> failures = [.. _permanentFailures];
            if (firstFailure is not null and not TimeoutException) failures.Add(firstFailure);
            if (finalFailure is not null and not TimeoutException) failures.Add(finalFailure);
            if (!firstTasks.All(static task => task.IsCompleted) || !finalTasks.All(static task => task.IsCompleted))
                failures.Add(new AssertFailedException("Retained work did not complete; this is not termination evidence."));
            if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }

    // A manually invoked, retained dispatcher for inert continuations. All posts remain queued;
    // the expected single post is asserted by the case, not enforced by dropping later work.
    private sealed class ManualContext : SynchronizationContext
    {
        private readonly Recording _recording;
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posts = new();
        private readonly TaskCompletionSource<bool> _dispatchStart;
        private TaskCompletionSource<bool> _signal;
        private bool _complete;
        private int _postCount;

        public ManualContext(Recording recording)
        {
            _recording = recording;
            _signal = recording.Signal();
            _dispatchStart = recording.Gate();
        }

        public int PostCount => Volatile.Read(ref _postCount);

        public void StartDispatch() => _dispatchStart.TrySetResult(true);

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate)
            {
                _posts.Enqueue((d, state));
                Interlocked.Increment(ref _postCount);
                if (_complete) _recording.RecordFailure(new AssertFailedException("Post after frontend completion."));
                _signal.TrySetResult(true);
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                _complete = true;
                _signal.TrySetResult(true);
            }
        }

        public async Task CompleteAfterAsync(Task frontend)
        {
            await _recording.ObserveAsync(frontend).ConfigureAwait(false);
            Complete();
        }

        public async Task DispatchAsync()
        {
            // Even teardown with an already-posted continuation cannot invoke it until the
            // dispatch producer has been returned and independently retained by its caller.
            var startFailure = await _recording.ObserveAsync(_dispatchStart.Task).ConfigureAwait(false);
            if (startFailure is not null) ExceptionDispatchInfo.Throw(startFailure);
            while (true)
            {
                Task signal;
                lock (_gate) signal = _signal.Task;
                var failure = await _recording.ObserveAsync(signal).ConfigureAwait(false);
                if (failure is not null) ExceptionDispatchInfo.Throw(failure);
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) posted;
                    lock (_gate)
                    {
                        if (!_posts.TryDequeue(out posted))
                        {
                            if (_complete) return;
                            // Arm and retain the next signal before another callback can post.
                            // Each outer iteration awaits a signal; no sleeps or polling.
                            _signal = _recording.Signal();
                            break;
                        }
                    }
                    var previous = Current;
                    try
                    {
                        SetSynchronizationContext(this);
                        posted.Callback(posted.State);
                    }
                    catch (Exception ex) { _recording.RecordFailure(ex); }
                    finally { SetSynchronizationContext(previous); }
                }
            }
        }
    }
}
