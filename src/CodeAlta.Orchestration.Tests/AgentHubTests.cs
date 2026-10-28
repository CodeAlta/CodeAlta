using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class AgentHubTests
{
    [TestMethod]
    public async Task StartSessionAsync_DoesNotProbeModelsOrListSessions()
    {
        var runtime = new CountingProviderRuntime("provider");
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(runtime.Descriptor, () => runtime);
        await using var hub = new AgentHub(registry, Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

        var handle = await hub.StartSessionAsync(
                new AgentSessionCreateOptions
                {
                    ProviderKey = "provider",
                    WorkingDirectory = Environment.CurrentDirectory,
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                })
            .ConfigureAwait(false);

        Assert.IsFalse(string.IsNullOrWhiteSpace(handle.SessionId));
        Assert.AreEqual(1, runtime.StartCallCount);
        Assert.AreEqual(0, runtime.ProbeCallCount);
    }

    [TestMethod]
    public async Task SessionJournalWrites_WaitForTheJournalLockOfTheHost()
    {
        // The list of sessions appends state records to the journal a session writes its events to. With locks of their own,
        // two writers append at the same offset on Linux and macOS, and the second record overwrites the first, leaving a
        // broken line in the middle of the journal (#253).
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var runtime = new CountingProviderRuntime("provider");
            var registry = new ModelProviderRegistry();
            registry.RegisterOrReplace(runtime.Descriptor, () => runtime);
            var journals = new AgentSessionJournalFile();
            await using var hub = new AgentHub(registry, root) { JournalFile = journals };
            var handle = await hub.StartSessionAsync(new AgentSessionCreateOptions
            {
                ProviderKey = "provider",
                WorkingDirectory = Environment.CurrentDirectory,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }).ConfigureAwait(false);
            var journal = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).Single();

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holding = journals.WithPathLockAsync(journal, () => release.Task, CancellationToken.None);
            var rename = hub.RenameSessionAsync(handle.HandleId, "Renamed");
            await Task.WhenAny(rename, Task.Delay(TimeSpan.FromMilliseconds(300))).ConfigureAwait(false);
            Assert.IsFalse(rename.IsCompleted, "The session wrote its journal while another writer of the process held it.");

            release.SetResult();
            await holding.ConfigureAwait(false);
            Assert.IsTrue(await rename.ConfigureAwait(false));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StartSessionAsync_RejectsRuntimeSessionIdMismatch()
    {
        var runtime = new RewritingSessionRuntime("provider");
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(runtime.Descriptor, () => runtime);
        await using var hub = new AgentHub(registry, Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => hub.StartSessionAsync(
                    new AgentSessionCreateOptions
                    {
                        SessionId = "codealta-session",
                        ProviderKey = "provider",
                        WorkingDirectory = Environment.CurrentDirectory,
                        OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                    }))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Message, "Providers must preserve CodeAlta-owned session identifiers.");
    }

    private sealed class CountingProviderRuntime(string providerId) : IAgentModelProviderRuntime
    {
        public int StartCallCount { get; private set; }

        public int ProbeCallCount { get; private set; }

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId(providerId), "Counting") { DefaultModelId = "test-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test",
            ProviderKey = providerId,
            DisplayName = "Counting",
            TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new()
        {
            Provider = RuntimeDescriptor,
            TurnExecutor = new NoOpTurnExecutor(),
        };

        public IModelProviderTurnExecutor CreateTurnExecutor() => new NoOpTurnExecutor();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            ProbeCallCount++;
            return Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RewritingSessionRuntime(string providerId) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId(providerId), "Rewriting") { DefaultModelId = "test-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test",
            ProviderKey = providerId,
            DisplayName = "Rewriting",
            TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });

        public IModelProviderTurnExecutor CreateTurnExecutor() => new NoOpTurnExecutor();

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]);

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new CountingSession(Descriptor.ProviderId, "provider-session"));

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new CountingSession(Descriptor.ProviderId, "provider-session"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpTurnExecutor : IModelProviderTurnExecutor
    {
        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CountingSession(ModelProviderId ProviderId, string sessionId = "provider-session") : IAgentSession
    {
        public ModelProviderId ProviderId { get; } = ProviderId;

        public string SessionId { get; } = sessionId;

        public string? WorkspacePath => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();

        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentRunId("run"));

        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentRunId("run"));

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CompactAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
    }

    private sealed class Subscription : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
