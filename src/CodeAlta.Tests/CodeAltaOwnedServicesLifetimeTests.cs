using System.Runtime.CompilerServices;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Tui.App;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaOwnedServicesLifetimeTests
{
    [TestMethod]
    public void OwnedServicesOwner_SourceWiring_RetainsHostAndRemovesParallelDisposal()
    {
        // Wiring evidence only: no production owner, service, factory or disposer is invoked.
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "CodeAlta.Tui", "App", "CodeAltaOwnedServices.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var fields = Scope(source, "internal sealed class CodeAltaOwnedServices : IAsyncDisposable", "    private CodeAltaOwnedServices(");
        RequireOnce(fields, "private readonly CodeAltaHost _host;");
        RequireOnce(fields, "private readonly Lazy<Task> _disposeTask;");
        var constructor = Scope(source, "    private CodeAltaOwnedServices(", "    public CatalogOptions CatalogOptions");
        RequireOnce(constructor, """
                private CodeAltaOwnedServices(
                    bool ownsLogging,
                    CodeAltaHost host,
                    CodeAltaConfigStore configStore,
                    ModelsDevCatalogService modelsDevCatalogService,
                    PluginHostBridge pluginHostBridge,
                    List<ModelProviderDescriptor> providerDescriptors)
            """);
        RequireOnce(constructor, "_host = host;");
        foreach (var assignment in new[]
        {
            "_modelProviderRegistry = host.ModelProviderRegistry;",
            "_modelProviderInitializationService = host.ModelProviderInitializationService;",
            "PluginRuntime = host.PluginRuntime;",
            "AgentSessionCatalog = host.AgentSessionCatalog;",
            "CatalogOptions = host.CatalogOptions;",
            "ProjectCatalog = host.ProjectCatalog;",
            "SessionViewCatalog = host.SessionViewCatalog;",
            "SkillCatalog = host.SkillCatalog;",
            "AgentHub = host.AgentHub;",
            "RuntimeService = host.RuntimeService;",
            "ProjectFileSearchService = host.ProjectFileSearchService;",
            "CurrentProject = host.CurrentProject;",
            "_modelsDevCatalogService = modelsDevCatalogService;",
        })
        {
            RequireOnce(constructor, assignment);
        }

        RequireOnce(constructor, """
                    _disposeTask = CreateOwnedServicesDisposal(
                        _host.DisposeAsync,
                        _modelsDevCatalogService.DisposeAsync,
                        LogManager.Shutdown,
                        ownsLogging);
            """);
        RequireOnce(source, "public ValueTask DisposeAsync() => new(_disposeTask.Value);");
        Assert.IsFalse(source.Contains("public async ValueTask DisposeAsync()", StringComparison.Ordinal));

        var create = Scope(source, "    public static async Task<CodeAltaOwnedServices> CreateAsync(",
            "            void RegisterFrontendModelProviders(");
        RequireOnce(create, "sharedHost = await CodeAltaHost.CreateAsync(");
        RequireOnce(create, "PrestartedPluginRuntime = prestartedPluginRuntime,");
        RequireOnce(create, """
                        return new CodeAltaOwnedServices(
                            ownsLogging,
                            sharedHost,
                            configStore,
                            modelsDevCatalogService,
                            pluginHostBridge,
                            providerDescriptors);
            """);

        foreach (var oldDisposal in new[]
        {
            "RuntimeService.DisposeAsync(",
            "AgentHub.DisposeAsync(",
            "_modelProviderRegistry.DisposeAsync(",
            "PluginRuntime.DisposeAsync(",
        })
        {
            Assert.IsFalse(source.Contains(oldDisposal, StringComparison.Ordinal), $"Parallel child disposal remains: {oldDisposal}");
        }
    }

    [TestMethod]
    public void OwnedServicesCreation_SourceWiring_TracksAcquisitionsAndAwaitsNamedRollback()
    {
        // Exact named-checkout wiring evidence only; no owner, metadata refresh or startup is executed.
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "CodeAlta.Tui", "App", "CodeAltaOwnedServices.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        source = PluginFeedbackExtractionSourceTests.Restore("CodeAlta.Tui/App/CodeAltaOwnedServices.cs", source);
        var create = Scope(source, "    public static async Task<CodeAltaOwnedServices> CreateAsync(", "\n    }\n");
        RequireOnce(create, "ModelsDevCatalogService? modelsDevCatalogService = null;");
        Assert.IsTrue(create.StartsWith("""
                public static async Task<CodeAltaOwnedServices> CreateAsync(
                    CancellationToken cancellationToken,
                    PluginRuntimeManager? prestartedPluginRuntime = null)
            """ + "\n", StringComparison.Ordinal));

        var setup = Scope(create, "    {\n", "        try\n");
        Assert.AreEqual("""
                {
                    ModelsDevCatalogService? modelsDevCatalogService = null;
                    CodeAltaHost? sharedHost = null;
                    var ownsLogging = false;
            """ + "\n\n", setup);

        // Keep root/logging/options order, immediate refresh, host options/token and registration captures.
        var acquisition = Scope(create, "        try\n", "        catch (Exception creationFailure)\n");
        Assert.AreEqual("""
                    try
                    {
                        var homeRoot = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".alta");
                        Directory.CreateDirectory(homeRoot);
                        var cacheRoot = Path.Combine(homeRoot, "cache");
                        ownsLogging = CodeAltaLogging.Initialize(homeRoot);

                        Directory.CreateDirectory(cacheRoot);
                        var rawArguments = Environment.GetCommandLineArgs();
                        var pluginBootstrapOptions = CodeAltaCliOptions.GetPluginBootstrapOptions(rawArguments);
                        var catalogOptions = new CatalogOptions { GlobalRoot = homeRoot };
                        var configStore = new CodeAltaConfigStore(catalogOptions);
                        modelsDevCatalogService = new ModelsDevCatalogService(
                            new ModelsDevCatalogServiceOptions
                            {
                                CacheFilePath = Path.Combine(cacheRoot, "model-catalog", "models_dev_db.json"),
                            });
                        modelsDevCatalogService.StartBackgroundRefresh();

                        var providerDescriptors = new List<ModelProviderDescriptor>();
                        var pluginAltaServiceBridge = new PluginAltaServiceBridge();
                        sharedHost = await CodeAltaHost.CreateAsync(
                                new CodeAltaHostOptions
                                {
                                    GlobalRoot = homeRoot,
                                    CurrentProjectPath = Environment.CurrentDirectory,
                                    IsHeadless = false,
                                    HasInteractiveUi = true,
                                    PluginSafeMode = pluginBootstrapOptions.PluginSafeMode,
                                    RawArguments = rawArguments,
                                    WaitForEnterAfterPluginLiveOutput = pluginBootstrapOptions.WaitForEnterAfterPluginLiveOutput,
                                    PrestartedPluginRuntime = prestartedPluginRuntime,
                                    PluginBuiltIns = CodeAltaBuiltInPlugins.All,
                                    PluginServices = new CodeAltaPluginServices(pluginAltaServiceBridge),
                                    ConfigureModelProviders = RegisterFrontendModelProviders,
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                        var pluginRuntime = sharedHost.PluginRuntime;
                        var pluginHostBridge = new PluginHostBridge(pluginRuntime, () => sharedHost.CurrentProject, pluginAltaServiceBridge);

                        return new CodeAltaOwnedServices(
                            ownsLogging,
                            sharedHost,
                            configStore,
                            modelsDevCatalogService,
                            pluginHostBridge,
                            providerDescriptors);

                        void RegisterFrontendModelProviders(ModelProviderRegistry modelProviderRegistry)
                        {
                            providerDescriptors.AddRange(
                                ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(
                                    modelProviderRegistry,
                                    configStore,
                                    homeRoot,
                                    modelsDevCatalogService));

                        }
                    }
            """ + "\n", acquisition);

        // Only the completed host owns its runtime children; failed inner creation returns no host slot.
        var rollback = Scope(create, "        catch (Exception creationFailure)\n", "\n        }");
        Assert.AreEqual("""
                    catch (Exception creationFailure)
                    {
                        await RollbackOwnedServicesCreationAsync(
                            creationFailure,
                            () => sharedHost?.DisposeAsync() ?? ValueTask.CompletedTask,
                            () => modelsDevCatalogService?.DisposeAsync() ?? ValueTask.CompletedTask,
                            LogManager.Shutdown,
                            ownsLogging).ConfigureAwait(false);
                        throw;
            """, rollback);
        Assert.IsTrue(create.EndsWith(rollback + "\n        }", StringComparison.Ordinal));
        foreach (var parallelDisposal in new[]
        {
            "RuntimeService.DisposeAsync(",
            "AgentHub.DisposeAsync(",
            "ModelProviderRegistry.DisposeAsync(",
            "PluginRuntime.DisposeAsync(",
        })
        {
            Assert.IsFalse(create.Contains(parallelDisposal, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [DataRow("host", false, "disposeHost")]
    [DataRow("metadata", false, "disposeModelsDevCatalog")]
    [DataRow("logging", false, "shutdownLogging")]
    [DataRow("logging", true, "shutdownLogging")]
    [DataRow("all", false, "disposeHost")]
    public void CreateOwnedServicesDisposal_ValidatesMandatoryOperationsBeforeInvocation(
        string missing, bool ownsLogging, string parameter)
    {
        var recording = new RecordingOperations();
        var failure = Assert.ThrowsExactly<ArgumentNullException>(() => CodeAltaOwnedServices.CreateOwnedServicesDisposal(
            missing is "host" or "all" ? null! : recording.DisposeHost,
            missing is "metadata" or "all" ? null! : recording.DisposeMetadata,
            missing is "logging" or "all" ? null! : recording.ShutdownLogging,
            ownsLogging));
        Assert.AreEqual(parameter, failure.ParamName);
        Assert.AreEqual(0, recording.Events.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CreateOwnedServicesDisposal_IsLazyAndUsesOwnedStageOrder(bool ownsLogging)
    {
        var recording = new RecordingOperations();
        var disposal = recording.Create(ownsLogging);
        Assert.IsFalse(disposal.IsValueCreated);
        Assert.AreEqual(0, recording.Events.Count);
        var task = disposal.Value;
        var failure = await ObserveAsync(task);
        Assert.IsNull(failure);
        Assert.IsTrue(task.IsCompletedSuccessfully);
        CollectionAssert.AreEqual(ExpectedStages(ownsLogging), recording.Events);
    }

    [TestMethod]
    [DataRow("host")]
    [DataRow("metadata")]
    public async Task OwnedServicesDisposal_SynchronousAsyncOperationThrow_ContinuesCleanup(string stage)
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
    [DataRow("host")]
    [DataRow("metadata")]
    public async Task OwnedServicesDisposal_ReturnedFault_ContinuesCleanup(string stage)
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
    [DataRow("host")]
    [DataRow("metadata")]
    public async Task OwnedServicesDisposal_ReturnedCancellation_ContinuesCleanup(string stage)
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
    public async Task OwnedServicesDisposal_LoggingThrow_PreservesSingleFailure()
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
    public async Task OwnedServicesDisposal_MultipleFailures_PreservesExecutionOrderAndIdentity(string kind)
    {
        var recording = new RecordingOperations();
        Exception first = kind switch
        {
            "nested" => new AggregateException(new InvalidOperationException("nested host failure")),
            "cancellations" => new OperationCanceledException(new CancellationToken(canceled: true)),
            _ => new InvalidOperationException("first"),
        };
        Exception second = kind == "ordinary"
            ? new ArgumentException("second")
            : new OperationCanceledException(new CancellationToken(canceled: true));
        recording.Operations["host"] = () => throw first;
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
    public async Task OwnedServicesDisposal_RepeatedValue_ReusesTerminalTaskWithoutRetry(string outcome)
    {
        var recording = new RecordingOperations();
        var expected = CreateOutcome(outcome);
        recording.Operations["host"] = () => expected is null ? ValueTask.CompletedTask : throw expected;
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
    public async Task OwnedServicesDisposal_ConcurrentValue_SharesPendingOperationAndOutcome(string outcome)
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
            recording.Operations["host"] = () =>
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
            CollectionAssert.AreEqual(new[] { "host" }, recording.Events);
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
    [DataRow("success")]
    [DataRow("aggregate")]
    [DataRow("cancellation")]
    public async Task OwnedServicesDisposal_ComposedFactories_AwaitsEntireHostBeforeMetadata(string outcome)
    {
        var events = new List<string>();
        var eventGate = new object();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? first = outcome switch
        {
            "aggregate" => new InvalidOperationException("early host failure"),
            "cancellation" => new OperationCanceledException(new CancellationToken(canceled: true)),
            _ => null,
        };
        var second = new ArgumentException("late host failure");
        Task? outerTask = null;
        Task? hostTask = null;
        // A duplicate callback reuses this independently retained task rather than overwriting it.
        var lateTask = HoldPluginAsync();
        try
        {
            var host = CodeAltaHost.CreateHostDisposal(
                () =>
                {
                    Record("host-runtime");
                    return first is null ? ValueTask.CompletedTask : throw first;
                },
                () => { Record("host-hub"); return ValueTask.CompletedTask; },
                () => { Record("host-registry"); return ValueTask.CompletedTask; },
                () =>
                {
                    Record("host-plugin-enter");
                    entered.TrySetResult();
                    return new ValueTask(lateTask);
                },
                () => Record("host-logging"),
                true, true);
            hostTask = host.Value;
            var outer = CodeAltaOwnedServices.CreateOwnedServicesDisposal(
                () => new ValueTask(hostTask),
                () => { Record("metadata"); return ValueTask.CompletedTask; },
                () => Record("outer-logging"),
                true);
            outerTask = outer.Value;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(hostTask);
            Assert.IsFalse(hostTask.IsCompleted);
            Assert.IsFalse(outerTask.IsCompleted);
            CollectionAssert.AreEqual(new[] { "host-runtime", "host-hub", "host-registry", "host-plugin-enter" }, events);
            release.TrySetResult();
            var outerFailure = await ObserveAsync(outerTask);
            var hostFailure = await ObserveAsync(hostTask);
            Assert.AreSame(hostFailure, outerFailure);
            if (outcome == "aggregate")
            {
                Assert.IsInstanceOfType<AggregateException>(outerFailure);
                var aggregate = (AggregateException)outerFailure;
                Assert.AreEqual(2, aggregate.InnerExceptions.Count);
                Assert.AreSame(first, aggregate.InnerExceptions[0]);
                Assert.AreSame(second, aggregate.InnerExceptions[1]);
            }
            else
            {
                Assert.AreSame(first, outerFailure);
            }
            AssertOutcome(hostTask, outcome == "aggregate" ? "fault" : outcome);
            AssertOutcome(outerTask, outcome == "aggregate" ? "fault" : outcome);
            CollectionAssert.AreEqual(new[]
            {
                "host-runtime", "host-hub", "host-registry", "host-plugin-enter", "host-plugin-exit",
                "host-logging", "metadata", "outer-logging",
            }, events);
        }
        finally
        {
            entered.TrySetResult();
            release.TrySetResult();
            await ObserveAllAsync(outerTask, hostTask, lateTask, entered.Task);
        }

        async Task HoldPluginAsync()
        {
            await release.Task.ConfigureAwait(false);
            Record("host-plugin-exit");
            if (outcome == "aggregate") throw second;
        }

        void Record(string stage)
        {
            lock (eventGate) events.Add(stage);
        }
    }

    [TestMethod]
    public async Task OwnedServicesDisposal_BorrowedLoggingRemainsUncalled()
    {
        var recording = new RecordingOperations { Logging = () => throw new AssertFailedException("Borrowed logging invoked.") };
        var task = recording.Create(ownsLogging: false).Value;
        var failure = await ObserveAsync(task);
        Assert.IsNull(failure);
        CollectionAssert.AreEqual(ExpectedStages(ownsLogging: false), recording.Events);
    }

    [TestMethod]
    [DataRow("creation", false, "creationFailure")]
    [DataRow("host", false, "disposeHost")]
    [DataRow("metadata", false, "disposeModelsDevCatalog")]
    [DataRow("logging", false, "shutdownLogging")]
    [DataRow("logging", true, "shutdownLogging")]
    [DataRow("all", false, "creationFailure")]
    public async Task OwnedServicesCreation_Rollback_ValidatesMandatoryInputsBeforeInvocation(
        string missing, bool ownsLogging, string parameter)
    {
        var recording = new RecordingOperations();
        var creationFailure = new InvalidOperationException("creation");
        Task? rollbackTask = null;
        try
        {
            var failure = Assert.ThrowsExactly<ArgumentNullException>(() =>
            {
                rollbackTask = CodeAltaOwnedServices.RollbackOwnedServicesCreationAsync(
                    missing is "creation" or "all" ? null! : creationFailure,
                    missing is "host" or "all" ? null! : recording.DisposeHost,
                    missing is "metadata" or "all" ? null! : recording.DisposeMetadata,
                    missing is "logging" or "all" ? null! : recording.ShutdownLogging,
                    ownsLogging);
            });
            Assert.AreEqual(parameter, failure.ParamName);
            Assert.AreEqual(0, recording.Events.Count);
            Assert.IsNull(rollbackTask);
        }
        finally
        {
            await ObserveAllAsync(rollbackTask);
        }
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("cancellation")]
    [DataRow("nested")]
    public async Task OwnedServicesCreation_Rollback_CleanCleanup_RethrowsOriginalCreationFailure(string kind)
    {
        var recording = new RecordingOperations();
        Exception expected = kind switch
        {
            "cancellation" => new OperationCanceledException(new CancellationToken(canceled: true)),
            "nested" => new AggregateException(new InvalidOperationException("nested creation")),
            _ => new InvalidOperationException("creation"),
        };
        Task? rollbackTask = null;
        try
        {
            rollbackTask = RunRollbackAsync(recording, expected);
            var failure = await ObserveAsync(rollbackTask);
            Assert.AreSame(expected, failure);
            Assert.AreEqual(kind == "cancellation", rollbackTask.IsCanceled);
            Assert.AreEqual(kind != "cancellation", rollbackTask.IsFaulted);
            if (expected is OperationCanceledException cancellation)
            {
                Assert.IsInstanceOfType<OperationCanceledException>(failure);
                Assert.AreEqual(cancellation.CancellationToken, ((OperationCanceledException)failure).CancellationToken);
            }
            CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
        }
        finally
        {
            await ObserveAllAsync(rollbackTask);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OwnedServicesCreation_Rollback_OwnershipFlags_PreserveCleanupOrder(bool ownsLogging)
    {
        var recording = new RecordingOperations();
        var expected = new InvalidOperationException("creation");
        if (!ownsLogging) recording.Logging = () => throw new AssertFailedException("Borrowed logging invoked.");
        Task? rollbackTask = null;
        try
        {
            rollbackTask = CodeAltaOwnedServices.RollbackOwnedServicesCreationAsync(
                expected, recording.DisposeHost, recording.DisposeMetadata, recording.ShutdownLogging, ownsLogging);
            Assert.AreSame(expected, await ObserveAsync(rollbackTask));
            Assert.IsTrue(rollbackTask.IsFaulted);
            CollectionAssert.AreEqual(ExpectedStages(ownsLogging), recording.Events);
        }
        finally
        {
            await ObserveAllAsync(rollbackTask);
        }
    }

    [TestMethod]
    [DataRow("host")]
    [DataRow("metadata")]
    public async Task OwnedServicesCreation_Rollback_SynchronousAsyncCleanupThrow_AttemptsLaterStages(string stage)
    {
        var recording = new RecordingOperations();
        var creationFailure = new InvalidOperationException("creation");
        var cleanupFailure = new ArgumentException("cleanup");
        recording.Operations[stage] = () => throw cleanupFailure;
        Task? rollbackTask = null;
        try
        {
            rollbackTask = RunRollbackAsync(recording, creationFailure);
            var aggregate = AssertCreationAggregate(rollbackTask, await ObserveAsync(rollbackTask), creationFailure);
            Assert.AreSame(cleanupFailure, aggregate.InnerExceptions[1]);
            CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
        }
        finally
        {
            await ObserveAllAsync(rollbackTask);
        }
    }

    [TestMethod]
    [DataRow("host")]
    [DataRow("metadata")]
    public async Task OwnedServicesCreation_Rollback_ReturnedFault_AttemptsLaterStages(string stage)
    {
        var recording = new RecordingOperations();
        var creationFailure = new InvalidOperationException("creation");
        var cleanupFailure = new ArgumentException("cleanup");
        Task? rollbackTask = null;
        var operationTask = Task.FromException(cleanupFailure);
        try
        {
            recording.Operations[stage] = () => new ValueTask(operationTask);
            rollbackTask = RunRollbackAsync(recording, creationFailure);
            var aggregate = AssertCreationAggregate(rollbackTask, await ObserveAsync(rollbackTask), creationFailure);
            Assert.AreSame(cleanupFailure, aggregate.InnerExceptions[1]);
            Assert.IsTrue(operationTask.IsFaulted);
            CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
        }
        finally
        {
            await ObserveAllAsync(rollbackTask, operationTask);
        }
    }

    [TestMethod]
    [DataRow("host")]
    [DataRow("metadata")]
    public async Task OwnedServicesCreation_Rollback_ReturnedCancellation_AttemptsLaterStages(string stage)
    {
        var recording = new RecordingOperations();
        var creationFailure = new OperationCanceledException(new CancellationToken(canceled: true));
        var cleanupFailure = new OperationCanceledException(new CancellationToken(canceled: true));
        Task? rollbackTask = null;
        var operationTask = CreateCanceledTaskAsync(cleanupFailure);
        try
        {
            Assert.IsTrue(operationTask.IsCanceled);
            recording.Operations[stage] = () => new ValueTask(operationTask);
            rollbackTask = RunRollbackAsync(recording, creationFailure);
            var aggregate = AssertCreationAggregate(rollbackTask, await ObserveAsync(rollbackTask), creationFailure);
            Assert.AreSame(cleanupFailure, aggregate.InnerExceptions[1]);
            Assert.AreEqual(cleanupFailure.CancellationToken, ((OperationCanceledException)aggregate.InnerExceptions[1]).CancellationToken);
            CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
        }
        finally
        {
            await ObserveAllAsync(rollbackTask, operationTask);
        }
    }

    [TestMethod]
    public async Task OwnedServicesCreation_Rollback_LoggingThrow_KeepsCreationFailureFirst()
    {
        var recording = new RecordingOperations();
        var creationFailure = new InvalidOperationException("creation");
        var cleanupFailure = new ArgumentException("logging");
        recording.Logging = () => throw cleanupFailure;
        Task? rollbackTask = null;
        try
        {
            rollbackTask = RunRollbackAsync(recording, creationFailure);
            var aggregate = AssertCreationAggregate(rollbackTask, await ObserveAsync(rollbackTask), creationFailure);
            Assert.AreSame(cleanupFailure, aggregate.InnerExceptions[1]);
            CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
        }
        finally
        {
            await ObserveAllAsync(rollbackTask);
        }
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("nested")]
    [DataRow("cancellations")]
    public async Task OwnedServicesCreation_Rollback_MultipleFailures_PreserveNestedOrderAndIdentity(string kind)
    {
        var recording = new RecordingOperations();
        Exception creationFailure = kind switch
        {
            "nested" => new AggregateException(new InvalidOperationException("nested creation")),
            "cancellations" => new OperationCanceledException(new CancellationToken(canceled: true)),
            _ => new InvalidOperationException("creation"),
        };
        Exception first = kind switch
        {
            "nested" => new AggregateException(new ArgumentException("nested cleanup")),
            "cancellations" => new OperationCanceledException(new CancellationToken(canceled: true)),
            _ => new ArgumentException("first cleanup"),
        };
        var second = new InvalidOperationException("second cleanup");
        recording.Operations["host"] = () => throw first;
        recording.Logging = () => throw second;
        Task? rollbackTask = null;
        try
        {
            rollbackTask = RunRollbackAsync(recording, creationFailure);
            var aggregate = AssertCreationAggregate(rollbackTask, await ObserveAsync(rollbackTask), creationFailure);
            Assert.IsInstanceOfType<AggregateException>(aggregate.InnerExceptions[1]);
            var cleanup = (AggregateException)aggregate.InnerExceptions[1];
            Assert.AreEqual(2, cleanup.InnerExceptions.Count);
            Assert.AreSame(first, cleanup.InnerExceptions[0]);
            Assert.AreSame(second, cleanup.InnerExceptions[1]);
            CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
        }
        finally
        {
            await ObserveAllAsync(rollbackTask);
        }
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("cancellation")]
    public async Task OwnedServicesCreation_Rollback_PendingCleanup_DelaysTerminalCreationOutcome(string outcome)
    {
        var recording = new RecordingOperations();
        var creationFailure = new InvalidOperationException("creation");
        var cleanupFailure = CreateOutcome(outcome);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? rollbackTask = null;
        // The callback signals entry but always returns this independently retained operation.
        var operationTask = HoldAsync();
        try
        {
            recording.Operations["host"] = () =>
            {
                entered.TrySetResult();
                return new ValueTask(operationTask);
            };
            rollbackTask = RunRollbackAsync(recording, creationFailure);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(rollbackTask.IsCompleted);
            CollectionAssert.AreEqual(new[] { "host" }, recording.Events);
            release.TrySetResult();
            var failure = await ObserveAsync(rollbackTask);
            if (cleanupFailure is null)
            {
                Assert.AreSame(creationFailure, failure);
                Assert.IsTrue(rollbackTask.IsFaulted);
            }
            else
            {
                var aggregate = AssertCreationAggregate(rollbackTask, failure, creationFailure);
                Assert.AreSame(cleanupFailure, aggregate.InnerExceptions[1]);
            }
            AssertOutcome(operationTask, outcome);
            CollectionAssert.AreEqual(ExpectedStages(), recording.Events);
        }
        finally
        {
            entered.TrySetResult();
            release.TrySetResult();
            await ObserveAllAsync(rollbackTask, operationTask, entered.Task, release.Task);
        }

        async Task HoldAsync()
        {
            await release.Task.ConfigureAwait(false);
            if (cleanupFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cleanupFailure);
        }
    }

    private static Task RunRollbackAsync(RecordingOperations recording, Exception creationFailure)
        => CodeAltaOwnedServices.RollbackOwnedServicesCreationAsync(
            creationFailure, recording.DisposeHost, recording.DisposeMetadata, recording.ShutdownLogging, true);

    private static AggregateException AssertCreationAggregate(Task task, Exception? failure, Exception creationFailure)
    {
        Assert.IsTrue(task.IsFaulted);
        Assert.IsInstanceOfType<AggregateException>(failure);
        var aggregate = (AggregateException)failure;
        Assert.AreEqual(2, aggregate.InnerExceptions.Count);
        Assert.AreSame(creationFailure, aggregate.InnerExceptions[0]);
        return aggregate;
    }

    private sealed class RecordingOperations
    {
        private readonly object _gate = new();
        internal List<string> Events { get; } = [];
        internal Dictionary<string, Func<ValueTask>> Operations { get; } = new(StringComparer.Ordinal);
        internal Action? Logging { get; set; }

        internal Lazy<Task> Create(bool ownsLogging = true)
            => CodeAltaOwnedServices.CreateOwnedServicesDisposal(DisposeHost, DisposeMetadata, ShutdownLogging, ownsLogging);

        internal ValueTask DisposeHost() => InvokeAsync("host");
        internal ValueTask DisposeMetadata() => InvokeAsync("metadata");

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

    private static string[] ExpectedStages(bool ownsLogging = true)
        => ownsLogging ? ["host", "metadata", "logging"] : ["host", "metadata"];

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
