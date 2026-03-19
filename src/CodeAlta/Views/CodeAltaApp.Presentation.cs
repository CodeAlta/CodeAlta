using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.ViewModels;
using XenoAtom.Ansi;
using XenoAtom.Logging;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Figlet;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;
using XenoAtom.Terminal.UI.Threading;

internal sealed partial class CodeAltaApp
{
    private TabControl CreateThreadTabControl()
    {
        var control = new TabControl()
            .Style(TabControlStyle.NoBorder);
        control.RegisterDynamicUpdate(_ => OnThreadTabControlSelectionChanged(control.SelectedIndex));
        return control;
    }

    private void SyncThreadTabControl()
    {
        if (_threadTabControl is null)
        {
            return;
        }

        _syncingThreadTabPages = true;
        try
        {
            var desiredPages = new List<TabPage>();
            foreach (var threadId in _viewState.OpenThreadIds)
            {
                var thread = FindThread(threadId);
                if (thread is null)
                {
                    continue;
                }

                desiredPages.Add(EnsureThreadTabPage(thread));
            }

            if (_draftTabOpen)
            {
                desiredPages.Add(EnsureDraftTabPage());
            }

            _threadTabControl.IsVisible = desiredPages.Count > 0;

            var existingPages = _threadTabControl.Tabs;
            var matches = existingPages.Count == desiredPages.Count;
            if (matches)
            {
                for (var i = 0; i < desiredPages.Count; i++)
                {
                    if (!ReferenceEquals(existingPages[i], desiredPages[i]))
                    {
                        matches = false;
                        break;
                    }
                }
            }

            if (!matches)
            {
                for (var i = existingPages.Count - 1; i >= 0; i--)
                {
                    _threadTabControl.TryCloseTab(existingPages[i]);
                }

                foreach (var page in desiredPages)
                {
                    _threadTabControl.AddTab(page);
                }
            }

            SyncThreadTabControlSelection();
        }
        finally
        {
            _syncingThreadTabPages = false;
        }
    }

    private TabPage EnsureThreadTabPage(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var tab = EnsureThreadTab(thread);
        if (tab.Page is not null)
        {
            tab.Page.Data = thread.ThreadId;
            return tab.Page;
        }

        var header = CreateComputedVisual(
            () =>
            {
                var current = tab.Thread;
                return new HStack(
                    [
                        CreateOpenTabIndicator(tab.ViewModel.StatusBusy, tab.ViewModel.StatusTone),
                        CreateOpenTabTitle(CompactTabTitle(tab.ViewModel.Title)),
                    ])
                {
                    Spacing = 1,
                }.Tooltip(current.Title);
            });

        var page = new TabPage(header, CreateThreadTabPageContentPlaceholder())
        {
            Data = thread.ThreadId,
            ShowCloseButton = true,
        };
        page.RequestClosing += (_, e) =>
        {
            if (e.Reason != TabCloseReason.CloseButton || e.Page.Data is not string threadId)
            {
                return;
            }

            e.Cancel = true;
            _ = CloseThreadAsync(threadId);
        };

        tab.Page = page;
        return page;
    }

    private TabPage EnsureDraftTabPage()
    {
        if (_draftTabPage is not null)
        {
            return _draftTabPage;
        }

        var header = CreateComputedVisual(
            () => new HStack(
                [
                    CreateOpenTabIndicator(isBusy: false, StatusTone.Info),
                    CreateOpenTabTitle(BuildDraftTabTitle(GetSelectedProject(), _globalScopeSelected)),
                ])
            {
                Spacing = 1,
            });

        var page = new TabPage(header, CreateThreadTabPageContentPlaceholder())
        {
            Data = DraftTabId,
            ShowCloseButton = true,
        };
        page.RequestClosing += (_, e) =>
        {
            if (e.Reason != TabCloseReason.CloseButton || !string.Equals(e.Page.Data as string, DraftTabId, StringComparison.Ordinal))
            {
                return;
            }

            e.Cancel = true;
            _ = CloseDraftTabAsync();
        };

        _draftTabPage = page;
        return page;
    }

    private static Visual CreateThreadTabPageContentPlaceholder()
        // The active thread flow is hosted by the splitter, so tabs need a detached placeholder.
        => new Placeholder
        {
            IsVisible = false,
        };

    private void SyncThreadTabControlSelection()
    {
        if (_threadTabControl is null || _threadTabControl.Tabs.Count == 0)
        {
            return;
        }

        var selectedIndex = -1;
        if (string.IsNullOrWhiteSpace(_selectedThreadId))
        {
            if (_draftTabOpen)
            {
                for (var i = 0; i < _threadTabControl.Tabs.Count; i++)
                {
                    if (string.Equals(_threadTabControl.Tabs[i].Data as string, DraftTabId, StringComparison.Ordinal))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }
        }
        else
        {
            for (var i = 0; i < _threadTabControl.Tabs.Count; i++)
            {
                if (_threadTabControl.Tabs[i].Data is string threadId &&
                    string.Equals(threadId, _selectedThreadId, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }
        }

        if (selectedIndex < 0 || _threadTabControl.SelectedIndex == selectedIndex)
        {
            return;
        }

        _syncingThreadTabSelection = true;
        try
        {
            _threadTabControl.SelectedIndex = selectedIndex;
        }
        finally
        {
            _syncingThreadTabSelection = false;
        }
    }

    private void OnThreadTabControlSelectionChanged(int selectedIndex)
    {
        if (_syncingThreadTabSelection || _syncingThreadTabPages || _threadTabControl is null)
        {
            return;
        }

        if (selectedIndex < 0 || selectedIndex >= _threadTabControl.Tabs.Count)
        {
            return;
        }

        if (string.Equals(_threadTabControl.Tabs[selectedIndex].Data as string, DraftTabId, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(_selectedThreadId))
            {
                return;
            }

            _ = ActivateDraftTabAsync();
            return;
        }

        if (_threadTabControl.Tabs[selectedIndex].Data is not string threadId ||
            string.Equals(threadId, _selectedThreadId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(threadId, _pendingThreadTabSelectionThreadId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pendingThreadTabSelectionThreadId = threadId;
        GetUiDispatcher().Post(
            () =>
            {
                if (!string.Equals(threadId, _pendingThreadTabSelectionThreadId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _pendingThreadTabSelectionThreadId = null;
                OpenThread(threadId);
            });
    }

    private void RefreshShellChrome()
        => DispatchToUi(RefreshShellChromeCore);

    internal void RefreshCatalogAndThreadWorkspace()
    {
        DispatchToUi(
            () =>
            {
                RefreshCatalogAndThreadWorkspaceCore();
            });
    }

    private void RefreshHeaderAndThreadWorkspace()
    {
        DispatchToUi(
            () =>
            {
                RefreshHeaderAndThreadWorkspaceCore();
            });
    }

    private void RefreshSelectionAndThreadWorkspace()
    {
        DispatchToUi(
            () =>
            {
                RefreshSelectionAndThreadWorkspaceCore();
            });
    }

    private void RefreshHeaderAndThreadWorkspaceCore()
    {
        EnsureSelectionDefaults();
        _shellViewModel.HeaderText = BuildHeaderText();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshShellChromeCore()
    {
        EnsureSelectionDefaults();
        _shellViewModel.HeaderText = BuildHeaderText();
        RefreshSidebarProjection();
    }

    private void RefreshCatalogAndThreadWorkspaceCore()
    {
        RefreshShellChromeCore();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshSelectionAndThreadWorkspaceCore()
    {
        EnsureSelectionDefaults();
        _shellViewModel.HeaderText = BuildHeaderText();
        SyncSidebarSelectionToCurrentState();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshThreadWorkspaceCore()
    {
        _viewRefreshState.Value++;
        _usageRefreshState.Value++;
        RefreshThreadPaneContent();
    }

    private void RefreshThreadPaneContent()
    {
        if (_threadPaneLayout is null || _threadBodySplitter is null || _threadInput is null)
        {
            return;
        }

        SyncThreadTabControl();

        var selectedThread = GetSelectedThread();
        if (selectedThread is null)
        {
            RefreshChatSelectorsForDraftScope();
            UpdatePromptAvailabilityUi();
            _threadBodySplitter.First = BuildWelcomePane(GetSelectedProject(), _globalScopeSelected);
            SetReadyStatusForCurrentSelection();

            return;
        }

        var tab = EnsureThreadTab(selectedThread);
        RefreshChatSelectorsForThread(tab);
        UpdatePromptAvailabilityUi();
        _threadBodySplitter.First = tab.Timeline.Flow;
        SetReadyStatusForCurrentSelection();
    }

    private void RefreshChatSelectorsForDraftScope(AgentBackendId? preferredBackendId = null)
    {
        _chatSelectorsRefreshing = true;
        try
        {
            var backendSelect = _chatBackendSelect!;
            var modelSelect = _chatModelSelect!;
            var reasoningSelect = _chatReasoningSelect!;
            var autoScrollCheckBox = _chatAutoScrollCheckBox!;
            var backendOptions = ChatBackendPresentation.BuildBackendOptions();
            ChatBackendPresentation.ReplaceSelectItems(backendSelect, backendOptions);

            var backendId = preferredBackendId ?? GetPreferredDraftBackendId(backendOptions);
            var backendIndex = Math.Max(0, backendOptions.FindIndex(option => string.Equals(option.BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase)));
            backendSelect.SelectedIndex = backendIndex;
            backendSelect.IsEnabled = true;

            var backendState = _chatBackendStates[backendOptions[backendIndex].BackendId.Value];
            ApplyDraftBackendPreference(backendState);
            var modelOptions = ChatBackendPresentation.BuildModelOptions(backendState);
            ChatBackendPresentation.ReplaceSelectItems(modelSelect, modelOptions);
            modelSelect.SelectedIndex = Math.Clamp(
                modelOptions.FindIndex(option => string.Equals(option.ModelId, backendState.SelectedModelId, StringComparison.Ordinal)),
                0,
                Math.Max(0, modelOptions.Count - 1));
            modelSelect.IsEnabled = backendState.Availability == ChatBackendAvailability.Ready;

            var selectedModel = backendState.Models.FirstOrDefault(model => string.Equals(model.Id, backendState.SelectedModelId, StringComparison.Ordinal))
                ?? ChatBackendPresentation.GetSelectedModel(backendState);
            var reasoningOptions = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
            ChatBackendPresentation.ReplaceSelectItems(reasoningSelect, reasoningOptions);
            reasoningSelect.SelectedIndex = Math.Clamp(
                reasoningOptions.FindIndex(option => option.Effort == backendState.SelectedReasoningEffort),
                0,
                Math.Max(0, reasoningOptions.Count - 1));
            reasoningSelect.IsEnabled = backendState.Availability == ChatBackendAvailability.Ready;
            autoScrollCheckBox.IsChecked = true;
            autoScrollCheckBox.IsEnabled = false;

            _threadWorkspaceViewModel.BackendStatusMarkup = ChatBackendPresentation.BuildBackendStatusMarkup(_chatBackendStates.Values, backendOptions[backendIndex].BackendId, isInitializing: false);
            _threadWorkspaceViewModel.CanSelectBackend = true;
            _threadWorkspaceViewModel.CanSelectModel = backendState.Availability == ChatBackendAvailability.Ready;
            _threadWorkspaceViewModel.CanSelectReasoning = backendState.Availability == ChatBackendAvailability.Ready;
            _threadWorkspaceViewModel.CanToggleAutoScroll = false;
        }
        finally
        {
            _chatSelectorsRefreshing = false;
        }
    }

    private AgentBackendId GetPreferredDraftBackendId(IReadOnlyList<ChatBackendOption> backendOptions)
    {
        if (_chatBackendSelect is not null &&
            (uint)_chatBackendSelect.SelectedIndex < (uint)backendOptions.Count)
        {
            var current = backendOptions[_chatBackendSelect.SelectedIndex].BackendId;
            if (IsChatBackendReady(current))
            {
                return current;
            }
        }

        var readyBackend = backendOptions.FirstOrDefault(option => IsChatBackendReady(option.BackendId));
        if (readyBackend is not null)
        {
            return readyBackend.BackendId;
        }

        return backendOptions.FirstOrDefault()?.BackendId ?? AgentBackendIds.Codex;
    }

    private void RefreshChatSelectorsForThread(ThreadTabState tab)
    {
        _chatSelectorsRefreshing = true;
        try
        {
            var backendSelect = _chatBackendSelect!;
            var modelSelect = _chatModelSelect!;
            var reasoningSelect = _chatReasoningSelect!;
            var autoScrollCheckBox = _chatAutoScrollCheckBox!;
            var backendOptions = ChatBackendPresentation.BuildBackendOptions();
            ChatBackendPresentation.ReplaceSelectItems(backendSelect, backendOptions);
            backendSelect.SelectedIndex = Math.Clamp(
                backendOptions.FindIndex(option => string.Equals(option.BackendId.Value, tab.BackendId.Value, StringComparison.OrdinalIgnoreCase)),
                0,
                Math.Max(0, backendOptions.Count - 1));

            var backendState = _chatBackendStates[tab.BackendId.Value];
            ApplyThreadPreference(tab);

            var modelOptions = ChatBackendPresentation.BuildModelOptions(backendState);
            ChatBackendPresentation.ReplaceSelectItems(modelSelect, modelOptions);
            modelSelect.SelectedIndex = Math.Clamp(
                modelOptions.FindIndex(option => string.Equals(option.ModelId, tab.ModelId ?? backendState.SelectedModelId, StringComparison.Ordinal)),
                0,
                Math.Max(0, modelOptions.Count - 1));
            modelSelect.IsEnabled = backendState.Availability == ChatBackendAvailability.Ready;

            var selectedModel = backendState.Models.FirstOrDefault(model =>
                string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal))
                ?? ChatBackendPresentation.GetSelectedModel(backendState);
            var reasoningOptions = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
            ChatBackendPresentation.ReplaceSelectItems(reasoningSelect, reasoningOptions);
            reasoningSelect.SelectedIndex = Math.Clamp(
                reasoningOptions.FindIndex(option => option.Effort == tab.ReasoningEffort),
                0,
                Math.Max(0, reasoningOptions.Count - 1));
            reasoningSelect.IsEnabled = backendState.Availability == ChatBackendAvailability.Ready;
            autoScrollCheckBox.IsChecked = tab.AutoScroll;
            autoScrollCheckBox.IsEnabled = true;

            backendSelect.IsEnabled = false;
            _threadWorkspaceViewModel.BackendStatusMarkup = ChatBackendPresentation.BuildBackendStatusMarkup(_chatBackendStates.Values, tab.BackendId, isInitializing: false);
            _threadWorkspaceViewModel.CanSelectBackend = false;
            _threadWorkspaceViewModel.CanSelectModel = backendState.Availability == ChatBackendAvailability.Ready;
            _threadWorkspaceViewModel.CanSelectReasoning = backendState.Availability == ChatBackendAvailability.Ready;
            _threadWorkspaceViewModel.CanToggleAutoScroll = true;
        }
        finally
        {
            _chatSelectorsRefreshing = false;
        }
    }

    private void OnChatBackendSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var options = ChatBackendPresentation.BuildBackendOptions();
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        var thread = GetSelectedThread();
        if (thread is null)
        {
            RefreshChatSelectorsForDraftScope(options[newIndex].BackendId);
            InvalidateSelectedSessionUsage();
            return;
        }

        if (thread.IsBackendLocked)
        {
            return;
        }

        var tab = EnsureThreadTab(thread);
        tab.BackendId = options[newIndex].BackendId;
        RefreshHeaderAndThreadWorkspace();
    }

    private void OnChatModelSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var thread = GetSelectedThread();
        if (thread is null)
        {
            var backendId = GetPreferredBackendId();
            var draftBackendState = _chatBackendStates[backendId.Value];
            var draftOptions = ChatBackendPresentation.BuildModelOptions(draftBackendState);
            if ((uint)newIndex >= (uint)draftOptions.Count)
            {
                return;
            }

            draftBackendState.SelectedModelId = draftOptions[newIndex].ModelId;
            var preferredModel = FindModel(draftBackendState.Models, draftBackendState.SelectedModelId);
            draftBackendState.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(preferredModel, preferredReasoningEffort: null);
            RememberGlobalBackendPreference(backendId, draftBackendState.SelectedModelId, draftBackendState.SelectedReasoningEffort);
            RefreshChatSelectorsForDraftScope(backendId);
            InvalidateSelectedSessionUsage();
            return;
        }

        var tab = EnsureThreadTab(thread);
        var backendState = _chatBackendStates[tab.BackendId.Value];
        var options = ChatBackendPresentation.BuildModelOptions(backendState);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ModelId = options[newIndex].ModelId;
        var selectedModel = FindModel(backendState.Models, tab.ModelId);
        tab.ReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(selectedModel, preferredReasoningEffort: null);
        RememberThreadPreference(tab.Thread.ThreadId, tab.ModelId, tab.ReasoningEffort, tab.AutoScroll, persistNow: true);
        backendState.SelectedModelId = tab.ModelId;
        backendState.SelectedReasoningEffort = tab.ReasoningEffort;
        RememberGlobalBackendPreference(tab.BackendId, tab.ModelId, tab.ReasoningEffort);
        RefreshHeaderAndThreadWorkspace();
    }

    private void OnChatReasoningSelectionChanged(int newIndex)
    {
        if (_chatSelectorsRefreshing)
        {
            return;
        }

        var thread = GetSelectedThread();
        if (thread is null)
        {
            var backendId = GetPreferredBackendId();
            var draftBackendState = _chatBackendStates[backendId.Value];
            var draftSelectedModel = draftBackendState.Models.FirstOrDefault(model => string.Equals(model.Id, draftBackendState.SelectedModelId, StringComparison.Ordinal));
            var draftOptions = ChatBackendPresentation.BuildReasoningOptions(draftSelectedModel);
            if ((uint)newIndex >= (uint)draftOptions.Count)
            {
                return;
            }

            draftBackendState.SelectedReasoningEffort = draftOptions[newIndex].Effort;
            RememberGlobalBackendPreference(backendId, draftBackendState.SelectedModelId, draftBackendState.SelectedReasoningEffort);
            InvalidateSelectedSessionUsage();
            return;
        }

        var tab = EnsureThreadTab(thread);
        var backendState = _chatBackendStates[tab.BackendId.Value];
        var selectedModel = backendState.Models.FirstOrDefault(model => string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal));
        var options = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ReasoningEffort = options[newIndex].Effort;
        RememberThreadPreference(tab.Thread.ThreadId, tab.ModelId, tab.ReasoningEffort, tab.AutoScroll, persistNow: true);
        backendState.SelectedModelId = tab.ModelId;
        backendState.SelectedReasoningEffort = tab.ReasoningEffort;
        RememberGlobalBackendPreference(tab.BackendId, tab.ModelId, tab.ReasoningEffort);
    }

    private void OnChatAutoScrollChanged()
    {
        if (_chatSelectorsRefreshing || _chatAutoScrollCheckBox is null)
        {
            return;
        }

        var thread = GetSelectedThread();
        if (thread is null)
        {
            return;
        }

        var tab = EnsureThreadTab(thread);
        if (tab.AutoScroll == _chatAutoScrollCheckBox.IsChecked)
        {
            return;
        }

        tab.AutoScroll = _chatAutoScrollCheckBox.IsChecked;
        RememberThreadPreference(tab.Thread.ThreadId, tab.ModelId, tab.ReasoningEffort, tab.AutoScroll, persistNow: true);
    }

    private AgentBackendId GetPreferredBackendId()
    {
        return UiDispatch.Invoke(
            GetUiDispatcher(),
            () =>
            {
                var options = ChatBackendPresentation.BuildBackendOptions();
                if (_chatBackendSelect is not null &&
                    (uint)_chatBackendSelect.SelectedIndex < (uint)options.Count)
                {
                    return options[_chatBackendSelect.SelectedIndex].BackendId;
                }

                var readyBackend = options.FirstOrDefault(option => IsChatBackendReady(option.BackendId));
                if (readyBackend is not null)
                {
                    return readyBackend.BackendId;
                }

                return AgentBackendIds.Codex;
            });
    }

    private void SelectGlobalScope()
    {
        _pendingThreadTabSelectionThreadId = null;
        _draftTabOpen = true;
        _globalScopeSelected = true;
        _selectedThreadId = null;
        _viewState.SelectedThreadId = null;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        RefreshSelectionAndThreadWorkspace();
    }

    private void SelectProjectScope(string projectId)
    {
        _pendingThreadTabSelectionThreadId = null;
        _draftTabOpen = true;
        _globalScopeSelected = false;
        _selectedProjectId = projectId;
        _selectedThreadId = null;
        _viewState.SelectedThreadId = null;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        RefreshSelectionAndThreadWorkspace();
    }

    private void EnsureSelectionDefaults()
    {
        if (!string.IsNullOrWhiteSpace(_selectedThreadId) &&
            _threads.All(thread => !string.Equals(thread.ThreadId, _selectedThreadId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedThreadId = null;
        }

        if (string.IsNullOrWhiteSpace(_selectedProjectId) ||
            _projects.All(project => !string.Equals(project.Id, _selectedProjectId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedProjectId = _projects.FirstOrDefault()?.Id;
        }

        if (!_globalScopeSelected && _selectedProjectId is null)
        {
            _globalScopeSelected = true;
        }
    }

    private string BuildHeaderText()
    {
        return BuildHeaderText(
            GetSelectedThread(),
            GetSelectedProject(),
            _catalogOptions.GlobalRoot,
            GetPreferredBackendId().Value,
            _globalScopeSelected);
    }

    internal static string BuildHeaderText(
        WorkThreadDescriptor? thread,
        ProjectDescriptor? selectedProject,
        string globalRoot,
        string preferredBackendId,
        bool globalScopeSelected)
    {
        if (thread is null)
        {
            if (globalScopeSelected)
            {
                return $"CodeAlta | {preferredBackendId} | global draft";
            }

            if (selectedProject is not null)
            {
                return $"CodeAlta | {preferredBackendId} | {selectedProject.Slug} draft";
            }

            return "CodeAlta | no thread selected";
        }

        return thread.Kind switch
        {
            WorkThreadKind.GlobalThread => $"CodeAlta | {thread.BackendId} | {CompactTabTitle(thread.Title)} | global",
            WorkThreadKind.ProjectThread => $"CodeAlta | {thread.BackendId} | {selectedProject?.Slug ?? "?"} | {CompactTabTitle(thread.Title)}",
            WorkThreadKind.InternalThread => $"CodeAlta | {thread.BackendId} | internal | {CompactTabTitle(thread.Title)}",
            _ => $"CodeAlta | thread={thread.Title}",
        };
    }

    internal static string BuildDraftPromptMessage(bool globalScopeSelected)
        => globalScopeSelected
            ? "Send the first prompt to start a global thread."
            : "Send the first prompt to start a thread for the selected project.";

    internal static string BuildDraftTabTitle(
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return "Global draft";
        }

        return selectedProject is null
            ? "Project draft"
            : $"{CompactTabTitle(selectedProject.DisplayName)} draft";
    }

    internal static string BuildDraftTabBodyText(
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return "Draft scope selected. Send a prompt to start a global thread.";
        }

        return selectedProject is null
            ? "Draft scope selected. Choose a project or send a prompt to start a thread."
            : $"Draft scope selected for '{selectedProject.DisplayName}'. Send a prompt to start a thread.";
    }

    internal static string BuildWelcomeSubtitle(ProjectDescriptor? selectedProject, bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return "Global workspace ready for a new thread.";
        }

        return selectedProject is null
            ? "Project draft selected. Choose a project or start typing below."
            : $"Next thread will start in {selectedProject.DisplayName}.";
    }

    internal static IReadOnlyList<string> BuildWelcomeGuidanceLines(
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        if (globalScopeSelected)
        {
            return
            [
                "Use the prompt below to start a new global thread.",
                "Pick a project in the sidebar before sending if you want repository context.",
                "Reopen any thread tab to continue previous work.",
            ];
        }

        if (selectedProject is null)
        {
            return
            [
                "Choose a project in the sidebar or keep typing below to prepare the next thread.",
                "Your first prompt will create the draft once a scope is selected.",
                "Reopen any thread tab to continue previous work.",
            ];
        }

        return
        [
            $"Use the prompt below to start a new thread for {selectedProject.DisplayName}.",
            "Switch projects in the sidebar before sending if you want a different scope.",
            "Reopen any thread tab to continue previous work.",
        ];
    }

    internal static Visual BuildWelcomePane(ProjectDescriptor? selectedProject, bool globalScopeSelected)
    {
        var guidanceLines = BuildWelcomeGuidanceLines(selectedProject, globalScopeSelected);
        return new Center(
            new VStack(
                [
                    BuildWelcomeLogo(),
                    new TextBlock(BuildWelcomeSubtitle(selectedProject, globalScopeSelected))
                    {
                        Wrap = true,
                        IsSelectable = false,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = Align.Stretch,
                    }
                        .Style(TextBlockStyle.Default with
                        {
                            Foreground = UiPalette.WelcomeSubtitleColor,
                            TextStyle = TextStyle.Bold,
                        }),
                    new TextBlock(guidanceLines[0])
                    {
                        Wrap = true,
                        IsSelectable = false,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = Align.Stretch,
                    }
                        .Style(TextBlockStyle.Default with
                        {
                            Foreground = UiPalette.WelcomeGuidanceColor,
                        }),
                    new TextBlock($"{NerdFont.MdArrowRightThinCircleOutline} {guidanceLines[1]}\n{NerdFont.MdTabPlus} {guidanceLines[2]}")
                    {
                        Wrap = true,
                        IsSelectable = false,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = Align.Stretch,
                    }
                        .Style(TextBlockStyle.Default with
                        {
                            Foreground = UiPalette.WelcomeGuidanceColor,
                        }),
                ])
            {
                Spacing = 1,
                HorizontalAlignment = Align.Center,
                VerticalAlignment = Align.Center,
                MaxWidth = 76,
            });
    }

    internal static FigletFont GetWelcomeFigletFont()
        => WelcomeFigletFont.Value;

    private static FigletFont LoadWelcomeFigletFont()
    {
        using var stream = typeof(CodeAltaApp).Assembly.GetManifestResourceStream("CodeAlta.Assets.3d.flf");
        if (stream is null)
        {
            throw new InvalidOperationException("Unable to load embedded welcome FIGlet font 'CodeAlta.Assets.3d.flf'.");
        }

        using var reader = new StreamReader(stream);
        return FigletFont.Parse(reader.ReadToEnd(), new FigletFontInfo("3-D", "Daniel Henninger"));
    }

    private static Visual BuildWelcomeLogo()
    {
        var font = GetWelcomeFigletFont();
        return new Center(
            new HStack(
                [
                    new TextFiglet("Code")
                        .Font(font)
                        .LetterSpacing(1)
                        .TrimTrailingSpaces(true)
                        .TextAlignment(TextAlignment.Left),
                    new TextFiglet("Alta")
                        .Font(font)
                        .LetterSpacing(1)
                        .TrimTrailingSpaces(true)
                        .TextAlignment(TextAlignment.Left)
                        .Style(() => BuildWelcomeAltaFigletStyle()),
                ])
            {
                Spacing = 2,
                HorizontalAlignment = Align.Center,
            });
    }

    private static TextFigletStyle BuildWelcomeAltaFigletStyle()
    {
        var phase = ComputeLoopAnimationPhase(DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 6L);
        return TextFigletStyle.Default with
        {
            ForegroundBrush = UiPalette.BuildWelcomeAltaBrush(phase),
        };
    }

    internal static float ComputeLoopAnimationPhase(long ticks, long cycleTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycleTicks);

        var normalizedTicks = ((ticks % cycleTicks) + cycleTicks) % cycleTicks;
        return (float)(normalizedTicks / (double)cycleTicks);
    }

    internal static string BuildReadyStatusText(
        WorkThreadDescriptor? thread,
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        _ = thread;
        _ = selectedProject;
        _ = globalScopeSelected;
        return ReadyStatusMessage;
    }

    internal static string BuildThinkingStatusText() => ThinkingStatusMessage;

    internal static string BuildStatusIconMarkup(StatusTone tone)
    {
        return tone switch
        {
            StatusTone.Ready => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Ready)}]{NerdFont.MdCheckCircleOutline}[/]",
            StatusTone.Warning => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Warning)}]{NerdFont.MdAlertOutline}[/]",
            StatusTone.Error => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Error)}]{NerdFont.MdAlertCircleOutline}[/]",
            _ => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Info)}]{NerdFont.OctInfo}[/]",
        };
    }

    internal static TextBlockStyle BuildStatusTextStyle(string message, bool busy, StatusTone tone)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (busy && string.Equals(message, ThinkingStatusMessage, StringComparison.Ordinal))
        {
            var phase = ComputeLoopAnimationPhase(DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 5L);
            var sweepBrush = Brush.LinearGradient(
                new GradientPoint(-0.55f + (0.75f * phase), 0f),
                new GradientPoint(0.20f + (0.75f * phase), 0f),
                BuildThinkingGradientStops(),
                tileMode: BrushTileMode.Repeat,
                mixSpaceOverride: ColorMixSpace.Oklab);
            return TextBlockStyle.Default with { ForegroundBrush = sweepBrush };
        }

        return TextBlockStyle.Default with { Foreground = UiPalette.GetStatusToneColor(tone) };
    }

    internal static GradientStop[] BuildThinkingGradientStops()
    {
        var baseColor = UiPalette.GetStatusToneColor(StatusTone.Info);
        var glowColor = Color.Mix(baseColor, Colors.White, 0.26f, ColorMixSpace.Oklab);
        var highlightColor = Color.Mix(baseColor, Colors.White, 0.52f, ColorMixSpace.Oklab);
        return
        [
            new GradientStop(0.00f, baseColor.WithOpacity(0.50f)),
            new GradientStop(0.12f, glowColor.WithOpacity(0.62f)),
            new GradientStop(0.22f, highlightColor),
            new GradientStop(0.34f, glowColor.WithOpacity(0.66f)),
            new GradientStop(0.48f, baseColor.WithOpacity(0.56f)),
            new GradientStop(0.62f, glowColor.WithOpacity(0.64f)),
            new GradientStop(0.74f, Colors.White),
            new GradientStop(0.86f, glowColor.WithOpacity(0.68f)),
            new GradientStop(1.00f, baseColor.WithOpacity(0.50f)),
        ];
    }

    private static string BuildPromptPlaceholder(
        WorkThreadDescriptor? thread,
        ProjectDescriptor? selectedProject,
        bool globalScopeSelected)
    {
        if (thread is not null)
        {
            return $"Continue '{thread.Title}'...";
        }

        if (globalScopeSelected)
        {
            return "Start a global thread...";
        }

        return selectedProject is null
            ? "Select a project to start a thread..."
            : $"Start a thread for {selectedProject.DisplayName}...";
    }

    internal static string BuildPromptUnavailablePlaceholder(
        WorkThreadDescriptor? thread,
        string backendDisplayName,
        ChatBackendAvailability availability,
        bool anyBackendReady)
    {
        if (thread is not null)
        {
            return availability == ChatBackendAvailability.Connecting
                ? $"Waiting for {backendDisplayName} to reconnect..."
                : $"'{thread.Title}' is unavailable until {backendDisplayName} is connected.";
        }

        if (availability == ChatBackendAvailability.Connecting)
        {
            return $"Connecting to {backendDisplayName}...";
        }

        return anyBackendReady
            ? "Select a connected backend to start a thread..."
            : "Install or connect Codex/Copilot to start a thread...";
    }

    internal static string BuildPromptUnavailableStatusText(
        WorkThreadDescriptor? thread,
        string backendDisplayName,
        ChatBackendAvailability availability,
        bool anyBackendReady)
    {
        if (thread is not null)
        {
            return availability == ChatBackendAvailability.Connecting
                ? $"Reconnecting '{thread.Title}' to {backendDisplayName}. Prompt sending is temporarily unavailable."
                : $"'{thread.Title}' is unavailable because {backendDisplayName} is not connected.";
        }

        if (availability == ChatBackendAvailability.Connecting)
        {
            return $"Connecting to {backendDisplayName}. Prompt sending will be available once the backend is ready.";
        }

        return anyBackendReady
            ? "Select a connected backend to send prompts."
            : "No chat backend is connected. Browse threads and projects, but prompt sending is unavailable.";
    }

    private static string CompactTabTitle(string title)
    {
        var normalized = title.Trim();
        return normalized.Length <= MaxTabTitleLength
            ? normalized
            : normalized[..Math.Max(1, MaxTabTitleLength - 1)].TrimEnd() + "…";
    }

    internal static OpenTabIndicatorKind ResolveOpenTabIndicatorKind(bool isBusy, StatusTone tone)
    {
        if (isBusy)
        {
            return OpenTabIndicatorKind.Running;
        }

        return tone switch
        {
            StatusTone.Warning => OpenTabIndicatorKind.Warning,
            StatusTone.Error => OpenTabIndicatorKind.Error,
            StatusTone.Info => OpenTabIndicatorKind.Info,
            _ => OpenTabIndicatorKind.Ready,
        };
    }

    private static Visual CreateOpenTabIndicator(bool isBusy, StatusTone tone)
    {
        var kind = ResolveOpenTabIndicatorKind(isBusy, tone);
        if (kind == OpenTabIndicatorKind.Running)
        {
            var spinner = new Spinner().Style(SpinnerStyles.Arc);
            spinner.IsActive(() => true);
            spinner.IsVisible(() => true);
            return spinner;
        }

        var statusTone = kind switch
        {
            OpenTabIndicatorKind.Warning => StatusTone.Warning,
            OpenTabIndicatorKind.Error => StatusTone.Error,
            OpenTabIndicatorKind.Info => StatusTone.Info,
            _ => StatusTone.Ready,
        };
        return new Markup(BuildStatusIconMarkup(statusTone))
        {
            Wrap = false,
        };
    }

    private static Visual CreateOpenTabTitle(string title)
    {
        return new Markup(AnsiMarkup.Escape(title))
        {
            Wrap = false,
        };
    }

    private bool IsChatBackendReady(AgentBackendId backendId)
    {
        return _chatBackendStates.TryGetValue(backendId.Value, out var state) &&
               state.Availability == ChatBackendAvailability.Ready;
    }

    private bool HasAnyReadyChatBackend()
        => _chatBackendStates.Values.Any(static state => state.Availability == ChatBackendAvailability.Ready);

    private bool TryGetPromptUnavailableStatus(out string message, out StatusTone tone)
    {
        var selectedThread = GetSelectedThread();
        var backendId = selectedThread is not null ? new AgentBackendId(selectedThread.BackendId) : GetPreferredBackendId();
        if (!_chatBackendStates.TryGetValue(backendId.Value, out var backendState) ||
            backendState.Availability == ChatBackendAvailability.Ready)
        {
            message = string.Empty;
            tone = StatusTone.Ready;
            return false;
        }

        message = BuildPromptUnavailableStatusText(
            selectedThread,
            backendState.DisplayName,
            backendState.Availability,
            HasAnyReadyChatBackend());
        tone = backendState.Availability == ChatBackendAvailability.Connecting
            ? StatusTone.Info
            : StatusTone.Warning;
        return true;
    }

    private bool TrySetPromptUnavailableStatus()
    {
        if (!TryGetPromptUnavailableStatus(out var message, out var tone))
        {
            return false;
        }

        SetStatus(message, tone: tone);
        return true;
    }

    private void UpdatePromptAvailabilityUi()
    {
        var selectedThread = GetSelectedThread();
        if (TryGetPromptUnavailableStatus(out _, out _) &&
            _chatBackendStates.TryGetValue(
                (selectedThread is not null ? new AgentBackendId(selectedThread.BackendId) : GetPreferredBackendId()).Value,
                out var backendState))
        {
            _promptComposerViewModel.Placeholder = BuildPromptUnavailablePlaceholder(
                selectedThread,
                backendState.DisplayName,
                backendState.Availability,
                HasAnyReadyChatBackend());
        }
        else
        {
            _promptComposerViewModel.Placeholder = BuildPromptPlaceholder(selectedThread, GetSelectedProject(), _globalScopeSelected);
        }

        var promptUnavailable = TryGetPromptUnavailableStatus(out _, out _);
        _promptComposerViewModel.IsEnabled = !promptUnavailable;
        _promptComposerViewModel.CanSend = !promptUnavailable;
        _promptComposerViewModel.CanSteer = selectedThread is not null && !promptUnavailable;
        _promptComposerViewModel.CanDelegate = selectedThread is not null && !promptUnavailable;
        _promptComposerViewModel.CanAbort = selectedThread is not null;
        _promptComposerViewModel.CanCloseTab = selectedThread is not null || (_draftTabOpen && string.IsNullOrWhiteSpace(_selectedThreadId));
    }

    internal void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
    {
        DispatchToUi(
            () =>
            {
                _shellViewModel.StatusText = message;
                _shellViewModel.StatusBusy = showSpinner;
                _shellViewModel.StatusTone = tone;
                _shellViewModel.StatusIconMarkup = BuildStatusIconMarkup(tone);
            });
    }

    internal static StatusSnapshot ResolveSelectionStatus(
        string readyMessage,
        bool hasThreadStatus,
        string? threadStatusMessage,
        bool threadStatusBusy,
        StatusTone threadStatusTone,
        bool promptUnavailable,
        string? promptUnavailableMessage,
        StatusTone promptUnavailableTone)
    {
        if (hasThreadStatus && !string.IsNullOrWhiteSpace(threadStatusMessage))
        {
            return new StatusSnapshot(threadStatusMessage!, threadStatusBusy, threadStatusTone);
        }

        if (promptUnavailable && !string.IsNullOrWhiteSpace(promptUnavailableMessage))
        {
            return new StatusSnapshot(promptUnavailableMessage!, Busy: false, promptUnavailableTone);
        }

        return new StatusSnapshot(readyMessage, Busy: false, StatusTone.Ready);
    }

    private void SetThreadStatus(
        ThreadTabState tab,
        string message,
        bool showSpinner = false,
        StatusTone tone = StatusTone.Info,
        bool hasCustomStatus = true)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var changed =
            !string.Equals(tab.StatusMessage, message, StringComparison.Ordinal) ||
            tab.StatusBusy != showSpinner ||
            tab.StatusTone != tone ||
            tab.HasCustomStatus != hasCustomStatus;

        tab.StatusMessage = message;
        tab.StatusBusy = showSpinner;
        tab.StatusTone = tone;
        tab.HasCustomStatus = hasCustomStatus;

        if (IsSelectedThread(tab.Thread.ThreadId))
        {
            SetReadyStatusForCurrentSelection();
        }

        if (changed)
        {
            InvalidateThreadChrome();
        }
    }

    private void ClearThreadStatus(ThreadTabState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        SetThreadStatus(
            tab,
            BuildReadyStatusText(tab.Thread, GetSelectedProject(), globalScopeSelected: false),
            tone: StatusTone.Ready,
            hasCustomStatus: false);
    }

    private void InvalidateThreadChrome()
    {
        DispatchToUi(() => _viewRefreshState.Value++);
    }

    private void InvalidateSelectedSessionUsage()
    {
        DispatchToUi(() => _usageRefreshState.Value++);
    }

    private bool IsSelectedThread(string threadId)
        => !string.IsNullOrWhiteSpace(threadId) &&
           string.Equals(_selectedThreadId, threadId, StringComparison.OrdinalIgnoreCase);

    internal void SetReadyStatusForCurrentSelection()
    {
        var selectedThread = GetSelectedThread();
        var readyMessage = BuildReadyStatusText(selectedThread, GetSelectedProject(), _globalScopeSelected);
        var promptUnavailable = TryGetPromptUnavailableStatus(out var promptUnavailableMessage, out var promptUnavailableTone);
        if (selectedThread is not null &&
            _threadTabs.TryGetValue(selectedThread.ThreadId, out var selectedTab))
        {
            var snapshot = ResolveSelectionStatus(
                readyMessage,
                selectedTab.HasCustomStatus,
                selectedTab.ViewModel.StatusMessage,
                selectedTab.ViewModel.StatusBusy,
                selectedTab.ViewModel.StatusTone,
                promptUnavailable,
                promptUnavailableMessage,
                promptUnavailableTone);
            SetStatus(snapshot.Message, snapshot.Busy, snapshot.Tone);
            return;
        }

        if (promptUnavailable)
        {
            SetStatus(promptUnavailableMessage, tone: promptUnavailableTone);
            return;
        }

        SetStatus(readyMessage, tone: StatusTone.Ready);
    }

    private void DispatchToUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = GetUiDispatcher();
        UiDispatch.Post(
            dispatcher,
            action,
            allowInline: ShouldRunInlineOnCurrentThread(
                dispatcher.CheckAccess(),
                _terminalLoopStarted));
    }

    internal static bool ShouldRunInlineOnCurrentThread(
        bool dispatcherHasAccess,
        bool terminalLoopStarted)
    {
        if (!terminalLoopStarted)
        {
            return true;
        }

        return dispatcherHasAccess;
    }

    private IUiDispatcher GetUiDispatcher()
        => _uiDispatcher ??= new TerminalUiDispatcher(Dispatcher.Current);

    private ComputedVisual CreateComputedVisual(Func<Visual> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new ComputedVisual(
            () =>
            {
                var _ = _viewRefreshState.Value;
                return build();
            });
    }

    private ComputedVisual CreateUsageComputedVisual(Func<Visual> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new ComputedVisual(
            () =>
            {
                var _ = _usageRefreshState.Value;
                return build();
            });
    }

    private void ClearThreadInput()
    {
        UiDispatch.Invoke(
            GetUiDispatcher(),
            () =>
            {
                _threadInput!.Text = string.Empty;
                return 0;
            });
    }

    private void ClearThreadTitleDraft()
    {
        _sidebarViewModel.DraftThreadTitle = string.Empty;
    }

    private async Task ActivateDraftTabAsync()
    {
        _pendingThreadTabSelectionThreadId = null;
        _draftTabOpen = true;
        _selectedThreadId = null;
        _viewState.SelectedThreadId = null;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync().ConfigureAwait(false);
        RefreshSelectionAndThreadWorkspace();
    }

    private async Task CloseDraftTabAsync()
    {
        _draftTabOpen = false;
        if (string.IsNullOrWhiteSpace(_selectedThreadId))
        {
            _selectedThreadId = _viewState.OpenThreadIds.FirstOrDefault();
            _viewState.SelectedThreadId = _selectedThreadId;
        }

        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync().ConfigureAwait(false);
        RefreshSelectionAndThreadWorkspace();
    }

    private bool GetAutoApproveEnabled()
        => DefaultAutoApproveEnabled;

    private async Task PersistViewStateAsync()
    {
        try
        {
            await _threadCatalog.SaveViewStateAsync(_viewState, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && UiLogger.IsEnabled(LogLevel.Error))
            {
                UiLogger.Error(ex, "Failed to persist thread view state.");
            }
        }
    }

    private static Group CreateSectionGroup(string title, Visual content)
    {
        return new Group(new Markup($"[bold]{title}[/]"), content)
            .Padding(1)
            .Style(XenoAtom.Terminal.UI.Styling.GroupStyle.Rounded);
    }

    private ChatPromptEditor CreatePromptEditor()
    {
        var converter = new MarkdownMarkupConverter();
        var editor = new ChatPromptEditor(text => _ = SendSelectedThreadPromptAsync(steer: false))
            .PromptMarkup("[primary]>[/] ")
            .ContinuationPromptMarkup("[muted]·[/] ")
            .Placeholder(_promptComposerViewModel.Bind.Placeholder)
            .EnterMode(PromptEditorEnterMode.EnterInsertsNewLine)
            .EnableWordHints(true)
            .Highlighter(HighlightMarkdown)
            .MinHeight(3)
            .Style(PromptEditorStyle.Default with
            {
                Padding = new Thickness(0, 0, 1, 0),
                PlaceholderForeground = UiPalette.PromptPlaceholderColor,
            });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.Steer",
            LabelMarkup = "Steer",
            DescriptionMarkup = "Send an immediate steering instruction to the selected thread.",
            Gesture = new KeyGesture(TerminalKey.F5),
            Importance = CommandImportance.Primary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => { _ = SendSelectedThreadPromptAsync(steer: true); },
            CanExecute = _visual => _promptComposerViewModel.CanSteer,
        });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.Delegate",
            LabelMarkup = "Delegate",
            DescriptionMarkup = "Create a delegated internal thread from the current project thread.",
            Gesture = new KeyGesture(TerminalKey.F7),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => { _ = DelegateSelectedThreadAsync(); },
            CanExecute = _visual => _promptComposerViewModel.CanDelegate,
        });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.Abort",
            LabelMarkup = "Abort",
            DescriptionMarkup = "Abort the selected thread run.",
            Gesture = new KeyGesture(TerminalKey.F8),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => { _ = AbortSelectedThreadAsync(); },
            CanExecute = _visual => _promptComposerViewModel.CanAbort,
        });

        editor.AddCommand(new Command
        {
            Id = "CodeAlta.Thread.CloseTab",
            LabelMarkup = "Close Tab",
            DescriptionMarkup = "Close the current thread tab.",
            Gesture = new KeyGesture(TerminalKey.F9),
            Presentation = CommandPresentation.CommandBar,
            Execute = _visual => { _ = GetSelectedThread() is not null ? CloseSelectedThreadAsync() : CloseDraftTabAsync(); },
            CanExecute = _visual => _promptComposerViewModel.CanCloseTab,
        });

        return editor;

        void HighlightMarkdown(in PromptEditorHighlightRequest request, List<StyledRun> runs)
        {
            converter.Theme = request.Theme;
            converter.Highlight(SnapshotToString(request.Snapshot), runs);
        }

        static string SnapshotToString(ITextSnapshot snapshot)
        {
            if (snapshot.Length == 0)
            {
                return string.Empty;
            }

            return string.Create(snapshot.Length, snapshot, static (span, s) => s.CopyTo(0, span));
        }
    }
}
