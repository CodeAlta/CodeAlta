using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.LiveTool;
using CodeAlta.Tui.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionExecutionOptionsFactoryTests
{
    [TestMethod]
    public void BuildPreferredExecutionOptions_CopiesInputRoots()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        var factory = CreateFactory(temp.Path, project);
        var roots = new List<string> { project.ProjectPath };
        var options = factory.BuildPreferredExecutionOptions(ModelProviderIds.Codex, project.ProjectPath, roots, project);

        roots[0] = Path.Combine(temp.Path, "unrelated");

        Assert.AreEqual(project.ProjectPath, options.ProjectRoots.Single());
    }

    [TestMethod]
    public async Task BuildExecutionOptions_CapturesDescriptorIdentityAndWorkingDirectory()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        ShellSessionStateCoordinator? state = null;
        var factory = CreateFactory(temp.Path, project, captureState: value => state = value);
        var session = CreateSession("session-a", "codex", temp.Path);
        session.Kind = SessionViewKind.ProjectSession;
        session.ProjectRef = project.Id;
        var tab = state!.EnsureSessionTab(session);
        tab.ModelId = "captured-model";
        tab.ReasoningEffort = AgentReasoningEffort.High;
        tab.AgentPromptId = " captured-prompt ";
        state.SelectedProjectId = "unrelated-selection";
        var options = factory.BuildExecutionOptions(session, tab);
        session.SessionId = "session-b";
        session.ProjectRef = "project-b";
        session.WorkingDirectory = Path.Combine(temp.Path, "unrelated");
        project.ProjectPath = session.WorkingDirectory;
        tab.ProviderId = new ModelProviderId("different-provider");
        tab.ModelId = "different-model";
        tab.ReasoningEffort = AgentReasoningEffort.Low;
        tab.AgentPromptId = "different-prompt";
        state.SelectedProjectId = "another-selection";

        var status = await InvokeToolStatusRecordAsync(options).ConfigureAwait(false);

        Assert.AreEqual(options.WorkingDirectory, status.GetProperty("cwd").GetString());
        Assert.AreEqual("session-a", status.GetProperty("caller").GetProperty("sourceSessionId").GetString());
        Assert.AreEqual("project-a", status.GetProperty("caller").GetProperty("sourceProjectId").GetString());
        Assert.AreEqual("codex", options.ProviderKey);
        Assert.AreEqual("captured-model", options.Model);
        Assert.AreEqual(AgentReasoningEffort.High, options.ReasoningEffort);
        Assert.AreEqual("captured-prompt", options.AgentPromptId);
    }

    [TestMethod]
    public async Task BuildPreferredExecutionOptions_GlobalScopeDoesNotInheritSelectedProjectForAltaTool()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        Directory.CreateDirectory(project.ProjectPath);
        var factory = CreateFactory(temp.Path, project);

        var options = factory.BuildPreferredExecutionOptions(ModelProviderIds.Codex, temp.Path, [], null);

        var caller = await InvokeToolStatusAsync(options).ConfigureAwait(false);
        Assert.AreEqual("agent", caller.GetProperty("kind").GetString());
        Assert.IsFalse(caller.TryGetProperty("sourceProjectId", out _), "Global coordinator tools must not be project-scoped just because a project is selected in the UI.");
    }

    [TestMethod]
    public async Task BuildPreferredExecutionOptions_ProjectScopeUsesExplicitProjectForAltaTool()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        Directory.CreateDirectory(project.ProjectPath);
        var unrelated = CreateProject("unrelated", Path.Combine(temp.Path, "unrelated"));
        var factory = CreateFactory(temp.Path, unrelated);

        var options = factory.BuildPreferredExecutionOptions(ModelProviderIds.Codex, project.ProjectPath, [project.ProjectPath], project);

        var caller = await InvokeToolStatusAsync(options).ConfigureAwait(false);
        Assert.AreEqual(project.Id, caller.GetProperty("sourceProjectId").GetString());
    }

    [TestMethod]
    public async Task BuildPreferredExecutionOptions_UsesDeferredSourceSessionProviderForAltaTool()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        Directory.CreateDirectory(project.ProjectPath);
        ShellSessionStateCoordinator? state = null;
        var factory = CreateFactory(temp.Path, project, captureState: value => state = value);
        string? createdSessionId = null;

        var options = factory.BuildPreferredExecutionOptions(
            ModelProviderIds.Codex,
            project.ProjectPath,
            [project.ProjectPath],
            project,
            () => createdSessionId);
        var beforeCreation = await InvokeToolStatusAsync(options).ConfigureAwait(false);
        Assert.IsFalse(beforeCreation.TryGetProperty("sourceSessionId", out _), "Canonical identity is deliberately unbound until creation completes.");
        createdSessionId = "canonical-session-id";
        state!.SelectedProjectId = "unrelated-selection";
        project.Id = "mutated-project";
        project.ProjectPath = temp.Path;

        var caller = await InvokeToolStatusAsync(options).ConfigureAwait(false);
        Assert.AreEqual(createdSessionId, caller.GetProperty("sourceSessionId").GetString());
        Assert.AreEqual("project-a", caller.GetProperty("sourceProjectId").GetString());
    }

    [TestMethod]
    public void BuildPreferredExecutionOptions_UsesCanonicalProviderStateForDraftSessionCreation()
    {
        using var temp = TestTempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var uiDispatcher = new InlineUiDispatcher();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(catalogOptions),
            new SessionViewCatalog(catalogOptions),
            uiDispatcher,
            new ShellStateStore(uiDispatcher));
        sessionState.ApplyInitialCatalogState(new ShellSessionStateCoordinator.InitialCatalogState([], [], new SessionViewViewState()));
        var selection = new SessionSelectionContext(
            sessionState,
            static (_, _) => Task.CompletedTask,
            static _ => false);
        var providerState = new ModelProviderState(ModelProviderIds.Codex, "Codex")
        {
            Availability = ModelProviderAvailability.Ready,
            SelectedModelId = "gpt-selected",
            SelectedReasoningEffort = AgentReasoningEffort.High,
        };
        providerState.Models.Add(new AgentModelInfo(
            "gpt-wrong",
            SupportedReasoningEfforts: [AgentReasoningEffort.Low]));
        providerState.Models.Add(new AgentModelInfo(
            "gpt-selected",
            SupportedReasoningEfforts: [AgentReasoningEffort.High]));
        var factory = new SessionExecutionOptionsFactory(
            catalogOptions,
            new Dictionary<string, ModelProviderState>(StringComparer.Ordinal)
            {
                [ModelProviderIds.Codex.Value] = providerState,
            },
            selection,
            new SessionPermissionRequestCoordinator(selection, CreateCommandContext(uiDispatcher), uiDispatcher),
            new SessionUserInputRequestCoordinator(selection, CreateCommandContext(uiDispatcher)),
            () => " preferred-prompt ");

        var options = factory.BuildPreferredExecutionOptions(ModelProviderIds.Codex, temp.Path, [], null);
        var portable = SessionExecutionPolicy.CapturePreferred(ModelProviderIds.Codex, temp.Path, [], null,
            providerState.SelectedModelId, providerState.SelectedReasoningEffort, " preferred-prompt ");
        providerState.SelectedModelId = "changed-after-capture";
        providerState.SelectedReasoningEffort = AgentReasoningEffort.Low;

        Assert.AreEqual("gpt-selected", options.Model);
        Assert.AreEqual(AgentReasoningEffort.High, options.ReasoningEffort);
        Assert.AreEqual(portable.ProviderId, options.ProviderId);
        Assert.AreEqual(portable.WorkingDirectory, options.WorkingDirectory);
        Assert.AreEqual(portable.Model, options.Model);
        Assert.AreEqual(portable.ReasoningEffort, options.ReasoningEffort);
        Assert.AreEqual(portable.AgentPromptId, options.AgentPromptId);
    }

    [TestMethod]
    public void BuildPreferredExecutionOptions_AllowsProviderBeforeStateCatalogSyncs()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        Directory.CreateDirectory(project.ProjectPath);
        var factory = CreateFactory(temp.Path, project);
        var ProviderId = new ModelProviderId("gemini");

        var options = factory.BuildPreferredExecutionOptions(new ModelProviderId(ProviderId.Value), temp.Path, [], null);

        Assert.AreEqual(ProviderId.Value, options.ProviderId.Value);
        Assert.AreEqual("gemini", options.ProviderKey);
        Assert.IsNull(options.Model);
        Assert.IsNull(options.ReasoningEffort);
    }

    [TestMethod]
    public void BuildExecutionOptions_UsesOpenTabProviderSelection()
    {
        using var temp = TestTempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var uiDispatcher = new InlineUiDispatcher();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(catalogOptions),
            new SessionViewCatalog(catalogOptions),
            uiDispatcher,
            new ShellStateStore(uiDispatcher));
        var session = CreateSession("session-1", "openai", temp.Path);
        sessionState.ApplyInitialCatalogState(new ShellSessionStateCoordinator.InitialCatalogState([], [session], new SessionViewViewState()));
        var tab = sessionState.EnsureSessionTab(session);
        Assert.IsNotNull(tab);
        tab.ProviderId = new ModelProviderId("anthropic");
        tab.ModelId = "claude-sonnet-4";
        tab.ReasoningEffort = AgentReasoningEffort.High;
        session.ProviderId = "openai";
        session.ProviderKey = "openai";
        var selection = new SessionSelectionContext(
            sessionState,
            static (_, _) => Task.CompletedTask,
            static _ => false);
        var commandContext = CreateCommandContext(uiDispatcher);
        var factory = new SessionExecutionOptionsFactory(
            catalogOptions,
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase),
            selection,
            new SessionPermissionRequestCoordinator(selection, commandContext, uiDispatcher),
            new SessionUserInputRequestCoordinator(selection, commandContext));

        var options = factory.BuildExecutionOptions(session, tab);

        Assert.AreEqual("anthropic", options.ProviderId.Value);
        Assert.AreEqual("anthropic", options.ProviderKey);
        Assert.AreEqual("claude-sonnet-4", options.Model);
        Assert.AreEqual(AgentReasoningEffort.High, options.ReasoningEffort);
    }

    [TestMethod]
    public async Task BuildPreferredExecutionOptions_RoutesCodexPermissionRequestsThroughCoordinator()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        Directory.CreateDirectory(project.ProjectPath);
        using var cancellation = new CancellationTokenSource();
        var uiDispatcher = new CancelingPermissionUiDispatcher(cancellation.Cancel);
        var factory = CreateFactory(temp.Path, project, uiDispatcher, autoApprove: false);

        var options = factory.BuildPreferredExecutionOptions(ModelProviderIds.Codex, temp.Path, [], null);

        var decision = await options.OnPermissionRequest(
                CreatePermissionRequest("new-session-id"),
                cancellation.Token)
            .ConfigureAwait(false);

        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, decision.Kind);
        Assert.IsTrue(uiDispatcher.PermissionDialogInvokeCount > 0, "Permission requests must reach the coordinator when AutoApprove is disabled.");
    }

    [TestMethod]
    public async Task BuildExecutionOptions_RoutesCodexPermissionRequestsThroughCoordinator()
    {
        using var temp = TestTempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        using var cancellation = new CancellationTokenSource();
        var uiDispatcher = new CancelingPermissionUiDispatcher(cancellation.Cancel);
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(catalogOptions),
            new SessionViewCatalog(catalogOptions),
            uiDispatcher,
            new ShellStateStore(uiDispatcher));
        var session = CreateSession("session-1", ModelProviderIds.Codex.Value, temp.Path);
        sessionState.ApplyInitialCatalogState(new ShellSessionStateCoordinator.InitialCatalogState([], [session], new SessionViewViewState()));
        var tab = sessionState.EnsureSessionTab(session);
        Assert.IsNotNull(tab);
        var selection = new SessionSelectionContext(
            sessionState,
            static (_, _) => Task.CompletedTask,
            static _ => false);
        var commandContext = CreateCommandContext(uiDispatcher, autoApprove: false);
        var factory = new SessionExecutionOptionsFactory(
            catalogOptions,
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase),
            selection,
            new SessionPermissionRequestCoordinator(selection, commandContext, uiDispatcher),
            new SessionUserInputRequestCoordinator(selection, commandContext));

        var options = factory.BuildExecutionOptions(session, tab);

        var decision = await options.OnPermissionRequest(
                CreatePermissionRequest(session.SessionId),
                cancellation.Token)
            .ConfigureAwait(false);

        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, decision.Kind);
        Assert.IsTrue(uiDispatcher.PermissionDialogInvokeCount > 0, "Codex sessions must not bypass the permission coordinator.");
    }

    [TestMethod]
    public async Task BuildPreferredExecutionOptions_AddsAltaToolForAnyProviderWhenServicesAreAvailable()
    {
        using var temp = TestTempDirectory.Create();
        var project = CreateProject("project-a", Path.Combine(temp.Path, "project-a"));
        Directory.CreateDirectory(project.ProjectPath);
        var factory = CreateFactory(temp.Path, project);
        var providerId = new ModelProviderId("gemma4-12b");

        var options = factory.BuildPreferredExecutionOptions(providerId, project.ProjectPath, [project.ProjectPath], project);

        Assert.AreEqual(providerId.Value, options.ProviderId.Value);
        var caller = await InvokeToolStatusAsync(options).ConfigureAwait(false);
        Assert.AreEqual(project.Id, caller.GetProperty("sourceProjectId").GetString());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BuildExecutionOptions_GlobalAndMissingProjectIgnoreSelection(bool global)
    {
        using var temp = TestTempDirectory.Create();
        var selected = CreateProject("selected", Path.Combine(temp.Path, "selected"));
        ShellSessionStateCoordinator? state = null;
        var factory = CreateFactory(temp.Path, selected, captureState: value => state = value);
        var session = CreateSession("session-a", "unsynced-provider", Path.Combine(temp.Path, "stored"));
        session.Kind = global ? SessionViewKind.GlobalSession : SessionViewKind.ProjectSession;
        session.ProjectRef = global ? selected.Id : "missing-project";
        session.AgentPromptId = " stored-prompt ";
        var tab = state!.EnsureSessionTab(session);
        tab.ProviderId = default;

        var options = factory.BuildExecutionOptions(session, tab);
        var status = await InvokeToolStatusRecordAsync(options).ConfigureAwait(false);

        Assert.AreEqual(global ? temp.Path : session.WorkingDirectory, options.WorkingDirectory);
        Assert.AreEqual(options.WorkingDirectory, status.GetProperty("cwd").GetString());
        Assert.AreEqual(0, options.ProjectRoots.Count);
        Assert.AreEqual("unsynced-provider", options.ProviderKey);
        Assert.AreEqual("stored-prompt", options.AgentPromptId);
        var caller = status.GetProperty("caller");
        if (global)
        {
            Assert.IsFalse(caller.TryGetProperty("sourceProjectId", out _));
        }
        else
        {
            Assert.AreEqual("missing-project", caller.GetProperty("sourceProjectId").GetString());
        }
    }

    [TestMethod]
    public async Task BuildExecutionOptions_InteractionAssociationAndCancellationStayCaptured()
    {
        using var temp = TestTempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var activeCancellation = cancellation;
        var dispatcher = new CancelingPermissionUiDispatcher(() => activeCancellation.Cancel());
        var rendered = new List<OpenSessionState>();
        ShellSessionStateCoordinator? state = null;
        var factory = CreateFactory(temp.Path, CreateProject("a", temp.Path), dispatcher, autoApprove: false,
            captureState: value => state = value, renderInteraction: rendered.Add);
        var session = CreateSession("original", "codex", temp.Path);
        var tab = state!.EnsureSessionTab(session);
        var other = state.EnsureSessionTab(CreateSession("other", "codex", temp.Path));
        var options = factory.BuildExecutionOptions(session, tab);
        session.SessionId = "other";
        state.SelectedSessionId = "other";

        // Empty request identity retains the captured permission fallback, not the changed descriptor.
        var decision = await options.OnPermissionRequest(CreatePermissionRequest(""), cancellation.Token).ConfigureAwait(false);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, decision.Kind);
        Assert.AreSame(tab, rendered.Single());
        Assert.AreEqual(1, dispatcher.PermissionDialogInvokeCount);

        // Existing permission semantics deliberately prefer an explicit provider request identity.
        rendered.Clear();
        using var secondCancellation = new CancellationTokenSource();
        activeCancellation = secondCancellation;
        var explicitDecision = await options.OnPermissionRequest(CreatePermissionRequest("other"), secondCancellation.Token).ConfigureAwait(false);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, explicitDecision.Kind);
        Assert.AreSame(other, rendered.Single());
        rendered.Clear();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => options.OnPermissionRequest(
            CreatePermissionRequest("other"), secondCancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(0, rendered.Count);

        Assert.IsNotNull(options.OnUserInputRequest);
        var input = new AgentUserInputRequest(ModelProviderIds.Codex, "other", DateTimeOffset.UtcNow, null, "input",
            new AgentUserInputForm([new AgentUserInputPrompt("question", "Continue?")]));
        var response = await options.OnUserInputRequest(input, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("", response.Answers["question"], "Preserve immediate non-autoapprove responses.");
        Assert.AreSame(tab, rendered.Single());
        Assert.AreNotSame(other, rendered.Single());
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => options.OnUserInputRequest(input, cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(1, rendered.Count);
    }

    [TestMethod]
    public async Task BuildPreferredExecutionOptions_InteractionsKeepTransientFallbackAfterCanonicalBinding()
    {
        using var temp = TestTempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new CancelingPermissionUiDispatcher(cancellation.Cancel);
        var rendered = new List<OpenSessionState>();
        ShellSessionStateCoordinator? state = null;
        var factory = CreateFactory(temp.Path, CreateProject("a", temp.Path), dispatcher, autoApprove: false,
            captureState: value => state = value, renderInteraction: rendered.Add);
        var transientKey = SessionExecutionOptionsFactory.CreateTransientSessionKey(ModelProviderIds.Codex, temp.Path);
        var transientTab = state!.EnsureSessionTab(CreateSession(transientKey, "codex", temp.Path));
        state.EnsureSessionTab(CreateSession("canonical", "codex", temp.Path));
        string? canonicalId = null;
        var options = factory.BuildPreferredExecutionOptions(ModelProviderIds.Codex, temp.Path, [], null, () => canonicalId);
        canonicalId = "canonical";
        state.SelectedSessionId = canonicalId;

        var permission = await options.OnPermissionRequest(CreatePermissionRequest(""), cancellation.Token).ConfigureAwait(false);
        Assert.AreEqual(AgentPermissionDecisionKind.Cancel, permission.Kind);
        Assert.AreSame(transientTab, rendered.Single());
        rendered.Clear();
        Assert.IsNotNull(options.OnUserInputRequest);
        var request = new AgentUserInputRequest(ModelProviderIds.Codex, canonicalId, DateTimeOffset.UtcNow, null, "input",
            new AgentUserInputForm([new AgentUserInputPrompt("question", "Continue?")]));
        var response = await options.OnUserInputRequest(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("", response.Answers["question"]);
        Assert.AreSame(transientTab, rendered.Single());
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => options.OnUserInputRequest(request, cancellation.Token)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CreationCoordinator_CapturesProjectBeforeStatusChangesSelection(bool global)
    {
        using var temp = TestTempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = temp.Path };
        var original = CreateProject("original", Path.Combine(temp.Path, "original"));
        var unrelated = CreateProject("unrelated", Path.Combine(temp.Path, "unrelated"));
        var selected = original;
        // Constructor-only runtime dependencies: no providers, catalog reads, instructions or sessions.
        // The options callback unconditionally stops before any runtime creation method can execute.
        await using var registry = new ModelProviderRegistry();
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = new SessionRuntimeService(hub, new UnexpectedSessionCatalog(),
            new ProjectCatalog(catalogOptions), new SessionViewCatalog(catalogOptions),
            new AgentInstructionTemplateProvider(catalogOptions: catalogOptions, contentLocator: new UnexpectedPromptLocator()),
            catalogOptions, new SkillCatalog([new UnexpectedSkillRootProvider()]));
        var factory = CreateFactory(temp.Path, unrelated);
        SessionExecutionOptions? capturedOptions = null;
        string? lastStatus = null;
        var coordinator = new SessionCreationCoordinator(runtime, catalogOptions,
            () => ModelProviderIds.Codex, () => selected, () => throw new AssertFailedException("Unexpected selection read."),
            () => "draft title", _ => throw new AssertFailedException("Unexpected persistence."),
            _ => throw new AssertFailedException("Unexpected upsert."),
            (provider, directory, roots, project, sourceSessionId) =>
            {
                capturedOptions = factory.BuildPreferredExecutionOptions(provider, directory, roots, project, sourceSessionId);
                throw new InvalidOperationException("stopped-before-runtime");
            },
            (_, _, _, _) => throw new AssertFailedException("Unexpected preference persistence."),
            _ => throw new AssertFailedException("Unexpected session registration."),
            () => throw new AssertFailedException("Unexpected draft clear."),
            (message, _, _) => { selected = unrelated; lastStatus = message; });

        var result = global
            ? await coordinator.CreateGlobalSessionAsync().ConfigureAwait(false)
            : await coordinator.CreateProjectSessionAsync().ConfigureAwait(false);

        Assert.IsNull(result);
        Assert.IsNotNull(lastStatus);
        StringAssert.Contains(lastStatus, "stopped-before-runtime");
        Assert.IsNotNull(capturedOptions);
        var status = await InvokeToolStatusRecordAsync(capturedOptions).ConfigureAwait(false);
        Assert.AreEqual(global ? temp.Path : original.ProjectPath, capturedOptions.WorkingDirectory);
        Assert.AreEqual(capturedOptions.WorkingDirectory, status.GetProperty("cwd").GetString());
        if (global)
        {
            Assert.IsFalse(status.GetProperty("caller").TryGetProperty("sourceProjectId", out _));
        }
        else
        {
            Assert.AreEqual(original.Id, status.GetProperty("caller").GetProperty("sourceProjectId").GetString());
        }
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(temp.Path).Any(), "Assembly must not discover or persist runtime state.");
    }

    private static SessionExecutionOptionsFactory CreateFactory(
        string globalRoot,
        ProjectDescriptor selectedProject,
        IUiDispatcher? uiDispatcher = null,
        bool autoApprove = true,
        Action<ShellSessionStateCoordinator>? captureState = null,
        Action<OpenSessionState>? renderInteraction = null)
    {
        var catalogOptions = new CatalogOptions { GlobalRoot = globalRoot };
        uiDispatcher ??= new InlineUiDispatcher();
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(catalogOptions),
            new SessionViewCatalog(catalogOptions),
            uiDispatcher,
            new ShellStateStore(uiDispatcher));
        sessionState.ApplyInitialCatalogState(new ShellSessionStateCoordinator.InitialCatalogState(
            [selectedProject],
            [],
            new SessionViewViewState()));
        sessionState.GlobalScopeSelected = false;
        sessionState.SelectedProjectId = selectedProject.Id;
        captureState?.Invoke(sessionState);

        var selection = new SessionSelectionContext(
            sessionState,
            static (_, _) => Task.CompletedTask,
            static _ => false);
        var services = new AltaServiceCollection().Add(catalogOptions);
        var commandContext = CreateCommandContext(uiDispatcher, autoApprove, renderInteraction);
        return new SessionExecutionOptionsFactory(
            catalogOptions,
            new Dictionary<string, ModelProviderState>(StringComparer.Ordinal)
            {
                [ModelProviderIds.Codex.Value] = new ModelProviderState(ModelProviderIds.Codex, "Codex")
                {
                    Availability = ModelProviderAvailability.Ready,
                    SelectedModelId = "gpt-test",
                },
            },
            selection,
            new SessionPermissionRequestCoordinator(selection, commandContext, uiDispatcher),
            new SessionUserInputRequestCoordinator(selection, commandContext),
            null,
            services);
    }

    private static ShellSessionCommandContext CreateCommandContext(
        IUiDispatcher uiDispatcher, bool autoApprove = true, Action<OpenSessionState>? renderInteraction = null)
        => new(
            new DelegatingSessionLifecycleCommandPort(
                static _ => Task.FromResult<SessionViewDescriptor?>(null),
                static _ => Task.FromResult<SessionViewDescriptor?>(null),
                static () => Task.CompletedTask),
            new SessionCommandUiPort(
                uiDispatcher,
                static () => false,
                () => autoApprove,
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                (tab, action, _) =>
                {
                    if (renderInteraction is null)
                    {
                        action();
                    }
                    else
                    {
                        renderInteraction(tab);
                    }
                }),
            new PromptSessionPort(
                uiDispatcher,
                static () => true,
                static () => { },
                static _ => { },
                static () => [],
                static _ => { }),
            static () => new PromptSessionId("test-prompt"),
            new ShellStatusPort(
                uiDispatcher,
                static (_, _, _) => { },
                static (_, _, _, _) => { }));

    private static AgentCommandPermissionRequest CreatePermissionRequest(string sessionId)
        => new(
            ModelProviderIds.Codex,
            sessionId,
            DateTimeOffset.UtcNow,
            RunId: null,
            InteractionId: "permission-test",
            ApprovalId: null,
            Command: "Write-Output approval-test",
            WorkingDirectory: null,
            Actions: null,
            Reason: null,
            Network: null,
            ProposedExecPolicyAmendment: null,
            ProposedNetworkPolicyAmendments: null);

    private static async Task<JsonElement> InvokeToolStatusAsync(SessionExecutionOptions options)
        => (await InvokeToolStatusRecordAsync(options).ConfigureAwait(false)).GetProperty("caller");

    private static async Task<JsonElement> InvokeToolStatusRecordAsync(SessionExecutionOptions options)
    {
        Assert.IsNotNull(options.Tools);
        var tool = options.Tools.Single(static candidate => string.Equals(candidate.Spec.Name, "alta", StringComparison.Ordinal));
        using var arguments = JsonDocument.Parse("""
            {"args":["tool","status"]}
            """);
        var result = await tool.Handler(
                new AgentToolInvocation(
                    ModelProviderIds.Codex,
                    "provider-session",
                    "tool-call",
                    "alta",
                    arguments.RootElement),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success, result.Error);
        var text = ((AgentToolResultItem.Text)result.Items.Single()).Value;
        var statusLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"alta.tool.status\"", StringComparison.Ordinal));
        using var status = JsonDocument.Parse(statusLine);
        return status.RootElement.Clone();
    }

    private static ProjectDescriptor CreateProject(string id, string path)
        => new()
        {
            Id = id,
            Slug = id,
            Name = id,
            DisplayName = id,
            ProjectPath = path,
            DefaultBranch = "main",
        };

    private static SessionViewDescriptor CreateSession(string sessionId, string providerKey, string workingDirectory)
        => new()
        {
            SessionId = sessionId,
            Kind = SessionViewKind.GlobalSession,
            ProviderId = providerKey,
            ProviderKey = providerKey,
            WorkingDirectory = workingDirectory,
            Title = sessionId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Task.FromResult(action());
        }
    }

    private sealed class UnexpectedPromptLocator : ISystemPromptContentLocator
    {
        public SystemPromptContentRoots GetRoots(SystemPromptDiscoveryContext context)
            => throw new AssertFailedException("Instruction discovery must not execute.");
        public string ResolveBuiltInPromptPath(string relativePromptPath) => throw new AssertFailedException();
        public string ResolveBuiltInDocPath(string fileName) => throw new AssertFailedException();
    }

    private sealed class UnexpectedSkillRootProvider : ISkillRootProvider
    {
        public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(SkillDiscoveryContext context, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Skill discovery must not execute.");
    }

    private sealed class UnexpectedSessionCatalog : IAgentSessionCatalog
    {
        public IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(AgentSessionListFilter? filter = null, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Session discovery must not execute.");
        public Task InvalidateAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException();
        public Task InvalidateAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException();
        public Task NotifySessionCreatedAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException();
        public Task NotifySessionResumedAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException();
        public Task NotifySessionDeletedAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException();
        public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException();
        public Task NotifySessionUpdatedAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException();
    }

    private sealed class CancelingPermissionUiDispatcher : IUiDispatcher
    {
        private readonly Action _cancelPermissionRequest;

        public CancelingPermissionUiDispatcher(Action cancelPermissionRequest)
        {
            ArgumentNullException.ThrowIfNull(cancelPermissionRequest);

            _cancelPermissionRequest = cancelPermissionRequest;
        }

        public int PermissionDialogInvokeCount { get; private set; }

        public bool CheckAccess() => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            PermissionDialogInvokeCount++;
            _cancelPermissionRequest();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Task.FromResult(action());
        }
    }
}
