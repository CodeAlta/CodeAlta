using System.Reflection;
using System.Runtime.ExceptionServices;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Tui.Plugins;

namespace CodeAlta.Tests;

[TestClass]
public sealed class TerminalPluginStartupFeedbackTests
{
    [TestMethod]
    [DataRow("status")]
    [DataRow("operation")]
    [DataRow("runLive")]
    [DataRow("all")]
    public void PresentedOperation_RejectsMissingArgumentsBeforeCallbacks(string missing)
    {
        Func<IPluginStartupProgress?, CancellationToken, ValueTask<object>> operation = (_, _) => throw new AssertFailedException("Operation invoked.");
        Action<Task<object>> driver = _ => throw new AssertFailedException("Driver invoked.");
        var error = Assert.ThrowsExactly<ArgumentNullException>(() => TerminalPluginStartupFeedback.RunPresentedOperationAsync(
            missing is "status" or "all" ? null! : new RecordingProgress(),
            missing is "operation" or "all" ? null! : operation,
            missing is "runLive" or "all" ? null! : driver, CancellationToken.None));
        Assert.AreEqual(missing == "all" ? "status" : missing, error.ParamName);
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("canceled")]
    public Task PresentedOperation_StartsInlineAndJoinsOriginal(string outcome)
        => RunAsync(async tasks =>
        {
            var gate = tasks.Gate();
            var status = new RecordingProgress();
            var token = new CancellationToken(true);
            var failure = new InvalidOperationException("operation");
            var events = new List<string>();
            var caller = tasks.Track(TerminalPluginStartupFeedback.RunPresentedOperationAsync(status,
                (actualStatus, actualToken) =>
                {
                    events.Add("operation");
                    Assert.AreSame(status, actualStatus);
                    Assert.AreEqual(token, actualToken);
                    return new ValueTask<object>(gate.Task);
                }, original =>
                {
                    tasks.Track(original);
                    events.Add("driver");
                    Assert.AreSame(gate.Task, original);
                }, token).AsTask());
            CollectionAssert.AreEqual(new[] { "operation", "driver" }, events);
            Assert.IsFalse(caller.IsCompleted);
            var expected = new object();
            if (outcome == "fault") gate.SetException(failure);
            else if (outcome == "canceled") gate.SetCanceled(token);
            else gate.SetResult(expected);
            Exception? observed = null;
            try { Assert.AreSame(expected, await tasks.WaitAsync(caller)); }
            catch (Exception error) { observed = error; }
            if (outcome == "success") Assert.IsNull(observed);
            else if (outcome == "fault") Assert.AreSame(failure, observed);
            else
            {
                Assert.IsInstanceOfType<OperationCanceledException>(observed);
                Assert.AreEqual(token, ((OperationCanceledException)observed!).CancellationToken);
            }
        });

    [TestMethod]
    public Task PresentedOperation_RequestedCancellationDoesNotAbandonOriginal()
        => RunAsync(async tasks =>
        {
            var gate = tasks.Gate();
            var token = new CancellationToken(true);
            var caller = tasks.Track(TerminalPluginStartupFeedback.RunPresentedOperationAsync(new RecordingProgress(),
                (_, actualToken) =>
                {
                    Assert.AreEqual(token, actualToken);
                    return new ValueTask<object>(gate.Task);
                }, original => tasks.Track(original), token).AsTask());
            Assert.IsFalse(caller.IsCompleted, "Display stop/cancellation is not operation termination.");
            var expected = new object();
            gate.SetResult(expected);
            Assert.AreSame(expected, await tasks.WaitAsync(caller));
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task PresentedOperation_DriverFailurePreservesExistingUnjoinedOriginal(bool cancellation)
        => RunAsync(async tasks =>
        {
            var gate = tasks.Gate();
            Exception failure = cancellation ? new OperationCanceledException(new CancellationToken(true)) : new InvalidOperationException("driver/summary");
            var caller = tasks.Track(TerminalPluginStartupFeedback.RunPresentedOperationAsync(new RecordingProgress(),
                (_, _) => new ValueTask<object>(gate.Task), original =>
                {
                    tasks.Track(original);
                    throw failure;
                }, CancellationToken.None).AsTask());
            Exception? observed = null;
            try { await tasks.WaitAsync(caller); }
            catch (Exception error) { observed = error; }
            Assert.AreSame(failure, observed);
            Assert.IsFalse(gate.Task.IsCompleted, "Preserved existing gap, not a new termination guarantee.");
            // The fixture, unlike the existing driver-failure path, finally releases and joins this original.
        });

    [TestMethod]
    public Task PresentedOperation_SynchronousOperationThrowSkipsDriver()
        => RunAsync(async tasks =>
        {
            var failure = new InvalidOperationException("synchronous operation");
            var driverCalls = 0;
            var caller = tasks.Track(TerminalPluginStartupFeedback.RunPresentedOperationAsync<object>(new RecordingProgress(),
                (_, _) => throw failure, _ => driverCalls++, CancellationToken.None).AsTask());
            Exception? observed = null;
            try { await tasks.WaitAsync(caller); }
            catch (Exception error) { observed = error; }
            Assert.AreSame(failure, observed);
            Assert.AreEqual(0, driverCalls);
        });

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

    // Relocated existing UI/reflection fixture. EXCLUDED from the inert selection: creates files and UI state.
    [TestMethod]
    [TestCategory("PluginFeedbackUiState")]
    public void LiveStatusAppliesQueuedProgressToBindableState()
    {
        using var temp = new TestTempDirectory();
        var firstPackage = CreatePackage(temp.Path, "hello");
        var secondPackage = CreatePackage(temp.Path, "world");
        var requests = new List<PluginBuildRequest>
        {
            new() { Package = firstPackage },
            new() { Package = secondPackage },
        };
        var status = CreateLiveStatus(requests);

        InvokeLiveStatus(status, "Report", new PluginBuildProgress { Package = firstPackage, Index = 0, Total = 2, State = PluginBuildProgressState.Running });

        Assert.IsTrue(BuildHeaderMarkup(status).Contains("0/2 complete", StringComparison.Ordinal));

        InvokeLiveStatus(status, "ApplyPendingUpdates");

        Assert.IsTrue(BuildHeaderMarkup(status).Contains("0/2 complete, 1 running", StringComparison.Ordinal));
        Assert.AreEqual("[warning]◌[/]  1. [warning]Building[/] hello", BuildItemMarkup(status, 0));

        InvokeLiveStatus(status, "Report", new PluginBuildProgress { Package = firstPackage, Index = 0, Total = 2, State = PluginBuildProgressState.Succeeded });
        InvokeLiveStatus(status, "Report", new PluginBuildProgress { Package = secondPackage, Index = 1, Total = 2, State = PluginBuildProgressState.UpToDate });
        InvokeLiveStatus(status, "ApplyPendingUpdates");
        InvokeLiveStatus(status, "MarkCompleted", "CodeAlta plugins: 2 source plugin packages checked (1 built, 1 up-to-date); 2 source plugins activated in 42ms.");

        Assert.AreEqual("[success]✓[/] Plugin startup complete (2/2 complete)", BuildHeaderMarkup(status));
        Assert.AreEqual("[dim]Press Enter to continue.[/]", BuildFooterMarkup(status, waitForEnterAfterCompletion: true));
        Assert.AreEqual("CodeAlta plugins: 2 source plugin packages checked (1 built, 1 up-to-date); 2 source plugins activated in 42ms.", BuildSummaryMarkup(status));
    }

    private static SourcePluginPackage CreatePackage(string rootPath, string id)
    {
        var directory = Path.Combine(rootPath, id);
        Directory.CreateDirectory(directory);
        var entry = Path.Combine(directory, "plugin.cs");
        File.WriteAllText(entry, "// plugin");
        return new SourcePluginPackage
        {
            PackageId = id,
            PackageDirectory = directory,
            EntryFilePath = entry,
            Root = new PluginRoot { RootPath = rootPath, Scope = PluginScope.Global },
        };
    }

    private static object CreateLiveStatus(IReadOnlyList<PluginBuildRequest> requests)
    {
        var statusType = GetLiveStatusType();
        return Activator.CreateInstance(statusType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, args: [requests], culture: null)
            ?? throw new InvalidOperationException("Could not create plugin build live status.");
    }

    private static string BuildHeaderMarkup(object status)
        => (string)InvokeLiveStatus(status, "BuildHeaderMarkup")!;

    private static string BuildItemMarkup(object status, int index)
        => (string)InvokeLiveStatus(status, "BuildItemMarkup", index)!;

    private static string BuildFooterMarkup(object status, bool waitForEnterAfterCompletion)
        => (string)InvokeLiveStatus(status, "BuildFooterMarkup", waitForEnterAfterCompletion)!;

    private static string BuildSummaryMarkup(object status)
        => ReadStateValue<string?>(status, "_summaryMarkup")!;

    private static T ReadStateValue<T>(object status, string fieldName)
    {
        var field = status.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find live status field {fieldName}.");
        var state = field.GetValue(status) ?? throw new InvalidOperationException($"Live status field {fieldName} is null.");
        var valueProperty = state.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Could not find Value property on {fieldName}.");
        return (T)valueProperty.GetValue(state)!;
    }

    private static object? InvokeLiveStatus(object status, string methodName, params object[] args)
    {
        var method = status.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find live status method {methodName}.");
        return method.Invoke(status, args);
    }

    private static Type GetLiveStatusType()
        => typeof(TerminalPluginStartupFeedback).GetNestedType("PluginBuildLiveStatus", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find plugin build live status type.");

    private sealed class TestTempDirectory : IDisposable
    {
        public TestTempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.Plugins.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch { }
        }
    }
}
