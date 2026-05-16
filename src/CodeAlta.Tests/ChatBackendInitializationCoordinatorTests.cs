using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Events;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ChatBackendInitializationCoordinatorTests
{
    [TestMethod]
    public async Task InitializeAsync_SkipsLoadedCodexBackend()
    {
        using var temp = TempDirectory.Create();
        var backendFactory = new AgentBackendFactory();
        var backend = new CountingBackend(AgentBackendIds.Codex);
        var factoryCreateCount = 0;
        backendFactory.Register(
            AgentBackendIds.Codex,
            () =>
            {
                factoryCreateCount++;
                return backend;
            });

        await using var hub = new AgentHub(backendFactory);
        var state = new ChatBackendState(AgentBackendIds.Codex, "Codex")
        {
            Availability = ChatBackendAvailability.Ready,
            StatusMessage = "Ready",
        };
        state.Models.Add(new AgentModelInfo("gpt-5"));

        var coordinator = CreateCoordinator(
            hub,
            [new AgentBackendDescriptor(AgentBackendIds.Codex, "Codex")],
            new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
            {
                [AgentBackendIds.Codex.Value] = state,
            });

        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(0, factoryCreateCount);
        Assert.AreEqual(0, backend.StartCount);
        Assert.AreEqual(0, backend.ListModelsCount);
        Assert.AreEqual(ChatBackendAvailability.Ready, state.Availability);
        Assert.AreEqual(1, state.Models.Count);
    }

    [TestMethod]
    public async Task InitializeAsync_RefreshesLoadedNonProcessBackedBackend()
    {
        using var temp = TempDirectory.Create();
        var backendId = new AgentBackendId("openai");
        var backendFactory = new AgentBackendFactory();
        var backend = new CountingBackend(backendId);
        backendFactory.Register(backendId, () => backend);

        await using var hub = new AgentHub(backendFactory);
        var state = new ChatBackendState(backendId, "OpenAI")
        {
            Availability = ChatBackendAvailability.Ready,
            StatusMessage = "Ready",
        };
        state.Models.Add(new AgentModelInfo("old-model"));

        var coordinator = CreateCoordinator(
            hub,
            [new AgentBackendDescriptor(backendId, "OpenAI")],
            new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
            {
                [backendId.Value] = state,
            });

        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, backend.StartCount);
        Assert.AreEqual(1, backend.ListModelsCount);
        Assert.AreEqual(ChatBackendAvailability.Ready, state.Availability);
        CollectionAssert.AreEqual(new[] { "new-model" }, state.Models.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task RefreshBackendAsync_EnablesSessionLoadingBeforeUiStateIsApplied()
    {
        using var temp = TempDirectory.Create();
        var backendId = new AgentBackendId("codex");
        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(backendId, () => new CountingBackend(backendId));

        await using var hub = new AgentHub(backendFactory);
        var state = new ChatBackendState(backendId, "ChatGPT");
        var queuedUiActions = new Queue<Action>();
        var sessionLoadingUpdates = new List<(AgentBackendId BackendId, bool Enabled)>();
        var publishedEvents = new List<ShellFrontendEvent>();
        var coordinator = new ChatBackendInitializationCoordinator(
            hub,
            [new AgentBackendDescriptor(backendId, "ChatGPT")],
            new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
            {
                [backendId.Value] = state,
            },
            action => queuedUiActions.Enqueue(action),
            CreatePublisher(publishedEvents),
            setBackendSessionLoadingEnabled: (id, enabled) => sessionLoadingUpdates.Add((id, enabled)));

        await coordinator.RefreshBackendAsync(backendId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ChatBackendAvailability.Unknown, state.Availability);
        Assert.IsTrue(
            sessionLoadingUpdates.Any(update => update.BackendId == backendId && update.Enabled),
            "Session loading should be enabled as soon as backend discovery succeeds, before queued UI state updates run.");

        while (queuedUiActions.TryDequeue(out var action))
        {
            action();
        }

        Assert.AreEqual(ChatBackendAvailability.Ready, state.Availability);
        Assert.IsTrue(publishedEvents.OfType<ModelProviderStateChangedEvent>().Any(evt => evt.ModelProviderId == backendId.Value));
        Assert.IsTrue(publishedEvents.OfType<HeaderChangedEvent>().Any());
    }

    [TestMethod]
    public async Task InitializeAsync_CreatesMissingBackendStateBeforeRefreshingModels()
    {
        var backendId = new AgentBackendId("gemini");
        var backendFactory = new AgentBackendFactory();
        var backend = new CountingBackend(backendId);
        backendFactory.Register(backendId, () => backend);
        await using var hub = new AgentHub(backendFactory);
        var states = new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase);
        var coordinator = CreateCoordinator(
            hub,
            [new AgentBackendDescriptor(backendId, "Gemini")],
            states);

        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(states.TryGetValue(backendId.Value, out var state));
        Assert.AreEqual("Gemini", state.DisplayName);
        Assert.AreEqual(1, backend.ListModelsCount);
        Assert.AreEqual(ChatBackendAvailability.Ready, state.Availability);
        CollectionAssert.AreEqual(new[] { "new-model" }, state.Models.Select(static model => model.Id).ToArray());
    }

    [TestMethod]
    public async Task InitializeAsync_DropsStaleQueuedProviderInitializationStatus()
    {
        var backendId = new AgentBackendId("openai");
        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(backendId, () => new CountingBackend(backendId));

        await using var hub = new AgentHub(backendFactory);
        var state = new ChatBackendState(backendId, "OpenAI");
        var queuedUiActions = new List<Action>();
        var providerStatuses = new List<string?>();
        var coordinator = new ChatBackendInitializationCoordinator(
            hub,
            [new AgentBackendDescriptor(backendId, "OpenAI")],
            new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
            {
                [backendId.Value] = state,
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
        var status = ChatBackendInitializationCoordinator.FormatProviderInitializationStatus(
            1,
            3,
            ["OpenAI", "Gemma", "Anthropic"]);

        Assert.AreEqual("Initializing OpenAI, Gemma, … [■■■□□□□□] 1/3", status);
    }

    [TestMethod]
    public void FormatProviderInitializationStatus_HidesWhenComplete()
    {
        var status = ChatBackendInitializationCoordinator.FormatProviderInitializationStatus(
            3,
            3,
            []);

        Assert.IsNull(status);
    }

    private static ChatBackendInitializationCoordinator CreateCoordinator(
        AgentHub hub,
        IReadOnlyList<AgentBackendDescriptor> descriptors,
        Dictionary<string, ChatBackendState> states)
        => new(
            hub,
            descriptors,
            states,
            static action => action(),
            CreatePublisher());

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

    private sealed class CountingBackend(AgentBackendId backendId) : IAgentBackend
    {
        public AgentBackendId BackendId { get; } = backendId;

        public string DisplayName => BackendId.Value;

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
            return Task.FromResult<IReadOnlyList<AgentModelInfo>>([new AgentModelInfo("new-model")]);
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

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codealta-chatbackend-tests-{Guid.NewGuid():N}");
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
