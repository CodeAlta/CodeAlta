using System.Runtime.CompilerServices;
using System.Reflection;
using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Chat;
using CodeAlta.Tui.Presentation.Workspace;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelProviderSelectorCoordinatorTests
{
    [TestMethod]
    public void RefreshForDraftScope_SwitchingProvider_UpdatesModelOptionsAndSyncsSelectors()
    {
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var providerStates = ModelProviderPresentation.CreateProviderStates();

        var codexState = providerStates[ModelProviderIds.Codex.Value];
        codexState.Availability = ModelProviderAvailability.Ready;
        codexState.Models.Add(new AgentModelInfo(
            "gpt-5-codex",
            DisplayName: "GPT-5 Codex",
            DefaultReasoningEffort: AgentReasoningEffort.High,
            SupportedReasoningEfforts: [AgentReasoningEffort.Medium, AgentReasoningEffort.High]));

        var copilotState = providerStates[ModelProviderIds.Copilot.Value];
        copilotState.Availability = ModelProviderAvailability.Ready;
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
        var sessionSelection = CreateSessionSelectionContext();
        var syncCallCount = 0;
        var coordinator = new ModelProviderSelectorCoordinator(
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            sessionSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            () => syncCallCount++);

        coordinator.RefreshForDraftScope(ModelProviderIds.Codex);

        CollectionAssert.AreEqual(new[] { "GPT-5 Codex" }, workspaceViewModel.ModelOptions.Select(static option => option.Label).ToArray());
        Assert.AreEqual(1, syncCallCount);

        coordinator.RefreshForDraftScope(ModelProviderIds.Copilot);

        Assert.AreEqual(1, workspaceViewModel.SelectedModelProviderIndex);
        CollectionAssert.AreEqual(new[] { "GPT-4.1", "o4-mini" }, workspaceViewModel.ModelOptions.Select(static option => option.Label).ToArray());
        Assert.AreEqual(2, syncCallCount);

        static void ApplyDraftModelProviderPreference(ModelProviderState providerState)
        {
            providerState.SelectedModelId = ModelProviderPresentation.ResolvePreferredModelId(
                providerState.Models,
                providerState.SelectedModelId);
            providerState.SelectedReasoningEffort = ModelProviderPresentation.ResolvePreferredReasoningEffort(
                ModelProviderPreferenceCoordinator.FindModel(providerState.Models, providerState.SelectedModelId),
                providerState.SelectedReasoningEffort);
        }
    }

    [TestMethod]
    public void GetPreferredModelProviderId_UsesConfiguredDefaultProvider()
    {
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new ModelProviderDescriptor(ModelProviderIds.Codex, "Codex"),
            new ModelProviderDescriptor(new ModelProviderId("zai"), "ZAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates[ModelProviderIds.Codex.Value].Availability = ModelProviderAvailability.Ready;
        providerStates["zai"].Availability = ModelProviderAvailability.Ready;

        var coordinator = CreateCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            static _ => "zai");

        var preferredProviderId = coordinator.GetPreferredModelProviderId();

        Assert.AreEqual("zai", preferredProviderId.Value);
    }

    [TestMethod]
    public void RefreshForDraftScope_RestoresPersistedDraftProviderBeforeConfiguredDefaultProvider()
    {
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
            new(new ModelProviderId("anthropic"), "Anthropic"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates["openai"].Models.Add(new AgentModelInfo("gpt-5.4"));
        providerStates["anthropic"].Availability = ModelProviderAvailability.Probing;

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => throw new NotSupportedException(),
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            CreateSessionSelectionContext(),
            preferences,
            new WorkspaceRefreshContext(static _ => { }),
            static _ => "openai",
            static () => { },
            getDraftModelProviderPreference: static () => new ModelProviderPreference(
                new ModelProviderId("anthropic"),
                "claude-sonnet-4.5",
                AgentReasoningEffort.High));

        coordinator.RefreshForDraftScope();

        Assert.AreEqual(1, workspaceViewModel.SelectedModelProviderIndex);
        Assert.AreEqual("Anthropic", workspaceViewModel.ModelProviderOptions[workspaceViewModel.SelectedModelProviderIndex].Label);
    }

    [TestMethod]
    public void RefreshForDraftScope_DoesNotDisplayFirstModelWhenPersistedModelIsMissingFromCatalog()
    {
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("codex"), "Codex subscription"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        var providerState = providerStates["codex"];
        providerState.Availability = ModelProviderAvailability.Ready;
        providerState.Models.Add(new AgentModelInfo("gpt-5.2", DisplayName: "GPT-5.2"));

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            state =>
            {
                state.SelectedModelId = "gpt-5.5";
                state.SelectedReasoningEffort = AgentReasoningEffort.High;
            },
            static _ => throw new NotSupportedException(),
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            CreateSessionSelectionContext(),
            preferences,
            new WorkspaceRefreshContext(static _ => { }),
            static _ => null,
            static () => { },
            getDraftModelProviderPreference: static () => new ModelProviderPreference(
                new ModelProviderId("codex"),
                "gpt-5.5",
                AgentReasoningEffort.High));

        coordinator.RefreshForDraftScope();

        Assert.AreEqual(0, workspaceViewModel.SelectedModelIndex);
        Assert.AreEqual("gpt-5.5", workspaceViewModel.ModelOptions[0].ModelId);
        Assert.AreEqual("gpt-5.2", workspaceViewModel.ModelOptions[1].ModelId);
    }

    [TestMethod]
    public void GetPreferredModelProviderId_FallsBackToFirstReadyProviderDeterministically()
    {
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new ModelProviderDescriptor(new ModelProviderId("zai"), "ZAI"),
            new ModelProviderDescriptor(new ModelProviderId("openai"), "OpenAI"),
            new ModelProviderDescriptor(ModelProviderIds.Codex, "Codex"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["zai"].Availability = ModelProviderAvailability.Unsupported;
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates[ModelProviderIds.Codex.Value].Availability = ModelProviderAvailability.Ready;

        var coordinator = CreateCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            static _ => null);

        var preferredProviderId = coordinator.GetPreferredModelProviderId();

        Assert.AreEqual("openai", preferredProviderId.Value);
    }

    [TestMethod]
    public void RefreshForDraftScope_UsesConfiguredProvidersForSummary()
    {
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new ModelProviderDescriptor(ModelProviderIds.Codex, "Codex"),
            new ModelProviderDescriptor(ModelProviderIds.Copilot, "Copilot"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates[ModelProviderIds.Codex.Value].Availability = ModelProviderAvailability.Ready;
        providerStates[ModelProviderIds.Copilot.Value].Availability = ModelProviderAvailability.Failed;

        var coordinator = CreateCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            static _ => null,
            static () => ["codex", "copilot", "openai", "anthropic", "google", "vertex"]);

        coordinator.RefreshForDraftScope(ModelProviderIds.Codex);

        StringAssert.Contains(workspaceViewModel.ProviderSummaryMarkup, "1 active provider");
        StringAssert.Contains(workspaceViewModel.ProviderSummaryMarkup, "6 configured");
        StringAssert.Contains(workspaceViewModel.ProviderSummaryMarkup, "5 errors");
    }

    [TestMethod]
    public void RefreshForSession_UsesSessionModelProviderSelectionCapability()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = sessionStateCoordinator.EnsureSessionTab(session);

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
            new(new ModelProviderId("anthropic"), "Anthropic"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates["anthropic"].Availability = ModelProviderAvailability.Ready;

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            sessionSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { },
            static (_, _) => true);

        coordinator.RefreshForSession(tab);

        Assert.IsTrue(workspaceViewModel.CanSelectModelProvider);
    }

    [TestMethod]
    public void RefreshForSession_PreservesUnavailableSessionProviderSelection()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        session.ProviderId = "unavailable-provider";
        session.ProviderKey = "unavailable-provider";
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = sessionStateCoordinator.EnsureSessionTab(session);
        tab.ProviderId = new ModelProviderId("unavailable-provider");
        tab.ModelId = "gpt-4.1";

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            sessionSelection,
            preferences,
            new WorkspaceRefreshContext(static _ => { }),
            static _ => null,
            static () => { },
            static (_, _) => true);

        coordinator.RefreshForSession(tab);

        Assert.AreEqual(0, workspaceViewModel.SelectedModelProviderIndex);
        Assert.AreEqual("unavailable-provider", workspaceViewModel.ModelProviderOptions[0].ProviderId.Value);
        StringAssert.Contains(workspaceViewModel.ModelProviderOptions[0].Label, "not configured");
        Assert.AreEqual("gpt-4.1", workspaceViewModel.ModelOptions[0].ModelId);
        Assert.IsFalse(workspaceViewModel.CanSelectModel);
        Assert.IsFalse(workspaceViewModel.CanSelectReasoning);
        Assert.IsTrue(workspaceViewModel.CanSelectModelProvider);

        coordinator.ApplyPromptAvailabilityProjection();

        Assert.IsFalse(promptComposerViewModel.CanSend);
        StringAssert.Contains(promptComposerViewModel.Placeholder, "unavailable-provider");
    }

    [TestMethod]
    public async Task OnModelProviderSelectionChangedAsync_SwitchesAwayFromUnavailableProviderAndEnablesPrompt()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        session.ProviderId = "disabled-provider";
        session.ProviderKey = "disabled-provider";
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = sessionStateCoordinator.EnsureSessionTab(session);
        tab.ProviderId = new ModelProviderId("disabled-provider");
        tab.ModelId = "old-model";

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates["openai"].Models.Add(new AgentModelInfo("gpt-5.4", DisplayName: "GPT-5.4"));

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            sessionSelection,
            preferences,
            new WorkspaceRefreshContext(static _ => { }),
            static _ => null,
            static () => { },
            static (_, _) => true,
            (_, selectedTab, targetProviderId) =>
            {
                session.ProviderId = targetProviderId.Value;
                session.ProviderKey = targetProviderId.Value;
                selectedTab.ProviderId = targetProviderId;
                selectedTab.ModelId = "gpt-5.4";
                return Task.FromResult(true);
            });

        coordinator.RefreshForSession(tab);
        coordinator.ApplyPromptAvailabilityProjection();
        Assert.IsFalse(promptComposerViewModel.CanSend);

        await coordinator.OnModelProviderSelectionChangedAsync(newIndex: 1);
        coordinator.RefreshForSession(tab);
        coordinator.ApplyPromptAvailabilityProjection();

        Assert.AreEqual(0, workspaceViewModel.SelectedModelProviderIndex);
        Assert.IsTrue(promptComposerViewModel.IsEnabled);
        Assert.IsTrue(promptComposerViewModel.CanSend);
        StringAssert.Contains(promptComposerViewModel.Placeholder, "Continue the selected session");
    }

    [TestMethod]
    public void ApplyPromptAvailabilityProjection_RefreshesSessionModelProviderSelectionCapability()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = sessionStateCoordinator.EnsureSessionTab(session);

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
            new(new ModelProviderId("anthropic"), "Anthropic"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates["anthropic"].Availability = ModelProviderAvailability.Ready;

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            sessionSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { },
            static (_, selectedTab) => !selectedTab.StatusBusy);

        coordinator.RefreshForSession(tab);
        Assert.IsTrue(workspaceViewModel.CanSelectModelProvider);

        tab.StatusBusy = true;
        coordinator.ApplyPromptAvailabilityProjection();
        Assert.IsFalse(workspaceViewModel.CanSelectModelProvider);

        tab.StatusBusy = false;
        coordinator.ApplyPromptAvailabilityProjection();

        Assert.IsTrue(workspaceViewModel.CanSelectModelProvider);
    }

    [TestMethod]
    public async Task OnModelProviderSelectionChangedAsync_UsesSwitchCallbackForSelectedSession()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = sessionStateCoordinator.EnsureSessionTab(session);

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
            new(new ModelProviderId("anthropic"), "Anthropic"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates["anthropic"].Availability = ModelProviderAvailability.Ready;

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
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            sessionSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { },
            static (_, _) => true,
            (selectedSession, selectedTab, targetProviderId) =>
            {
                Assert.AreSame(session, selectedSession);
                Assert.AreSame(tab, selectedTab);
                Assert.AreEqual("anthropic", targetProviderId.Value);
                switchCallCount++;
                return Task.FromResult(true);
            },
            () => refreshedSelectionCount++);

        coordinator.RefreshForSession(tab);
        await coordinator.OnModelProviderSelectionChangedAsync(newIndex: 1);

        Assert.AreEqual(1, switchCallCount);
        Assert.AreEqual(1, refreshedSelectionCount);
    }

    [TestMethod]
    public async Task OnModelProviderSelectionChangedAsync_UpdatesSelectedIndexBeforeSwitchCompletes()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = sessionStateCoordinator.EnsureSessionTab(session);

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
            new(new ModelProviderId("anthropic"), "Anthropic"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates["anthropic"].Availability = ModelProviderAvailability.Ready;

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var switchCompletion = new TaskCompletionSource<bool>();
        var coordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            sessionSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { },
            static (_, _) => true,
            (_, selectedTab, targetProviderId) =>
            {
                session.ProviderId = targetProviderId.Value;
                selectedTab.ProviderId = new ModelProviderId(targetProviderId.Value);
                return switchCompletion.Task;
            });

        coordinator.RefreshForSession(tab);

        var selectionChanged = coordinator.OnModelProviderSelectionChangedAsync(newIndex: 1);

        Assert.AreEqual(1, workspaceViewModel.SelectedModelProviderIndex);
        Assert.IsFalse(selectionChanged.IsCompleted);

        switchCompletion.SetResult(true);
        await selectionChanged;
    }

    [TestMethod]
    public async Task OnModelProviderSelectionChangedAsync_DoesNotResetWhenPassingUnavailableProvider()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = sessionStateCoordinator.EnsureSessionTab(session);

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
            new(ModelProviderIds.Codex, "Codex"),
            new(new ModelProviderId("anthropic"), "Anthropic"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates[ModelProviderIds.Codex.Value].Availability = ModelProviderAvailability.Failed;
        providerStates["anthropic"].Availability = ModelProviderAvailability.Ready;

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var switchCallCount = 0;
        var coordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            sessionSelection,
            preferences,
            new WorkspaceRefreshContext(static _ => { }),
            static _ => null,
            static () => { },
            static (_, _) => true,
            (_, selectedTab, targetProviderId) =>
            {
                switchCallCount++;
                session.ProviderId = targetProviderId.Value;
                session.ProviderKey = targetProviderId.Value;
                selectedTab.ProviderId = targetProviderId;
                return Task.FromResult(true);
            });

        coordinator.RefreshForSession(tab);
        await coordinator.OnModelProviderSelectionChangedAsync(newIndex: 1);

        Assert.AreEqual(1, workspaceViewModel.SelectedModelProviderIndex, "Unavailable providers should not force the selector back while the user is navigating the dropdown.");
        Assert.AreEqual("openai", session.ProviderId);
        Assert.AreEqual(0, switchCallCount);

        await coordinator.OnModelProviderSelectionChangedAsync(newIndex: 2);

        Assert.AreEqual(2, workspaceViewModel.SelectedModelProviderIndex);
        Assert.AreEqual("anthropic", session.ProviderId);
        Assert.AreEqual("anthropic", tab.ProviderId.Value);
        Assert.AreEqual(1, switchCallCount);
    }

    [TestMethod]
    public void SessionWorkspaceViewModel_SelectedIndexChangesNotifyConfiguredHandlers()
    {
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var providerChanges = new List<int>();
        var modelChanges = new List<int>();
        var reasoningChanges = new List<int>();
        workspaceViewModel.SetModelProviderSelectionChangedHandlers(
            providerChanges.Add,
            modelChanges.Add,
            reasoningChanges.Add);

        workspaceViewModel.SelectedModelProviderIndex = 1;
        workspaceViewModel.SelectedModelIndex = 2;
        workspaceViewModel.SelectedReasoningIndex = 3;
        using (workspaceViewModel.SuppressSelectionChangedNotifications())
        {
            workspaceViewModel.SelectedModelProviderIndex = 4;
            workspaceViewModel.SelectedModelIndex = 5;
            workspaceViewModel.SelectedReasoningIndex = 6;
        }

        CollectionAssert.AreEqual(new[] { 1 }, providerChanges);
        CollectionAssert.AreEqual(new[] { 2 }, modelChanges);
        CollectionAssert.AreEqual(new[] { 3 }, reasoningChanges);
    }

    [TestMethod]
    public void OnModelSelectionChanged_ImmediatelyUpdatesSelectedIndexForOpenSession()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = sessionStateCoordinator.EnsureSessionTab(session);

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates["openai"].Models.Add(new AgentModelInfo("gpt-5.4", DisplayName: "GPT-5.4"));
        providerStates["openai"].Models.Add(new AgentModelInfo("gpt-5.4-mini", DisplayName: "GPT-5.4 Mini"));

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            sessionSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { },
            static (_, _) => true);

        coordinator.RefreshForSession(tab);
        coordinator.OnModelSelectionChanged(newIndex: 1);

        Assert.AreEqual(1, workspaceViewModel.SelectedModelIndex);
        Assert.AreEqual("gpt-5.4-mini", tab.ModelId);
    }

    [TestMethod]
    public void OnModelSelectionChanged_DraftScope_DoesNotReapplyDefaultsOverExplicitSelection()
    {
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        var providerState = providerStates["openai"];
        providerState.Availability = ModelProviderAvailability.Ready;
        providerState.Models.Add(new AgentModelInfo("gpt-5.4", DisplayName: "GPT-5.4"));
        providerState.Models.Add(new AgentModelInfo("gpt-5.4-mini", DisplayName: "GPT-5.4 Mini"));

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var draftPreferenceApplyCount = 0;
        var preferences = new FrontendModelProviderPreferencePort(
            state =>
            {
                draftPreferenceApplyCount++;
                state.SelectedModelId = "gpt-5.4";
                state.SelectedReasoningEffort = ModelProviderPresentation.ResolvePreferredReasoningEffort(
                    ModelProviderPreferenceCoordinator.FindModel(state.Models, state.SelectedModelId),
                    preferredReasoningEffort: null);
            },
            static _ => throw new NotSupportedException(),
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            CreateSessionSelectionContext(),
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { });

        coordinator.RefreshForDraftScope(new ModelProviderId("openai"));
        coordinator.OnModelSelectionChanged(newIndex: 1);

        Assert.AreEqual(1, workspaceViewModel.SelectedModelIndex);
        Assert.AreEqual("gpt-5.4-mini", providerState.SelectedModelId);
        Assert.AreEqual(1, draftPreferenceApplyCount);
    }

    [TestMethod]
    public void RefreshForDraftScope_DoesNotUsePreviousSessionProviderSelection()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        session.ProviderId = "anthropic";
        session.ProviderKey = "anthropic";
        var tab = sessionStateCoordinator.EnsureSessionTab(session);
        tab.ProviderId = new ModelProviderId("anthropic");

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
            new(new ModelProviderId("anthropic"), "Anthropic"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates["openai"].Models.Add(new AgentModelInfo("gpt-5.4"));
        providerStates["anthropic"].Availability = ModelProviderAvailability.Ready;
        providerStates["anthropic"].Models.Add(new AgentModelInfo("claude-sonnet-4.5"));

        var coordinator = CreateCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            static _ => null,
            sessionSelection: sessionSelection,
            applySessionModelProviderPreference: static _ => { });

        sessionStateCoordinator.SelectProjectScope("project-1");
        coordinator.RefreshForDraftScope(new ModelProviderId("openai"));
        Assert.AreEqual(0, workspaceViewModel.SelectedModelProviderIndex);

        sessionStateCoordinator.OpenSession(session.SessionId);
        coordinator.RefreshForSession(tab);
        Assert.AreEqual(1, workspaceViewModel.SelectedModelProviderIndex);

        sessionStateCoordinator.SelectProjectScope("project-1");
        coordinator.RefreshForDraftScope();

        Assert.AreEqual(0, workspaceViewModel.SelectedModelProviderIndex);
    }

    [TestMethod]
    public void RefreshForDraftScope_RemembersDraftProviderPerScope()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out _);
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
            new(new ModelProviderId("anthropic"), "Anthropic"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates["openai"].Models.Add(new AgentModelInfo("gpt-5.4"));
        providerStates["anthropic"].Availability = ModelProviderAvailability.Ready;
        providerStates["anthropic"].Models.Add(new AgentModelInfo("claude-sonnet-4.5"));

        var coordinator = CreateCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            static _ => null,
            sessionSelection: sessionSelection,
            applySessionModelProviderPreference: static _ => { });

        sessionStateCoordinator.SelectProjectScope("project-1");
        coordinator.RefreshForDraftScope(new ModelProviderId("anthropic"));
        Assert.AreEqual(1, workspaceViewModel.SelectedModelProviderIndex);

        sessionStateCoordinator.SelectGlobalScope();
        coordinator.RefreshForDraftScope(new ModelProviderId("openai"));
        Assert.AreEqual(0, workspaceViewModel.SelectedModelProviderIndex);

        sessionStateCoordinator.SelectProjectScope("project-1");
        coordinator.RefreshForDraftScope();

        Assert.AreEqual(1, workspaceViewModel.SelectedModelProviderIndex);
    }

    [TestMethod]
    public void RefreshForDraftScope_DoesNotUsePreviousSessionModelSelection()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = sessionStateCoordinator.EnsureSessionTab(session);

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        var providerState = providerStates["openai"];
        providerState.Availability = ModelProviderAvailability.Ready;
        providerState.Models.Add(new AgentModelInfo("gpt-5.4", DisplayName: "GPT-5.4"));
        providerState.Models.Add(new AgentModelInfo("gpt-5.4-mini", DisplayName: "GPT-5.4 Mini"));

        var coordinator = CreateCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            static _ => null,
            sessionSelection: sessionSelection,
            applySessionModelProviderPreference: static _ => { });

        sessionStateCoordinator.SelectProjectScope("project-1");
        coordinator.RefreshForDraftScope(new ModelProviderId("openai"));
        coordinator.OnModelSelectionChanged(newIndex: 1);
        Assert.AreEqual("gpt-5.4-mini", providerState.SelectedModelId);

        sessionStateCoordinator.OpenSession(session.SessionId);
        coordinator.RefreshForSession(tab);
        coordinator.OnModelSelectionChanged(newIndex: 0);
        Assert.AreEqual("gpt-5.4", tab.ModelId);

        sessionStateCoordinator.SelectProjectScope("project-1");
        coordinator.RefreshForDraftScope();

        Assert.AreEqual(1, workspaceViewModel.SelectedModelIndex);
        Assert.AreEqual("gpt-5.4-mini", providerState.SelectedModelId);
    }

    [TestMethod]
    public void OnReasoningSelectionChanged_ImmediatelyUpdatesSelectedIndexForOpenSession()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = sessionStateCoordinator.EnsureSessionTab(session);

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates["openai"].Models.Add(new AgentModelInfo(
            "gpt-5.4",
            DisplayName: "GPT-5.4",
            SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High]));
        tab.ModelId = "gpt-5.4";

        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            static _ => { },
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        var coordinator = new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            sessionSelection,
            preferences,
            workspaceRefresh,
            static _ => null,
            static () => { },
            static (_, _) => true);

        coordinator.RefreshForSession(tab);
        coordinator.OnReasoningSelectionChanged(newIndex: 1);

        Assert.AreEqual(1, workspaceViewModel.SelectedReasoningIndex);
        Assert.AreEqual(AgentReasoningEffort.High, tab.ReasoningEffort);
    }

    [TestMethod]
    public async Task SelectProviderModelAsync_DraftScope_SelectsRequestedModel()
    {
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        var providerState = providerStates["openai"];
        providerState.Availability = ModelProviderAvailability.Ready;
        providerState.Models.Add(new AgentModelInfo("gpt-5.4", DisplayName: "GPT-5.4"));
        providerState.Models.Add(new AgentModelInfo("gpt-5.4-mini", DisplayName: "GPT-5.4 Mini"));

        var coordinator = CreateCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            static _ => null);

        var selected = await coordinator.SelectProviderModelAsync(new ModelProviderId("openai"), "gpt-5.4-mini");

        Assert.IsTrue(selected);
        Assert.AreEqual("gpt-5.4-mini", providerState.SelectedModelId);
        Assert.AreEqual(1, workspaceViewModel.SelectedModelIndex);
    }

    [TestMethod]
    public async Task SelectProviderModelAsync_OpenSession_SelectsRequestedModel()
    {
        using var temp = TempDirectory.Create();
        var sessionStateCoordinator = CreateSessionStateCoordinator(temp.Path, out var session);
        var sessionSelection = new SessionSelectionContext(
            sessionStateCoordinator,
            static (_, _) => Task.CompletedTask,
            static _ => true);
        var tab = sessionStateCoordinator.EnsureSessionTab(session);

        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        ModelProviderDescriptor[] providerDescriptors =
        [
            new(new ModelProviderId("openai"), "OpenAI"),
        ];
        var providerStates = ModelProviderPresentation.CreateProviderStates(providerDescriptors);
        providerStates["openai"].Availability = ModelProviderAvailability.Ready;
        providerStates["openai"].Models.Add(new AgentModelInfo("gpt-5.4", DisplayName: "GPT-5.4"));
        providerStates["openai"].Models.Add(new AgentModelInfo("gpt-5.4-mini", DisplayName: "GPT-5.4 Mini"));

        var coordinator = CreateCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            static _ => null,
            sessionSelection: sessionSelection,
            applySessionModelProviderPreference: static _ => { });

        var selected = await coordinator.SelectProviderModelAsync(new ModelProviderId("openai"), "gpt-5.4-mini");

        Assert.IsTrue(selected);
        Assert.AreEqual("gpt-5.4-mini", tab.ModelId);
        Assert.AreEqual(1, workspaceViewModel.SelectedModelIndex);
    }

    private static ModelProviderSelectorCoordinator CreateCoordinator(
        IReadOnlyList<ModelProviderDescriptor> providerDescriptors,
        SessionWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        Dictionary<string, ModelProviderState> providerStates,
        Func<string?, string?> getEffectiveDefaultProviderKey,
        Func<IReadOnlyList<string>>? getConfiguredProviderKeys = null,
        SessionSelectionContext? sessionSelection = null,
        Action<OpenSessionState>? applySessionModelProviderPreference = null)
    {
        var selectorState = new ModelProviderSelectorStateStore(workspaceViewModel, new InlineUiDispatcher());
        var preferences = new FrontendModelProviderPreferencePort(
            ApplyDraftModelProviderPreference,
            applySessionModelProviderPreference ?? (static _ => throw new NotSupportedException()),
            static (_, _, _) => { },
            static (_, _, _, _) => { });
        var workspaceRefresh = new WorkspaceRefreshContext(static _ => { });
        return new ModelProviderSelectorCoordinator(
            providerDescriptors,
            workspaceViewModel,
            promptComposerViewModel,
            providerStates,
            selectorState,
            sessionSelection ?? CreateSessionSelectionContext(),
            preferences,
            workspaceRefresh,
            getEffectiveDefaultProviderKey,
            static () => { },
            getConfiguredProviderKeys: getConfiguredProviderKeys);
    }

    private static SessionSelectionContext CreateSessionSelectionContext()
    {
        var coordinator = (ShellSessionStateCoordinator)RuntimeHelpers.GetUninitializedObject(typeof(ShellSessionStateCoordinator));
        var selectionCoordinatorField = typeof(ShellSessionStateCoordinator).GetField("_selectionCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(selectionCoordinatorField);
        selectionCoordinatorField.SetValue(coordinator, new ShellSelectionCoordinator());
        return new SessionSelectionContext(
            coordinator,
            static (_, _) => Task.CompletedTask,
            static _ => false);
    }

    private static ShellSessionStateCoordinator CreateSessionStateCoordinator(string rootPath, out SessionViewDescriptor session)
    {
        var options = new CatalogOptions { GlobalRoot = rootPath };
        var coordinator = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new SessionViewCatalog(options),
            new InlineUiDispatcher(),
            new ShellStateStore(new InlineUiDispatcher()));

        var project = new ProjectDescriptor
        {
            Id = "project-1",
            Slug = "project-1",
            Name = "Project 1",
            DisplayName = "Project 1",
            ProjectPath = Path.Combine(rootPath, "project-1"),
            DefaultBranch = "main",
        };
        session = new SessionViewDescriptor
        {
            SessionId = "openai:session-1",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = "openai",
            ProviderKey = "openai",
            ProjectRef = project.Id,
            WorkingDirectory = project.ProjectPath,
            Title = "Review startup",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"),
            UpdatedAt = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"),
            LastActiveAt = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"),
            StartedAt = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00"),
        };

        coordinator.ApplyRecoveredCatalogState([project], [session]);
        coordinator.OpenSession(session.SessionId);
        return coordinator;
    }

    private static void ApplyDraftModelProviderPreference(ModelProviderState providerState)
    {
        providerState.SelectedModelId = ModelProviderPresentation.ResolvePreferredModelId(
            providerState.Models,
            providerState.SelectedModelId);
        providerState.SelectedReasoningEffort = ModelProviderPresentation.ResolvePreferredReasoningEffort(
            ModelProviderPreferenceCoordinator.FindModel(providerState.Models, providerState.SelectedModelId),
            providerState.SelectedReasoningEffort);
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

