using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Roles;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration;
using CodeAlta.Orchestration.Context;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class OrchestrationInfrastructureTests
{
    [TestMethod]
    public async Task RoleProfileStore_ParsesAgentMarkdownAndProjectOverlay()
    {
        using var temp = TempDirectory.Create();
        var globalAgentsRoot = Path.Combine(temp.Path, ".alta", "agents");
        var projectRoot = Path.Combine(temp.Path, "repo-main");
        var projectAgentsRoot = Path.Combine(projectRoot, ".alta", "agents");
        Directory.CreateDirectory(globalAgentsRoot);
        Directory.CreateDirectory(projectAgentsRoot);

        await File.WriteAllTextAsync(
            Path.Combine(globalAgentsRoot, "coordinator.agent.md"),
            """
            ---
            name: coordinator
            description: Plans project goals into durable tasks.
            tools:
              - read
              - grep
            model: gpt-5.4
            user-invocable: false
            codealta:
              default_backend: codex
              scope: project
              skills:
                - code-review
              tags:
                - planning
            ---
            Create and maintain a clear task tree with acceptance criteria.
            """).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(projectAgentsRoot, "reviewer.agent.md"),
            """
            ---
            name: reviewer
            description: Reviews diffs for regressions and risk.
            ---
            # Reviewer

            Focus on correctness, missing tests, and user-facing behavior.
            """).ConfigureAwait(false);

        var store = new RoleProfileStore();
        var profiles = await store.LoadCatalogAgentsAsync(
                Path.Combine(temp.Path, ".alta"),
                [projectRoot])
            .ConfigureAwait(false);

        Assert.AreEqual(2, profiles.Count);
        var planner = profiles.Single(x => x.RoleId == "coordinator");
        Assert.AreEqual("coordinator", planner.Name);
        CollectionAssert.Contains(planner.ToolsPolicy.Allowed.ToArray(), "read");
        Assert.AreEqual("codex", planner.DefaultBackend);
        Assert.AreEqual("gpt-5.4", planner.DefaultModel);
        Assert.AreEqual("project", planner.Scope);
        CollectionAssert.Contains(planner.Skills.ToArray(), "code-review");
        Assert.IsFalse(planner.UserInvocable);

        var reviewer = profiles.Single(x => x.RoleId == "reviewer");
        Assert.AreEqual("reviewer", reviewer.Name);
        StringAssert.Contains(reviewer.Description, "regressions");
    }

    [TestMethod]
    public void AgentInstructionTemplateProvider_LoadsDefaultSystemPrompt()
    {
        var provider = new AgentInstructionTemplateProvider();
        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = @"C:\code\repo-main",
            DefaultBranch = "main",
        };
        var thread = new WorkThreadDescriptor
        {
            ThreadId = "fake:thread-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = "fake",
            BackendSessionId = "thread-1",
            ProjectRef = project.Id,
            WorkingDirectory = project.ProjectPath,
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
            Scope = "project",
            IsBuiltIn = true,
            SourcePath = "builtin://coordinator",
        };

        var instructions = provider.BuildCoordinatorInstructions(thread, project, profile);

        Assert.IsFalse(string.IsNullOrWhiteSpace(instructions.SystemMessage));
        StringAssert.Contains(instructions.SystemMessage, "You are CodeAlta");
        Assert.IsNotNull(instructions.DeveloperInstructions);
        StringAssert.Contains(instructions.DeveloperInstructions, "# Role");
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_CreateThread_AddsSkillActivationToolAndAdvertisements()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var skillCatalog = new SkillCatalog();
        var instructionProvider = new AgentInstructionTemplateProvider(skillCatalog, catalogOptions);

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        Directory.CreateDirectory(Path.Combine(project.ProjectPath, ".alta", "skills", "code-review"));
        await File.WriteAllTextAsync(
            Path.Combine(project.ProjectPath, ".alta", "skills", "code-review", "SKILL.md"),
            """
            ---
            name: code-review
            description: Review code for regressions.
            ---
            # Code Review

            Review code for regressions.
            """).ConfigureAwait(false);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var agentsRoot = Path.Combine(temp.Path, "agents");
        Directory.CreateDirectory(agentsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(agentsRoot, "coordinator.agent.md"),
            """
            ---
            name: coordinator
            description: Coordinates thread work.
            model: gpt-5.4
            ---
            Emit a schedule when coordination is required.
            """).ConfigureAwait(false);

        var backendFactory = new AgentBackendFactory();
        var fakeBackend = new FakeBackend();
        backendFactory.Register("fake", () => fakeBackend);
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions,
            skillCatalog);

        _ = await runtime.CreateProjectThreadAsync(
            project,
            CreateExecutionOptions(project.ProjectPath),
            "Skills Thread").ConfigureAwait(false);

        Assert.IsNotNull(fakeBackend.LastCreateOptions);
        Assert.IsNotNull(fakeBackend.LastCreateOptions.Tools);
        Assert.IsTrue(fakeBackend.LastCreateOptions.Tools.Any(static tool => tool.Spec.Name == "codealta.skills.activate"));
        Assert.IsNotNull(fakeBackend.LastCreateOptions.DeveloperInstructions);
        StringAssert.Contains(fakeBackend.LastCreateOptions.DeveloperInstructions, "<available_skills>");
        StringAssert.Contains(fakeBackend.LastCreateOptions.DeveloperInstructions, "code-review");
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("copilot")]
    public async Task WorkThreadRuntimeService_CreateThread_DoesNotInjectSkillsForProviderManagedSkills(string backendIdValue)
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var skillCatalog = new SkillCatalog();
        var instructionProvider = new AgentInstructionTemplateProvider(skillCatalog, catalogOptions);

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        Directory.CreateDirectory(Path.Combine(project.ProjectPath, ".alta", "skills", "code-review"));
        await File.WriteAllTextAsync(
            Path.Combine(project.ProjectPath, ".alta", "skills", "code-review", "SKILL.md"),
            """
            ---
            name: code-review
            description: Review code for regressions.
            ---
            # Code Review

            Review code for regressions.
            """).ConfigureAwait(false);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var agentsRoot = Path.Combine(temp.Path, "agents");
        Directory.CreateDirectory(agentsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(agentsRoot, "coordinator.agent.md"),
            """
            ---
            name: coordinator
            description: Coordinates thread work.
            model: gpt-5.4
            ---
            Emit a schedule when coordination is required.
            """).ConfigureAwait(false);

        var backendId = new AgentBackendId(backendIdValue);
        var backendFactory = new AgentBackendFactory();
        var fakeBackend = new FakeBackend(backendId: backendId);
        backendFactory.Register(backendId.Value, () => fakeBackend);
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions,
            skillCatalog);

        var options = new WorkThreadExecutionOptions
        {
            BackendId = backendId,
            WorkingDirectory = project.ProjectPath,
            ProjectRoots = [project.ProjectPath],
            OnPermissionRequest = static (_, _) =>
                Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

        _ = await runtime.CreateProjectThreadAsync(
            project,
            options,
            "Codex Skills Thread").ConfigureAwait(false);

        Assert.IsNotNull(fakeBackend.LastCreateOptions);
        Assert.IsTrue(fakeBackend.LastCreateOptions.Tools is null ||
            !fakeBackend.LastCreateOptions.Tools.Any(static tool => tool.Spec.Name == "codealta.skills.activate"));
        Assert.IsNull(fakeBackend.LastCreateOptions.DeveloperInstructions);
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
                ProjectId = "project-1",
                Steps = ["Add planner", "Add builder"],
            }).ConfigureAwait(false);

        Assert.AreEqual(2, plan.ChildTaskIds.Count);
        var root = await taskRepository.GetAsync(plan.RootTaskId).ConfigureAwait(false);
        Assert.IsNotNull(root);
        Assert.AreEqual(CodeAlta.Persistence.TaskStatus.Pending, root.Status);

        var buildResult = await builder.CompleteTaskAsync(
            new BuilderExecutionRequest
            {
                TaskId = plan.ChildTaskIds[0],
                ProjectId = "project-1",
                VerificationSummary = "dotnet build and dotnet test passed.",
            }).ConfigureAwait(false);

        var completed = await taskRepository.GetAsync(buildResult.TaskId).ConfigureAwait(false);
        Assert.IsNotNull(completed);
        Assert.AreEqual(CodeAlta.Persistence.TaskStatus.Completed, completed.Status);
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
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_SendAsync_UsesCoordinatorDefaultsAndSanitizesSchedule()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

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
                runId.Value,
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
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var thread = await runtime.CreateProjectThreadAsync(
            project,
            new WorkThreadExecutionOptions
            {
                BackendId = new AgentBackendId("fake"),
                ProviderKey = "compat-provider",
                WorkingDirectory = project.ProjectPath,
                ProjectRoots = [project.ProjectPath],
                OnPermissionRequest = static (_, _) =>
                    Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            },
            title: "Review sqlitevec integration").ConfigureAwait(false);

        _ = await runtime.SendAsync(
                thread,
                new WorkThreadExecutionOptions
                {
                    BackendId = new AgentBackendId("fake"),
                    ProviderKey = "compat-provider",
                    WorkingDirectory = project.ProjectPath,
                    ProjectRoots = [project.ProjectPath],
                    OnPermissionRequest = static (_, _) =>
                        Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                },
                new AgentSendOptions { Input = AgentInput.Text("review the project") })
            .ConfigureAwait(false);

        Assert.IsNotNull(fakeBackend.LastCreateOptions);
        Assert.AreEqual("compat-provider", thread.ProviderKey);
        Assert.AreEqual("compat-provider", fakeBackend.LastCreateOptions.ProviderKey);
        Assert.AreEqual("gpt-5.4", fakeBackend.LastCreateOptions.Model);
        Assert.AreEqual(AgentReasoningEffort.High, fakeBackend.LastCreateOptions.ReasoningEffort);
        Assert.IsFalse(string.IsNullOrWhiteSpace(fakeBackend.LastCreateOptions.SystemMessage));
        StringAssert.Contains(fakeBackend.LastCreateOptions.SystemMessage, "You are CodeAlta");
        Assert.IsNotNull(fakeBackend.LastCreateOptions.DeveloperInstructions);
        StringAssert.Contains(fakeBackend.LastCreateOptions.DeveloperInstructions, "# Role");

        var history = await runtime.GetHistoryAsync(thread.ThreadId).ConfigureAwait(false);
        var assistant = history.OfType<AgentContentCompletedEvent>().Single();
        StringAssert.Contains(assistant.Content, "I’m going to coordinate this.");
        Assert.IsFalse(assistant.Content.Contains("codealta_schedule", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_HandoffAsync_EmitsHostEvents()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

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
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var source = await runtime.CreateProjectThreadAsync(
            project,
            CreateExecutionOptions(project.ProjectPath),
            "Source Thread").ConfigureAwait(false);
        var target = await runtime.CreateProjectThreadAsync(
            project,
            CreateExecutionOptions(project.ProjectPath),
            "Target Thread").ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var enumerator = runtime.StreamEventsAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        _ = await runtime.HandoffAsync(source, target, CreateExecutionOptions(project.ProjectPath), "Continue the review work.").ConfigureAwait(false);

        var received = new List<WorkThreadRuntimeEvent>();
        while (received.Count < 2 && await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            received.Add(enumerator.Current);
        }

        Assert.AreEqual(2, received.Count);
        Assert.IsTrue(received.OfType<WorkThreadHostEvent>().Any(x => x.ThreadId == source.ThreadId));
        Assert.IsTrue(received.OfType<WorkThreadHostEvent>().Any(x => x.ThreadId == target.ThreadId));
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_CompactAsync_InvokesSessionCompaction()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var agentsRoot = Path.Combine(temp.Path, "agents");
        Directory.CreateDirectory(agentsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(agentsRoot, "coordinator.agent.md"),
            """
            ---
            name: coordinator
            description: Coordinates thread work.
            ---
            Keep thread state compact.
            """).ConfigureAwait(false);

        var backendFactory = new AgentBackendFactory();
        var fakeBackend = new FakeBackend();
        backendFactory.Register("fake", () => fakeBackend);

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var thread = await runtime.CreateProjectThreadAsync(
            project,
            CreateExecutionOptions(project.ProjectPath),
            "Compact Thread").ConfigureAwait(false);

        await runtime.CompactAsync(thread, CreateExecutionOptions(project.ProjectPath)).ConfigureAwait(false);

        Assert.AreEqual(1, fakeBackend.CompactCallCount);
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_CompactAsync_ResumesAfterLiveShutdown()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var agentsRoot = Path.Combine(temp.Path, "agents");
        Directory.CreateDirectory(agentsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(agentsRoot, "coordinator.agent.md"),
            """
            ---
            name: coordinator
            description: Coordinates thread work.
            ---
            Keep thread state compact.
            """).ConfigureAwait(false);

        var backendFactory = new AgentBackendFactory();
        var fakeBackend = new FakeBackend();
        backendFactory.Register("fake", () => fakeBackend);

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var thread = await runtime.CreateProjectThreadAsync(
            project,
            CreateExecutionOptions(project.ProjectPath),
            "Compact Thread").ConfigureAwait(false);

        Assert.AreEqual(1, fakeBackend.ResumeSessionCount + fakeBackend.CreateSessionCount);

        fakeBackend.LastSession!.Publish(
            new AgentSessionUpdateEvent(
                fakeBackend.BackendId,
                thread.BackendSessionId,
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.Shutdown,
                "routine"));

        await runtime.CompactAsync(thread, CreateExecutionOptions(project.ProjectPath)).ConfigureAwait(false);

        Assert.AreEqual(1, fakeBackend.ResumeSessionCount);
        Assert.AreEqual(1, fakeBackend.CompactCallCount);
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_CompactAsync_EmitsManualLifecycleHostEventsWhenOutcomeIsSynchronous()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var agentsRoot = Path.Combine(temp.Path, "agents");
        Directory.CreateDirectory(agentsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(agentsRoot, "coordinator.agent.md"),
            """
            ---
            name: coordinator
            description: Coordinates thread work.
            ---
            Keep thread state compact.
            """).ConfigureAwait(false);

        var backendFactory = new AgentBackendFactory();
        var fakeBackend = new FakeBackend
        {
            CompactionOutcome = new AgentCompactionOutcome(
                Success: true,
                Message: "Manual compaction completed (1200 tokens and 3 messages removed).",
                MessagesRemoved: 3,
                TokensRemoved: 1200)
        };
        backendFactory.Register("fake", () => fakeBackend);

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var thread = await runtime.CreateProjectThreadAsync(
            project,
            CreateExecutionOptions(project.ProjectPath),
            "Compact Thread").ConfigureAwait(false);

        await using var enumerator = runtime.StreamEventsAsync().GetAsyncEnumerator();

        await runtime.CompactAsync(thread, CreateExecutionOptions(project.ProjectPath)).ConfigureAwait(false);

        var received = new List<WorkThreadRuntimeEvent>();
        while (received.Count < 2 && await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            received.Add(enumerator.Current);
        }

        Assert.AreEqual(2, received.Count);
        Assert.AreEqual(AgentSessionUpdateKind.CompactionStarted, Assert.IsInstanceOfType<WorkThreadHostEvent>(received[0]).Kind);
        Assert.AreEqual(AgentSessionUpdateKind.CompactionCompleted, Assert.IsInstanceOfType<WorkThreadHostEvent>(received[1]).Kind);
        Assert.AreEqual("Manual compaction completed (1200 tokens and 3 messages removed).", ((WorkThreadHostEvent)received[1]).Message);
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_CreateInternalThreadAsync_PersistsRecoverableChildThread()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var agentsRoot = Path.Combine(temp.Path, "agents");
        Directory.CreateDirectory(agentsRoot);
        await File.WriteAllTextAsync(
            Path.Combine(agentsRoot, "coordinator.agent.md"),
            """
            ---
            name: coordinator
            description: Coordinates thread work.
            ---
            Keep delegated work focused and concise.
            """).ConfigureAwait(false);

        var backendFactory = new AgentBackendFactory();
        backendFactory.Register("fake", static () => new FakeBackend());

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var parent = await runtime.CreateProjectThreadAsync(
            project,
            CreateExecutionOptions(project.ProjectPath),
            "Parent Thread").ConfigureAwait(false);

        var child = await runtime.CreateInternalThreadAsync(
            parent,
            project,
            CreateExecutionOptions(project.ProjectPath),
            "Internal Reviewer").ConfigureAwait(false);

        Assert.AreEqual(WorkThreadKind.InternalThread, child.Kind);
        Assert.AreEqual(parent.ThreadId, child.ParentThreadId);
        Assert.AreEqual(project.Id, child.ProjectRef);

        var internalThreads = await threadCatalog.LoadInternalAsync().ConfigureAwait(false);
        Assert.AreEqual(1, internalThreads.Count);
        Assert.AreEqual(child.ThreadId, internalThreads[0].ThreadId);

        var recoverable = await runtime.ListRecoverableThreadsAsync().ConfigureAwait(false);
        Assert.IsTrue(recoverable.Any(thread => string.Equals(thread.ThreadId, child.ThreadId, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_ListRecoverableThreadsAsync_MatchesProjectsWithLongPathPrefix()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(
            AgentBackendIds.Codex.Value,
            () => new FakeBackend(
                backendId: AgentBackendIds.Codex,
                sessions:
                [
                    new AgentSessionMetadata(
                        "session-1",
                        DateTimeOffset.UtcNow.AddMinutes(-5),
                        DateTimeOffset.UtcNow,
                        "Review the repo",
                        Context: null,
                        WorkspacePath: $@"\\?\{project.ProjectPath}")
                ]));

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var recoverable = await runtime.ListRecoverableThreadsAsync().ConfigureAwait(false);

        Assert.AreEqual(1, recoverable.Count);
        Assert.AreEqual(WorkThreadKind.ProjectThread, recoverable[0].Kind);
        Assert.AreEqual(project.Id, recoverable[0].ProjectRef);
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_ListRecoverableThreadsAsync_SetsStartedAtFromSessionMetadata()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-12);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(
            AgentBackendIds.Copilot.Value,
            () => new FakeBackend(
                backendId: AgentBackendIds.Copilot,
                sessions:
                [
                    new AgentSessionMetadata(
                        "session-1",
                        createdAt,
                        updatedAt,
                        "Recovered review",
                        Context: new AgentSessionContext(project.ProjectPath, project.ProjectPath, "repo-main", "main"),
                        WorkspacePath: null)
                ]));

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var recoverable = await runtime.ListRecoverableThreadsAsync().ConfigureAwait(false);

        Assert.AreEqual(1, recoverable.Count);
        Assert.AreEqual(createdAt, recoverable[0].StartedAt);
        Assert.AreEqual(updatedAt, recoverable[0].LastActiveAt);
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_ListRecoverableThreadsAsync_IncludesOpenAIResponsesSessions()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(
            AgentBackendIds.OpenAIResponses.Value,
            () => new FakeBackend(
                backendId: AgentBackendIds.OpenAIResponses,
                sessions:
                [
                    new AgentSessionMetadata(
                        "session-1",
                        DateTimeOffset.UtcNow.AddMinutes(-5),
                        DateTimeOffset.UtcNow,
                        "Review the repo",
                        Context: new AgentSessionContext(project.ProjectPath, project.ProjectPath, "repo-main", "main"),
                        WorkspacePath: project.ProjectPath)
                ]));

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var recoverable = await runtime.ListRecoverableThreadsAsync().ConfigureAwait(false);

        Assert.AreEqual(1, recoverable.Count);
        Assert.AreEqual(AgentBackendIds.OpenAIResponses.Value, recoverable[0].BackendId);
        Assert.AreEqual(WorkThreadKind.ProjectThread, recoverable[0].Kind);
        Assert.AreEqual(project.Id, recoverable[0].ProjectRef);
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_ListRecoverableThreadsAsync_SuppressesNativeSessionWhenCodeAltaSessionExists()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(
            "openai",
            () => new FakeBackend(
                backendId: new AgentBackendId("openai"),
                sessions:
                [
                    new AgentSessionMetadata(
                        "shared-session-1",
                        createdAt,
                        updatedAt,
                        "CodeAlta local copy",
                        Context: new AgentSessionContext(project.ProjectPath, project.ProjectPath, "repo-main", "main"),
                        WorkspacePath: project.ProjectPath,
                        ProtocolFamily: "openai-responses",
                        ProviderKey: "openai")
                ]));
        backendFactory.Register(
            AgentBackendIds.Codex.Value,
            () => new FakeBackend(
                backendId: AgentBackendIds.Codex,
                sessions:
                [
                    new AgentSessionMetadata(
                        "shared-session-1",
                        createdAt,
                        updatedAt.AddSeconds(30),
                        "Native Codex copy",
                        Context: new AgentSessionContext(project.ProjectPath, project.ProjectPath, "repo-main", "main"),
                        WorkspacePath: project.ProjectPath)
                ]));

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var recoverable = await runtime.ListRecoverableThreadsAsync().ConfigureAwait(false);

        Assert.AreEqual(1, recoverable.Count);
        Assert.AreEqual("openai", recoverable[0].BackendId);
        Assert.AreEqual("openai:shared-session-1", recoverable[0].ThreadId);
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_ListRecoverableThreadsAsync_UsesNativeLocalMirrorBeforeNativeSession()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-12);
        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(catalogOptions.GlobalRoot));
        await store.UpsertSessionAsync(new LocalAgentSessionSummary
        {
            SessionId = "codex-session-1",
            BackendId = AgentBackendIds.Codex,
            ProtocolFamily = AgentBackendIds.Codex.Value,
            ProviderKey = AgentBackendIds.Codex.Value,
            WorkingDirectory = project.ProjectPath,
            Title = "Mirrored Codex copy",
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddMinutes(2),
        }).ConfigureAwait(false);

        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(
            AgentBackendIds.Codex.Value,
            () => new FakeBackend(
                backendId: AgentBackendIds.Codex,
                sessions:
                [
                    new AgentSessionMetadata(
                        "codex-session-1",
                        createdAt,
                        createdAt.AddMinutes(3),
                        "Native Codex copy",
                        Context: new AgentSessionContext(project.ProjectPath, project.ProjectPath, "repo-main", "main"),
                        WorkspacePath: project.ProjectPath)
                ]));

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var recoverable = await runtime.ListRecoverableThreadsAsync().ConfigureAwait(false);

        Assert.AreEqual(1, recoverable.Count);
        Assert.AreEqual(AgentBackendIds.Codex.Value, recoverable[0].BackendId);
        Assert.AreEqual("Mirrored Codex copy", recoverable[0].Title);
    }

    [TestMethod]
    public async Task WorkThreadRuntimeService_ListRecoverableThreadsAsync_ContinuesWhenBackendIsUnavailable()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var threadCatalog = new WorkThreadCatalog(catalogOptions);
        var roleStore = new RoleProfileStore();
        var instructionProvider = new AgentInstructionTemplateProvider();

        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo-main",
            DisplayName = "Main Repo",
            ProjectPath = Path.Combine(temp.Path, "repo-main"),
            DefaultBranch = "main",
        };
        Directory.CreateDirectory(project.ProjectPath);
        await projectCatalog.SaveAsync(project).ConfigureAwait(false);

        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(
            AgentBackendIds.Codex.Value,
            static () => throw new FileNotFoundException("codex executable was not found"));
        backendFactory.Register(
            AgentBackendIds.Copilot.Value,
            () => new FakeBackend(
                backendId: AgentBackendIds.Copilot,
                sessions:
                [
                    new AgentSessionMetadata(
                        "session-1",
                        DateTimeOffset.UtcNow.AddMinutes(-3),
                        DateTimeOffset.UtcNow,
                        "Recovered review",
                        Context: new AgentSessionContext(project.ProjectPath, project.ProjectPath, "repo-main", "main"),
                        WorkspacePath: null)
                ]));

        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        await using var hub = new AgentHub(backendFactory, repository);
        await using var runtime = new WorkThreadRuntimeService(
            hub,
            projectCatalog,
            threadCatalog,
            roleStore,
            instructionProvider,
            catalogOptions);

        var recoverable = await runtime.ListRecoverableThreadsAsync().ConfigureAwait(false);

        Assert.AreEqual(1, recoverable.Count);
        Assert.AreEqual(AgentBackendIds.Copilot.Value, recoverable[0].BackendId);
        Assert.AreEqual(project.Id, recoverable[0].ProjectRef);
    }

    private static WorkThreadExecutionOptions CreateExecutionOptions(string workingDirectory)
    {
        return new WorkThreadExecutionOptions
        {
            BackendId = new AgentBackendId("fake"),
            WorkingDirectory = workingDirectory,
            ProjectRoots = [workingDirectory],
            OnPermissionRequest = static (_, _) =>
                Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };
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
        private readonly AgentBackendId _backendId;
        private readonly IReadOnlyList<AgentModelInfo> _models;
        private readonly IReadOnlyList<AgentSessionMetadata> _sessions;
        private readonly Func<AgentBackendId, string, AgentRunId, IReadOnlyList<AgentEvent>>? _sendEventFactory;

        public FakeBackend(
            AgentBackendId? backendId = null,
            IReadOnlyList<AgentModelInfo>? models = null,
            IReadOnlyList<AgentSessionMetadata>? sessions = null,
            Func<AgentBackendId, string, AgentRunId, IReadOnlyList<AgentEvent>>? sendEventFactory = null)
        {
            _backendId = backendId ?? new AgentBackendId("fake");
            _models = models ?? [];
            _sessions = sessions ?? [];
            _sendEventFactory = sendEventFactory;
        }

        public AgentBackendId BackendId => _backendId;

        public string DisplayName => "Fake";

        public AgentSessionCreateOptions? LastCreateOptions { get; private set; }

        public int CompactCallCount { get; private set; }

        public int CreateSessionCount { get; private set; }

        public int ResumeSessionCount { get; private set; }

        public AgentCompactionOutcome? CompactionOutcome { get; set; }

        public FakeSession? LastSession { get; private set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_models);

        public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_sessions);

        public Task<IAgentSession> CreateSessionAsync(
            AgentSessionCreateOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            LastCreateOptions = options;
            CreateSessionCount++;
            LastSession = new FakeSession(this);
            return Task.FromResult<IAgentSession>(LastSession);
        }

        public Task<IAgentSession> ResumeSessionAsync(
            string sessionId,
            AgentSessionResumeOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            ResumeSessionCount++;
            LastSession = new FakeSession(this, sessionId);
            return Task.FromResult<IAgentSession>(LastSession);
        }

        public sealed class FakeSession : IAgentSession, IAgentCompactionOutcomeProvider
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

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
                => Task.FromResult(options.ExpectedRunId ?? new AgentRunId("fake-run-1"));

            public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task CompactAsync(CancellationToken cancellationToken = default)
            {
                _backend.CompactCallCount++;
                return Task.CompletedTask;
            }

            public Task<AgentCompactionOutcome?> CompactWithOutcomeAsync(CancellationToken cancellationToken = default)
            {
                _backend.CompactCallCount++;
                return Task.FromResult(_backend.CompactionOutcome);
            }

            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<AgentEvent>>(_events.ToArray());

            public void Publish(AgentEvent @event)
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

    private sealed class DisposableAction : IDisposable
    {
        private readonly Action _dispose;
        private bool _disposed;

        private DisposableAction(Action dispose)
        {
            _dispose = dispose;
        }

        public static IDisposable Create(Action dispose) => new DisposableAction(dispose);

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
            }
        }
    }
}
