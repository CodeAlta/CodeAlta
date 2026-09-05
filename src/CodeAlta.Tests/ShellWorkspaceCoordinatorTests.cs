using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Shell;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellWorkspaceCoordinatorTests
{
    [TestMethod]
    public void ApplySelectionProjection_FocusesPromptWhenDisplayingSession()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var session = CreateSession("session-1", "project-1");
        var sessionStateCoordinator = CreateSessionStateCoordinator(options);
        sessionStateCoordinator.ApplyRecoveredCatalogState([CreateProject("project-1", "CodeAlta")], [session]);
        sessionStateCoordinator.OpenSession(session.SessionId);

        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            sessionId => string.Equals(sessionId, sessionStateCoordinator.SelectedSessionId, StringComparison.OrdinalIgnoreCase));
        var deferredActions = new Queue<Action>();
        var focusedPromptCount = 0;
        Visual? paneContent = null;
        var uiDispatcher = new QueueingUiDispatcher(deferredActions);
        var workspaceContext = new ShellWorkspaceContext(
            new DelegatingShellPromptAvailabilityPort(
                static () => ModelProviderIds.Codex,
                static () => (false, string.Empty, StatusTone.Info)),
            new ShellWorkspaceSurfacePort(
                static () => true,
                static () => null,
                static () => null,
                content => paneContent = content,
                () => focusedPromptCount++,
                static _ => { },
                static _ => { }),
            new DelegatingShellWorkspaceProjectionPort(
                sessionStateCoordinator.EnsureSelectionDefaults,
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static _ => { },
                static _ => { },
                static () => { },
                static () => { },
                static () => { }),
            uiDispatcher);
        var workspace = new ShellWorkspaceCoordinator(
            new CodeAltaShellViewModel(),
            new SessionWorkspaceViewModel(),
            new SessionUsageViewModel(),
            CreateModelProviderStates(),
            sessionSelection,
            workspaceContext);

        workspace.ApplySelectionProjection();

        var tab = sessionStateCoordinator.FindOpenSession(session.SessionId);
        Assert.IsNotNull(tab);
        Assert.IsNull(paneContent);
        Assert.AreEqual(0, focusedPromptCount);

        while (deferredActions.Count > 0)
        {
            deferredActions.Dequeue()();
        }

        Assert.AreEqual(1, focusedPromptCount);
    }

    [TestMethod]
    public async Task ApplySelectionProjection_FocusesPromptWhenClosingSessionFallsBackToDraft()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var session = CreateSession("session-1", "project-1");
        var sessionStateCoordinator = CreateSessionStateCoordinator(options);
        sessionStateCoordinator.ApplyRecoveredCatalogState([CreateProject("project-1", "CodeAlta")], [session]);
        sessionStateCoordinator.OpenSession(session.SessionId);

        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            sessionId => string.Equals(sessionId, sessionStateCoordinator.SelectedSessionId, StringComparison.OrdinalIgnoreCase));
        var deferredActions = new Queue<Action>();
        var focusedPromptCount = 0;
        var uiDispatcher = new QueueingUiDispatcher(deferredActions);
        var workspaceContext = new ShellWorkspaceContext(
            new DelegatingShellPromptAvailabilityPort(
                static () => ModelProviderIds.Codex,
                static () => (false, string.Empty, StatusTone.Info)),
            new ShellWorkspaceSurfacePort(
                static () => true,
                static () => null,
                static () => null,
                static _ => { },
                () => focusedPromptCount++,
                static _ => { },
                static _ => { }),
            new DelegatingShellWorkspaceProjectionPort(
                sessionStateCoordinator.EnsureSelectionDefaults,
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static _ => { },
                static _ => { },
                static () => { },
                static () => { },
                static () => { }),
            uiDispatcher);
        var workspace = new ShellWorkspaceCoordinator(
            new CodeAltaShellViewModel(),
            new SessionWorkspaceViewModel(),
            new SessionUsageViewModel(),
            CreateModelProviderStates(),
            sessionSelection,
            workspaceContext);

        workspace.ApplySelectionProjection();
        Drain(deferredActions);
        Assert.AreEqual(1, focusedPromptCount);

        await sessionStateCoordinator.CloseSessionTabAsync(session.SessionId);
        workspace.ApplySelectionProjection();

        Assert.IsInstanceOfType(sessionStateCoordinator.Selection.Target, typeof(WorkspaceTarget.Draft));
        Assert.AreEqual(1, focusedPromptCount);

        Drain(deferredActions);

        Assert.AreEqual(2, focusedPromptCount);
    }

    [TestMethod]
    public void ApplySelectionProjection_DraftScopeWithoutConfiguredProviders_DoesNotThrow()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var sessionStateCoordinator = CreateSessionStateCoordinator(options);
        sessionStateCoordinator.ApplyRecoveredCatalogState([], []);

        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            sessionId => string.Equals(sessionId, sessionStateCoordinator.SelectedSessionId, StringComparison.OrdinalIgnoreCase));

        var (workspace, sessionUsage) = CreateWorkspaceCoordinator(
            sessionStateCoordinator,
            sessionSelection,
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase),
            static () => ModelProviderIds.Codex);

        workspace.ApplySelectionProjection();

        Assert.AreEqual("Codex", sessionUsage.ProviderName);
        Assert.IsNull(sessionUsage.ModelName);
        Assert.IsNull(sessionUsage.Usage);
    }

    [TestMethod]
    public void ApplySelectionProjection_SelectedSessionWithMissingProvider_DoesNotThrow()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var session = CreateSession("session-1", "project-1");
        var sessionStateCoordinator = CreateSessionStateCoordinator(options);
        sessionStateCoordinator.ApplyRecoveredCatalogState([CreateProject("project-1", "CodeAlta")], [session]);
        sessionStateCoordinator.OpenSession(session.SessionId);

        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            sessionId => string.Equals(sessionId, sessionStateCoordinator.SelectedSessionId, StringComparison.OrdinalIgnoreCase));
        var (workspace, sessionUsage) = CreateWorkspaceCoordinator(
            sessionStateCoordinator,
            sessionSelection,
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase),
            static () => ModelProviderIds.Codex);

        workspace.ApplySelectionProjection();

        Assert.AreEqual("Codex", sessionUsage.ProviderName);
        Assert.IsNull(sessionUsage.ModelName);
        Assert.IsNull(sessionUsage.Usage);
    }

    [TestMethod]
    public void ApplySelectionProjection_DraftUsageReflectsModelSelectorPreference()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var sessionStateCoordinator = CreateSessionStateCoordinator(options);
        sessionStateCoordinator.ApplyRecoveredCatalogState([], []);
        var providerState = new ModelProviderState(ModelProviderIds.Codex, "Codex")
        {
            Availability = ModelProviderAvailability.Ready,
            SelectedModelId = "first-listed-model",
        };
        var sessionUsage = new SessionUsageViewModel();
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            sessionId => string.Equals(sessionId, sessionStateCoordinator.SelectedSessionId, StringComparison.OrdinalIgnoreCase));
        var workspaceContext = new ShellWorkspaceContext(
            new DelegatingShellPromptAvailabilityPort(
                static () => ModelProviderIds.Codex,
                static () => (false, string.Empty, StatusTone.Info)),
            new ShellWorkspaceSurfacePort(
                static () => true,
                static () => null,
                static () => null,
                static _ => { },
                static () => { },
                static _ => { },
                static _ => { }),
            new DelegatingShellWorkspaceProjectionPort(
                sessionStateCoordinator.EnsureSelectionDefaults,
                static () => { },
                static () => { },
                static () => { },
                () => providerState.SelectedModelId = "configured-default-model",
                static _ => { },
                static _ => { },
                static () => { },
                static () => { },
                static () => { }),
            new InlineUiDispatcher());
        var workspace = new ShellWorkspaceCoordinator(
            new CodeAltaShellViewModel(),
            new SessionWorkspaceViewModel(),
            sessionUsage,
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
            {
                [ModelProviderIds.Codex.Value] = providerState,
            },
            sessionSelection,
            workspaceContext);

        workspace.ApplySelectionProjection();

        Assert.AreEqual("configured-default-model", sessionUsage.ModelName);
    }

    [TestMethod]
    public void ApplySelectionProjection_SessionUsageReflectsRestoredModelPreference()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var session = CreateSession("session-1", "project-1");
        var sessionStateCoordinator = CreateSessionStateCoordinator(options);
        sessionStateCoordinator.ApplyRecoveredCatalogState([CreateProject("project-1", "CodeAlta")], [session]);
        sessionStateCoordinator.OpenSession(session.SessionId);
        var providerState = new ModelProviderState(ModelProviderIds.Codex, "Codex")
        {
            Availability = ModelProviderAvailability.Ready,
            SelectedModelId = "first-listed-model",
        };
        var sessionUsage = new SessionUsageViewModel();
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            sessionId => string.Equals(sessionId, sessionStateCoordinator.SelectedSessionId, StringComparison.OrdinalIgnoreCase));
        var workspaceContext = new ShellWorkspaceContext(
            new DelegatingShellPromptAvailabilityPort(
                static () => ModelProviderIds.Codex,
                static () => (false, string.Empty, StatusTone.Info)),
            new ShellWorkspaceSurfacePort(
                static () => true,
                static () => null,
                static () => null,
                static _ => { },
                static () => { },
                static _ => { },
                static _ => { }),
            new DelegatingShellWorkspaceProjectionPort(
                sessionStateCoordinator.EnsureSelectionDefaults,
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                tab => tab.ModelId = "restored-session-model",
                static _ => { },
                static () => { },
                static () => { },
                static () => { }),
            new InlineUiDispatcher());
        var workspace = new ShellWorkspaceCoordinator(
            new CodeAltaShellViewModel(),
            new SessionWorkspaceViewModel(),
            sessionUsage,
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
            {
                [ModelProviderIds.Codex.Value] = providerState,
            },
            sessionSelection,
            workspaceContext);

        workspace.ApplySelectionProjection();

        Assert.AreEqual("restored-session-model", sessionUsage.ModelName);
    }

    [TestMethod]
    public void ApplySelectionProjection_EnrichesRecoveredUsageWithProviderModelLimit()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var session = CreateSession("session-1", "project-1");
        var sessionStateCoordinator = CreateSessionStateCoordinator(options);
        sessionStateCoordinator.ApplyRecoveredCatalogState([CreateProject("project-1", "CodeAlta")], [session]);
        sessionStateCoordinator.OpenSession(session.SessionId);
        var tab = sessionStateCoordinator.FindOpenSession(session.SessionId);
        Assert.IsNotNull(tab);
        tab.ModelId = "gpt-5.4";
        tab.Usage = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(
                CurrentTokens: 168_400,
                TokenLimit: null,
                MessageCount: 42,
                Label: "Estimated active context"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.RecoveredHistory,
            UpdatedAt: DateTimeOffset.Parse("2026-05-11T15:10:00+00:00"));

        var providerState = new ModelProviderState(ModelProviderIds.Codex, "Codex")
        {
            Availability = ModelProviderAvailability.Ready,
            SelectedModelId = "gpt-5.4",
        };
        providerState.Models.Add(new AgentModelInfo(
            "gpt-5.4-2026-03-05",
            "GPT-5.4",
            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["contextWindow"] = 400_000L,
                ["inputTokenLimit"] = 272_000L,
                ["outputTokenLimit"] = 128_000L,
            }));

        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            sessionId => string.Equals(sessionId, sessionStateCoordinator.SelectedSessionId, StringComparison.OrdinalIgnoreCase));
        var (workspace, sessionUsage) = CreateWorkspaceCoordinator(
            sessionStateCoordinator,
            sessionSelection,
            new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
            {
                [ModelProviderIds.Codex.Value] = providerState,
            },
            static () => ModelProviderIds.Codex);

        workspace.ApplySelectionProjection();

        Assert.AreEqual(272_000L, sessionUsage.Usage?.TokenLimit);
        Assert.AreEqual(400_000L, sessionUsage.Usage?.Window?.TotalContextEnvelope);
        Assert.AreEqual(128_000L, sessionUsage.Usage?.Window?.MaxOutputTokens);
        Assert.AreEqual(61.9d, sessionUsage.Usage?.WindowUsagePercentage ?? 0d, 0.1d);
    }

    [TestMethod]
    public void ShellWorkspaceContext_DispatchToUi_RunsInlineWhenDispatcherHasAccess()
    {
        var deferredActions = new Queue<Action>();
        var workspaceContext = CreateMinimalWorkspaceContext(new QueueingUiDispatcher(deferredActions));
        var count = 0;

        workspaceContext.DispatchToUi(() => count++);

        Assert.AreEqual(1, count);
        Assert.AreEqual(0, deferredActions.Count);
    }

    [TestMethod]
    public void ShellWorkspaceContext_DispatchToUiDeferred_QueuesWhenDispatcherHasAccess()
    {
        var deferredActions = new Queue<Action>();
        var workspaceContext = CreateMinimalWorkspaceContext(new QueueingUiDispatcher(deferredActions));
        var count = 0;

        workspaceContext.DispatchToUiDeferred(() => count++);

        Assert.AreEqual(0, count);
        Assert.AreEqual(1, deferredActions.Count);
    }

    [TestMethod]
    public void RefreshRunningStatusElapsed_SyncsActivePromptPanelProjection()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var session = CreateSession("session-1", "project-1");
        var sessionStateCoordinator = CreateSessionStateCoordinator(options);
        sessionStateCoordinator.ApplyRecoveredCatalogState([CreateProject("project-1", "CodeAlta")], [session]);
        sessionStateCoordinator.OpenSession("session-1");
        var tab = sessionStateCoordinator.FindOpenSession("session-1");
        Assert.IsNotNull(tab);
        tab.HasCustomStatus = true;
        tab.StatusBusy = true;
        tab.StatusTone = StatusTone.Info;
        tab.StatusMessage = StatusVisualFormatter.BuildThinkingStatusText();
        tab.ActiveRunStartedAt = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00");
        var syncActivePromptPanelProjectionCount = 0;

        var workspaceContext = new ShellWorkspaceContext(
            new DelegatingShellPromptAvailabilityPort(
                static () => ModelProviderIds.Codex,
                static () => (false, string.Empty, StatusTone.Info)),
            new ShellWorkspaceSurfacePort(
                static () => true,
                static () => null,
                static () => null,
                static _ => { },
                static () => { },
                static _ => { },
                static _ => { }),
            new DelegatingShellWorkspaceProjectionPort(
                sessionStateCoordinator.EnsureSelectionDefaults,
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static _ => { },
                static _ => { },
                static () => { },
                () => syncActivePromptPanelProjectionCount++,
                static () => { }),
            new InlineUiDispatcher());
        var shellViewModel = new CodeAltaShellViewModel();
        var controller = new ShellStatusProjectionController(
            shellViewModel,
            new SessionSelectionContext(
                sessionStateCoordinator,
                static (_, _) => Task.CompletedTask,
                sessionId => string.Equals(sessionId, sessionStateCoordinator.SelectedSessionId, StringComparison.OrdinalIgnoreCase)),
            workspaceContext,
            new State<int>(0));

        controller.RefreshRunningStatusElapsed(DateTimeOffset.Parse("2026-03-29T12:00:05+00:00"));

        Assert.AreEqual(1, syncActivePromptPanelProjectionCount);
        Assert.AreEqual("Thinking for 5 seconds...", shellViewModel.StatusText);
        Assert.IsTrue(shellViewModel.StatusBusy);
        Assert.AreEqual(StatusTone.Info, shellViewModel.StatusTone);
    }

    private static (ShellWorkspaceCoordinator Workspace, SessionUsageViewModel SessionUsage) CreateWorkspaceCoordinator(
        ShellSessionStateCoordinator sessionStateCoordinator,
        SessionSelectionContext sessionSelection,
        Dictionary<string, ModelProviderState> modelProviderStates,
        Func<ModelProviderId> getPreferredProviderId)
    {
        var workspaceContext = new ShellWorkspaceContext(
            new DelegatingShellPromptAvailabilityPort(
                getPreferredProviderId,
                static () => (true, "No model provider is ready.", StatusTone.Warning)),
            new ShellWorkspaceSurfacePort(
                static () => true,
                static () => null,
                static () => null,
                static _ => { },
                static () => { },
                static _ => { },
                static _ => { }),
            new DelegatingShellWorkspaceProjectionPort(
                sessionStateCoordinator.EnsureSelectionDefaults,
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static _ => { },
                static _ => { },
                static () => { },
                static () => { },
                static () => { }),
            new InlineUiDispatcher());
        var sessionUsage = new SessionUsageViewModel();
        var workspace = new ShellWorkspaceCoordinator(
            new CodeAltaShellViewModel(),
            new SessionWorkspaceViewModel(),
            sessionUsage,
            modelProviderStates,
            sessionSelection,
            workspaceContext);

        return (workspace, sessionUsage);
    }

    private static void Drain(Queue<Action> deferredActions)
    {
        while (deferredActions.Count > 0)
        {
            deferredActions.Dequeue()();
        }
    }

    private static ShellWorkspaceContext CreateMinimalWorkspaceContext(IUiDispatcher uiDispatcher)
        => new(
            new DelegatingShellPromptAvailabilityPort(
                static () => ModelProviderIds.Codex,
                static () => (false, string.Empty, StatusTone.Info)),
            new ShellWorkspaceSurfacePort(
                static () => false,
                static () => null,
                static () => null,
                static _ => { },
                static () => { },
                static _ => { },
                static _ => { }),
            new DelegatingShellWorkspaceProjectionPort(
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static _ => { },
                static _ => { },
                static () => { },
                static () => { },
                static () => { }),
            uiDispatcher);

    private static ShellSessionStateCoordinator CreateSessionStateCoordinator(CatalogOptions options)
        => TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new SessionViewCatalog(options),
            new InlineUiDispatcher(),
            new ShellStateStore(new InlineUiDispatcher()));

    private static Dictionary<string, ModelProviderState> CreateModelProviderStates()
        => new(StringComparer.Ordinal)
        {
            [ModelProviderIds.Codex.Value] = new ModelProviderState(ModelProviderIds.Codex, "Codex")
            {
                Availability = ModelProviderAvailability.Ready,
            },
        };

    private static SessionViewDescriptor CreateSession(string sessionId, string projectId)
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00");
        return new SessionViewDescriptor
        {
            SessionId = sessionId,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProjectRef = projectId,
            WorkingDirectory = @"C:\repo",
            Title = "Test session",
            Status = SessionViewStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
        };
    }

    private static ProjectDescriptor CreateProject(string id, string displayName)
        => new()
        {
            Id = id,
            Slug = displayName.ToLowerInvariant(),
            Name = displayName,
            DisplayName = displayName,
            ProjectPath = $@"C:\repo\{displayName}",
            DefaultBranch = "main",
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

    private sealed class QueueingUiDispatcher(Queue<Action> deferredActions) : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            deferredActions.Enqueue(action);
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
                $"CodeAlta.Tests.{Guid.NewGuid():N}");
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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
