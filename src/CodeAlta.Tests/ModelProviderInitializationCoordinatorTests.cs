using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.Events;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelProviderInitializationCoordinatorTests
{
    [TestMethod]
    public async Task InitializeAsync_RefreshesLoadedNonProcessBackedProviderRuntime()
    {
        var providerId = new ModelProviderId("openai");
        var runtime = new CountingProviderRuntime(providerId);
        var descriptor = new ModelProviderDescriptor(providerId, "OpenAI");
        var initializationService = CreateInitializationService(descriptor, runtime);
        var state = new ModelProviderState(new ModelProviderId(providerId.Value), "OpenAI")
        {
            Availability = ModelProviderAvailability.Ready,
            StatusMessage = "Ready",
        };
        state.Models.Add(new AgentModelInfo("old-model"));

        var coordinator = CreateCoordinator(
            initializationService,
            [descriptor],
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
            {
                [providerId.Value] = state,
            });

        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, runtime.StartCount);
        Assert.AreEqual(1, runtime.ListModelsCount);
        Assert.AreEqual(ModelProviderAvailability.Ready, state.Availability);
        CollectionAssert.AreEqual(new[] { "new-model" }, state.Models.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task RefreshProviderAsync_PreservesSelectedModelWhenMissingFromDiscoveredModels()
    {
        var providerId = new ModelProviderId("codex");
        var runtime = new CountingProviderRuntime(
            providerId,
            [
                new AgentModelInfo(
                    "gpt-5.2",
                    SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.Medium]),
            ]);
        var descriptor = new ModelProviderDescriptor(providerId, "Codex");
        var initializationService = CreateInitializationService(descriptor, runtime);
        var state = new ModelProviderState(new ModelProviderId(providerId.Value), "Codex")
        {
            SelectedModelId = "gpt-5.5",
            SelectedReasoningEffort = AgentReasoningEffort.High,
        };
        var coordinator = CreateCoordinator(
            initializationService,
            [descriptor],
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
            {
                [providerId.Value] = state,
            });

        await coordinator.RefreshProviderAsync(new ModelProviderId(providerId.Value), CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { "gpt-5.2" }, state.Models.Select(static model => model.Id).ToArray());
        Assert.AreEqual("gpt-5.5", state.SelectedModelId);
        Assert.AreEqual(AgentReasoningEffort.High, state.SelectedReasoningEffort);
    }

    [TestMethod]
    public async Task RefreshProviderAsync_UpdatesUiStateAfterProviderRefresh()
    {
        var providerId = new ModelProviderId("codex");
        var runtime = new CountingProviderRuntime(providerId);
        var descriptor = new ModelProviderDescriptor(providerId, "ChatGPT");
        var initializationService = CreateInitializationService(descriptor, runtime);
        var state = new ModelProviderState(new ModelProviderId(providerId.Value), "ChatGPT");
        var queuedUiActions = new Queue<Action>();
        var publishedEvents = new List<ShellFrontendEvent>();
        var coordinator = new ModelProviderInitializationCoordinator(
            initializationService,
            [descriptor],
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
            {
                [providerId.Value] = state,
            },
            action => queuedUiActions.Enqueue(action),
            CreatePublisher(publishedEvents));

        await coordinator.RefreshProviderAsync(new ModelProviderId(providerId.Value), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ModelProviderAvailability.Unknown, state.Availability);

        while (queuedUiActions.TryDequeue(out var action))
        {
            action();
        }

        Assert.AreEqual(ModelProviderAvailability.Ready, state.Availability);
        Assert.IsTrue(publishedEvents.OfType<ModelProviderStateChangedEvent>().Any(evt => evt.ModelProviderId == providerId.Value));
        Assert.IsTrue(publishedEvents.OfType<HeaderChangedEvent>().Any());
    }

    [TestMethod]
    public async Task InitializeAsync_CreatesMissingProviderStateBeforeRefreshingModels()
    {
        var providerId = new ModelProviderId("gemini");
        var runtime = new CountingProviderRuntime(providerId);
        var descriptor = new ModelProviderDescriptor(providerId, "Gemini");
        var initializationService = CreateInitializationService(descriptor, runtime);
        var states = new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase);
        var coordinator = CreateCoordinator(
            initializationService,
            [descriptor],
            states);

        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(states.TryGetValue(providerId.Value, out var state));
        Assert.AreEqual("Gemini", state.DisplayName);
        Assert.AreEqual(1, runtime.ListModelsCount);
        Assert.AreEqual(ModelProviderAvailability.Ready, state.Availability);
        CollectionAssert.AreEqual(new[] { "new-model" }, state.Models.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task InitializeAsync_ReplaysCurrentStatesAfterStoppingStateReader()
    {
        var providerId = new ModelProviderId("gemini");
        var descriptor = new ModelProviderDescriptor(providerId, "Gemini");
        var initializationService = new BlockingStateReaderInitializationService(descriptor);
        var state = new ModelProviderState(new ModelProviderId(providerId.Value), "Gemini");
        var coordinator = CreateCoordinator(
            initializationService,
            [descriptor],
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
            {
                [providerId.Value] = state,
            });

        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(initializationService.CurrentStatesAccessedAfterStreamEnded);
        Assert.AreEqual(ModelProviderAvailability.Ready, state.Availability);
        CollectionAssert.AreEqual(new[] { "ready-model" }, state.Models.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task InitializeAsync_DropsStaleQueuedProviderInitializationStatus()
    {
        var providerId = new ModelProviderId("openai");
        var descriptor = new ModelProviderDescriptor(providerId, "OpenAI");
        var initializationService = CreateInitializationService(descriptor, new CountingProviderRuntime(providerId));
        var state = new ModelProviderState(new ModelProviderId(providerId.Value), "OpenAI");
        var queuedUiActions = new List<Action>();
        var providerStatuses = new List<string?>();
        var coordinator = new ModelProviderInitializationCoordinator(
            initializationService,
            [descriptor],
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
            {
                [providerId.Value] = state,
            },
            action => queuedUiActions.Add(action),
            CreatePublisher(),
            setProviderInitializationStatus: providerStatuses.Add);

        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (var action in queuedUiActions.AsEnumerable().Reverse())
        {
            action();
        }

        Assert.IsTrue(providerStatuses.Count > 0);
        Assert.IsNull(providerStatuses.Last());
        Assert.IsFalse(
            providerStatuses.SkipWhile(static status => status is not null).Skip(1).Any(static status => status is not null),
            "Older queued provider-loading statuses must not overwrite the final cleared status.");
    }

    [TestMethod]
    public void FormatProviderInitializationStatus_ShowsProgressAndProviderNames()
    {
        var status = ModelProviderInitializationCoordinator.FormatProviderInitializationStatus(
            1,
            3,
            ["OpenAI", "Gemma", "Anthropic"]);

        Assert.AreEqual("Initializing OpenAI, Gemma, … [■■■□□□□□] 1/3", status);
    }

    [TestMethod]
    public void FormatProviderInitializationStatus_HidesWhenComplete()
    {
        var status = ModelProviderInitializationCoordinator.FormatProviderInitializationStatus(
            3,
            3,
            []);

        Assert.IsNull(status);
    }

    private static ModelProviderInitializationCoordinator CreateCoordinator(
        IModelProviderInitializationService initializationService,
        IReadOnlyList<ModelProviderDescriptor> descriptors,
        Dictionary<string, ModelProviderState> states)
        => new(
            initializationService,
            descriptors,
            states,
            static action => action(),
            CreatePublisher());

    private static ModelProviderInitializationService CreateInitializationService(
        ModelProviderDescriptor descriptor,
        CountingProviderRuntime runtime)
    {
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplaceSessionRuntime(descriptor, () => runtime);
        return new ModelProviderInitializationService(registry);
    }

    private static FrontendEventPublisher CreatePublisher(List<ShellFrontendEvent>? events = null)
    {
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        if (events is not null)
        {
            publisher.Subscribe(events.Add);
        }

        return publisher;
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
            => Task.FromResult(action());
    }

    private sealed class CountingProviderRuntime : ITestModelProviderSessionRuntime
    {
        private readonly IReadOnlyList<AgentModelInfo> _models;

        public CountingProviderRuntime(ModelProviderId providerId, IReadOnlyList<AgentModelInfo>? models = null)
        {
            ProviderId = providerId;
            _models = models ?? [new AgentModelInfo("new-model")];
        }

        public ModelProviderId ProviderId { get; }

        public string DisplayName => ProviderId.Value;

        public int StartCount { get; private set; }

        public int ListModelsCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            ListModelsCount++;
            return Task.FromResult(_models);
        }

        public async IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public Task<IAgentSession> CreateSessionAsync(
            AgentSessionCreateOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IAgentSession> ResumeSessionAsync(
            string sessionId,
            AgentSessionResumeOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingStateReaderInitializationService : IModelProviderInitializationService
    {
        private readonly ModelProviderDescriptor _descriptor;
        private readonly TaskCompletionSource _streamStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _streamEnded;

        public BlockingStateReaderInitializationService(ModelProviderDescriptor descriptor)
        {
            _descriptor = descriptor;
        }

        public bool CurrentStatesAccessedAfterStreamEnded { get; private set; }

        public IReadOnlyList<ModelProviderStateSnapshot> CurrentStates
        {
            get
            {
                CurrentStatesAccessedAfterStreamEnded = Volatile.Read(ref _streamEnded);
                return
                [
                    new ModelProviderStateSnapshot
                    {
                        Descriptor = _descriptor,
                        Availability = ModelProviderAvailability.Ready,
                        Models = [new AgentModelInfo("ready-model")],
                    },
                ];
            }
        }

        public async IAsyncEnumerable<ModelProviderStateChanged> StreamStateChangesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _streamStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _streamEnded, true);
            }

            yield break;
        }

        public async Task InitializeAllAsync(CancellationToken cancellationToken = default)
        {
            await _streamStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task RefreshProviderAsync(ModelProviderId providerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(ModelProviderId providerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codealta-provider-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
