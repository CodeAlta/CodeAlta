using System.Runtime.CompilerServices;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaHostLifetimeTests
{
    [TestMethod]
    public void HostOwner_SourceWiring_UsesSingleNamedDisposalFactory()
    {
        // Wiring evidence only: read this named checkout file, never construct a host.
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "CodeAlta.Orchestration", "Hosting", "CodeAltaHost.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var fields = Scope(source, "public sealed class CodeAltaHost : IAsyncDisposable", "    private CodeAltaHost(");
        RequireOnce(fields, "private readonly Lazy<Task> _disposeTask;");
        var constructor = Scope(source, "    private CodeAltaHost(", "    public CatalogOptions CatalogOptions");
        RequireOnce(constructor, """
                    _disposeTask = CreateHostDisposal(
                        RuntimeService.DisposeAsync,
                        AgentHub.DisposeAsync,
                        ModelProviderRegistry.DisposeAsync,
                        PluginRuntime.DisposeAsync,
                        LogManager.Shutdown,
                        ownsPluginRuntime,
                        ownsLogging);
            """);
        RequireOnce(source, "public ValueTask DisposeAsync() => new(_disposeTask.Value);");
        Assert.IsFalse(source.Contains("public async ValueTask DisposeAsync()", StringComparison.Ordinal));

        var create = Scope(source, "    public static async Task<CodeAltaHost> CreateAsync(",
            "    private static async Task<ProjectDescriptor> ResolveCurrentProjectAsync(");
        RequireOnce(create, "var pluginRuntime = options.PrestartedPluginRuntime ?? new PluginRuntimeManager();");
        RequireOnce(create, "var ownsPluginRuntime = options.PrestartedPluginRuntime is null;");
        RequireOnce(create, "if (options.StartPlugins && options.PrestartedPluginRuntime is null)");
        RequireOnce(create, """
                        projectFileSearchService,
                        pluginRuntime,
                        ownsPluginRuntime,
                        ownsLogging,
                        currentProject);
            """);

        var core = Scope(source, "    private static async Task DisposeHostCoreAsync(", "\n    }\n");
        RequireOnce(core, "if (ownsPluginRuntime)");
        RequireOnce(core, "await disposePluginRuntime().ConfigureAwait(false);");
        RequireOnce(core, "if (ownsLogging)");
        RequireOnce(core, "shutdownLogging();");
    }

    [TestMethod]
    [DataRow("runtime", false, false, "disposeRuntimeService")]
    [DataRow("hub", false, false, "disposeAgentHub")]
    [DataRow("registry", false, false, "disposeModelProviderRegistry")]
    [DataRow("plugin", false, false, "disposePluginRuntime")]
    [DataRow("plugin", true, false, "disposePluginRuntime")]
    [DataRow("logging", false, false, "shutdownLogging")]
    [DataRow("logging", false, true, "shutdownLogging")]
    [DataRow("all", false, false, "disposeRuntimeService")]
    public void CreateHostDisposal_ValidatesMandatoryOperationsBeforeInvocation(
        string missing, bool ownsPlugin, bool ownsLogging, string parameter)
    {
        var recording = new RecordingOperations();
        var failure = Assert.ThrowsExactly<ArgumentNullException>(() => CodeAltaHost.CreateHostDisposal(
            missing is "runtime" or "all" ? null! : recording.DisposeRuntime,
            missing is "hub" or "all" ? null! : recording.DisposeHub,
            missing is "registry" or "all" ? null! : recording.DisposeRegistry,
            missing is "plugin" or "all" ? null! : recording.DisposePlugin,
            missing is "logging" or "all" ? null! : recording.ShutdownLogging,
            ownsPlugin, ownsLogging));
        Assert.AreEqual(parameter, failure.ParamName);
        Assert.AreEqual(0, recording.Events.Count);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task CreateHostDisposal_IsLazyAndUsesOwnedStageOrder(bool ownsPlugin, bool ownsLogging)
    {
        var recording = new RecordingOperations();
        var disposal = recording.Create(ownsPlugin, ownsLogging);
        Assert.IsFalse(disposal.IsValueCreated);
        Assert.AreEqual(0, recording.Events.Count);
        var task = disposal.Value;
        var failure = await ObserveAsync(task);
        Assert.IsNull(failure);
        Assert.IsTrue(task.IsCompletedSuccessfully);
        CollectionAssert.AreEqual(ExpectedStages(ownsPlugin, ownsLogging), recording.Events);
    }

    [TestMethod]
    [DataRow("runtime")]
    [DataRow("hub")]
    [DataRow("registry")]
    [DataRow("plugin")]
    public async Task HostDisposal_SynchronousAsyncOperationThrow_StillAttemptsLaterStages(string stage)
    {
        var recording = new RecordingOperations();
        var expected = new InvalidOperationException(stage);
        recording.Operations[stage] = () => throw expected;
        var task = recording.Create().Value;
        var failure = await ObserveAsync(task);
        Assert.AreSame(expected, failure);
        Assert.IsTrue(task.IsFaulted);
        CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
    }

    [TestMethod]
    [DataRow("runtime")]
    [DataRow("hub")]
    [DataRow("registry")]
    [DataRow("plugin")]
    public async Task HostDisposal_ReturnedFault_StillAttemptsLaterStages(string stage)
    {
        var recording = new RecordingOperations();
        var expected = new InvalidOperationException(stage);
        var operationTask = Task.FromException(expected);
        Task? task = null;
        try
        {
            recording.Operations[stage] = () => new ValueTask(operationTask);
            task = recording.Create().Value;
            var failure = await ObserveAsync(task);
            var operationFailure = await ObserveAsync(operationTask);
            Assert.AreSame(expected, operationFailure);
            Assert.AreSame(expected, failure);
            Assert.IsTrue(task.IsFaulted);
            CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
        }
        finally
        {
            await ObserveAllAsync(operationTask, task);
        }
    }

    [TestMethod]
    [DataRow("runtime")]
    [DataRow("hub")]
    [DataRow("registry")]
    [DataRow("plugin")]
    public async Task HostDisposal_ReturnedCancellation_StillAttemptsLaterStages(string stage)
    {
        var recording = new RecordingOperations();
        var token = new CancellationToken(canceled: true);
        var expected = new OperationCanceledException(stage, token);
        var operationTask = CreateCanceledTaskAsync(expected);
        Task? task = null;
        try
        {
            recording.Operations[stage] = () => new ValueTask(operationTask);
            task = recording.Create().Value;
            var failure = await ObserveAsync(task);
            var operationFailure = await ObserveAsync(operationTask);
            Assert.IsTrue(operationTask.IsCanceled);
            Assert.IsTrue(task.IsCanceled);
            Assert.AreSame(expected, operationFailure);
            Assert.AreSame(expected, failure);
            Assert.AreEqual(token, ((OperationCanceledException)failure!).CancellationToken);
            CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
        }
        finally
        {
            await ObserveAllAsync(operationTask, task);
        }
    }

    [TestMethod]
    public async Task HostDisposal_LoggingThrow_PreservesSingleFailure()
    {
        var recording = new RecordingOperations();
        var expected = new InvalidOperationException("logging");
        recording.Logging = () => throw expected;
        var task = recording.Create().Value;
        var failure = await ObserveAsync(task);
        Assert.AreSame(expected, failure);
        Assert.IsTrue(task.IsFaulted);
        CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("nested")]
    [DataRow("cancellations")]
    public async Task HostDisposal_MultipleFailures_PreservesExecutionOrderAndIdentity(string kind)
    {
        var recording = new RecordingOperations();
        Exception first = kind switch
        {
            "nested" => new AggregateException(new InvalidOperationException("nested")),
            "cancellations" => new OperationCanceledException(new CancellationToken(canceled: true)),
            _ => new InvalidOperationException("first"),
        };
        Exception second = kind == "ordinary"
            ? new ArgumentException("second")
            : new OperationCanceledException(new CancellationToken(canceled: true));
        recording.Operations["runtime"] = () => throw first;
        recording.Logging = () => throw second;
        var task = recording.Create().Value;
        var failure = await ObserveAsync(task);
        Assert.IsInstanceOfType<AggregateException>(failure);
        var aggregate = (AggregateException)failure;
        Assert.AreEqual(2, aggregate.InnerExceptions.Count);
        Assert.AreSame(first, aggregate.InnerExceptions[0]);
        Assert.AreSame(second, aggregate.InnerExceptions[1]);
        Assert.IsTrue(task.IsFaulted);
        CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("cancellation")]
    public async Task HostDisposal_RepeatedValue_ReusesTerminalTaskWithoutRetry(string outcome)
    {
        var recording = new RecordingOperations();
        var expected = CreateOutcome(outcome);
        recording.Operations["runtime"] = () => expected is null ? ValueTask.CompletedTask : throw expected;
        var disposal = recording.Create();
        var first = disposal.Value;
        var firstFailure = await ObserveAsync(first);
        var second = disposal.Value;
        var secondFailure = await ObserveAsync(second);
        Assert.AreSame(first, second);
        Assert.AreSame(expected, firstFailure);
        Assert.AreSame(expected, secondFailure);
        AssertOutcome(first, outcome);
        CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("cancellation")]
    public async Task HostDisposal_ConcurrentValue_SharesPendingOperationAndOutcome(string outcome)
    {
        var recording = new RecordingOperations();
        var expected = CreateOutcome(outcome);
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquired = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var sharedTasks = new Task?[3];
        var failures = new Exception?[3];
        var disposal = recording.Create();
        var allAcquired = Task.WhenAll(acquired.Select(static signal => signal.Task));
        Task? joined = null;
        // Independently retain the one pending operation, even if a broken owner invokes the callback twice.
        var operationTask = HoldAsync();
        try
        {
            recording.Operations["runtime"] = () =>
            {
                entered.TrySetResult();
                return new ValueTask(operationTask);
            };
            var callers = new[] { ReadAsync(0), ReadAsync(1), ReadAsync(2) };
            joined = Task.WhenAll(callers);
            launch.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await allAcquired.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(sharedTasks[0], sharedTasks[1]);
            Assert.AreSame(sharedTasks[0], sharedTasks[2]);
            Assert.IsFalse(sharedTasks[0]!.IsCompleted);
            CollectionAssert.AreEqual(new[] { "runtime" }, recording.Events);
            release.TrySetResult();
            await joined.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var failure in failures)
            {
                Assert.AreSame(expected, failure);
            }
            AssertOutcome(sharedTasks[0]!, outcome);
            CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
        }
        finally
        {
            launch.TrySetResult();
            entered.TrySetResult();
            release.TrySetResult();
            foreach (var signal in acquired) signal.TrySetResult();
            try
            {
                await ObserveAllAsync(joined, allAcquired, operationTask);
            }
            finally
            {
                await ObserveAllAsync(sharedTasks);
            }
        }

        async Task HoldAsync()
        {
            await release.Task.ConfigureAwait(false);
            if (expected is not null) throw expected;
        }

        async Task ReadAsync(int index)
        {
            await launch.Task.ConfigureAwait(false);
            var task = disposal.Value;
            sharedTasks[index] = task;
            acquired[index].TrySetResult();
            failures[index] = await ObserveAsync(task);
        }
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    public async Task HostDisposal_BorrowedCallbacksRemainUncalled(bool ownsPlugin, bool ownsLogging)
    {
        var recording = new RecordingOperations();
        if (!ownsPlugin) recording.Operations["plugin"] = () => throw new AssertFailedException("Borrowed plugin invoked.");
        if (!ownsLogging) recording.Logging = () => throw new AssertFailedException("Borrowed logging invoked.");
        var task = recording.Create(ownsPlugin, ownsLogging).Value;
        var failure = await ObserveAsync(task);
        Assert.IsNull(failure);
        CollectionAssert.AreEqual(ExpectedStages(ownsPlugin, ownsLogging), recording.Events);
    }

    private sealed class RecordingOperations
    {
        private readonly object _gate = new();
        internal List<string> Events { get; } = [];
        internal Dictionary<string, Func<ValueTask>> Operations { get; } = new(StringComparer.Ordinal);
        internal Action? Logging { get; set; }

        internal Lazy<Task> Create(bool ownsPlugin = true, bool ownsLogging = true)
            => CodeAltaHost.CreateHostDisposal(DisposeRuntime, DisposeHub, DisposeRegistry, DisposePlugin, ShutdownLogging, ownsPlugin, ownsLogging);

        internal ValueTask DisposeRuntime() => InvokeAsync("runtime");
        internal ValueTask DisposeHub() => InvokeAsync("hub");
        internal ValueTask DisposeRegistry() => InvokeAsync("registry");
        internal ValueTask DisposePlugin() => InvokeAsync("plugin");

        internal void ShutdownLogging()
        {
            lock (_gate) Events.Add("logging");
            Logging?.Invoke();
        }

        private ValueTask InvokeAsync(string stage)
        {
            lock (_gate) Events.Add(stage);
            return Operations.TryGetValue(stage, out var operation) ? operation() : ValueTask.CompletedTask;
        }
    }

    private static string[] ExpectedStages(bool ownsPlugin = true, bool ownsLogging = true)
    {
        var stages = new List<string> { "runtime", "hub", "registry" };
        if (ownsPlugin) stages.Add("plugin");
        if (ownsLogging) stages.Add("logging");
        return stages.ToArray();
    }

    private static Exception? CreateOutcome(string outcome) => outcome switch
    {
        "success" => null,
        "fault" => new InvalidOperationException("recorded failure"),
        "cancellation" => new OperationCanceledException(new CancellationToken(canceled: true)),
        _ => throw new AssertFailedException("Unknown synthetic outcome."),
    };

    private static void AssertOutcome(Task task, string outcome)
    {
        Assert.AreEqual(outcome == "success", task.IsCompletedSuccessfully);
        Assert.AreEqual(outcome == "fault", task.IsFaulted);
        Assert.AreEqual(outcome == "cancellation", task.IsCanceled);
    }

    private static async Task CreateCanceledTaskAsync(OperationCanceledException failure)
    {
        await Task.CompletedTask;
        throw failure;
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return null;
        }
        catch (Exception failure) when (task.IsCompleted)
        {
            return failure;
        }
    }

    private static async Task ObserveAllAsync(params Task?[] tasks)
    {
        // Start every bounded observation before awaiting any: one timeout cannot skip an independent join.
        var observations = tasks.OfType<Task>().Select(ObserveAsync).ToArray();
        var joined = Task.WhenAll(observations);
        await joined.ConfigureAwait(false);
    }

    private static string SourceRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile) ?? throw new AssertFailedException("Missing fixture source directory."), ".."));

    private static string Scope(string source, string startAnchor, string endAnchor)
    {
        var start = source.IndexOf(startAnchor, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Missing source anchor: {startAnchor}");
        var end = source.IndexOf(endAnchor, start + startAnchor.Length, StringComparison.Ordinal);
        Assert.IsTrue(end > start, $"Missing following source anchor: {endAnchor}");
        return source[start..end];
    }

    private static void RequireOnce(string source, string expected)
    {
        var first = source.IndexOf(expected, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0, $"Missing expected wiring: {expected}");
        Assert.AreEqual(first, source.LastIndexOf(expected, StringComparison.Ordinal), $"Duplicate wiring: {expected}");
    }
}
