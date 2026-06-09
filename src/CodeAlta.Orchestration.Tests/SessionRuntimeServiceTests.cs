using System.Runtime.CompilerServices;
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
    public void FormatAgentPromptSourcePathForTimeline_UsesProjectRelativePathWhenAvailable()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "CodeAltaProject");
        var sourcePath = Path.Combine(projectRoot, ".alta", "prompts", "agents", "plan.prompt.md");

        var formatted = SessionRuntimeService.FormatAgentPromptSourcePathForTimeline(sourcePath, projectRoot);

        Assert.AreEqual(Path.Combine(".alta", "prompts", "agents", "plan.prompt.md"), formatted);
    }

    [TestMethod]
    public void FormatAgentPromptSourcePathForTimeline_KeepsExternalSourcePathAbsolute()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "CodeAltaProject");
        var sourcePath = Path.Combine(Path.GetTempPath(), "CodeAltaBuiltIn", "prompts", "agents", "default.prompt.md");

        var formatted = SessionRuntimeService.FormatAgentPromptSourcePathForTimeline(sourcePath, projectRoot);

        Assert.AreEqual(Path.GetFullPath(sourcePath), formatted);
    }

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
    public async Task CompactAsync_PublishesStartButDoesNotMirrorAgentCompletionOutcome()
    {
        using var temp = new TempDirectory();
        var providerId = new ModelProviderId("compaction-provider");
        const string completionMessage = "Manual local compaction summarized 131 messages.";
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(
            new ModelProviderDescriptor(new ModelProviderId(providerId.Value), "Compaction Provider") { DefaultModelId = "test-model" },
            () => new CompactionProviderRuntime(providerId, completionMessage));
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var session = CreateSession("session-1", providerId, temp.Path);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var eventsTask = CollectUntilAgentCompactionCompletedAsync(runtime, session.SessionId, cts.Token);

        await runtime.CompactAsync(session, CreateOptions(providerId, temp.Path), cts.Token).ConfigureAwait(false);
        var events = await eventsTask.ConfigureAwait(false);

        var hostCompactionStarted = events.OfType<SessionHostEvent>()
            .Where(static evt => evt.Kind == AgentSessionUpdateKind.CompactionStarted)
            .ToArray();
        var hostCompactionCompleted = events.OfType<SessionHostEvent>()
            .Where(static evt => evt.Kind == AgentSessionUpdateKind.CompactionCompleted)
            .ToArray();
        var agentCompactionCompleted = events.OfType<SessionAgentEvent>()
            .Where(static evt => evt.Event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.CompactionCompleted })
            .ToArray();

        Assert.AreEqual(1, hostCompactionStarted.Length);
        Assert.AreEqual(0, hostCompactionCompleted.Length);
        Assert.AreEqual(1, agentCompactionCompleted.Length);
    }

    [TestMethod]
    public async Task CompactAsync_ProjectsAgentStartBeforeCompactionCompletes()
    {
        using var temp = new TempDirectory();
        var providerId = new ModelProviderId("compaction-blocking-provider");
        const string completionMessage = "Manual local compaction summarized 131 messages.";
        var releaseCompaction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(
            new ModelProviderDescriptor(new ModelProviderId(providerId.Value), "Blocking Compaction Provider") { DefaultModelId = "test-model" },
            () => new CompactionProviderRuntime(
                providerId,
                completionMessage,
                emitAgentCompletion: true,
                outcome: null,
                emitAgentStart: true,
                releaseCompaction: releaseCompaction.Task));
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var session = CreateSession("session-1", providerId, temp.Path);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var eventsTask = CollectUntilAgentCompactionStartedAsync(runtime, session.SessionId, cts.Token);

        var compactTask = runtime.CompactAsync(session, CreateOptions(providerId, temp.Path), cts.Token);
        try
        {
            var events = await eventsTask.ConfigureAwait(false);
            Assert.IsTrue(events.Any(static runtimeEvent => runtimeEvent is SessionAgentEvent { Event: AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.CompactionStarted } }));
            Assert.IsFalse(compactTask.IsCompleted, "The agent compaction-started event should be projected while compaction is still running.");
        }
        finally
        {
            releaseCompaction.TrySetResult();
        }

        await compactTask.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CompactAsync_PublishesHostCompletionForNoOpOutcome()
    {
        using var temp = new TempDirectory();
        var providerId = new ModelProviderId("compaction-noop-provider");
        var outcome = new AgentCompactionOutcome(true, "Nothing to compact.");
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(
            new ModelProviderDescriptor(new ModelProviderId(providerId.Value), "Compaction NoOp Provider") { DefaultModelId = "test-model" },
            () => new CompactionProviderRuntime(providerId, outcome.Message!, emitAgentCompletion: false, outcome));
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var session = CreateSession("session-1", providerId, temp.Path);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var eventsTask = CollectUntilHostCompactionCompletedAsync(runtime, session.SessionId, cts.Token);

        await runtime.CompactAsync(session, CreateOptions(providerId, temp.Path), cts.Token).ConfigureAwait(false);
        var events = await eventsTask.ConfigureAwait(false);

        var hostCompactionCompleted = events.OfType<SessionHostEvent>()
            .Where(static evt => evt.Kind == AgentSessionUpdateKind.CompactionCompleted)
            .ToArray();
        var agentCompactionCompleted = events.OfType<SessionAgentEvent>()
            .Where(static evt => evt.Event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.CompactionCompleted })
            .ToArray();

        Assert.AreEqual(1, hostCompactionCompleted.Length);
        Assert.AreEqual("Nothing to compact.", hostCompactionCompleted[0].Message);
        Assert.AreEqual(0, agentCompactionCompleted.Length);
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
    public async Task GetOrResumeHistoryAsync_ReusesActiveSessionWhenOptionsDiffer()
    {
        using var temp = new TempDirectory();
        var providerId = new ModelProviderId("history-active-session");
        var recorder = new BlockingProviderRuntime.Recorder();
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(
            new ModelProviderDescriptor(new ModelProviderId(providerId.Value), "History Active Session") { DefaultModelId = "model-a" },
            () => new BlockingProviderRuntime(providerId, recorder));
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var session = CreateSession("session-1", providerId, temp.Path);
        session.ModelId = "model-a";

        await runtime.EnsureCoordinatorSessionAsync(session, CreateOptions(providerId, temp.Path, model: "model-a")).ConfigureAwait(false);
        var sendTask = runtime.SendAsync(
            session,
            CreateOptions(providerId, temp.Path, model: "model-a"),
            new AgentSendOptions { Input = new AgentInput([new AgentInputItem.Text("hello")]) });
        try
        {
            await recorder.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var history = await runtime.GetOrResumeHistoryAsync(
                    session,
                    CreateOptions(providerId, temp.Path, [CreateTool("mcp__changed__tool")], model: "model-b"))
                .ConfigureAwait(false);

            Assert.AreEqual(0, history.Count);
            Assert.AreEqual(1, recorder.ResumeCount, "History loading must not recreate an already-active coordinator session.");
            Assert.AreEqual(1, recorder.HistoryReadCount);
            Assert.AreEqual(0, recorder.AbortCount, "History loading must not abort an in-flight run just because reconstructed options differ.");
            Assert.IsFalse(sendTask.IsCompleted, "The in-flight send should still be running after history is loaded.");
        }
        finally
        {
            recorder.ReleaseSend.TrySetResult();
        }

        await sendTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task EnsureCoordinatorSessionAsync_UsesPendingAgentPromptFromLiveToolForStaleSessionState()
    {
        using var temp = new TempDirectory();
        var providerId = new ModelProviderId("prompt-refresh");
        var recorder = new ToolSchemaRecordingProviderRuntime.Recorder();
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(
            new ModelProviderDescriptor(new ModelProviderId(providerId.Value), "Prompt Refresh") { DefaultModelId = "test-model" },
            () => new ToolSchemaRecordingProviderRuntime(providerId, recorder));
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var session = CreateSession("session-1", providerId, temp.Path);

        var firstHandle = await runtime.EnsureCoordinatorSessionAsync(session, CreateOptions(providerId, temp.Path)).ConfigureAwait(false);
        session.AgentPromptId = "default";
        await runtime.SetActiveSessionAgentPromptIdAsync(session.SessionId, "plan").ConfigureAwait(false);
        var secondHandle = await runtime.EnsureCoordinatorSessionAsync(session, CreateOptions(providerId, temp.Path, agentPromptId: "default")).ConfigureAwait(false);

        Assert.AreNotEqual(firstHandle, secondHandle);
        Assert.AreEqual("plan", session.AgentPromptId);
        Assert.AreEqual(2, recorder.ResumeAgentPromptIds.Count);
        Assert.AreEqual("default", recorder.ResumeAgentPromptIds[0]);
        Assert.AreEqual("plan", recorder.ResumeAgentPromptIds[1]);
    }

    [TestMethod]
    public async Task TryGetActiveSessionDescriptorAsync_PreservesPendingAgentPromptOverPersistedLocalState()
    {
        using var temp = new TempDirectory();
        var providerId = new ModelProviderId("prompt-descriptor");
        var recorder = new ToolSchemaRecordingProviderRuntime.Recorder();
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(
            new ModelProviderDescriptor(new ModelProviderId(providerId.Value), "Prompt Descriptor") { DefaultModelId = "test-model" },
            () => new ToolSchemaRecordingProviderRuntime(providerId, recorder));
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var session = CreateSession("session-1", providerId, temp.Path);

        _ = await runtime.EnsureCoordinatorSessionAsync(session, CreateOptions(providerId, temp.Path, agentPromptId: "default")).ConfigureAwait(false);
        await runtime.SetActiveSessionAgentPromptIdAsync(session.SessionId, "plan").ConfigureAwait(false);

        var descriptor = await runtime.TryGetActiveSessionDescriptorAsync(session.SessionId).ConfigureAwait(false);

        Assert.IsNotNull(descriptor);
        Assert.AreEqual("plan", descriptor.AgentPromptId);
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

    private static Task<IReadOnlyList<SessionRuntimeEvent>> CollectUntilAgentCompactionCompletedAsync(
        SessionRuntimeService runtime,
        string sessionId,
        CancellationToken cancellationToken)
        => CollectUntilRuntimeEventAsync(
            runtime,
            sessionId,
            static runtimeEvent => runtimeEvent is SessionAgentEvent { Event: AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.CompactionCompleted } },
            cancellationToken);

    private static Task<IReadOnlyList<SessionRuntimeEvent>> CollectUntilAgentCompactionStartedAsync(
        SessionRuntimeService runtime,
        string sessionId,
        CancellationToken cancellationToken)
        => CollectUntilRuntimeEventAsync(
            runtime,
            sessionId,
            static runtimeEvent => runtimeEvent is SessionAgentEvent { Event: AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.CompactionStarted } },
            cancellationToken);

    private static Task<IReadOnlyList<SessionRuntimeEvent>> CollectUntilHostCompactionCompletedAsync(
        SessionRuntimeService runtime,
        string sessionId,
        CancellationToken cancellationToken)
        => CollectUntilRuntimeEventAsync(
            runtime,
            sessionId,
            static runtimeEvent => runtimeEvent is SessionHostEvent { Kind: AgentSessionUpdateKind.CompactionCompleted },
            cancellationToken);

    private static async Task<IReadOnlyList<SessionRuntimeEvent>> CollectUntilRuntimeEventAsync(
        SessionRuntimeService runtime,
        string sessionId,
        Func<SessionRuntimeEvent, bool> stopWhen,
        CancellationToken cancellationToken)
    {
        var results = new List<SessionRuntimeEvent>();
        await foreach (var runtimeEvent in runtime.StreamEventsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(runtimeEvent.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(runtimeEvent);
            if (stopWhen(runtimeEvent))
            {
                return results;
            }
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
        IReadOnlyList<AgentToolDefinition>? tools = null,
        string? agentPromptId = null,
        string? model = null)
        => new()
        {
            ProviderId = ProviderId,
            ProviderKey = ProviderId.Value,
            WorkingDirectory = root,
            ProjectRoots = [root],
            Model = model,
            AgentPromptId = agentPromptId,
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

    private sealed class BlockingProviderRuntime(ModelProviderId providerId, BlockingProviderRuntime.Recorder recorder) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId(providerId.Value), "History Active Session") { DefaultModelId = "model-a" };

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
        {
            recorder.RecordCreate();
            return Task.FromResult<IAgentSession>(new BlockingAgentSession(providerId, options.SessionId ?? Guid.CreateVersion7().ToString(), recorder));
        }

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
        {
            recorder.RecordResume();
            return Task.FromResult<IAgentSession>(new BlockingAgentSession(providerId, sessionId, recorder));
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });

        public IModelProviderTurnExecutor CreateTurnExecutor() => new NoOpTurnExecutor();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public sealed class Recorder
        {
            private int _abortCount;
            private int _createCount;
            private int _historyReadCount;
            private int _resumeCount;

            public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int AbortCount => Volatile.Read(ref _abortCount);

            public int CreateCount => Volatile.Read(ref _createCount);

            public int HistoryReadCount => Volatile.Read(ref _historyReadCount);

            public int ResumeCount => Volatile.Read(ref _resumeCount);

            public void RecordAbort() => Interlocked.Increment(ref _abortCount);

            public void RecordCreate() => Interlocked.Increment(ref _createCount);

            public void RecordHistoryRead() => Interlocked.Increment(ref _historyReadCount);

            public void RecordResume() => Interlocked.Increment(ref _resumeCount);
        }

        private sealed class BlockingAgentSession(ModelProviderId ProviderId, string sessionId, Recorder recorder) : IAgentSession
        {
            public ModelProviderId ProviderId { get; } = ProviderId;

            public string SessionId { get; } = sessionId;

            public string? WorkspacePath => null;

            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask.ConfigureAwait(false);
                yield break;
            }

            public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();

            public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            {
                recorder.SendStarted.TrySetResult();
                await recorder.ReleaseSend.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new AgentRunId("run-blocking");
            }

            public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task AbortAsync(CancellationToken cancellationToken = default)
            {
                recorder.RecordAbort();
                return Task.CompletedTask;
            }

            public Task CompactAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
            {
                recorder.RecordHistoryRead();
                return Task.FromResult<IReadOnlyList<AgentEvent>>([]);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ToolSchemaRecordingProviderRuntime(ModelProviderId providerId, ToolSchemaRecordingProviderRuntime.Recorder recorder) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId(providerId.Value), "Tool Refresh") { DefaultModelId = "test-model" };

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
        {
            recorder.CreateToolNames.Add(options.Tools?.Select(static tool => tool.Spec.Name).ToArray() ?? []);
            recorder.CreateAgentPromptIds.Add(options.AgentPromptId);
            return Task.FromResult<IAgentSession>(new EmptyAgentSession(providerId, options.SessionId ?? Guid.CreateVersion7().ToString()));
        }

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
        {
            recorder.ResumeToolNames.Add(options.Tools?.Select(static tool => tool.Spec.Name).ToArray() ?? []);
            recorder.ResumeAgentPromptIds.Add(options.AgentPromptId);
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

            public List<string?> CreateAgentPromptIds { get; } = [];

            public List<string?> ResumeAgentPromptIds { get; } = [];
        }
    }

    private sealed class CompactionProviderRuntime(
        ModelProviderId providerId,
        string completionMessage,
        bool emitAgentCompletion = true,
        AgentCompactionOutcome? outcome = null,
        bool emitAgentStart = false,
        Task? releaseCompaction = null) : IModelProviderSessionRuntime
    {
        private readonly AgentCompactionOutcome _outcome = outcome ?? new AgentCompactionOutcome(
            Success: true,
            Message: completionMessage,
            MessagesRemoved: 131,
            TokensRemoved: 600,
            PreCompactionTokens: 1000,
            PostCompactionTokens: 400);

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId(providerId.Value), "Compaction Provider") { DefaultModelId = "test-model" };

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(CreateSession(options.SessionId ?? Guid.CreateVersion7().ToString()));

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(CreateSession(sessionId));

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });

        public IModelProviderTurnExecutor CreateTurnExecutor() => new NoOpTurnExecutor();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private EmptyAgentSession CreateSession(string sessionId)
        {
            var compactionStartedEvent = emitAgentStart
                ? CreateCompactionStartedEvent(providerId, sessionId)
                : null;
            var compactionEvent = emitAgentCompletion
                ? CreateCompactionCompletedEvent(providerId, sessionId, completionMessage)
                : null;
            return new EmptyAgentSession(providerId, sessionId, _outcome, compactionEvent, compactionStartedEvent, releaseCompaction);
        }

        private static AgentSessionUpdateEvent CreateCompactionStartedEvent(ModelProviderId providerId, string sessionId)
            => new(
                providerId,
                sessionId,
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.CompactionStarted,
                "Manual local compaction started.");

        private static AgentSessionUpdateEvent CreateCompactionCompletedEvent(ModelProviderId providerId, string sessionId, string completionMessage)
        {
            using var details = JsonDocument.Parse("""
                {
                  "schema": "codealta.localCompaction.v1",
                  "summaryMarkdown": "## Summary\nContinue.",
                  "summarizedMessageCount": 131,
                  "tokensBefore": 1000,
                  "tokensAfter": 400
                }
                """);
            return new AgentSessionUpdateEvent(
                providerId,
                sessionId,
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.CompactionCompleted,
                completionMessage,
                Details: details.RootElement.Clone());
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

    private sealed class EmptyAgentSession(
        ModelProviderId ProviderId,
        string sessionId,
        AgentCompactionOutcome? compactionOutcome = null,
        AgentEvent? compactionEvent = null,
        AgentEvent? compactionStartedEvent = null,
        Task? releaseCompaction = null) : IAgentSession, IAgentCompactionOutcomeProvider
    {
        private readonly object _gate = new();
        private readonly List<Action<AgentEvent>> _handlers = [];
        private readonly List<AgentEvent> _history = [];

        public ModelProviderId ProviderId { get; } = ProviderId;

        public string SessionId { get; } = sessionId;

        public string? WorkspacePath => null;

        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            IReadOnlyList<AgentEvent> history;
            lock (_gate)
            {
                history = _history.ToArray();
            }

            foreach (var @event in history)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return @event;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public IDisposable Subscribe(Action<AgentEvent> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_gate)
            {
                _handlers.Add(handler);
            }

            return new Subscription(() =>
            {
                lock (_gate)
                {
                    _handlers.Remove(handler);
                }
            });
        }

        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentRunId($"run-{Guid.NewGuid():N}"));

        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CompactAsync(CancellationToken cancellationToken = default)
            => CompactWithOutcomeAsync(cancellationToken);

        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<AgentEvent>>(_history.ToArray());
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<AgentCompactionOutcome?> CompactWithOutcomeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (compactionStartedEvent is not null)
            {
                Publish(compactionStartedEvent);
            }

            if (releaseCompaction is not null)
            {
                await releaseCompaction.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (compactionEvent is not null)
            {
                Publish(compactionEvent);
            }

            return compactionOutcome;
        }

        private void Publish(AgentEvent @event)
        {
            Action<AgentEvent>[] handlers;
            lock (_gate)
            {
                _history.Add(@event);
                handlers = _handlers.ToArray();
            }

            foreach (var handler in handlers)
            {
                handler(@event);
            }
        }
    }

    private sealed class Subscription(Action? dispose = null) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
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
