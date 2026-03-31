using CodeAlta.App;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Frontend.Commands;

internal sealed class ShellCommandSurfaceCoordinator
{
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly ThreadWorkspaceViewModel _threadWorkspaceViewModel;
    private readonly ThreadCommandCoordinator _threadCommandCoordinator;
    private readonly Func<Rectangle?> _getHelpBounds;
    private readonly Func<Visual?> _getHelpFocusTarget;
    private readonly Func<WorkThreadDescriptor?> _getSelectedThread;
    private readonly Func<WorkThreadDescriptor, OpenThreadState> _ensureThreadTab;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private readonly Action _openSessionUsage;
    private readonly Action _openThreadInfo;
    private readonly Action _openExpandedPromptEditor;
    private readonly ShellInputCoordinator _shellInputCoordinator;
    private CommandPalette? _commandPalette;
    private ShellHelpDialog? _helpDialog;

    public ShellCommandSurfaceCoordinator(
        PromptComposerViewModel promptComposerViewModel,
        ThreadWorkspaceViewModel threadWorkspaceViewModel,
        ThreadCommandCoordinator threadCommandCoordinator,
        Func<string?> getPromptText,
        Func<Task> closeCurrentTabAsync,
        Action<string, bool, StatusTone> setStatus,
        Func<Rectangle?> getHelpBounds,
        Func<Visual?> getHelpFocusTarget,
        Func<WorkThreadDescriptor?> getSelectedThread,
        Func<WorkThreadDescriptor, OpenThreadState> ensureThreadTab,
        Action openSessionUsage,
        Action openThreadInfo,
        Action openExpandedPromptEditor)
    {
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(threadWorkspaceViewModel);
        ArgumentNullException.ThrowIfNull(threadCommandCoordinator);
        ArgumentNullException.ThrowIfNull(getPromptText);
        ArgumentNullException.ThrowIfNull(closeCurrentTabAsync);
        ArgumentNullException.ThrowIfNull(setStatus);
        ArgumentNullException.ThrowIfNull(getHelpBounds);
        ArgumentNullException.ThrowIfNull(getHelpFocusTarget);
        ArgumentNullException.ThrowIfNull(getSelectedThread);
        ArgumentNullException.ThrowIfNull(ensureThreadTab);
        ArgumentNullException.ThrowIfNull(openSessionUsage);
        ArgumentNullException.ThrowIfNull(openThreadInfo);
        ArgumentNullException.ThrowIfNull(openExpandedPromptEditor);

        _promptComposerViewModel = promptComposerViewModel;
        _threadWorkspaceViewModel = threadWorkspaceViewModel;
        _threadCommandCoordinator = threadCommandCoordinator;
        _getHelpBounds = getHelpBounds;
        _getHelpFocusTarget = getHelpFocusTarget;
        _getSelectedThread = getSelectedThread;
        _ensureThreadTab = ensureThreadTab;
        _setStatus = setStatus;
        _openSessionUsage = openSessionUsage;
        _openThreadInfo = openThreadInfo;
        _openExpandedPromptEditor = openExpandedPromptEditor;
        _shellInputCoordinator = new ShellInputCoordinator(
            new ShellInputRouter(),
            getPromptText,
            closeCurrentTabAsync,
            () => ShowShellHelpAsync(),
            ShowShellHelpAsync,
            ShowSelectedThreadQueueStatusAsync,
            threadCommandCoordinator,
            setStatus);
    }

    public IReadOnlyList<ThreadWorkspaceCommandBinding> BuildWorkspaceCommandBindings()
    {
        return
        [
            CreateCommandBinding("CodeAlta.Shell.Help", () => _ = ShowHelpAsync()),
            CreateCommandBinding("CodeAlta.Thread.SessionUsage", _openSessionUsage),
            CreateCommandBinding("CodeAlta.Thread.Info", _openThreadInfo),
            CreateCommandBinding("CodeAlta.Thread.ExpandPrompt", _openExpandedPromptEditor),
            CreateCommandBinding("CodeAlta.Thread.Steer", () => _ = _shellInputCoordinator.SubmitCurrentPromptAsync(steer: true)),
            CreateCommandBinding("CodeAlta.Thread.Delegate", () => _ = _shellInputCoordinator.SubmitCurrentDelegationAsync()),
            CreateCommandBinding("CodeAlta.Thread.Abort", () => _ = _shellInputCoordinator.AbortSelectedThreadAsync()),
            CreateCommandBinding("CodeAlta.Thread.ClearQueue", () => _ = _threadCommandCoordinator.ClearSelectedThreadQueueAsync()),
            CreateCommandBinding("CodeAlta.Thread.Compact", () => _ = _shellInputCoordinator.CompactSelectedThreadAsync()),
            CreateCommandBinding("CodeAlta.Thread.CloseTab", () => _ = _shellInputCoordinator.CloseCurrentTabAsync()),
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
        => (_commandPalette ??= new CommandPalette()).Show();

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
}
