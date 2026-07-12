using CodeAlta.Agent;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelProviderInitializationServiceTests
{
    [TestMethod]
    public async Task InitializeAllAsync_IsolatesHangingProviderFromReadyProviderAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var readyDescriptor = new ModelProviderDescriptor(new ModelProviderId("ready"), "Ready", "test");
        var hangingDescriptor = new ModelProviderDescriptor(new ModelProviderId("hanging"), "Hanging", "test");
        var readyRuntime = new TestRuntime(readyDescriptor, [new AgentModelInfo("ready-model")]);
        var hangingRuntime = new TestRuntime(hangingDescriptor, hangDuringProbe: true);
        registry.RegisterOrReplace(readyDescriptor, () => readyRuntime);
        registry.RegisterOrReplace(hangingDescriptor, () => hangingRuntime);
        var service = new ModelProviderInitializationService(
            registry,
            new ModelProviderInitializationOptions { DefaultProbeTimeout = TimeSpan.FromMilliseconds(50) });

        var initializeTask = service.InitializeAllAsync(CancellationToken.None);
        await readyRuntime.ProbeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var readyState = service.CurrentStates.Single(state => state.ProviderId == readyDescriptor.ProviderId);
        Assert.AreEqual(ModelProviderAvailability.Ready, readyState.Availability);
        CollectionAssert.AreEqual(new[] { "ready-model" }, readyState.Models.Select(static model => model.Id).ToArray());

        await initializeTask.WaitAsync(TimeSpan.FromSeconds(5));
        var hangingState = service.CurrentStates.Single(state => state.ProviderId == hangingDescriptor.ProviderId);
        Assert.AreEqual(ModelProviderAvailability.Failed, hangingState.Availability);
        Assert.AreEqual("Timeout", hangingState.ErrorCategory);
    }

    [TestMethod]
    public async Task GetModelsAsync_ReusesModelCatalogLoadedDuringInitializationAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("cached"), "Cached", "test");
        var runtime = new TestRuntime(descriptor, [new AgentModelInfo("cached-model")]);
        registry.RegisterOrReplace(descriptor, () => runtime);
        var service = new ModelProviderInitializationService(registry);

        await service.InitializeAllAsync(CancellationToken.None);
        var first = await service.GetModelsAsync(descriptor.ProviderId, CancellationToken.None);
        var second = await service.GetModelsAsync(new ModelProviderId("CACHED"), CancellationToken.None);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, runtime.ProbeCount);
        CollectionAssert.AreEqual(new[] { "cached-model" }, second.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task GetModelsAsync_PreservesProviderOrderByDefaultAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("ordered"), "Ordered", "test");
        var runtime = new TestRuntime(descriptor, CreateUnsortedModels());
        registry.RegisterOrReplace(descriptor, () => runtime);
        var service = new ModelProviderInitializationService(registry);

        var models = await service.GetModelsAsync(descriptor.ProviderId, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "z-model", "a-model", "b-model" }, models.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task GetModelsAsync_SortsModelsByDisplayNameWhenConfiguredAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("sorted"), "Sorted", "test") { SortModels = true };
        var runtime = new TestRuntime(descriptor, CreateUnsortedModels());
        registry.RegisterOrReplace(descriptor, () => runtime);
        var service = new ModelProviderInitializationService(registry);

        var models = await service.GetModelsAsync(descriptor.ProviderId, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "a-model", "b-model", "z-model" }, models.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task GetModelsAsync_DoesNotReturnCatalogForUnregisteredProviderAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var descriptor = new ModelProviderDescriptor(new ModelProviderId("removed"), "Removed", "test");
        registry.RegisterOrReplace(descriptor, () => new TestRuntime(descriptor, [new AgentModelInfo("removed-model")]));
        var service = new ModelProviderInitializationService(registry);
        await service.InitializeAllAsync(CancellationToken.None);

        Assert.IsTrue(registry.Unregister(descriptor.ProviderId));

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            () => service.GetModelsAsync(descriptor.ProviderId, CancellationToken.None));
        Assert.AreEqual(0, service.CurrentStates.Count);
    }

    private static AgentModelInfo[] CreateUnsortedModels()
        =>
        [
            new AgentModelInfo("z-model", DisplayName: "Zulu"),
            new AgentModelInfo("a-model", DisplayName: "alpha"),
            new AgentModelInfo("b-model", DisplayName: "Bravo"),
        ];

    private sealed class TestRuntime : IModelProviderRuntime
    {
        private readonly IReadOnlyList<AgentModelInfo> _models;
        private readonly bool _hangDuringProbe;

        public TestRuntime(
            ModelProviderDescriptor descriptor,
            IReadOnlyList<AgentModelInfo>? models = null,
            bool hangDuringProbe = false)
        {
            Descriptor = descriptor;
            _models = models ?? [];
            _hangDuringProbe = hangDuringProbe;
        }

        public ModelProviderDescriptor Descriptor { get; }

        public int ProbeCount { get; private set; }

        public TaskCompletionSource ProbeCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            if (_hangDuringProbe)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var result = new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = _models,
            };
            ProbeCompleted.TrySetResult();
            return result;
        }

        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
