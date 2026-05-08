using System.Runtime.CompilerServices;
using System.Reflection;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Workspace;
using CodeAlta.Threading;
using CodeAlta.ViewModels;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelProviderSelectorCoordinatorTests
{
    [TestMethod]
    public void RefreshForDraftScope_SwitchingBackend_UpdatesModelOptionsAndSyncsSelectors()
    {
        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var backendStates = ChatBackendPresentation.CreateBackendStates();

        var codexState = backendStates[AgentBackendIds.Codex.Value];
        codexState.Availability = ChatBackendAvailability.Ready;
        codexState.Models.Add(new AgentModelInfo(
            "gpt-5-codex",
            DisplayName: "GPT-5 Codex",
            DefaultReasoningEffort: AgentReasoningEffort.High,
            SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.High]));

        var copilotState = backendStates[AgentBackendIds.Copilot.Value];
        copilotState.Availability = ChatBackendAvailability.Ready;
        copilotState.Models.Add(new AgentModelInfo(
            "gpt-4.1",
            DisplayName: "GPT-4.1",
            DefaultReasoningEffort: AgentReasoningEffort.Medium,
            SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.Medium]));
        copilotState.Models.Add(new AgentModelInfo(
            "o4-mini",
            DisplayName: "o4-mini",
            DefaultReasoningEffort: AgentReasoningEffort.Low,
            SupportedReasoningEfforts: [AgentReasoningEffort.Low]));

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => throw new NotSupportedException(),
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var threadSelection = CreateThreadSelectionContext();
        var syncCallCount = 0;
        var coordinator = new ModelProviderSelectorCoordinator(
            workspaceViewModel,
            promptComposerViewModel,
            backendStates,
            selectorState,
            threadSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            () => syncCallCount++);

        coordinator.RefreshForDraftScope(AgentBackendIds.Codex);

        CollectionAssert.AreEqual(new[] { "GPT-5 Codex" }, workspaceViewModel.ModelOptions.Select(static option => option.Label).ToArray());
        Assert.AreEqual(1, syncCallCount);

        coordinator.RefreshForDraftScope(AgentBackendIds.Copilot);

        Assert.AreEqual(1, workspaceViewModel.SelectedModelProviderIndex);
        CollectionAssert.AreEqual(new[] { "GPT-4.1", "o4-mini" }, workspaceViewModel.ModelOptions.Select(static option => option.Label).ToArray());
        Assert.AreEqual(2, syncCallCount);

        static void ApplyDraftModelProviderPreference(ChatBackendState backendState)
        {
            backendState.SelectedModelId = ChatBackendPresentation.ResolvePreferredModelId(
                backendState.Models,
                backendState.SelectedModelId);
            backendState.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(
                ModelProviderPreferenceCoordinator.FindModel(backendState.Models, backendState.SelectedModelId),
                backendState.SelectedReasoningEffort);
        }
    }

    [TestMethod]
    public void GetPreferredModelProviderId_UsesConfiguredDefaultProvider()
    {
        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        AgentBackendDescriptor[] backendDescriptors =
        [
            new AgentBackendDescriptor(AgentBackendIds.Codex, "Codex"),
            new AgentBackendDescriptor(new AgentBackendId("zai"), "ZAI"),
        ];
        var backendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        backendStates[AgentBackendIds.Codex.Value].Availability = ChatBackendAvailability.Ready;
        backendStates["zai"].Availability = ChatBackendAvailability.Ready;

        var coordinator = CreateCoordinator(
            backendDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            backendStates,
            static _ => "zai");

        var preferredBackendId = coordinator.GetPreferredModelProviderId();

        Assert.AreEqual("zai", preferredBackendId.Value);
    }

    [TestMethod]
    public void GetPreferredModelProviderId_FallsBackToFirstReadyProviderDeterministically()
    {
        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        AgentBackendDescriptor[] backendDescriptors =
        [
            new AgentBackendDescriptor(new AgentBackendId("zai"), "ZAI"),
            new AgentBackendDescriptor(new AgentBackendId("openai"), "OpenAI"),
            new AgentBackendDescriptor(AgentBackendIds.Codex, "Codex"),
        ];
        var backendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        backendStates["zai"].Availability = ChatBackendAvailability.Unsupported;
        backendStates["openai"].Availability = ChatBackendAvailability.Ready;
        backendStates[AgentBackendIds.Codex.Value].Availability = ChatBackendAvailability.Ready;

        var coordinator = CreateCoordinator(
            backendDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            backendStates,
            static _ => null);

        var preferredBackendId = coordinator.GetPreferredModelProviderId();

        Assert.AreEqual("openai", preferredBackendId.Value);
    }

    [TestMethod]
    public void RefreshForDraftScope_UsesConfiguredProvidersForSummary()
    {
        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        AgentBackendDescriptor[] backendDescriptors =
        [
            new AgentBackendDescriptor(AgentBackendIds.Codex, "Codex"),
            new AgentBackendDescriptor(AgentBackendIds.Copilot, "Copilot"),
        ];
        var backendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        backendStates[AgentBackendIds.Codex.Value].Availability = ChatBackendAvailability.Ready;
        backendStates[AgentBackendIds.Copilot.Value].Availability = ChatBackendAvailability.Failed;

        var coordinator = CreateCoordinator(
            backendDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            backendStates,
            static _ => null,
            static () => ["codex", "copilot", "openai", "anthropic", "google", "vertex"]);

        coordinator.RefreshForDraftScope(AgentBackendIds.Codex);

        StringAssert.Contains(workspaceViewModel.ProviderSummaryMarkup, "1 active provider");
        StringAssert.Contains(workspaceViewModel.ProviderSummaryMarkup, "6 configured");
        StringAssert.Contains(workspaceViewModel.ProviderSummaryMarkup, "5 errors");
    }

    [TestMethod]
    public void RefreshForThread_UsesThreadModelProviderSelectionCapability()
    {
        using var temp = TempDirectory.Create();
        var threadStateCoordinator = CreateThreadStateCoordinator(temp.Path, out var thread);
        var threadSelection = new ThreadSelectionContext(
            threadStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = threadStateCoordinator.EnsureThreadTab(thread);

        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        AgentBackendDescriptor[] backendDescriptors =
        [
            new(new AgentBackendId("openai"), "OpenAI"),
            new(new AgentBackendId("anthropic"), "Anthropic"),
        ];
        var backendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        backendStates["openai"].Availability = ChatBackendAvailability.Ready;
        backendStates["anthropic"].Availability = ChatBackendAvailability.Ready;

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            backendDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            backendStates,
            selectorState,
            threadSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { },
            static (_, _) => true);

        coordinator.RefreshForThread(tab);

        Assert.IsTrue(workspaceViewModel.CanSelectModelProvider);
    }

    [TestMethod]
    public void UpdatePromptAvailabilityUi_RefreshesThreadModelProviderSelectionCapability()
    {
        using var temp = TempDirectory.Create();
        var threadStateCoordinator = CreateThreadStateCoordinator(temp.Path, out var thread);
        var threadSelection = new ThreadSelectionContext(
            threadStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = threadStateCoordinator.EnsureThreadTab(thread);

        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        AgentBackendDescriptor[] backendDescriptors =
        [
            new(new AgentBackendId("openai"), "OpenAI"),
            new(new AgentBackendId("anthropic"), "Anthropic"),
        ];
        var backendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        backendStates["openai"].Availability = ChatBackendAvailability.Ready;
        backendStates["anthropic"].Availability = ChatBackendAvailability.Ready;

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            backendDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            backendStates,
            selectorState,
            threadSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { },
            static (_, selectedTab) => !selectedTab.StatusBusy);

        coordinator.RefreshForThread(tab);
        Assert.IsTrue(workspaceViewModel.CanSelectModelProvider);

        tab.StatusBusy = true;
        coordinator.UpdatePromptAvailabilityUi();
        Assert.IsFalse(workspaceViewModel.CanSelectModelProvider);

        tab.StatusBusy = false;
        coordinator.UpdatePromptAvailabilityUi();

        Assert.IsTrue(workspaceViewModel.CanSelectModelProvider);
    }

    [TestMethod]
    public async Task OnModelProviderSelectionChangedAsync_UsesSwitchCallbackForSelectedThread()
    {
        using var temp = TempDirectory.Create();
        var threadStateCoordinator = CreateThreadStateCoordinator(temp.Path, out var thread);
        var threadSelection = new ThreadSelectionContext(
            threadStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = threadStateCoordinator.EnsureThreadTab(thread);

        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        AgentBackendDescriptor[] backendDescriptors =
        [
            new(new AgentBackendId("openai"), "OpenAI"),
            new(new AgentBackendId("anthropic"), "Anthropic"),
        ];
        var backendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        backendStates["openai"].Availability = ChatBackendAvailability.Ready;
        backendStates["anthropic"].Availability = ChatBackendAvailability.Ready;

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var switchCallCount = 0;
        var refreshedSelectionCount = 0;
        var coordinator = new ModelProviderSelectorCoordinator(
            backendDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            backendStates,
            selectorState,
            threadSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { },
            static (_, _) => true,
            (selectedThread, selectedTab, targetBackendId) =>
            {
                Assert.AreSame(thread, selectedThread);
                Assert.AreSame(tab, selectedTab);
                Assert.AreEqual("anthropic", targetBackendId.Value);
                switchCallCount++;
                return Task.FromResult(true);
            },
            () => refreshedSelectionCount++);

        coordinator.RefreshForThread(tab);
        await coordinator.OnModelProviderSelectionChangedAsync(newIndex: 1);

        Assert.AreEqual(1, switchCallCount);
        Assert.AreEqual(1, refreshedSelectionCount);
    }

    [TestMethod]
    public void OnModelSelectionChanged_ImmediatelyUpdatesSelectedIndexForOpenThread()
    {
        using var temp = TempDirectory.Create();
        var threadStateCoordinator = CreateThreadStateCoordinator(temp.Path, out var thread);
        var threadSelection = new ThreadSelectionContext(
            threadStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = threadStateCoordinator.EnsureThreadTab(thread);

        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        AgentBackendDescriptor[] backendDescriptors =
        [
            new(new AgentBackendId("openai"), "OpenAI"),
        ];
        var backendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        backendStates["openai"].Availability = ChatBackendAvailability.Ready;
        backendStates["openai"].Models.Add(new AgentModelInfo("gpt-5.4", DisplayName: "GPT-5.4"));
        backendStates["openai"].Models.Add(new AgentModelInfo("gpt-5.4-mini", DisplayName: "GPT-5.4 Mini"));

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            backendDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            backendStates,
            selectorState,
            threadSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { },
            static (_, _) => true);

        coordinator.RefreshForThread(tab);
        coordinator.OnModelSelectionChanged(newIndex: 1);

        Assert.AreEqual(1, workspaceViewModel.SelectedModelIndex);
        Assert.AreEqual("gpt-5.4-mini", tab.ModelId);
    }

    [TestMethod]
    public void OnModelSelectionChanged_DraftScope_DoesNotReapplyDefaultsOverExplicitSelection()
    {
        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        AgentBackendDescriptor[] backendDescriptors =
        [
            new(new AgentBackendId("openai"), "OpenAI"),
        ];
        var backendStates = ChatBackendPresentation.CreateBackendStates(backendDescriptors);
        var backendState = backendStates["openai"];
        backendState.Availability = ChatBackendAvailability.Ready;
        backendState.Models.Add(new AgentModelInfo("gpt-5.4", DisplayName: "GPT-5.4"));
        backendState.Models.Add(new AgentModelInfo("gpt-5.4-mini", DisplayName: "GPT-5.4 Mini"));

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var draftPreferenceApplyCount = 0;
        var preferences = new FrontendModelProviderPreferencePort(
            state =>
            {
                draftPreferenceApplyCount++;
                state.SelectedModelId = "gpt-5.4";
                state.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(
                    ModelProviderPreferenceCoordinator.FindModel(state.Models, state.SelectedModelId),
                    preferredReasoningEffort: null);
            },
            static _ => throw new NotSupportedException(),
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            backendDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            backendStates,
            selectorState,
            CreateThreadSelectionContext(),
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { });

        coordinator.RefreshForDraftScope(new AgentBackendId("openai"));
        coordinator.OnModelSelectionChanged(newIndex: 1);

        Assert.AreEqual(1, workspaceViewModel.SelectedModelIndex);
        Assert.AreEqual("gpt-5.4-mini", backendState.SelectedModelId);
        Assert.AreEqual(1, draftPreferenceApplyCount);
    }

    private static ModelProviderSelectorCoordinator CreateCoordinator(
        IReadOnlyList<AgentBackendDescriptor> backendDescriptors,
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        Dictionary<string, ChatBackendState> backendStates,
        Func<string?, string?> getEffectiveDefaultProviderKey,
        Func<IReadOnlyList<string>>? getConfiguredProviderKeys = null)
    {
        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => throw new NotSupportedException(),
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var threadSelection = CreateThreadSelectionContext();
        return new ModelProviderSelectorCoordinator(
            backendDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            backendStates,
            selectorState,
            threadSelection,
            preferences,
            workspaceRefresh,
            getEffectiveDefaultProviderKey,
            static () => { },
            getConfiguredProviderKeys: getConfiguredProviderKeys);
    }

    private static ThreadSelectionContext CreateThreadSelectionContext()
    {
        var coordinator = (ShellThreadStateCoordinator)RuntimeHelpers.GetUninitializedObject(typeof(ShellThreadStateCoordinator));
        var selectionCoordinatorField = typeof(ShellThreadStateCoordinator).GetField("_selectionCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(selectionCoordinatorField);
        selectionCoordinatorField.SetValue(coordinator, new ShellSelectionCoordinator());
        return new ThreadSelectionContext(
            coordinator,
            static (_, _) => Task.CompletedTask,
            static _ => false);
    }

    private static ShellThreadStateCoordinator CreateThreadStateCoordinator(string rootPath, out WorkThreadDescriptor thread)
    {
        var options = new CatalogOptions { GlobalRoot = rootPath };
        var coordinator = new ShellThreadStateCoordinator(
            new ProjectCatalog(options),
            new WorkThreadCatalog(options),
            new InlineUiDispatcher(),
            new ShellStateStore(new InlineUiDispatcher()),
            static () => null,
            static _ => true,
            static _ => null,
            static _ => { },
            static _ => { },
            static (_, _, _, _) => { },
            static (_, _) => Task.CompletedTask,
            static () => { },
            static _ => { });

        var project = new ProjectDescriptor
        {
            Id = "project-1",
            Slug = "project-1",
            Name = "Project 1",
            DisplayName = "Project 1",
            ProjectPath = Path.Combine(rootPath, "project-1"),
            DefaultBranch = "main",
        };
        thread = new WorkThreadDescriptor
        {
            ThreadId = "openai:session-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = "openai",
            ProviderKey = "openai",
            BackendSessionId = "session-1",
            ProjectRef = project.Id,
            WorkingDirectory = project.ProjectPath,
            Title = "Review startup",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"),
            UpdatedAt = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"),
            LastActiveAt = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"),
            StartedAt = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"),
        };

        coordinator.ApplyRecoveredCatalogState([project], [thread]);
        coordinator.OpenThread(thread.ThreadId);
        return coordinator;
    }

    private static void ApplyDraftModelProviderPreference(ChatBackendState backendState)
    {
        backendState.SelectedModelId = ChatBackendPresentation.ResolvePreferredModelId(
            backendState.Models,
            backendState.SelectedModelId);
        backendState.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(
            ModelProviderPreferenceCoordinator.FindModel(backendState.Models, backendState.SelectedModelId),
            backendState.SelectedReasoningEffort);
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            callback();
        }

        public Task InvokeAsync(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            callback();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return Task.FromResult(callback());
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
            catch
            {
            }
        }
    }
}

