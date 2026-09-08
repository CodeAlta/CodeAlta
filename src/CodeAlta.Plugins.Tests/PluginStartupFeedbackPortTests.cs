using System.Runtime.ExceptionServices;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

/// <summary>Inert tests of the production routing seam; no runtime, scheduler, files or terminal.</summary>
[TestClass]
public sealed class PluginStartupFeedbackPortTests
{
    [TestMethod]
    [DataRow("requests")]
    [DataRow("feedback")]
    [DataRow("operation")]
    [DataRow("summaryFactory")]
    [DataRow("all")]
    public void Route_RejectsMissingArgumentsBeforeCallbacks(string missing)
    {
        var feedback = new RecordingFeedback { FailIfCalled = true };
        Func<IPluginStartupProgress?, CancellationToken, ValueTask<object>> operation = (_, _) => throw new AssertFailedException("Operation invoked.");
        Func<object, TimeSpan, string> summary = (_, _) => throw new AssertFailedException("Summary invoked.");
        var error = Assert.ThrowsExactly<ArgumentNullException>(() => PluginStartupFeedbackRouting.RunAsync(
            missing is "requests" or "all" ? null! : Requests(),
            missing is "feedback" or "all" ? null! : feedback,
            missing is "operation" or "all" ? null! : operation,
            missing is "summaryFactory" or "all" ? null! : summary,
            isHeadless: true, waitForAcknowledgement: false, CancellationToken.None));
        Assert.AreEqual(missing == "all" ? "requests" : missing, error.ParamName);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public Task Route_BypassesPresentationForHeadlessOrEmptyRequests(bool headless, bool empty)
        => RunAsync(async tasks =>
        {
            var expected = new object();
            var token = new CancellationToken(true);
            var calls = 0;
            var original = tasks.Track(Task.FromResult(expected));
            var caller = tasks.Track(PluginStartupFeedbackRouting.RunAsync(
                empty ? [] : Requests(), new RecordingFeedback { FailIfCalled = true },
                (status, actualToken) =>
                {
                    calls++;
                    Assert.IsNull(status);
                    Assert.AreEqual(token, actualToken);
                    return new ValueTask<object>(original);
                }, (_, _) => throw new AssertFailedException("Bypass must not summarize."),
                headless, true, token).AsTask());
            Assert.AreSame(expected, await tasks.WaitAsync(caller));
            Assert.AreEqual(1, calls);
        });

    [TestMethod]
    public Task Route_ForwardsSelectedPortAndOriginalArguments()
        => RunAsync(async tasks =>
        {
            var requests = Requests();
            var feedback = new RecordingFeedback();
            var expected = new object();
            var original = tasks.Track(Task.FromResult(expected));
            var token = new CancellationToken(true);
            Func<IPluginStartupProgress?, CancellationToken, ValueTask<object>> operation = (status, actualToken) =>
            {
                Assert.AreSame(feedback.Progress, status);
                Assert.AreEqual(token, actualToken);
                return new ValueTask<object>(original);
            };
            Func<object, TimeSpan, string> summary = (_, _) => "summary";
            var caller = tasks.Track(PluginStartupFeedbackRouting.RunAsync(requests, feedback, operation, summary, false, true, token).AsTask());
            Assert.AreSame(expected, await tasks.WaitAsync(caller));
            Assert.AreSame(requests, feedback.Requests);
            Assert.AreSame(operation, feedback.Operation);
            Assert.AreSame(summary, feedback.Summary);
            Assert.AreEqual(token, feedback.Token);
            Assert.IsTrue(feedback.Wait);
            Assert.AreEqual(1, feedback.Calls);
        });

    [TestMethod]
    [DataRow("success")]
    [DataRow("synchronous")]
    [DataRow("fault")]
    [DataRow("canceled")]
    [DataRow("faulted-oce")]
    public Task Route_PreservesTerminalOutcomes(string outcome)
        => RunAsync(async tasks =>
        {
            var expected = new object();
            var token = new CancellationToken(true);
            Exception failure = outcome == "faulted-oce" ? new OperationCanceledException(token) : new InvalidOperationException("original");
            var original = tasks.Track(outcome switch
            {
                "fault" or "faulted-oce" => Task.FromException<object>(failure),
                "canceled" => Task.FromCanceled<object>(token),
                _ => Task.FromResult(expected),
            });
            Task<object>? caller = null;
            Exception? observed = null;
            try
            {
                caller = tasks.Track(PluginStartupFeedbackRouting.RunAsync(
                    Requests(), new RecordingFeedback(),
                    (_, _) => outcome == "synchronous" ? throw failure : new ValueTask<object>(original),
                    (_, _) => "summary", false, false, token).AsTask());
                Assert.AreSame(expected, await tasks.WaitAsync(caller));
            }
            catch (Exception error) { observed = error; }
            if (outcome == "success") Assert.IsNull(observed);
            else if (outcome == "canceled")
            {
                Assert.IsInstanceOfType<OperationCanceledException>(observed);
                Assert.AreEqual(token, ((OperationCanceledException)observed!).CancellationToken);
            }
            else Assert.AreSame(failure, observed);
            if (outcome == "synchronous") Assert.IsNull(caller);
        });

    [TestMethod]
    public Task NoFeedback_InvokesOriginalOnceWithoutSummaryOrStatus()
        => RunAsync(async tasks =>
        {
            var gate = tasks.Gate();
            var calls = 0;
            var token = new CancellationToken(true);
            var caller = tasks.Track(new SilentPluginStartupFeedback().RunAsync(Requests(), true,
                (status, actualToken) =>
                {
                    calls++;
                    Assert.IsNull(status);
                    Assert.AreEqual(token, actualToken);
                    return new ValueTask<object>(gate.Task);
                }, (_, _) => throw new AssertFailedException("Silent feedback must not summarize."), token).AsTask());
            Assert.AreEqual(1, calls);
            Assert.IsFalse(caller.IsCompleted);
            var expected = new object();
            gate.SetResult(expected);
            Assert.AreSame(expected, await tasks.WaitAsync(caller));
        });

    private static IReadOnlyList<PluginBuildRequest> Requests() =>
    [
        new() { Package = new SourcePluginPackage { PackageId = "inert", PackageDirectory = "unused/inert", EntryFilePath = "unused/inert/plugin.cs", Root = new PluginRoot { RootPath = "unused", Scope = PluginScope.Global } } },
    ];

    private sealed class RecordingFeedback : IPluginStartupFeedback
    {
        public bool FailIfCalled { get; init; }
        public RecordingProgress Progress { get; } = new();
        public object? Requests { get; private set; }
        public object? Operation { get; private set; }
        public object? Summary { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool Wait { get; private set; }
        public int Calls { get; private set; }
        public ValueTask<T> RunAsync<T>(IReadOnlyList<PluginBuildRequest> requests, bool waitForAcknowledgement,
            Func<IPluginStartupProgress?, CancellationToken, ValueTask<T>> operation, Func<T, TimeSpan, string> summaryFactory, CancellationToken cancellationToken)
        {
            if (FailIfCalled) throw new AssertFailedException("Presentation invoked.");
            Calls++;
            Requests = requests;
            Operation = operation;
            Summary = summaryFactory;
            Token = cancellationToken;
            Wait = waitForAcknowledgement;
            return operation(Progress, cancellationToken);
        }
    }

    private sealed class RecordingProgress : IPluginStartupProgress
    {
        public void MarkPreparing() { }
        public void MarkBuilding() { }
        public void Report(PluginBuildProgress progress) { }
        public void MarkBuildsCompleted() { }
        public void MarkActivating() { }
    }

    private static async Task RunAsync(Func<RetainedTasks, Task> body)
    {
        var tasks = new RetainedTasks();
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try { await tasks.Track(body(tasks)); }
        catch (Exception error) { primary = error; }
        finally
        {
            var cleanupTask = tasks.FinishAsync();
            try { cleanup.AddRange(await cleanupTask); }
            catch (Exception error) { cleanup.Add(error); }
        }
        if (primary is not null && cleanup.Count != 0) throw new AggregateException("Body and cleanup failed.", [primary, .. cleanup]);
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (cleanup.Count != 0) throw new AggregateException(cleanup);
    }

    private sealed class RetainedTasks
    {
        private readonly List<Task> _tasks = [];
        private readonly List<TaskCompletionSource<object>> _gates = [];
        public T Track<T>(T task) where T : Task { _tasks.Add(task); return task; }
        public TaskCompletionSource<object> Gate()
        {
            var gate = new TaskCompletionSource<object>();
            _gates.Add(gate);
            Track(gate.Task);
            return gate;
        }
        public Task<T> WaitAsync<T>(Task<T> task) => Track(task.WaitAsync(TimeSpan.FromSeconds(5)));
        public async Task<List<Exception>> FinishAsync()
        {
            foreach (var gate in _gates) gate.TrySetResult(new object());
            var errors = new List<Exception>();
            // Start and retain every independent observer before joining the group. No timeout filter:
            // a later original completion must never rehabilitate a failed observation.
            var observers = _tasks.Select(ObserveAsync).ToArray();
            var group = Task.WhenAll(observers);
            await group;
            foreach (var observer in observers) if (observer.Result is { } error) errors.Add(error);
            return errors;
        }
        private static async Task<Exception?> ObserveAsync(Task original)
        {
            try { await original.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException error) { return error; }
            catch (Exception) { _ = original.Exception; }
            return null;
        }
    }
}
