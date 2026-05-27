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
