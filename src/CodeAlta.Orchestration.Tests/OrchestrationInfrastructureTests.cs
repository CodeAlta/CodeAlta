using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Orchestration;
using CodeAlta.Orchestration.Context;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;
using CodeAlta.Workspaces;
using CodeAlta.Workspaces.Roles;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class OrchestrationInfrastructureTests
{
    [TestMethod]
    public async Task RoleProfileStore_ParsesAgentMarkdownAndProjectOverlay()
    {
        using var temp = TempDirectory.Create();
        var globalAgentsRoot = Path.Combine(temp.Path, ".codealta", "agents");
        var projectRoot = Path.Combine(temp.Path, "repo-main");
        var projectAgentsRoot = Path.Combine(projectRoot, ".codealta", "agents");
        Directory.CreateDirectory(globalAgentsRoot);
        Directory.CreateDirectory(projectAgentsRoot);

        var frontmatterRolePath = Path.Combine(globalAgentsRoot, "coordinator.agent.md");
        await File.WriteAllTextAsync(
            frontmatterRolePath,
            """
            ---
            name: coordinator
            description: Plans workspace goals into durable tasks.
            tools:
              - read
              - grep
            model: gpt-5.4
            user-invocable: false
            codealta:
              default_backend: codex
              scope: workspace
              tags:
                - planning
            ---
            Create and maintain a clear task tree with acceptance criteria.
            """
        ).ConfigureAwait(false);

        var projectRolePath = Path.Combine(projectAgentsRoot, "reviewer.agent.md");
        await File.WriteAllTextAsync(
            projectRolePath,
            """
            ---
            name: reviewer
            description: Reviews diffs for regressions and risk.
            ---
            # Reviewer

            Focus on correctness, missing tests, and user-facing behavior.
            """
        ).ConfigureAwait(false);

        var store = new RoleProfileStore();
        var profiles = await store.LoadCatalogAgentsAsync(
                Path.Combine(temp.Path, ".codealta"),
                [projectRoot])
            .ConfigureAwait(false);

        Assert.AreEqual(2, profiles.Count);
        var planner = profiles.Single(x => x.RoleId == "coordinator");
        Assert.AreEqual("coordinator", planner.Name);
        CollectionAssert.Contains(planner.ToolsPolicy.Allowed.ToArray(), "read");
        Assert.AreEqual("codex", planner.DefaultBackend);
        Assert.AreEqual("gpt-5.4", planner.DefaultModel);
        Assert.AreEqual("workspace", planner.Scope);
        Assert.IsFalse(planner.UserInvocable);

        var reviewer = profiles.Single(x => x.RoleId == "reviewer");
        Assert.AreEqual("reviewer", reviewer.Name);
        StringAssert.Contains(reviewer.Description, "regressions");
    }

    [TestMethod]
    public void AgentInstructionTemplateProvider_BuildsCoordinatorInstructions()
    {
        var provider = new AgentInstructionTemplateProvider();
        var workspace = new WorkspaceDescriptor
        {
            Id = WorkspaceId.NewVersion7().ToString(),
            Slug = "wk-core",
            DisplayName = "Core Workspace",
            DefaultCheckoutRoot = @"C:\code",
        };
        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            RepoUrl = "https://example.com/repo-main.git",
            DefaultBranch = "main",
        };
        var thread = new WorkThreadDescriptor
        {
            ThreadId = "thread-1",
            Kind = WorkThreadKind.WorkspaceThread,
            WorkspaceRef = workspace.Id,
            ProjectRefs = [project.Id],
            ScopeMode = WorkThreadScopeMode.SingleProject,
            Title = "Review sqlitevec integration",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var profile = new RoleProfile
        {
            RoleId = "coordinator",
            Name = "coordinator",
            Description = "Plans work.",
            Instructions = "Emit one schedule block when you need coordination.",
            ToolsPolicy = new RoleToolsPolicy(),
            DefaultBackend = "codex",
            Scope = "workspace",
            IsBuiltIn = true,
            SourcePath = "builtin://coordinator",
        };

        var instructions = provider.BuildCoordinatorInstructions(thread, workspace, [project], profile);

        StringAssert.Contains(instructions.SystemMessage, "CodeAlta Coordinator");
        StringAssert.Contains(instructions.DeveloperInstructions, "Review sqlitevec integration");
        StringAssert.Contains(instructions.DeveloperInstructions, "Main Repo");
        StringAssert.Contains(instructions.DeveloperInstructions, "codealta_schedule");
    }

    [TestMethod]
    public async Task ContextPackBuilder_EnforcesBudgetAndPreservesSourceLinks()
    {
        var providerA = new StaticContextProvider(
        [
            new ContextItem
            {
                Title = "File A",
                Content = "01234567890123456789",
                SourceUri = "file://repo/src/A.cs#L10",
                Priority = 10,
            },
        ]);
        var providerB = new StaticContextProvider(
        [
            new ContextItem
            {
                Title = "File B",
                Content = "abcdefghijklmnopqrstuvwxyz",
                SourceUri = "file://repo/src/B.cs#L20",
                Priority = 20,
            },
        ]);

        var builder = new ContextPackBuilder([providerA, providerB]);
        var pack = await builder.BuildAsync(
            new AgentScope { Kind = AgentScopeKind.Project, Id = "project-1" },
            "implement parser",
            maxCharacters: 40).ConfigureAwait(false);

        Assert.IsTrue(pack.Truncated);
        Assert.IsTrue(pack.TotalCharacters <= 40);
        Assert.IsTrue(pack.Items.Any(x => x.SourceUri == "codealta://request"));
        Assert.IsTrue(pack.Items.Any(x => x.SourceUri.StartsWith("file://", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OrchestrationFlow_PlannerCreatesAndBuilderCompletesTasks()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var taskRepository = new TaskRepository(db);
        var artifactStore = new ArtifactStore();
        var artifactRepository = new ArtifactRepository(db);
        var options = new OrchestrationOptions { ArtifactRoot = Path.Combine(temp.Path, "orchestration") };

        var planner = new PlannerService(taskRepository, artifactStore, artifactRepository, options);
        var builder = new BuilderService(taskRepository, artifactStore, artifactRepository, options);

        var plan = await planner.CreatePlanAsync(
            new PlannerPlanRequest
            {
                Goal = "Implement orchestration flow",
                WorkspaceId = "workspace-1",
                ProjectId = "project-1",
                Steps = ["Add planner", "Add builder"],
            }).ConfigureAwait(false);

        Assert.AreEqual(2, plan.ChildTaskIds.Count);
        var root = await taskRepository.GetAsync(plan.RootTaskId).ConfigureAwait(false);
        Assert.IsNotNull(root);
        Assert.AreEqual(CodeAlta.Persistence.TaskStatus.Pending, root.Status);

        var planArtifact = await artifactRepository.GetByIdAsync(plan.PlanArtifactId).ConfigureAwait(false);
        Assert.IsNotNull(planArtifact);
        StringAssert.Contains(planArtifact.Type, "plan.output");

        var buildResult = await builder.CompleteTaskAsync(
            new BuilderExecutionRequest
            {
                TaskId = plan.ChildTaskIds[0],
                WorkspaceId = "workspace-1",
                ProjectId = "project-1",
                VerificationSummary = "dotnet build and dotnet test passed.",
            }).ConfigureAwait(false);

        var completed = await taskRepository.GetAsync(buildResult.TaskId).ConfigureAwait(false);
        Assert.IsNotNull(completed);
        Assert.AreEqual(CodeAlta.Persistence.TaskStatus.Completed, completed.Status);

        var verificationArtifact = await artifactRepository.GetByIdAsync(buildResult.VerificationArtifactId).ConfigureAwait(false);
        Assert.IsNotNull(verificationArtifact);
        Assert.AreEqual("builder.verification", verificationArtifact.Type);
    }

    [TestMethod]
    public async Task AgentHub_RunAsync_WithFakeBackend_EmitsRunEvents()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);

        var backendFactory = new AgentBackendFactory();
        backendFactory.Register("fake", static () => new FakeBackend());

        await using var hub = new AgentHub(backendFactory, repository);
        var identity = await hub.RegisterAgentAsync(
            "builder.project",
            new AgentScope { Kind = AgentScopeKind.Project, Id = "project-1" },
            new AgentBackendId("fake")).ConfigureAwait(false);

        await hub.StartSessionAsync(
            identity.AgentId,
            new AgentSessionCreateOptions
            {
                OnPermissionRequest = static (_, _) =>
                    Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }).ConfigureAwait(false);

        var runId = await hub.RunAsync(
            identity.AgentId,
            new AgentSendOptions { Input = AgentInput.Text("hello") }).ConfigureAwait(false);
        Assert.AreEqual("fake-run-1", runId.ToString());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var events = new List<OrchestrationEvent>();
        await foreach (var @event in hub.StreamEventsAsync(cts.Token))
        {
            events.Add(@event);
            if (events.Count >= 2)
            {
                break;
            }
        }

        Assert.IsTrue(events.OfType<RunStartedEvent>().Any());
        Assert.IsTrue(events.OfType<RunCompletedEvent>().Any());
    }

    [TestMethod]
    public async Task AgentHub_SubscribeSessionEventsAsync_ForwardsSessionEvents()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);

        var backendFactory = new AgentBackendFactory();
        backendFactory.Register("fake", static () => new FakeBackend());

        await using var hub = new AgentHub(backendFactory, repository);
        var identity = await hub.RegisterAgentAsync(
            "builder.project",
            new AgentScope { Kind = AgentScopeKind.Project, Id = "project-1" },
            new AgentBackendId("fake")).ConfigureAwait(false);

        await hub.StartSessionAsync(
            identity.AgentId,
            new AgentSessionCreateOptions
            {
                OnPermissionRequest = static (_, _) =>
                    Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }).ConfigureAwait(false);

        var received = new List<AgentEvent>();
        using var subscription = await hub.SubscribeSessionEventsAsync(
                identity.AgentId,
                received.Add)
            .ConfigureAwait(false);

        _ = await hub.RunAsync(
                identity.AgentId,
                new AgentSendOptions { Input = AgentInput.Text("hello") })
            .ConfigureAwait(false);

        Assert.IsTrue(received.OfType<AgentContentCompletedEvent>().Any(x => x.Kind == AgentContentKind.Assistant));
        Assert.IsTrue(received.OfType<AgentSessionUpdateEvent>().Any(x => x.Kind == AgentSessionUpdateKind.Idle));
    }

    [TestMethod]
    public async Task AgentHub_SteerAsync_UsesSessionSteerWhenSupported()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);

        var backendFactory = new AgentBackendFactory();
        backendFactory.Register("fake", static () => new FakeBackend());

        await using var hub = new AgentHub(backendFactory, repository);
        var identity = await hub.RegisterAgentAsync(
            "builder.project",
            new AgentScope { Kind = AgentScopeKind.Project, Id = "project-1" },
            new AgentBackendId("fake")).ConfigureAwait(false);

        await hub.StartSessionAsync(
            identity.AgentId,
            new AgentSessionCreateOptions
            {
                OnPermissionRequest = static (_, _) =>
                    Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }).ConfigureAwait(false);

        var runId = await hub.SteerAsync(
            identity.AgentId,
            new AgentSteerOptions
            {
                Input = AgentInput.Text("continue"),
                ExpectedRunId = new AgentRunId("fake-run-1")
            }).ConfigureAwait(false);

        Assert.AreEqual("fake-run-1", runId.Value);
    }

    [TestMethod]
    public async Task AgentHub_ListModelsAsync_ReturnsBackendModels()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);

        var backendFactory = new AgentBackendFactory();
        backendFactory.Register("fake", static () => new FakeBackend(
        [
            new AgentModelInfo("model-a", DisplayName: "Model A"),
            new AgentModelInfo("model-b", DisplayName: "Model B"),
        ]));

        await using var hub = new AgentHub(backendFactory, repository);
        var models = await hub.ListModelsAsync(new AgentBackendId("fake")).ConfigureAwait(false);

        Assert.AreEqual(2, models.Count);
        Assert.AreEqual("model-a", models[0].Id);
        Assert.AreEqual("Model B", models[1].DisplayName);
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_SendAsync_UsesCoordinatorDefaultsAndSanitizesSchedule()
    {
        using var temp = TempDirectory.Create();
        var options = new WorkspaceCatalogOptions { GlobalRepoRoot = temp.Path };
        var workspaceCatalog = new WorkspaceCatalog(options);
        var threadCatalog = new WorkThreadCatalog(workspaceCatalog, options);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            RepoUrl = "https://example.com/repo-main.git",
            DefaultBranch = "main",
            Checkout = new CheckoutRule { PathTemplate = @"{workspaceSlug}\{projectSlug}" },
        };
        await workspaceCatalog.SaveProjectAsync(project).ConfigureAwait(false);

        var workspace = new WorkspaceDescriptor
        {
            Id = WorkspaceId.NewVersion7().ToString(),
            Slug = "wk-core",
            DisplayName = "Core Workspace",
            DefaultCheckoutRoot = @"C:\code",
            ProjectRefs = [project.Id],
        };
        await workspaceCatalog.SaveWorkspaceAsync(workspace).ConfigureAwait(false);

        var agentsRoot = Path.Combine(temp.Path, "agents");
        Directory.CreateDirectory(agentsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(agentsRoot, "coordinator.agent.md"),
            """
            ---
            name: coordinator
            description: Coordinates thread work.
            model: gpt-5.4
            codealta:
              default_backend: fake
              default_reasoning_effort: high
            ---
            Emit a schedule when coordination is required.
            """).ConfigureAwait(false);

        var backendFactory = new AgentBackendFactory();
        var fakeBackend = new FakeBackend(sendEventFactory: (backendId, sessionId, runId) =>
        [
            new AgentContentCompletedEvent(
                backendId,
                sessionId,
                DateTimeOffset.UtcNow,
                runId,
                AgentContentKind.Assistant,
                "assistant-1",
                null,
                """
                I’m going to coordinate this.

                ```codealta_schedule
                version: 1
                dispatches: []
                ```
                """),
            new AgentSessionUpdateEvent(
                backendId,
                sessionId,
                DateTimeOffset.UtcNow,
                runId,
                AgentSessionUpdateKind.Idle,
                "idle"),
        ]);
        backendFactory.Register("fake", () => fakeBackend);

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            workspaceCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            options);

        var thread = new WorkThreadDescriptor
        {
            ThreadId = "platform-search-review",
            Kind = WorkThreadKind.WorkspaceThread,
            WorkspaceRef = workspace.Id,
            ProjectRefs = [project.Id],
            ScopeMode = WorkThreadScopeMode.SingleProject,
            Title = "Review sqlitevec integration",
            Status = WorkThreadStatus.Draft,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };
        await threadCatalog.SaveAsync(thread).ConfigureAwait(false);

        var executionOptions = new WorkThreadExecutionOptions
        {
            BackendId = new AgentBackendId("fake"),
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) =>
                Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

        _ = await runtime.SendAsync(
                thread,
                executionOptions,
                new AgentSendOptions { Input = AgentInput.Text("review the project") })
            .ConfigureAwait(false);

        Assert.IsNotNull(fakeBackend.LastCreateOptions);
        Assert.AreEqual("gpt-5.4", fakeBackend.LastCreateOptions.Model);
        Assert.AreEqual(AgentReasoningEffort.High, fakeBackend.LastCreateOptions.ReasoningEffort);
        StringAssert.Contains(fakeBackend.LastCreateOptions.SystemMessage, "CodeAlta Coordinator");
        StringAssert.Contains(fakeBackend.LastCreateOptions.DeveloperInstructions, "Main Repo");

        var history = await runtime.GetHistoryAsync(thread.ThreadId).ConfigureAwait(false);
        var assistant = history.OfType<AgentContentCompletedEvent>().Single();
        StringAssert.Contains(assistant.Content, "I’m going to coordinate this.");
        Assert.IsFalse(assistant.Content.Contains("codealta_schedule", StringComparison.OrdinalIgnoreCase));

        var persisted = await threadCatalog.GetByIdAsync(thread.ThreadId).ConfigureAwait(false);
        Assert.IsNotNull(persisted);
        Assert.IsTrue(persisted.IsWorkspaceLocked);
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_HandoffAsync_EmitsHostEvents()
    {
        using var temp = TempDirectory.Create();
        var options = new WorkspaceCatalogOptions { GlobalRepoRoot = temp.Path };
        var workspaceCatalog = new WorkspaceCatalog(options);
        var threadCatalog = new WorkThreadCatalog(workspaceCatalog, options);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            RepoUrl = "https://example.com/repo-main.git",
            DefaultBranch = "main",
        };
        await workspaceCatalog.SaveProjectAsync(project).ConfigureAwait(false);

        var workspace = new WorkspaceDescriptor
        {
            Id = WorkspaceId.NewVersion7().ToString(),
            Slug = "wk-core",
            DisplayName = "Core Workspace",
            DefaultCheckoutRoot = @"C:\code",
            ProjectRefs = [project.Id],
        };
        await workspaceCatalog.SaveWorkspaceAsync(workspace).ConfigureAwait(false);

        var agentsRoot = Path.Combine(temp.Path, "agents");
        Directory.CreateDirectory(agentsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(agentsRoot, "coordinator.agent.md"),
            """
            ---
            name: coordinator
            description: Coordinates thread work.
            ---
            Emit a schedule when coordination is required.
            """).ConfigureAwait(false);

        var backendFactory = new AgentBackendFactory();
        backendFactory.Register("fake", static () => new FakeBackend());

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            workspaceCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            options);

        var source = CreateThread("source-thread", workspace.Id, project.Id, "Source Thread");
        var target = CreateThread("target-thread", workspace.Id, project.Id, "Target Thread");
        await threadCatalog.SaveAsync(source).ConfigureAwait(false);
        await threadCatalog.SaveAsync(target).ConfigureAwait(false);

        var executionOptions = new WorkThreadExecutionOptions
        {
            BackendId = new AgentBackendId("fake"),
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) =>
                Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var enumerator = runtime.StreamEventsAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        _ = await runtime.HandoffAsync(source, target, executionOptions, "Continue the review work.").ConfigureAwait(false);

        var received = new List<WorkThreadRuntimeEvent>();
        while (received.Count < 2 && await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            received.Add(enumerator.Current);
        }

        Assert.AreEqual(2, received.Count);
        Assert.IsTrue(received.OfType<WorkThreadHostEvent>().Any(x => x.ThreadId == source.ThreadId));
        Assert.IsTrue(received.OfType<WorkThreadHostEvent>().Any(x => x.ThreadId == target.ThreadId));
    }

    private static async Task<CodeAltaDb> CreateDbAsync(string rootPath)
    {
        var dbPath = Path.Combine(rootPath, "state", "db", "codealta.db");
        var db = new CodeAltaDb(new CodeAltaDbOptions { DatabasePath = dbPath });
        await db.InitializeAsync().ConfigureAwait(false);
        return db;
    }

    private sealed class StaticContextProvider : IContextProvider
    {
        private readonly IReadOnlyList<ContextItem> _items;

        public StaticContextProvider(IReadOnlyList<ContextItem> items)
        {
            _items = items;
        }

        public Task<IReadOnlyList<ContextItem>> ProvideAsync(
            ContextProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.FromResult(_items);
        }
    }

    private sealed class FakeBackend : IAgentBackend
    {
        private int _runCounter;
        private readonly IReadOnlyList<AgentModelInfo> _models;
        private readonly Func<AgentBackendId, string, AgentRunId, IReadOnlyList<AgentEvent>>? _sendEventFactory;

        public FakeBackend(
            IReadOnlyList<AgentModelInfo>? models = null,
            Func<AgentBackendId, string, AgentRunId, IReadOnlyList<AgentEvent>>? sendEventFactory = null)
        {
            _models = models ?? [];
            _sendEventFactory = sendEventFactory;
        }

        public AgentBackendId BackendId => new("fake");

        public string DisplayName => "Fake";

        public AgentSessionCreateOptions? LastCreateOptions { get; private set; }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_models);
        }

        public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentSessionMetadata>>([]);
        }

        public Task<IAgentSession> CreateSessionAsync(
            AgentSessionCreateOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            LastCreateOptions = options;
            return Task.FromResult<IAgentSession>(new FakeSession(this));
        }

        public Task<IAgentSession> ResumeSessionAsync(
            string sessionId,
            AgentSessionResumeOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Task.FromResult<IAgentSession>(new FakeSession(this, sessionId));
        }

        private sealed class FakeSession : IAgentSession
        {
            private readonly FakeBackend _backend;
            private readonly List<AgentEvent> _events = [];
            private readonly List<Action<AgentEvent>> _subscribers = [];
            private readonly object _subscriberLock = new();

            public FakeSession(FakeBackend backend, string? sessionId = null)
            {
                _backend = backend;
                SessionId = sessionId ?? "fake-session-1";
            }

            public AgentBackendId BackendId => _backend.BackendId;

            public string SessionId { get; }

            public string? WorkspacePath => null;

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }

            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (var @event in _events.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return @event;
                    await Task.Yield();
                }
            }

            public IDisposable Subscribe(Action<AgentEvent> handler)
            {
                ArgumentNullException.ThrowIfNull(handler);

                lock (_subscriberLock)
                {
                    _subscribers.Add(handler);
                }

                return DisposableAction.Create(() =>
                {
                    lock (_subscriberLock)
                    {
                        _subscribers.Remove(handler);
                    }
                });
            }

            public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(options);
                var runId = new AgentRunId($"fake-run-{Interlocked.Increment(ref _backend._runCounter)}");
                var events = _backend._sendEventFactory?.Invoke(BackendId, SessionId, runId)
                    ?? DefaultSendEvents(BackendId, SessionId, runId);

                foreach (var @event in events)
                {
                    _events.Add(@event);
                    Publish(@event);
                }

                return Task.FromResult(runId);
            }

            public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(options);
                return Task.FromResult(options.ExpectedRunId ?? new AgentRunId("fake-run-1"));
            }

            private void Publish(AgentEvent @event)
            {
                Action<AgentEvent>[] subscribers;
                lock (_subscriberLock)
                {
                    subscribers = _subscribers.ToArray();
                }

                foreach (var subscriber in subscribers)
                {
                    subscriber(@event);
                }
            }

            public Task AbortAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AgentEvent>>(_events.ToArray());
            }

            private static IReadOnlyList<AgentEvent> DefaultSendEvents(
                AgentBackendId backendId,
                string sessionId,
                AgentRunId runId)
            {
                return
                [
                    new AgentContentCompletedEvent(
                        backendId,
                        sessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        AgentContentKind.Assistant,
                        "assistant-1",
                        runId.Value,
                        "ok"),
                    new AgentSessionUpdateEvent(
                        backendId,
                        sessionId,
                        DateTimeOffset.UtcNow,
                        null,
                        AgentSessionUpdateKind.Idle,
                        null),
                ];
            }
        }
    }

    private static WorkThreadDescriptor CreateThread(string threadId, string workspaceId, string projectId, string title)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new WorkThreadDescriptor
        {
            ThreadId = threadId,
            Kind = WorkThreadKind.WorkspaceThread,
            WorkspaceRef = workspaceId,
            ProjectRefs = [projectId],
            ScopeMode = WorkThreadScopeMode.SingleProject,
            Title = title,
            Status = WorkThreadStatus.Draft,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
        };
    }

    private sealed class DisposableAction : IDisposable
    {
        private readonly Action _dispose;
        private bool _disposed;

        private DisposableAction(Action dispose)
        {
            _dispose = dispose;
        }

        public static IDisposable Create(Action dispose)
        {
            return new DisposableAction(dispose);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dispose();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Orchestration.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for temporary test files.
            }
        }
    }
}
