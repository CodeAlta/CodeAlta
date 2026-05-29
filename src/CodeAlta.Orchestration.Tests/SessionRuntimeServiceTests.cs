using System.Text.Json;

using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SessionRuntimeServiceTests
{
    [TestMethod]
    public async Task ListRecoverableSessionsAsync_IncludesAgentRuntimeSessionsForUnregisteredProviders()
    {
        using var temp = new TempDirectory();
        var registry = new ModelProviderRegistry();
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var store = new SessionViewCatalog(new CatalogOptions { GlobalRoot = temp.Path }).JournalStore.CreateSessionStore();
        var createdAt = DateTimeOffset.Parse("2026-05-16T12:00:00+00:00");
        await store.UpsertSessionAsync(
            new AgentSessionSummary
            {
                SessionId = "session-1",
                ProviderId = new ModelProviderId("old-provider"),
                ProtocolFamily = "openai-responses",
                ProviderKey = "old-provider",
                WorkingDirectory = temp.Path,
                Title = "Recovered old provider",
                CreatedAt = createdAt,
                UpdatedAt = createdAt.AddMinutes(1),
            }).ConfigureAwait(false);

        var sessions = await CollectAsync(runtime.ListRecoverableSessionsAsync()).ConfigureAwait(false);

        Assert.AreEqual(1, sessions.Count);
        Assert.AreEqual("session-1", sessions[0].SessionId);
        Assert.AreEqual("old-provider", sessions[0].ProviderId);
        Assert.AreEqual("old-provider", sessions[0].ProviderKey);
    }

    [TestMethod]
    public async Task ListRecoverableSessionsAsync_DoesNotInitializeProviders()
    {
        using var temp = new TempDirectory();
        var ProviderId = new ModelProviderId("registered-provider");
        var provider = new ThrowingProviderRuntime(ProviderId);
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(new ModelProviderDescriptor(new ModelProviderId(ProviderId.Value), "Throwing Provider"), () => provider);
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var store = new SessionViewCatalog(new CatalogOptions { GlobalRoot = temp.Path }).JournalStore.CreateSessionStore();
        var createdAt = DateTimeOffset.Parse("2026-05-16T12:00:00+00:00");
        await store.UpsertSessionAsync(
            new AgentSessionSummary
            {
                SessionId = "session-1",
                ProviderId = ProviderId,
                ProtocolFamily = "openai-responses",
                ProviderKey = ProviderId.Value,
                WorkingDirectory = temp.Path,
                Title = "Recovered provider",
                CreatedAt = createdAt,
                UpdatedAt = createdAt.AddMinutes(1),
            }).ConfigureAwait(false);

        var sessions = await CollectAsync(runtime.ListRecoverableSessionsAsync()).ConfigureAwait(false);

        Assert.AreEqual(1, sessions.Count);
        Assert.AreEqual(0, provider.StartAttempts);
    }

    [TestMethod]
    public async Task EnsureCoordinatorSessionAsync_RecreatesSessionWhenResumeTargetIsMissing()
    {
        using var temp = new TempDirectory();
        var ProviderId = new ModelProviderId("shared-missing");
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(
            new ModelProviderDescriptor(new ModelProviderId(ProviderId.Value), "Missing Resume") { DefaultModelId = "test-model" },
            () => new MinimalProviderRuntime(ProviderId));
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var session = CreateSession("session-1", ProviderId, temp.Path);

        var agentId = await runtime.EnsureCoordinatorSessionAsync(session, CreateOptions(ProviderId, temp.Path)).ConfigureAwait(false);
        var history = await runtime.GetHistoryAsync(session.SessionId).ConfigureAwait(false);

        Assert.AreNotEqual(Guid.Empty, agentId.Value);
        Assert.AreEqual("session-1", session.SessionId);
        Assert.AreEqual(0, history.Count);
    }

    [TestMethod]
    public async Task EnsureCoordinatorSessionAsync_RecreatesSessionWhenToolSchemaChanges()
    {
        using var temp = new TempDirectory();
        var providerId = new ModelProviderId("tool-refresh");
        var recorder = new ToolSchemaRecordingProviderRuntime.Recorder();
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(
            new ModelProviderDescriptor(new ModelProviderId(providerId.Value), "Tool Refresh") { DefaultModelId = "test-model" },
            () => new ToolSchemaRecordingProviderRuntime(providerId, recorder));
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var session = CreateSession("session-1", providerId, temp.Path);

        var firstHandle = await runtime.EnsureCoordinatorSessionAsync(session, CreateOptions(providerId, temp.Path)).ConfigureAwait(false);
        var secondHandle = await runtime.EnsureCoordinatorSessionAsync(
                session,
                CreateOptions(providerId, temp.Path, [CreateTool("mcp__github__issue_read")]))
            .ConfigureAwait(false);

        Assert.AreNotEqual(firstHandle, secondHandle);
        Assert.AreEqual(2, recorder.ResumeToolNames.Count);
        CollectionAssert.AreEqual(Array.Empty<string>(), recorder.ResumeToolNames[0].ToArray());
        CollectionAssert.AreEqual(new[] { "mcp__github__issue_read" }, recorder.ResumeToolNames[1].ToArray());
    }

    [TestMethod]
    public async Task CreateGlobalSessionAsync_PersistsParentSessionIdForRecoverableSessionListing()
    {
        using var temp = new TempDirectory();
        var providerId = new ModelProviderId("lineage-provider");
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(
            new ModelProviderDescriptor(new ModelProviderId(providerId.Value), "Lineage Provider") { DefaultModelId = "test-model" },
            () => new MinimalProviderRuntime(providerId));
        await using var hub = new AgentHub(registry, temp.Path);
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var sessionViewCatalog = new SessionViewCatalog(options);
        var agentSessionCatalog = new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore());
        await using var runtime = new SessionRuntimeService(
            hub,
            agentSessionCatalog,
            new ProjectCatalog(options),
            sessionViewCatalog,
            new AgentInstructionTemplateProvider(catalogOptions: options),
            options);

        var parent = await runtime.CreateGlobalSessionAsync(CreateOptions(providerId, temp.Path), "Parent", cancellationToken: default).ConfigureAwait(false);
        var child = await runtime.CreateGlobalSessionAsync(
                CreateOptions(providerId, temp.Path),
                "Child",
                parent.SessionId,
                createdBy: null,
                cancellationToken: default)
            .ConfigureAwait(false);
        await sessionViewCatalog.JournalStore.CreateSessionStore()
            .UpsertSessionAsync(
                new AgentSessionSummary
                {
                    SessionId = child.SessionId,
                    ProviderId = providerId,
                    ProtocolFamily = providerId.Value,
                    ProviderKey = providerId.Value,
                    WorkingDirectory = temp.Path,
                    Title = child.Title,
                    CreatedAt = child.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow,
                })
            .ConfigureAwait(false);

        var metadata = await CollectAgentMetadataAsync(agentSessionCatalog.ListSessionsAsync()).ConfigureAwait(false);
        var recoverable = await CollectAsync(runtime.ListRecoverableSessionsAsync()).ConfigureAwait(false);

        Assert.AreEqual(parent.SessionId, metadata.Single(session => session.SessionId == child.SessionId).ParentSessionId);
        Assert.AreEqual(parent.SessionId, recoverable.Single(session => session.SessionId == child.SessionId).ParentSessionId);
    }

    private static async Task<IReadOnlyList<SessionViewDescriptor>> CollectAsync(
        IAsyncEnumerable<SessionViewDescriptor> sessions)
    {
        var results = new List<SessionViewDescriptor>();
        await foreach (var session in sessions.ConfigureAwait(false))
        {
            results.Add(session);
        }

        return results;
    }

    private static async Task<IReadOnlyList<AgentSessionMetadata>> CollectAgentMetadataAsync(
        IAsyncEnumerable<AgentSessionMetadata> sessions)
    {
        var results = new List<AgentSessionMetadata>();
        await foreach (var session in sessions.ConfigureAwait(false))
        {
            results.Add(session);
        }

        return results;
    }

    private static SessionRuntimeService CreateRuntime(string root, AgentHub hub)
    {
        var options = new CatalogOptions { GlobalRoot = root };
        var sessionViewCatalog = new SessionViewCatalog(options);
        var agentSessionCatalog = new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore());
        return new SessionRuntimeService(
            hub,
            agentSessionCatalog,
            new ProjectCatalog(options),
            sessionViewCatalog,
            new AgentInstructionTemplateProvider(catalogOptions: options),
            options);
    }

    private static SessionViewDescriptor CreateSession(string sessionId, ModelProviderId ProviderId, string root)
        => new()
        {
            SessionId = sessionId,
            ProviderId = ProviderId.Value,
            ProviderKey = ProviderId.Value,
            Kind = SessionViewKind.GlobalSession,
            Status = SessionViewStatus.Active,
            Title = sessionId,
            WorkingDirectory = root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

    private static SessionExecutionOptions CreateOptions(
        ModelProviderId ProviderId,
        string root,
        IReadOnlyList<AgentToolDefinition>? tools = null)
        => new()
        {
            ProviderId = ProviderId,
            ProviderKey = ProviderId.Value,
            WorkingDirectory = root,
            ProjectRoots = [root],
            Tools = tools,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

    private static AgentToolDefinition CreateTool(string name)
        => new(
            new AgentToolSpec(name, "Test tool", JsonSerializer.SerializeToElement(new { type = "object" })),
            static (_, _) => Task.FromResult(new AgentToolResult(true, [])));

    private sealed class MinimalProviderRuntime(ModelProviderId providerId) : IAgentModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId(providerId.Value), "Missing Resume") { DefaultModelId = "test-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test",
            ProviderKey = providerId.Value,
            DisplayName = "Missing Resume",
            TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new()
        {
            Provider = RuntimeDescriptor,
            TurnExecutor = new NoOpTurnExecutor(),
        };

        public IModelProviderTurnExecutor CreateTurnExecutor() => new NoOpTurnExecutor();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingProviderRuntime(ModelProviderId providerId) : IAgentModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId(providerId.Value), "Throwing Provider");

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test",
            ProviderKey = providerId.Value,
            DisplayName = "Throwing Provider",
            TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public int StartAttempts { get; private set; }

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new()
        {
            Provider = RuntimeDescriptor,
            TurnExecutor = new NoOpTurnExecutor(),
        };

        public IModelProviderTurnExecutor CreateTurnExecutor() => new NoOpTurnExecutor();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartAttempts++;
            throw new InvalidOperationException("Provider should not be initialized while listing recoverable sessions.");
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Provider models should not be listed while listing recoverable sessions.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ToolSchemaRecordingProviderRuntime(ModelProviderId providerId, ToolSchemaRecordingProviderRuntime.Recorder recorder) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId(providerId.Value), "Tool Refresh") { DefaultModelId = "test-model" };

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
        {
            recorder.CreateToolNames.Add(options.Tools?.Select(static tool => tool.Spec.Name).ToArray() ?? []);
            return Task.FromResult<IAgentSession>(new EmptyAgentSession(providerId, options.SessionId ?? Guid.CreateVersion7().ToString()));
        }

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
        {
            recorder.ResumeToolNames.Add(options.Tools?.Select(static tool => tool.Spec.Name).ToArray() ?? []);
            return Task.FromResult<IAgentSession>(new EmptyAgentSession(providerId, sessionId));
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });

        public IModelProviderTurnExecutor CreateTurnExecutor() => new NoOpTurnExecutor();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public sealed class Recorder
        {
            public List<IReadOnlyList<string>> CreateToolNames { get; } = [];

            public List<IReadOnlyList<string>> ResumeToolNames { get; } = [];
        }
    }

    private sealed class NoOpTurnExecutor : IModelProviderTurnExecutor
    {
        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class EmptyAgentSession(ModelProviderId ProviderId, string sessionId) : IAgentSession
    {
        public ModelProviderId ProviderId { get; } = ProviderId;

        public string SessionId { get; } = sessionId;

        public string? WorkspacePath => null;

        public IAsyncEnumerable<AgentEvent> StreamEventsAsync(CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<AgentEvent>();

        public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();

        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentRunId($"run-{Guid.NewGuid():N}"));

        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CompactAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEvent>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Subscription : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
