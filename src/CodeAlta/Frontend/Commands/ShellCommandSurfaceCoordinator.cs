using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Frontend.Commands;

internal sealed class ShellCommandSurfaceCoordinator
{
    internal static CommandPaletteStyle CommandPalettePopupStyle { get; } = CommandPaletteStyle.Default with
    {
        PopupWidthPercent = 50,
        MaxWidth = int.MaxValue,
        PopupHorizontalAlignment = Align.Center,
        PopupVerticalAlignment = Align.End,
        PopupOffsetY = -2,
    };
    internal static CommandPaletteStyle DialogCommandPalettePopupStyle { get; } = CommandPaletteStyle.Default with
    {
        PopupWidthPercent = 50,
        MaxWidth = int.MaxValue,
        PopupHorizontalAlignment = Align.Center,
        PopupVerticalAlignment = Align.Center,
    };

    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly ThreadWorkspaceViewModel _threadWorkspaceViewModel;
    private readonly ThreadCommandCoordinator _threadCommandCoordinator;
    private readonly Func<Rectangle?> _getHelpBounds;
    private readonly Func<Visual?> _getHelpFocusTarget;
    private readonly Func<IReadOnlyList<ProjectDescriptor>> _getProjects;
    private readonly Func<string, bool, Task> _openFolderAsync;
    private readonly Func<Task> _openFileEditorAsync;
    private readonly Func<WorkThreadDescriptor?> _getSelectedThread;
    private readonly Func<WorkThreadDescriptor, OpenThreadState> _ensureThreadTab;
    private readonly Action _focusSidebar;
    private readonly Action _focusPrompt;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private readonly Action _openSessionUsage;
    private readonly Action _openThreadInfo;
    private readonly Action _openExpandedPromptEditor;
    private readonly Func<Task> _selectTabLeftAsync;
    private readonly Func<Task> _selectTabRightAsync;
    private readonly ShellInputCoordinator _shellInputCoordinator;
    private CommandPaletteStyle _activeCommandPaletteStyle = CommandPalettePopupStyle;
    private CommandPalette? _commandPalette;
    private ShellHelpDialog? _helpDialog;

    public ShellCommandSurfaceCoordinator(
        PromptComposerViewModel promptComposerViewModel,
        ThreadWorkspaceViewModel threadWorkspaceViewModel,
        ThreadCommandCoordinator threadCommandCoordinator,
        Func<IReadOnlyList<ProjectDescriptor>> getProjects,
        Func<string, bool, Task> openFolderAsync,
        Func<Task> openFileEditorAsync,
        Func<string?> getPromptText,
        Func<Task> closeCurrentTabAsync,
        Action<string, bool, StatusTone> setStatus,
        Func<Rectangle?> getHelpBounds,
        Func<Visual?> getHelpFocusTarget,
        Func<WorkThreadDescriptor?> getSelectedThread,
        Func<WorkThreadDescriptor, OpenThreadState> ensureThreadTab,
        Action focusSidebar,
        Action focusPrompt,
        Action openSessionUsage,
        Action openThreadInfo,
        Action openExpandedPromptEditor,
        Func<Task> selectTabLeftAsync,
        Func<Task> selectTabRightAsync)
    {
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(threadWorkspaceViewModel);
        ArgumentNullException.ThrowIfNull(threadCommandCoordinator);
        ArgumentNullException.ThrowIfNull(getProjects);
        ArgumentNullException.ThrowIfNull(openFolderAsync);
        ArgumentNullException.ThrowIfNull(openFileEditorAsync);
        ArgumentNullException.ThrowIfNull(getPromptText);
        ArgumentNullException.ThrowIfNull(closeCurrentTabAsync);
        ArgumentNullException.ThrowIfNull(setStatus);
        ArgumentNullException.ThrowIfNull(getHelpBounds);
        ArgumentNullException.ThrowIfNull(getHelpFocusTarget);
        ArgumentNullException.ThrowIfNull(getSelectedThread);
        ArgumentNullException.ThrowIfNull(ensureThreadTab);
        ArgumentNullException.ThrowIfNull(focusSidebar);
        ArgumentNullException.ThrowIfNull(focusPrompt);
        ArgumentNullException.ThrowIfNull(openSessionUsage);
        ArgumentNullException.ThrowIfNull(openThreadInfo);
        ArgumentNullException.ThrowIfNull(openExpandedPromptEditor);
        ArgumentNullException.ThrowIfNull(selectTabLeftAsync);
        ArgumentNullException.ThrowIfNull(selectTabRightAsync);

        _promptComposerViewModel = promptComposerViewModel;
        _threadWorkspaceViewModel = threadWorkspaceViewModel;
        _threadCommandCoordinator = threadCommandCoordinator;
        _getProjects = getProjects;
        _openFolderAsync = openFolderAsync;
        _openFileEditorAsync = openFileEditorAsync;
        _getHelpBounds = getHelpBounds;
        _getHelpFocusTarget = getHelpFocusTarget;
        _getSelectedThread = getSelectedThread;
        _ensureThreadTab = ensureThreadTab;
        _focusSidebar = focusSidebar;
        _focusPrompt = focusPrompt;
        _setStatus = setStatus;
        _openSessionUsage = openSessionUsage;
        _openThreadInfo = openThreadInfo;
        _openExpandedPromptEditor = openExpandedPromptEditor;
        _selectTabLeftAsync = selectTabLeftAsync;
        _selectTabRightAsync = selectTabRightAsync;
        _shellInputCoordinator = new ShellInputCoordinator(
            new ShellInputRouter(),
            getPromptText,
            closeCurrentTabAsync,
            () => ShowShellHelpAsync(),
            ShowShellHelpAsync,
            ShowCommandPaletteAsync,
            ExitAppAsync,
            ShowOpenFolderDialogAsync,
            OpenFileEditorAsync,
            FocusSidebarAsync,
            FocusPromptAsync,
            ShowSelectedSessionUsageAsync,
            ShowSelectedThreadInfoAsync,
            ShowExpandedPromptEditorAsync,
            ShowSelectedThreadQueueStatusAsync,
            SelectTabLeftAsync,
            SelectTabRightAsync,
            ClearSelectedThreadQueueAsync,
            threadCommandCoordinator,
            setStatus);
    }

    public IReadOnlyList<ThreadWorkspaceCommandBinding> BuildWorkspaceCommandBindings()
    {
        return
        [
            CreateCommandBinding("CodeAlta.Shell.Help", () => ObserveUiTask(ShowHelpAsync(), "show help")),
            CreateCommandBinding("CodeAlta.Project.OpenFolder", () => ObserveUiTask(ShowOpenFolderDialogAsync(), "open a project")),
            CreateCommandBinding("CodeAlta.File.Edit", () => ObserveUiTask(OpenFileEditorAsync(), "open a file")),
            CreateCommandBinding("CodeAlta.Thread.SessionUsage", _openSessionUsage),
            CreateCommandBinding("CodeAlta.Thread.Info", _openThreadInfo),
            CreateCommandBinding("CodeAlta.Thread.ExpandPrompt", _openExpandedPromptEditor),
            CreateCommandBinding("CodeAlta.Thread.Steer", () => ObserveUiTask(_shellInputCoordinator.SubmitCurrentPromptAsync(steer: true), "steer the current thread")),
            CreateCommandBinding("CodeAlta.Thread.Delegate", () => ObserveUiTask(_shellInputCoordinator.SubmitCurrentDelegationAsync(), "delegate internal work")),
            CreateCommandBinding("CodeAlta.Thread.Abort", () => ObserveUiTask(_shellInputCoordinator.AbortSelectedThreadAsync(), "abort the selected thread")),
            CreateCommandBinding("CodeAlta.Thread.ClearQueue", () => ObserveUiTask(_threadCommandCoordinator.ClearSelectedThreadQueueAsync(), "clear the thread queue")),
            CreateCommandBinding("CodeAlta.Thread.Compact", () => ObserveUiTask(_shellInputCoordinator.CompactSelectedThreadAsync(), "compact the selected thread")),
            CreateCommandBinding("CodeAlta.Thread.CloseTab", () => ObserveUiTask(_shellInputCoordinator.CloseCurrentTabAsync(), "close the current tab")),
            CreateCommandBinding("CodeAlta.Thread.TabLeft", () => ObserveUiTask(SelectTabLeftAsync(), "select the tab to the left")),
            CreateCommandBinding("CodeAlta.Thread.TabRight", () => ObserveUiTask(SelectTabRightAsync(), "select the tab to the right")),
        ];
    }

    public Task HandleAcceptedPromptAsync(string? rawInput, CancellationToken cancellationToken = default)
        => _shellInputCoordinator.HandleAcceptedPromptAsync(rawInput, cancellationToken);

    public Task SubmitCurrentPromptAsync(bool steer, CancellationToken cancellationToken = default)
        => _shellInputCoordinator.SubmitCurrentPromptAsync(steer, cancellationToken);

    public Task SubmitCurrentDelegationAsync(CancellationToken cancellationToken = default)
        => _shellInputCoordinator.SubmitCurrentDelegationAsync(cancellationToken);

    public Task AbortSelectedThreadAsync(CancellationToken cancellationToken = default)
        => _shellInputCoordinator.AbortSelectedThreadAsync(cancellationToken);

    public Task CompactSelectedThreadAsync(CancellationToken cancellationToken = default)
        => _shellInputCoordinator.CompactSelectedThreadAsync(cancellationToken);

    public Task CloseCurrentTabAsync(CancellationToken cancellationToken = default)
        => _shellInputCoordinator.CloseCurrentTabAsync(cancellationToken);

    public Task ShowHelpAsync(string? filterText = null, CancellationToken cancellationToken = default)
        => ExecuteHelpAsync(filterText, cancellationToken);

    public void ShowCommandPalette()
    {
        var app = _getHelpFocusTarget()?.App ?? _commandPalette?.App;
        _activeCommandPaletteStyle = ResolveCommandPalettePopupStyle(app?.FocusedElement);
        (_commandPalette ??= CreateCommandPalette(() => _activeCommandPaletteStyle)).Show();
    }

    public Task ShowCommandPaletteAsync()
    {
        ShowCommandPalette();
        return Task.CompletedTask;
    }

    public Task ExitAppAsync()
    {
        _getHelpFocusTarget()?.App?.Stop();
        return Task.CompletedTask;
    }

    public Task ShowOpenFolderDialogAsync(string? initialPath = null)
    {
        new DirectoryPathDialog(
            "Open Project",
            "Type a project name from the sidebar or a rooted folder path.",
            "Open",
            _openFolderAsync,
            _getHelpBounds,
            _getHelpFocusTarget,
            _getHelpFocusTarget,
            () => _getProjects(),
            initialPath,
            placeholder: "CodeAlta or C:\\code\\SomeFolder")
            .Show();
        return Task.CompletedTask;
    }

    public Task OpenFileEditorAsync()
        => _openFileEditorAsync();

    private ThreadWorkspaceCommandBinding CreateCommandBinding(string commandId, Action execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(execute);

        var metadata = ShellCommandCatalog.Get(commandId);
        return new ThreadWorkspaceCommandBinding(
            metadata,
            execute,
            () => CanExecuteShellCommand(metadata.Availability));
    }

    private bool CanExecuteShellCommand(ShellCommandAvailability availability)
    {
        return availability switch
        {
            ShellCommandAvailability.Always => true,
            ShellCommandAvailability.PromptEnabled => _promptComposerViewModel.IsEnabled,
            ShellCommandAvailability.CanSend => _promptComposerViewModel.CanSend,
            ShellCommandAvailability.CanSteer => _promptComposerViewModel.CanSteer,
            ShellCommandAvailability.CanDelegate => _promptComposerViewModel.CanDelegate,
            ShellCommandAvailability.CanAbort => _promptComposerViewModel.CanAbort,
            ShellCommandAvailability.CanClearQueue => _promptComposerViewModel.CanClearQueue,
            ShellCommandAvailability.CanCompact => _promptComposerViewModel.CanCompact,
            ShellCommandAvailability.CanCloseTab => _promptComposerViewModel.CanCloseTab,
            ShellCommandAvailability.CanShowThreadInfo => _threadWorkspaceViewModel.CanShowThreadInfo,
            _ => false,
        };
    }

    private void ObserveUiTask(Task task, string operation)
        => _ = UiTaskDiagnostics.ObserveAsync(task, operation, _setStatus);

    private Task ExecuteHelpAsync(string? filterText, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return ShowShellHelpAsync(filterText);
    }

    private Task ShowShellHelpAsync(string? filterText = null)
    {
        _helpDialog ??= new ShellHelpDialog(_getHelpBounds, _getHelpFocusTarget);
        return _helpDialog.ShowAsync(filterText);
    }

    private Task ShowSelectedThreadQueueStatusAsync()
    {
        if (_getSelectedThread() is not { } thread)
        {
            _setStatus("Open a thread before inspecting its queue.", false, StatusTone.Warning);
            return Task.CompletedTask;
        }

        var tab = _ensureThreadTab(thread);
        var queuedCount = tab.QueuedPrompts.Count;
        var tone = queuedCount == 0
            ? StatusTone.Ready
            : tab.StatusBusy ? StatusTone.Info : StatusTone.Warning;
        var message = queuedCount == 0
            ? $"Queue empty · {thread.Title}"
            : $"{queuedCount} queued prompt(s) waiting in '{thread.Title}'.";

        _setStatus(message, false, tone);
        return Task.CompletedTask;
    }

    private Task ShowSelectedSessionUsageAsync()
    {
        _openSessionUsage();
        return Task.CompletedTask;
    }

    private Task FocusSidebarAsync()
    {
        _focusSidebar();
        return Task.CompletedTask;
    }

    private Task FocusPromptAsync()
    {
        _focusPrompt();
        return Task.CompletedTask;
    }

    private Task ShowSelectedThreadInfoAsync()
    {
        _openThreadInfo();
        return Task.CompletedTask;
    }

    private Task ShowExpandedPromptEditorAsync()
    {
        _openExpandedPromptEditor();
        return Task.CompletedTask;
    }

    private Task SelectTabLeftAsync()
        => _selectTabLeftAsync();

    private Task SelectTabRightAsync()
        => _selectTabRightAsync();

    private Task ClearSelectedThreadQueueAsync()
        => _threadCommandCoordinator.ClearSelectedThreadQueueAsync();

    internal static CommandPaletteStyle ResolveCommandPalettePopupStyle(Visual? focusElement)
    {
        return IsInsideDialog(focusElement)
            ? DialogCommandPalettePopupStyle
            : CommandPalettePopupStyle;
    }

    private static bool IsInsideDialog(Visual? visual)
    {
        for (var current = visual; current is not null; current = current.Parent)
        {
            if (current is Dialog)
            {
                return true;
            }
        }

        return false;
    }

    private static CommandPalette CreateCommandPalette(Func<CommandPaletteStyle> getStyle)
        => new CommandPalette().Style(() => getStyle());
}
