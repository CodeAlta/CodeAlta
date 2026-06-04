using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Frontend.Commands;

internal sealed class ShellCommandContext
{
    public required IShellPromptInputService PromptInput { get; init; }

    public required IShellPromptDispatchService PromptDispatch { get; init; }

    public required IShellSessionCommandService Sessions { get; init; }

    public required IShellDialogCommandService Dialogs { get; init; }

    public required IShellNavigationCommandService Navigation { get; init; }

    public required IShellTabCommandService Tabs { get; init; }

    public required IShellStatusService Status { get; init; }

    public required IShellCommandAvailabilityService Availability { get; init; }

    public required IShellCommandPresenter Presenter { get; init; }

    public required IPluginCommandService Plugins { get; init; }

    public required IShellSessionActionService SessionActions { get; init; }

    public required IShellDiagnosticsCommandService Diagnostics { get; init; }

    public required IShellAgentPromptCommandService AgentPrompts { get; init; }

    public required Func<IReadOnlyList<ShellCommand>> GetCommands { get; init; }

    public required Func<bool> IsCommandBarMultiLine { get; init; }
}

internal interface IShellDiagnosticsCommandService
{
    void ToggleTerminalLoop();
}

internal interface IShellAgentPromptCommandService
{
    bool CanSelectNextAgentPrompt();

    void SelectNextAgentPrompt();
}

internal sealed class DelegatingShellAgentPromptCommandService : IShellAgentPromptCommandService
{
    private readonly Func<bool> _canSelectNextAgentPrompt;
    private readonly Action _selectNextAgentPrompt;

    public DelegatingShellAgentPromptCommandService(Func<bool> canSelectNextAgentPrompt, Action selectNextAgentPrompt)
    {
        ArgumentNullException.ThrowIfNull(canSelectNextAgentPrompt);
        ArgumentNullException.ThrowIfNull(selectNextAgentPrompt);
        _canSelectNextAgentPrompt = canSelectNextAgentPrompt;
        _selectNextAgentPrompt = selectNextAgentPrompt;
    }

    public bool CanSelectNextAgentPrompt() => _canSelectNextAgentPrompt();

    public void SelectNextAgentPrompt() => _selectNextAgentPrompt();
}

internal sealed class DelegatingShellDiagnosticsCommandService : IShellDiagnosticsCommandService
{
    private readonly Action _toggleTerminalLoop;

    public DelegatingShellDiagnosticsCommandService(Action toggleTerminalLoop)
    {
        ArgumentNullException.ThrowIfNull(toggleTerminalLoop);
        _toggleTerminalLoop = toggleTerminalLoop;
    }

    public void ToggleTerminalLoop() => _toggleTerminalLoop();
}

internal interface IShellSessionActionService
{
    Task AbortSelectedSessionAsync();

    Task CompactSelectedSessionAsync();

    Task ClearSelectedSessionQueueAsync();
}

internal sealed class DelegatingShellSessionActionService : IShellSessionActionService
{
    private readonly Func<Task> _abortSelectedSessionAsync;
    private readonly Func<Task> _compactSelectedSessionAsync;
    private readonly Func<Task> _clearSelectedSessionQueueAsync;

    public DelegatingShellSessionActionService(
        Func<Task> abortSelectedSessionAsync,
        Func<Task> compactSelectedSessionAsync,
        Func<Task> clearSelectedSessionQueueAsync)
    {
        _abortSelectedSessionAsync = abortSelectedSessionAsync ?? throw new ArgumentNullException(nameof(abortSelectedSessionAsync));
        _compactSelectedSessionAsync = compactSelectedSessionAsync ?? throw new ArgumentNullException(nameof(compactSelectedSessionAsync));
        _clearSelectedSessionQueueAsync = clearSelectedSessionQueueAsync ?? throw new ArgumentNullException(nameof(clearSelectedSessionQueueAsync));
    }

    public Task AbortSelectedSessionAsync() => _abortSelectedSessionAsync();

    public Task CompactSelectedSessionAsync() => _compactSelectedSessionAsync();

    public Task ClearSelectedSessionQueueAsync() => _clearSelectedSessionQueueAsync();
}

internal interface IShellPromptInputService
{
    string? GetPromptText();

    bool IsCurrentPromptEmpty();
}

internal sealed class DelegatingShellPromptInputService : IShellPromptInputService
{
    private readonly Func<string?> _getPromptText;
    private readonly Func<bool> _isCurrentPromptEmpty;

    public DelegatingShellPromptInputService(Func<string?> getPromptText, Func<bool> isCurrentPromptEmpty)
    {
        ArgumentNullException.ThrowIfNull(getPromptText);
        ArgumentNullException.ThrowIfNull(isCurrentPromptEmpty);
        _getPromptText = getPromptText;
        _isCurrentPromptEmpty = isCurrentPromptEmpty;
    }

    public string? GetPromptText() => _getPromptText();

    public bool IsCurrentPromptEmpty() => _isCurrentPromptEmpty();
}

internal interface IShellPromptDispatchService
{
    Task SendPromptAsync(string? text, bool steer, CancellationToken cancellationToken = default);
}

internal sealed class DelegatingShellPromptDispatchService : IShellPromptDispatchService
{
    private readonly Func<string?, bool, CancellationToken, Task> _sendPromptAsync;

    public DelegatingShellPromptDispatchService(Func<string?, bool, CancellationToken, Task> sendPromptAsync)
    {
        ArgumentNullException.ThrowIfNull(sendPromptAsync);
        _sendPromptAsync = sendPromptAsync;
    }

    public Task SendPromptAsync(string? text, bool steer, CancellationToken cancellationToken = default)
        => _sendPromptAsync(text, steer, cancellationToken);
}

internal interface IShellSessionCommandService
{
    SessionViewDescriptor? GetSelectedSession();

    OpenSessionState EnsureSessionTab(SessionViewDescriptor session);
}

internal sealed class DelegatingShellSessionCommandService : IShellSessionCommandService
{
    private readonly Func<SessionViewDescriptor?> _getSelectedSession;
    private readonly Func<SessionViewDescriptor, OpenSessionState> _ensureSessionTab;

    public DelegatingShellSessionCommandService(
        Func<SessionViewDescriptor?> getSelectedSession,
        Func<SessionViewDescriptor, OpenSessionState> ensureSessionTab)
    {
        ArgumentNullException.ThrowIfNull(getSelectedSession);
        ArgumentNullException.ThrowIfNull(ensureSessionTab);
        _getSelectedSession = getSelectedSession;
        _ensureSessionTab = ensureSessionTab;
    }

    public SessionViewDescriptor? GetSelectedSession() => _getSelectedSession();

    public OpenSessionState EnsureSessionTab(SessionViewDescriptor session) => _ensureSessionTab(session);
}

internal interface IShellNavigationCommandService
{
    void FocusSidebar();

    void FocusPrompt();

    void FocusModelProvider();

    void ToggleNavigator();

    Task SelectRelativeTabAsync(int offset);

    Task ScrollSelectedSessionMessageAsync(SessionMessageScrollTarget target);
}

internal sealed class DelegatingShellNavigationCommandService : IShellNavigationCommandService
{
    private readonly Action _focusSidebar;
    private readonly Action _focusPrompt;
    private readonly Action _focusModelProvider;
    private readonly Action _toggleNavigator;
    private readonly Func<Task> _selectTabLeftAsync;
    private readonly Func<Task> _selectTabRightAsync;
    private readonly Func<Task> _scrollToPreviousMessageAsync;
    private readonly Func<Task> _scrollToNextMessageAsync;
    private readonly Func<Task> _scrollToFirstMessageAsync;
    private readonly Func<Task> _scrollToLastMessageAsync;

    public DelegatingShellNavigationCommandService(
        Action focusSidebar,
        Action focusPrompt,
        Action focusModelProvider,
        Action toggleNavigator,
        Func<Task> selectTabLeftAsync,
        Func<Task> selectTabRightAsync,
        Func<Task> scrollToPreviousMessageAsync,
        Func<Task> scrollToNextMessageAsync,
        Func<Task> scrollToFirstMessageAsync,
        Func<Task> scrollToLastMessageAsync)
    {
        ArgumentNullException.ThrowIfNull(focusSidebar);
        ArgumentNullException.ThrowIfNull(focusPrompt);
        ArgumentNullException.ThrowIfNull(focusModelProvider);
        ArgumentNullException.ThrowIfNull(toggleNavigator);
        ArgumentNullException.ThrowIfNull(selectTabLeftAsync);
        ArgumentNullException.ThrowIfNull(selectTabRightAsync);
        ArgumentNullException.ThrowIfNull(scrollToPreviousMessageAsync);
        ArgumentNullException.ThrowIfNull(scrollToNextMessageAsync);
        ArgumentNullException.ThrowIfNull(scrollToFirstMessageAsync);
        ArgumentNullException.ThrowIfNull(scrollToLastMessageAsync);
        _focusSidebar = focusSidebar;
        _focusPrompt = focusPrompt;
        _focusModelProvider = focusModelProvider;
        _toggleNavigator = toggleNavigator;
        _selectTabLeftAsync = selectTabLeftAsync;
        _selectTabRightAsync = selectTabRightAsync;
        _scrollToPreviousMessageAsync = scrollToPreviousMessageAsync;
        _scrollToNextMessageAsync = scrollToNextMessageAsync;
        _scrollToFirstMessageAsync = scrollToFirstMessageAsync;
        _scrollToLastMessageAsync = scrollToLastMessageAsync;
    }

    public void FocusSidebar() => _focusSidebar();

    public void FocusPrompt() => _focusPrompt();

    public void FocusModelProvider() => _focusModelProvider();

    public void ToggleNavigator() => _toggleNavigator();

    public Task SelectRelativeTabAsync(int offset) => offset < 0 ? _selectTabLeftAsync() : _selectTabRightAsync();

    public Task ScrollSelectedSessionMessageAsync(SessionMessageScrollTarget target)
        => target switch
        {
            SessionMessageScrollTarget.Previous => _scrollToPreviousMessageAsync(),
            SessionMessageScrollTarget.Next => _scrollToNextMessageAsync(),
            SessionMessageScrollTarget.First => _scrollToFirstMessageAsync(),
            SessionMessageScrollTarget.Last => _scrollToLastMessageAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown session message scroll target."),
        };
}

internal interface IShellDialogCommandService
{
    Rectangle? GetDialogBounds();

    Visual? GetDialogFocusTarget();

    IReadOnlyList<ProjectDescriptor> GetProjects();

    Task OpenFolderAsync(string path, bool trustFolder);

    Task OpenModelProvidersAsync();

    Task RefreshModelProvidersAsync(CancellationToken cancellationToken = default);

    Task OpenPromptsAsync();

    void OpenAbout();

    void OpenModels();

    void OpenApplicationLogs();

    Task OpenFileEditorAsync();

    Task OpenSkillsAsync();

    Task OpenPluginsAsync();

    void OpenWorkspaceSettings();

    void OpenSessionUsage();

    void OpenSessionInfo();

    void OpenExpandedPromptEditor();

    void ToggleCommandBarMultiLine();

    void OpenReminders();

    void ExitApp();
}

internal sealed class DelegatingShellDialogCommandService : IShellDialogCommandService
{
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Func<Visual?> _getDialogFocusTarget;
    private readonly Func<IReadOnlyList<ProjectDescriptor>> _getProjects;
    private readonly Func<string, bool, Task> _openFolderAsync;
    private readonly Func<Task> _openModelProvidersAsync;
    private readonly Func<CancellationToken, Task> _refreshModelProvidersAsync;
    private readonly Func<Task> _openPromptsAsync;
    private readonly Action _openAbout;
    private readonly Action _openModels;
    private readonly Action _openApplicationLogs;
    private readonly Func<Task> _openFileEditorAsync;
    private readonly Func<Task> _openSkillsAsync;
    private readonly Func<Task> _openPluginsAsync;
    private readonly Action _openWorkspaceSettings;
    private readonly Action _openSessionUsage;
    private readonly Action _openSessionInfo;
    private readonly Action _openExpandedPromptEditor;
    private readonly Action _toggleCommandBarMultiLine;
    private readonly Action _openReminders;

    public DelegatingShellDialogCommandService(
        Func<Rectangle?> getDialogBounds,
        Func<Visual?> getDialogFocusTarget,
        Func<IReadOnlyList<ProjectDescriptor>> getProjects,
        Func<string, bool, Task> openFolderAsync,
        Func<Task> openModelProvidersAsync,
        Func<CancellationToken, Task> refreshModelProvidersAsync,
        Func<Task> openPromptsAsync,
        Action openAbout,
        Action openModels,
        Action openApplicationLogs,
        Func<Task> openFileEditorAsync,
        Func<Task> openSkillsAsync,
        Func<Task> openPluginsAsync,
        Action openWorkspaceSettings,
        Action openSessionUsage,
        Action openSessionInfo,
        Action openExpandedPromptEditor,
        Action toggleCommandBarMultiLine,
        Action openReminders)
    {
        ArgumentNullException.ThrowIfNull(getDialogBounds);
        ArgumentNullException.ThrowIfNull(getDialogFocusTarget);
        ArgumentNullException.ThrowIfNull(getProjects);
        ArgumentNullException.ThrowIfNull(openFolderAsync);
        ArgumentNullException.ThrowIfNull(openModelProvidersAsync);
        ArgumentNullException.ThrowIfNull(refreshModelProvidersAsync);
        ArgumentNullException.ThrowIfNull(openPromptsAsync);
        ArgumentNullException.ThrowIfNull(openAbout);
        ArgumentNullException.ThrowIfNull(openModels);
        ArgumentNullException.ThrowIfNull(openApplicationLogs);
        ArgumentNullException.ThrowIfNull(openFileEditorAsync);
        ArgumentNullException.ThrowIfNull(openSkillsAsync);
        ArgumentNullException.ThrowIfNull(openPluginsAsync);
        ArgumentNullException.ThrowIfNull(openWorkspaceSettings);
        ArgumentNullException.ThrowIfNull(openSessionUsage);
        ArgumentNullException.ThrowIfNull(openSessionInfo);
        ArgumentNullException.ThrowIfNull(openExpandedPromptEditor);
        ArgumentNullException.ThrowIfNull(toggleCommandBarMultiLine);
        ArgumentNullException.ThrowIfNull(openReminders);
        _getDialogBounds = getDialogBounds;
        _getDialogFocusTarget = getDialogFocusTarget;
        _getProjects = getProjects;
        _openFolderAsync = openFolderAsync;
        _openModelProvidersAsync = openModelProvidersAsync;
        _refreshModelProvidersAsync = refreshModelProvidersAsync;
        _openPromptsAsync = openPromptsAsync;
        _openAbout = openAbout;
        _openModels = openModels;
        _openApplicationLogs = openApplicationLogs;
        _openFileEditorAsync = openFileEditorAsync;
        _openSkillsAsync = openSkillsAsync;
        _openPluginsAsync = openPluginsAsync;
        _openWorkspaceSettings = openWorkspaceSettings;
        _openSessionUsage = openSessionUsage;
        _openSessionInfo = openSessionInfo;
        _openExpandedPromptEditor = openExpandedPromptEditor;
        _toggleCommandBarMultiLine = toggleCommandBarMultiLine;
        _openReminders = openReminders;
    }

    public Rectangle? GetDialogBounds() => _getDialogBounds();

    public Visual? GetDialogFocusTarget() => _getDialogFocusTarget();

    public IReadOnlyList<ProjectDescriptor> GetProjects() => _getProjects();

    public Task OpenFolderAsync(string path, bool trustFolder) => _openFolderAsync(path, trustFolder);

    public Task OpenModelProvidersAsync() => _openModelProvidersAsync();

    public Task RefreshModelProvidersAsync(CancellationToken cancellationToken = default) => _refreshModelProvidersAsync(cancellationToken);

    public Task OpenPromptsAsync() => _openPromptsAsync();

    public void OpenAbout() => _openAbout();

    public void OpenModels() => _openModels();

    public void OpenApplicationLogs() => _openApplicationLogs();

    public Task OpenFileEditorAsync() => _openFileEditorAsync();

    public Task OpenSkillsAsync() => _openSkillsAsync();

    public Task OpenPluginsAsync() => _openPluginsAsync();

    public void OpenWorkspaceSettings() => _openWorkspaceSettings();

    public void OpenSessionUsage() => _openSessionUsage();

    public void OpenSessionInfo() => _openSessionInfo();

    public void OpenExpandedPromptEditor() => _openExpandedPromptEditor();

    public void ToggleCommandBarMultiLine() => _toggleCommandBarMultiLine();

    public void OpenReminders() => _openReminders();

    public void ExitApp() => GetDialogFocusTarget()?.App?.Stop();
}

internal interface IShellTabCommandService
{
    Task CloseCurrentTabAsync();
}

internal sealed class DelegatingShellTabCommandService : IShellTabCommandService
{
    private readonly Func<Task> _closeCurrentTabAsync;

    public DelegatingShellTabCommandService(Func<Task> closeCurrentTabAsync)
    {
        ArgumentNullException.ThrowIfNull(closeCurrentTabAsync);
        _closeCurrentTabAsync = closeCurrentTabAsync;
    }

    public Task CloseCurrentTabAsync() => _closeCurrentTabAsync();
}

internal interface IShellCommandAvailabilityService
{
    bool CanUseCommandPalette();

    bool IsPromptEnabled();

    bool CanSendPrompt();

    bool CanSteerPrompt();

    bool CanAbortSelectedSession();

    bool CanClearSelectedSessionQueue();

    bool CanCompactSelectedSession();

    bool CanCloseCurrentTab();

    bool CanShowSessionInfo();
}

internal sealed class DelegatingShellCommandAvailabilityService : IShellCommandAvailabilityService
{
    private readonly Func<bool> _canUseCommandPalette;
    private readonly Func<bool> _isPromptEnabled;
    private readonly Func<bool> _canSendPrompt;
    private readonly Func<bool> _canSteerPrompt;
    private readonly Func<bool> _canAbortSelectedSession;
    private readonly Func<bool> _canClearSelectedSessionQueue;
    private readonly Func<bool> _canCompactSelectedSession;
    private readonly Func<bool> _canCloseCurrentTab;
    private readonly Func<bool> _canShowSessionInfo;

    public DelegatingShellCommandAvailabilityService(
        Func<bool> canUseCommandPalette,
        Func<bool> isPromptEnabled,
        Func<bool> canSendPrompt,
        Func<bool> canSteerPrompt,
        Func<bool> canAbortSelectedSession,
        Func<bool> canClearSelectedSessionQueue,
        Func<bool> canCompactSelectedSession,
        Func<bool> canCloseCurrentTab,
        Func<bool> canShowSessionInfo)
    {
        _canUseCommandPalette = canUseCommandPalette ?? throw new ArgumentNullException(nameof(canUseCommandPalette));
        _isPromptEnabled = isPromptEnabled ?? throw new ArgumentNullException(nameof(isPromptEnabled));
        _canSendPrompt = canSendPrompt ?? throw new ArgumentNullException(nameof(canSendPrompt));
        _canSteerPrompt = canSteerPrompt ?? throw new ArgumentNullException(nameof(canSteerPrompt));
        _canAbortSelectedSession = canAbortSelectedSession ?? throw new ArgumentNullException(nameof(canAbortSelectedSession));
        _canClearSelectedSessionQueue = canClearSelectedSessionQueue ?? throw new ArgumentNullException(nameof(canClearSelectedSessionQueue));
        _canCompactSelectedSession = canCompactSelectedSession ?? throw new ArgumentNullException(nameof(canCompactSelectedSession));
        _canCloseCurrentTab = canCloseCurrentTab ?? throw new ArgumentNullException(nameof(canCloseCurrentTab));
        _canShowSessionInfo = canShowSessionInfo ?? throw new ArgumentNullException(nameof(canShowSessionInfo));
    }

    public bool CanUseCommandPalette() => _canUseCommandPalette();

    public bool IsPromptEnabled() => _isPromptEnabled();

    public bool CanSendPrompt() => _canSendPrompt();

    public bool CanSteerPrompt() => _canSteerPrompt();

    public bool CanAbortSelectedSession() => _canAbortSelectedSession();

    public bool CanClearSelectedSessionQueue() => _canClearSelectedSessionQueue();

    public bool CanCompactSelectedSession() => _canCompactSelectedSession();

    public bool CanCloseCurrentTab() => _canCloseCurrentTab();

    public bool CanShowSessionInfo() => _canShowSessionInfo();
}

internal interface IShellCommandPresenter
{
    Task ShowHelpDialogAsync(IReadOnlyList<ShellCommand> commands, string? filterText = null);

    void ShowCommandPalette();

    void ShowOpenFolderDialog(string? initialPath = null);
}

internal interface IPluginCommandService
{
    IReadOnlyList<PluginCommandContribution> GetCommandContributions();

    Task<PluginCommandResult> ExecuteCommandAsync(PluginCommandContribution contribution, CancellationToken cancellationToken = default);
}

internal sealed class PluginHostCommandService : IPluginCommandService
{
    private readonly PluginHostBridge? _pluginHostBridge;

    public PluginHostCommandService(PluginHostBridge? pluginHostBridge)
        => _pluginHostBridge = pluginHostBridge;

    public IReadOnlyList<PluginCommandContribution> GetCommandContributions()
        => _pluginHostBridge?.GetCommandContributions() ?? Array.Empty<PluginCommandContribution>();

    public Task<PluginCommandResult> ExecuteCommandAsync(PluginCommandContribution contribution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        return _pluginHostBridge is null
            ? Task.FromResult(PluginCommandResult.NotHandled)
            : _pluginHostBridge.ExecuteCommandAsync(contribution, cancellationToken);
    }
}
