using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Frontend.Commands;

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

    Task SelectRelativeTabAsync(int offset);

    Task ScrollSelectedSessionMessageAsync(SessionMessageScrollTarget target);
}

internal sealed class DelegatingShellNavigationCommandService : IShellNavigationCommandService
{
    private readonly Action _focusSidebar;
    private readonly Action _focusPrompt;
    private readonly Action _focusModelProvider;
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
        ArgumentNullException.ThrowIfNull(selectTabLeftAsync);
        ArgumentNullException.ThrowIfNull(selectTabRightAsync);
        ArgumentNullException.ThrowIfNull(scrollToPreviousMessageAsync);
        ArgumentNullException.ThrowIfNull(scrollToNextMessageAsync);
        ArgumentNullException.ThrowIfNull(scrollToFirstMessageAsync);
        ArgumentNullException.ThrowIfNull(scrollToLastMessageAsync);
        _focusSidebar = focusSidebar;
        _focusPrompt = focusPrompt;
        _focusModelProvider = focusModelProvider;
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

    public Task SelectRelativeTabAsync(int offset) => offset < 0 ? _selectTabLeftAsync() : _selectTabRightAsync();

    public Task ScrollSelectedSessionMessageAsync(SessionMessageScrollTarget target)
    {
        return target switch
        {
            SessionMessageScrollTarget.Previous => _scrollToPreviousMessageAsync(),
            SessionMessageScrollTarget.Next => _scrollToNextMessageAsync(),
            SessionMessageScrollTarget.First => _scrollToFirstMessageAsync(),
            SessionMessageScrollTarget.Last => _scrollToLastMessageAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown session message scroll target."),
        };
    }
}

internal interface IShellDialogCommandService
{
    Rectangle? GetDialogBounds();

    Visual? GetDialogFocusTarget();

    IReadOnlyList<ProjectDescriptor> GetProjects();

    Task OpenFolderAsync(string path, bool trustFolder);

    Task OpenModelProvidersAsync();

    void OpenAbout();

    void OpenModels();

    void OpenApplicationLogs();

    Task OpenFileEditorAsync();

    Task OpenSkillsAsync();

    Task OpenPluginsAsync();

    Task OpenMcpServersAsync();

    void OpenWorkspaceSettings();

    void OpenSessionUsage();

    void OpenSessionInfo();

    void OpenExpandedPromptEditor();

    void ToggleCommandBarMultiLine();

    void ExitApp();
}

internal sealed class DelegatingShellDialogCommandService : IShellDialogCommandService
{
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Func<Visual?> _getDialogFocusTarget;
    private readonly Func<IReadOnlyList<ProjectDescriptor>> _getProjects;
    private readonly Func<string, bool, Task> _openFolderAsync;
    private readonly Func<Task> _openModelProvidersAsync;
    private readonly Action _openAbout;
    private readonly Action _openModels;
    private readonly Action _openApplicationLogs;
    private readonly Func<Task> _openFileEditorAsync;
    private readonly Func<Task> _openSkillsAsync;
    private readonly Func<Task> _openPluginsAsync;
    private readonly Func<Task> _openMcpServersAsync;
    private readonly Action _openWorkspaceSettings;
    private readonly Action _openSessionUsage;
    private readonly Action _openSessionInfo;
    private readonly Action _openExpandedPromptEditor;
    private readonly Action _toggleCommandBarMultiLine;

    public DelegatingShellDialogCommandService(
        Func<Rectangle?> getDialogBounds,
        Func<Visual?> getDialogFocusTarget,
        Func<IReadOnlyList<ProjectDescriptor>> getProjects,
        Func<string, bool, Task> openFolderAsync,
        Func<Task> openModelProvidersAsync,
        Action openAbout,
        Action openModels,
        Action openApplicationLogs,
        Func<Task> openFileEditorAsync,
        Func<Task> openSkillsAsync,
        Func<Task> openPluginsAsync,
        Func<Task> openMcpServersAsync,
        Action openWorkspaceSettings,
        Action openSessionUsage,
        Action openSessionInfo,
        Action openExpandedPromptEditor,
        Action toggleCommandBarMultiLine)
    {
        ArgumentNullException.ThrowIfNull(getDialogBounds);
        ArgumentNullException.ThrowIfNull(getDialogFocusTarget);
        ArgumentNullException.ThrowIfNull(getProjects);
        ArgumentNullException.ThrowIfNull(openFolderAsync);
        ArgumentNullException.ThrowIfNull(openModelProvidersAsync);
        ArgumentNullException.ThrowIfNull(openAbout);
        ArgumentNullException.ThrowIfNull(openModels);
        ArgumentNullException.ThrowIfNull(openApplicationLogs);
        ArgumentNullException.ThrowIfNull(openFileEditorAsync);
        ArgumentNullException.ThrowIfNull(openSkillsAsync);
        ArgumentNullException.ThrowIfNull(openPluginsAsync);
        ArgumentNullException.ThrowIfNull(openMcpServersAsync);
        ArgumentNullException.ThrowIfNull(openWorkspaceSettings);
        ArgumentNullException.ThrowIfNull(openSessionUsage);
        ArgumentNullException.ThrowIfNull(openSessionInfo);
        ArgumentNullException.ThrowIfNull(openExpandedPromptEditor);
        ArgumentNullException.ThrowIfNull(toggleCommandBarMultiLine);
        _getDialogBounds = getDialogBounds;
        _getDialogFocusTarget = getDialogFocusTarget;
        _getProjects = getProjects;
        _openFolderAsync = openFolderAsync;
        _openModelProvidersAsync = openModelProvidersAsync;
        _openAbout = openAbout;
        _openModels = openModels;
        _openApplicationLogs = openApplicationLogs;
        _openFileEditorAsync = openFileEditorAsync;
        _openSkillsAsync = openSkillsAsync;
        _openPluginsAsync = openPluginsAsync;
        _openMcpServersAsync = openMcpServersAsync;
        _openWorkspaceSettings = openWorkspaceSettings;
        _openSessionUsage = openSessionUsage;
        _openSessionInfo = openSessionInfo;
        _openExpandedPromptEditor = openExpandedPromptEditor;
        _toggleCommandBarMultiLine = toggleCommandBarMultiLine;
    }

    public Rectangle? GetDialogBounds() => _getDialogBounds();

    public Visual? GetDialogFocusTarget() => _getDialogFocusTarget();

    public IReadOnlyList<ProjectDescriptor> GetProjects() => _getProjects();

    public Task OpenFolderAsync(string path, bool trustFolder) => _openFolderAsync(path, trustFolder);

    public Task OpenModelProvidersAsync() => _openModelProvidersAsync();

    public void OpenAbout() => _openAbout();

    public void OpenModels() => _openModels();

    public void OpenApplicationLogs() => _openApplicationLogs();

    public Task OpenFileEditorAsync() => _openFileEditorAsync();

    public Task OpenSkillsAsync() => _openSkillsAsync();

    public Task OpenPluginsAsync() => _openPluginsAsync();

    public Task OpenMcpServersAsync() => _openMcpServersAsync();

    public void OpenWorkspaceSettings() => _openWorkspaceSettings();

    public void OpenSessionUsage() => _openSessionUsage();

    public void OpenSessionInfo() => _openSessionInfo();

    public void OpenExpandedPromptEditor() => _openExpandedPromptEditor();

    public void ToggleCommandBarMultiLine() => _toggleCommandBarMultiLine();

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

internal interface IPluginCommandService
{
    IReadOnlyList<PluginCommandContribution> GetCommandContributions();

    Task<PluginCommandResult> ExecuteCommandAsync(string name, string? arguments, CancellationToken cancellationToken = default);
}

internal sealed class PluginHostCommandService : IPluginCommandService
{
    private readonly PluginHostBridge? _pluginHostBridge;

    public PluginHostCommandService(PluginHostBridge? pluginHostBridge)
        => _pluginHostBridge = pluginHostBridge;

    public IReadOnlyList<PluginCommandContribution> GetCommandContributions()
        => _pluginHostBridge?.GetCommandContributions() ?? Array.Empty<PluginCommandContribution>();

    public Task<PluginCommandResult> ExecuteCommandAsync(string name, string? arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _pluginHostBridge is null
            ? Task.FromResult(PluginCommandResult.NotHandled)
            : _pluginHostBridge.ExecuteCommandAsync(name, arguments, cancellationToken);
    }
}
