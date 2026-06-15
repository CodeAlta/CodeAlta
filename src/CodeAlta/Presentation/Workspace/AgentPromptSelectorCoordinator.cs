using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Presentation.Chat;
using CodeAlta.ViewModels;

namespace CodeAlta.Presentation.Workspace;

internal sealed class AgentPromptSelectorCoordinator : IShellAgentPromptCommandService
{
    private readonly SessionWorkspaceViewModel _workspaceViewModel;
    private readonly CatalogOptions _catalogOptions;
    private readonly SessionSelectionContext _sessionSelection;
    private readonly AgentPromptPreferenceCoordinator _preferences;
    private readonly WorkspaceRefreshContext _workspaceRefresh;
    private readonly AgentPromptCatalog _promptCatalog;
    private readonly Func<SessionViewViewState> _getViewState;
    private readonly Action _persistViewState;
    private readonly Action<SessionViewDescriptor> _persistSessionLocalState;
    private readonly Func<string, string, CancellationToken, Task>? _setActiveSessionAgentPromptId;
    private readonly Action _syncAgentPromptSelectorItems;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private bool _selectorsRefreshing;

    public AgentPromptSelectorCoordinator(
        SessionWorkspaceViewModel workspaceViewModel,
        CatalogOptions catalogOptions,
        SessionSelectionContext sessionSelection,
        AgentPromptPreferenceCoordinator preferences,
        WorkspaceRefreshContext workspaceRefresh,
        Func<SessionViewViewState> getViewState,
        Action persistViewState,
        Action<SessionViewDescriptor> persistSessionLocalState,
        Func<string, string, CancellationToken, Task>? setActiveSessionAgentPromptId,
        Action syncAgentPromptSelectorItems,
        Action<string, bool, StatusTone> setStatus,
        AgentPromptCatalog? promptCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(workspaceRefresh);
        ArgumentNullException.ThrowIfNull(getViewState);
        ArgumentNullException.ThrowIfNull(persistViewState);
        ArgumentNullException.ThrowIfNull(persistSessionLocalState);
        ArgumentNullException.ThrowIfNull(syncAgentPromptSelectorItems);
        ArgumentNullException.ThrowIfNull(setStatus);

        _workspaceViewModel = workspaceViewModel;
        _catalogOptions = catalogOptions;
        _sessionSelection = sessionSelection;
        _preferences = preferences;
        _workspaceRefresh = workspaceRefresh;
        _getViewState = getViewState;
        _persistViewState = persistViewState;
        _persistSessionLocalState = persistSessionLocalState;
        _setActiveSessionAgentPromptId = setActiveSessionAgentPromptId;
        _syncAgentPromptSelectorItems = syncAgentPromptSelectorItems;
        _setStatus = setStatus;
        _promptCatalog = promptCatalog ?? new AgentPromptCatalog();
    }

    public void RefreshForDraftScope(string? preferredPromptName = null)
    {
        _selectorsRefreshing = true;
        try
        {
            var project = _sessionSelection.GetSelectedProject();
            var prompts = LoadPromptOptions(project);
            if (prompts.Count == 0)
            {
                SetPromptSelection([], -1, canSelect: false);
                return;
            }

            var viewState = _getViewState();
            var preferred = NormalizeOptionalText(preferredPromptName)
                ?? _preferences.GetDraftAgentPromptId(viewState, project?.ProjectPath, project?.Id)
                ?? AgentPromptCatalog.DefaultPromptName;
            var selectedIndex = FindPromptIndex(prompts, preferred);
            SetPromptSelection(prompts, selectedIndex, canSelect: true);
            if (preferredPromptName is not null)
            {
                _preferences.RememberDraftAgentPromptId(viewState, prompts[selectedIndex].PromptName, project?.ProjectPath, project?.Id);
                _persistViewState();
            }
        }
        finally
        {
            _selectorsRefreshing = false;
        }
    }

    public void RefreshForSession(OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _selectorsRefreshing = true;
        try
        {
            _preferences.ApplySessionAgentPromptId(tab, _getViewState());
            var project = _sessionSelection.GetProjectById(tab.SessionView.ProjectRef);
            var prompts = LoadPromptOptions(project);
            if (prompts.Count == 0)
            {
                SetPromptSelection([], -1, canSelect: false);
                return;
            }

            var selectedIndex = FindPromptIndex(prompts, tab.AgentPromptId ?? AgentPromptCatalog.DefaultPromptName);
            var selectedPromptName = prompts[selectedIndex].PromptName;
            tab.AgentPromptId = selectedPromptName;
            tab.SessionView.AgentPromptId = selectedPromptName;
            SetPromptSelection(prompts, selectedIndex, canSelect: true);
        }
        finally
        {
            _selectorsRefreshing = false;
        }
    }

    public void OnAgentPromptSelectionChanged(int newIndex)
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var options = _workspaceViewModel.AgentPromptOptions;
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        var selectedPrompt = options[newIndex];
        _workspaceViewModel.SelectedAgentPromptIndex = newIndex;
        var session = _sessionSelection.GetSelectedSession();
        if (session is null)
        {
            var project = _sessionSelection.GetSelectedProject();
            _preferences.RememberDraftAgentPromptId(_getViewState(), selectedPrompt.PromptName, project?.ProjectPath, project?.Id);
            _persistViewState();
            _workspaceRefresh.ApplySessionUsageProjection();
            _setStatus(SR.T("Selected prompt '{0}'.", selectedPrompt.Label), false, StatusTone.Ready);
            return;
        }

        var tab = _sessionSelection.EnsureSessionTab(session);
        _preferences.RememberSessionAgentPromptId(_getViewState(), tab, selectedPrompt.PromptName);
        _persistViewState();
        _persistSessionLocalState(tab.SessionView);
        if (_setActiveSessionAgentPromptId is not null)
        {
            global::CodeAlta.CodeAltaTaskMonitor.Observe(
                _setActiveSessionAgentPromptId(session.SessionId, selectedPrompt.PromptName, CancellationToken.None),
                $"Set active agent prompt for session {session.SessionId}");
        }

        _workspaceRefresh.ApplyHeaderProjection();
        _setStatus(SR.T("Selected prompt '{0}'.", selectedPrompt.Label), false, StatusTone.Ready);
    }

    public bool CanSelectNextAgentPrompt()
        => _workspaceViewModel.CanSelectAgentPrompt && _workspaceViewModel.AgentPromptOptions.Count > 0;

    public void SelectNextAgentPrompt()
    {
        if (!_workspaceViewModel.CanSelectAgentPrompt || _workspaceViewModel.AgentPromptOptions.Count == 0)
        {
            _setStatus(SR.T("No agent prompts are available."), false, StatusTone.Warning);
            return;
        }

        if (_workspaceViewModel.AgentPromptOptions.Count == 1)
        {
            _setStatus(SR.T("Only prompt '{0}' is available.", _workspaceViewModel.AgentPromptOptions[0].Label), false, StatusTone.Ready);
            return;
        }

        var selectedIndex = _workspaceViewModel.SelectedAgentPromptIndex;
        var nextIndex = (uint)selectedIndex < (uint)_workspaceViewModel.AgentPromptOptions.Count
            ? (selectedIndex + 1) % _workspaceViewModel.AgentPromptOptions.Count
            : 0;
        OnAgentPromptSelectionChanged(nextIndex);
    }

    public string? GetPreferredAgentPromptId()
    {
        if (_workspaceViewModel.SelectedAgentPromptIndex is var index &&
            (uint)index < (uint)_workspaceViewModel.AgentPromptOptions.Count)
        {
            return _workspaceViewModel.AgentPromptOptions[index].PromptName;
        }

        var session = _sessionSelection.GetSelectedSession();
        if (session is not null)
        {
            return _sessionSelection.FindOpenSession(session.SessionId)?.AgentPromptId ?? session.AgentPromptId;
        }

        var project = _sessionSelection.GetSelectedProject();
        return _preferences.GetDraftAgentPromptId(_getViewState(), project?.ProjectPath, project?.Id);
    }

    public void RefreshPrompts()
    {
        if (_sessionSelection.GetSelectedSession() is { } session && _sessionSelection.FindOpenSession(session.SessionId) is { } tab)
        {
            RefreshForSession(tab);
        }
        else
        {
            RefreshForDraftScope();
        }
    }

    private IReadOnlyList<AgentPromptOption> LoadPromptOptions(ProjectDescriptor? project)
    {
        var prompts = _promptCatalog.ListEffectivePrompts(new AgentPromptCatalogQuery
        {
            UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UserCodeAltaRoot = _catalogOptions.GlobalRoot,
            ProjectRoot = project?.ProjectPath,
            ProjectPromptResourcesTrusted = project is not null,
        });
        return AgentPromptPresentation.BuildPromptOptions(prompts);
    }

    private void SetPromptSelection(IReadOnlyList<AgentPromptOption> prompts, int selectedIndex, bool canSelect)
    {
        _workspaceViewModel.AgentPromptOptions = prompts;
        _workspaceViewModel.SelectedAgentPromptIndex = selectedIndex;
        _workspaceViewModel.CanSelectAgentPrompt = canSelect;
        _syncAgentPromptSelectorItems();
    }

    private static int FindPromptIndex(IReadOnlyList<AgentPromptOption> prompts, string? preferredPromptName)
    {
        if (prompts.Count == 0)
        {
            return -1;
        }

        var preferred = NormalizeOptionalText(preferredPromptName) ?? AgentPromptCatalog.DefaultPromptName;
        var index = prompts.ToList().FindIndex(prompt => string.Equals(prompt.PromptName, preferred, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            return index;
        }

        index = prompts.ToList().FindIndex(static prompt => string.Equals(prompt.PromptName, AgentPromptCatalog.DefaultPromptName, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : 0;
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
