using System.Collections.Concurrent;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class HeadlessHostFixtureTests
{
    [TestMethod]
    public async Task HeadlessHost_CreatesSessionSubmitsPromptStreamsEventsAndShutsDown()
    {
        using var temp = TempDirectory.Create();
        var ProviderId = new ModelProviderId("fake-headless");
        await using var host = await CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = temp.GlobalRoot,
                CurrentProjectPath = temp.ProjectRoot,
                IsHeadless = true,
                HasInteractiveUi = false,
                StartPlugins = false,
                ConfigureModelProviders = registry => RegisterFakeProvider(registry, ProviderId),
            },
            CancellationToken.None);
        var executionOptions = new SessionExecutionOptions
        {
            ProviderId = ProviderId,
            ProviderKey = ProviderId.Value,
            WorkingDirectory = temp.ProjectRoot,
            ProjectRoots = [temp.ProjectRoot],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        };
        var streamedEvents = new List<SessionRuntimeEvent>();
        using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedAssistantContent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamTask = Task.Run(async () =>
        {
            await foreach (var runtimeEvent in host.RuntimeService.StreamEventsAsync(streamCts.Token))
            {
                streamedEvents.Add(runtimeEvent);
                if (runtimeEvent is SessionAgentEvent { Event: AgentContentCompletedEvent completed } &&
                    string.Equals(completed.Content, "fake response", StringComparison.Ordinal))
                {
                    receivedAssistantContent.TrySetResult();
                    break;
                }
            }
        });

        var session = await host.RuntimeService.CreateProjectSessionAsync(
            host.CurrentProject,
            executionOptions,
            title: "Headless sample",
            CancellationToken.None);

        var runId = await host.RuntimeService.SendAsync(
            session,
            executionOptions,
            new AgentSendOptions { Input = AgentInput.Text("hello from headless") },
            CancellationToken.None);

        await receivedAssistantContent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await streamCts.CancelAsync();
        await streamTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        Assert.IsFalse(string.IsNullOrWhiteSpace(runId.Value));
        Assert.IsTrue(streamedEvents.OfType<SessionLifecycleRuntimeEvent>().Any(static runtimeEvent =>
            runtimeEvent.Event.Kind == SessionLifecycleEventKind.SessionStarted));
        Assert.IsTrue(streamedEvents.OfType<SessionAgentEvent>().Any(static runtimeEvent =>
            runtimeEvent.Event is AgentContentCompletedEvent { Content: "fake response" }));
    }

    [TestMethod]
    public async Task HeadlessHost_ComposesPluginSkillRoots()
    {
        using var temp = TempDirectory.Create();
        var skillRoot = Path.Combine(temp.Root, "plugin-skills");
        var skillDirectory = Path.Combine(skillRoot, "shared-host-skill");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(skillDirectory, "SKILL.md"),
            """
            ---
            name: shared-host-skill
            description: Skill contributed by the shared host plugin fixture.
            ---

            # Shared host skill

            Use this fixture skill from plugin resource roots.
            """);
        SharedHostFixturePlugin.SkillRootPath = skillRoot;

        await using var host = await CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = temp.GlobalRoot,
                CurrentProjectPath = temp.ProjectRoot,
                IsHeadless = true,
                HasInteractiveUi = false,
                PluginBuiltIns =
                [
                    new BuiltInPluginDefinition
                    {
                        Id = "shared-host-fixture",
                        DisplayName = "Shared host fixture",
                        Factory = static () => new SharedHostFixturePlugin(),
                    },
                ],
            },
            CancellationToken.None);

        var skills = await host.SkillCatalog.ListAsync(
            new SkillCatalogQuery
            {
                Discovery = new SkillDiscoveryContext
                {
                    ProjectRoots = [temp.ProjectRoot],
                    UserCodeAltaRoot = temp.GlobalRoot,
                },
                IncludeUntrusted = true,
            },
            CancellationToken.None);

        Assert.IsTrue(skills.Any(static skill =>
            string.Equals(skill.Name, "shared-host-skill", StringComparison.Ordinal) &&
            skill.SourceKind == SkillSourceKind.Plugin));
    }

    [TestMethod]
    public async Task HeadlessHost_DoesNotLeaveRunActiveWhenIdleArrivesBeforeSendReturns()
    {
        using var temp = TempDirectory.Create();
        var ProviderId = new ModelProviderId("fake-race");
        await using var host = await CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = temp.GlobalRoot,
                CurrentProjectPath = temp.ProjectRoot,
                IsHeadless = true,
                HasInteractiveUi = false,
                StartPlugins = false,
                ConfigureModelProviders = registry => RegisterFakeProvider(registry, ProviderId),
            },
            CancellationToken.None);
        var executionOptions = new SessionExecutionOptions
        {
            ProviderId = ProviderId,
            ProviderKey = ProviderId.Value,
            WorkingDirectory = temp.ProjectRoot,
            ProjectRoots = [temp.ProjectRoot],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        };
        var session = await host.RuntimeService.CreateProjectSessionAsync(
            host.CurrentProject,
            executionOptions,
            title: "Race sample",
            CancellationToken.None);

        var runId = await host.RuntimeService.SendAsync(
            session,
            executionOptions,
            new AgentSendOptions { Input = AgentInput.Text("complete before returning") },
            CancellationToken.None);
        var hasActiveRun = await host.RuntimeService.HasActiveRunAsync(session, CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(runId.Value));
        Assert.IsFalse(hasActiveRun);
    }

    private static void RegisterFakeProvider(ModelProviderRegistry registry, ModelProviderId ProviderId)
    {
        var descriptor = new ModelProviderDescriptor(new ModelProviderId(ProviderId.Value), "Fake Headless") { DefaultModelId = "fake-model" };
        registry.RegisterOrReplace(descriptor, () => new FakeModelProviderRuntime(descriptor));
    }

    private sealed class FakeModelProviderRuntime(ModelProviderDescriptor descriptor) : IAgentModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = descriptor;

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test",
            ProviderKey = descriptor.ProviderId.Value,
            DisplayName = descriptor.DisplayName,
            TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new()
        {
            Provider = RuntimeDescriptor,
            TurnExecutor = new FakeTurnExecutor(),
        };

        public IModelProviderTurnExecutor CreateTurnExecutor() => new FakeTurnExecutor();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo("fake-model", DisplayName: "Fake Model")],
                SelectedModelId = "fake-model",
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTurnExecutor : IModelProviderTurnExecutor
    {
        public async Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            await onUpdate(
                    new AgentTurnDelta
                    {
                        Kind = AgentContentKind.Assistant,
                        ContentId = "content-1",
                        Text = "fake response",
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(
                    AgentConversationRole.Assistant,
                    [new AgentMessagePart.Text("fake response")]),
                AssistantPartContentIds = ["content-1"],
            };
        }
    }

    public sealed class SharedHostFixturePlugin : PluginBase
    {
        public static string SkillRootPath { get; set; } = string.Empty;

        public override IEnumerable<PluginResourceContribution> GetResources()
        {
            yield return new PluginResourceContribution
            {
                Kind = PluginResourceKind.SkillRoot,
                Path = SkillRootPath,
                IsPackageRelative = false,
            };
        }
    }

    private sealed class FakeAgentSession(ModelProviderId ProviderId, string sessionId, string? workspacePath) : IAgentSession
    {
        private readonly ConcurrentDictionary<Guid, Action<AgentEvent>> _subscribers = new();
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public ModelProviderId ProviderId { get; } = ProviderId;

        public string SessionId { get; } = sessionId;

        public string? WorkspacePath { get; } = workspacePath;

        public IAsyncEnumerable<AgentEvent> StreamEventsAsync(CancellationToken cancellationToken = default)
            => _events.Reader.ReadAllAsync(cancellationToken);

        public IDisposable Subscribe(Action<AgentEvent> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            var id = Guid.NewGuid();
            _subscribers[id] = handler;
            return new Subscription(() => _subscribers.TryRemove(id, out _));
        }

        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            var runId = new AgentRunId("run-1");
            Publish(new AgentSessionUpdateEvent(
                ProviderId,
                SessionId,
                DateTimeOffset.UtcNow,
                runId,
                AgentSessionUpdateKind.Started,
                "Fake session started."));
            Publish(new AgentContentCompletedEvent(
                ProviderId,
                SessionId,
                DateTimeOffset.UtcNow,
                runId,
                AgentContentKind.Assistant,
                "content-1",
                ParentActivityId: null,
                "fake response"));
            Publish(new AgentSessionUpdateEvent(
                ProviderId,
                SessionId,
                DateTimeOffset.UtcNow,
                runId,
                AgentSessionUpdateKind.Idle,
                "Fake session idle."));
            return Task.FromResult(runId);
        }

        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CompactAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEvent>>([]);

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            _subscribers.Clear();
            return ValueTask.CompletedTask;
        }

        private void Publish(AgentEvent agentEvent)
        {
            _events.Writer.TryWrite(agentEvent);
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber(agentEvent);
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string root)
        {
            Root = root;
            GlobalRoot = Path.Combine(root, "global");
            ProjectRoot = Path.Combine(root, "project");
            Directory.CreateDirectory(GlobalRoot);
            Directory.CreateDirectory(ProjectRoot);
        }

        public string Root { get; }

        public string GlobalRoot { get; }

        public string ProjectRoot { get; }

        public static TempDirectory Create()
            => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.HeadlessHost.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
