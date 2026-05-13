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

internal interface IShellThreadCommandService
{
    WorkThreadDescriptor? GetSelectedThread();

    OpenThreadState EnsureThreadTab(WorkThreadDescriptor thread);
}

internal sealed class DelegatingShellThreadCommandService : IShellThreadCommandService
{
    private readonly Func<WorkThreadDescriptor?> _getSelectedThread;
    private readonly Func<WorkThreadDescriptor, OpenThreadState> _ensureThreadTab;

    public DelegatingShellThreadCommandService(
        Func<WorkThreadDescriptor?> getSelectedThread,
        Func<WorkThreadDescriptor, OpenThreadState> ensureThreadTab)
    {
        ArgumentNullException.ThrowIfNull(getSelectedThread);
        ArgumentNullException.ThrowIfNull(ensureThreadTab);
        _getSelectedThread = getSelectedThread;
        _ensureThreadTab = ensureThreadTab;
    }

    public WorkThreadDescriptor? GetSelectedThread() => _getSelectedThread();

    public OpenThreadState EnsureThreadTab(WorkThreadDescriptor thread) => _ensureThreadTab(thread);
}

internal interface IShellNavigationCommandService
{
    void FocusSidebar();

    void FocusPrompt();

    void FocusModelProvider();

    Task SelectRelativeTabAsync(int offset);

    Task ScrollSelectedThreadMessageAsync(ThreadMessageScrollTarget target);
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

    public Task ScrollSelectedThreadMessageAsync(ThreadMessageScrollTarget target)
    {
        return target switch
        {
            ThreadMessageScrollTarget.Previous => _scrollToPreviousMessageAsync(),
            ThreadMessageScrollTarget.Next => _scrollToNextMessageAsync(),
            ThreadMessageScrollTarget.First => _scrollToFirstMessageAsync(),
            ThreadMessageScrollTarget.Last => _scrollToLastMessageAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown thread message scroll target."),
        };
    }
}

internal interface IShellDialogCommandService
{
    Rectangle? GetDialogBounds();

    Visual? GetDialogFocusTarget();

    IReadOnlyList<ProjectDescriptor> GetProjects();

    Task OpenFolderAsync(string path, bool trustFolder);

    void OpenAcpManagement();

    Task OpenModelProvidersAsync();

    void OpenModels();

    Task OpenFileEditorAsync();

    Task OpenSkillsAsync();

    Task OpenPluginsAsync();

    void OpenSessionUsage();

    void OpenThreadInfo();

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
    private readonly Action _openAcpManagement;
    private readonly Func<Task> _openModelProvidersAsync;
    private readonly Action _openModels;
    private readonly Func<Task> _openFileEditorAsync;
    private readonly Func<Task> _openSkillsAsync;
    private readonly Func<Task> _openPluginsAsync;
    private readonly Action _openSessionUsage;
    private readonly Action _openThreadInfo;
    private readonly Action _openExpandedPromptEditor;
    private readonly Action _toggleCommandBarMultiLine;

    public DelegatingShellDialogCommandService(
        Func<Rectangle?> getDialogBounds,
        Func<Visual?> getDialogFocusTarget,
        Func<IReadOnlyList<ProjectDescriptor>> getProjects,
        Func<string, bool, Task> openFolderAsync,
        Action openAcpManagement,
        Func<Task> openModelProvidersAsync,
        Action openModels,
        Func<Task> openFileEditorAsync,
        Func<Task> openSkillsAsync,
        Func<Task> openPluginsAsync,
        Action openSessionUsage,
        Action openThreadInfo,
        Action openExpandedPromptEditor,
        Action toggleCommandBarMultiLine)
    {
        ArgumentNullException.ThrowIfNull(getDialogBounds);
        ArgumentNullException.ThrowIfNull(getDialogFocusTarget);
        ArgumentNullException.ThrowIfNull(getProjects);
        ArgumentNullException.ThrowIfNull(openFolderAsync);
        ArgumentNullException.ThrowIfNull(openAcpManagement);
        ArgumentNullException.ThrowIfNull(openModelProvidersAsync);
        ArgumentNullException.ThrowIfNull(openModels);
        ArgumentNullException.ThrowIfNull(openFileEditorAsync);
        ArgumentNullException.ThrowIfNull(openSkillsAsync);
        ArgumentNullException.ThrowIfNull(openPluginsAsync);
        ArgumentNullException.ThrowIfNull(openSessionUsage);
        ArgumentNullException.ThrowIfNull(openThreadInfo);
        ArgumentNullException.ThrowIfNull(openExpandedPromptEditor);
        ArgumentNullException.ThrowIfNull(toggleCommandBarMultiLine);
        _getDialogBounds = getDialogBounds;
        _getDialogFocusTarget = getDialogFocusTarget;
        _getProjects = getProjects;
        _openFolderAsync = openFolderAsync;
        _openAcpManagement = openAcpManagement;
        _openModelProvidersAsync = openModelProvidersAsync;
        _openModels = openModels;
        _openFileEditorAsync = openFileEditorAsync;
        _openSkillsAsync = openSkillsAsync;
        _openPluginsAsync = openPluginsAsync;
        _openSessionUsage = openSessionUsage;
        _openThreadInfo = openThreadInfo;
        _openExpandedPromptEditor = openExpandedPromptEditor;
        _toggleCommandBarMultiLine = toggleCommandBarMultiLine;
    }

    public Rectangle? GetDialogBounds() => _getDialogBounds();

    public Visual? GetDialogFocusTarget() => _getDialogFocusTarget();

    public IReadOnlyList<ProjectDescriptor> GetProjects() => _getProjects();

    public Task OpenFolderAsync(string path, bool trustFolder) => _openFolderAsync(path, trustFolder);

    public void OpenAcpManagement() => _openAcpManagement();

    public Task OpenModelProvidersAsync() => _openModelProvidersAsync();

    public void OpenModels() => _openModels();

    public Task OpenFileEditorAsync() => _openFileEditorAsync();

    public Task OpenSkillsAsync() => _openSkillsAsync();

    public Task OpenPluginsAsync() => _openPluginsAsync();

    public void OpenSessionUsage() => _openSessionUsage();

    public void OpenThreadInfo() => _openThreadInfo();

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
