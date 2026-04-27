using CodeAlta.App;
using CodeAlta.Frontend.Help;
using CodeAlta.Models;

namespace CodeAlta.Frontend.Commands;

internal sealed class ShellInputCoordinator
{
    private readonly ShellInputRouter _router;
    private readonly Func<string?> _getPromptText;
    private readonly Func<Task> _closeCurrentTabAsync;
    private readonly Func<Task> _showHelpAsync;
    private readonly Func<string?, Task> _showHelpAsyncWithFilter;
    private readonly Func<Task> _showCommandPaletteAsync;
    private readonly Func<Task> _exitAppAsync;
    private readonly Func<string?, Task> _showOpenFolderAsync;
    private readonly Func<Task> _openModelProvidersAsync;
    private readonly Func<Task> _openFileEditorAsync;
    private readonly Func<Task> _openSkillsAsync;
    private readonly Func<Task> _focusSidebarAsync;
    private readonly Func<Task> _focusPromptAsync;
    private readonly Func<Task> _showSessionUsageAsync;
    private readonly Func<Task> _showThreadInfoAsync;
    private readonly Func<Task> _showExpandedPromptAsync;
    private readonly Func<Task> _showQueueStatusAsync;
    private readonly Func<Task> _selectTabLeftAsync;
    private readonly Func<Task> _selectTabRightAsync;
    private readonly Func<Task> _scrollToPreviousMessageAsync;
    private readonly Func<Task> _scrollToNextMessageAsync;
    private readonly Func<Task> _scrollToFirstMessageAsync;
    private readonly Func<Task> _scrollToLastMessageAsync;
    private readonly Func<Task> _clearQueueAsync;
    private readonly ThreadCommandCoordinator _threadCommandCoordinator;
    private readonly Action<string, bool, StatusTone> _setStatus;

    public ShellInputCoordinator(
        ShellInputRouter router,
        Func<string?> getPromptText,
        Func<Task> closeCurrentTabAsync,
        Func<Task> showHelpAsync,
        Func<string?, Task> showHelpAsyncWithFilter,
        Func<Task> showCommandPaletteAsync,
        Func<Task> exitAppAsync,
        Func<string?, Task> showOpenFolderAsync,
        Func<Task> openModelProvidersAsync,
        Func<Task> openFileEditorAsync,
        Func<Task> openSkillsAsync,
        Func<Task> focusSidebarAsync,
        Func<Task> focusPromptAsync,
        Func<Task> showSessionUsageAsync,
        Func<Task> showThreadInfoAsync,
        Func<Task> showExpandedPromptAsync,
        Func<Task> showQueueStatusAsync,
        Func<Task> selectTabLeftAsync,
        Func<Task> selectTabRightAsync,
        Func<Task> scrollToPreviousMessageAsync,
        Func<Task> scrollToNextMessageAsync,
        Func<Task> scrollToFirstMessageAsync,
        Func<Task> scrollToLastMessageAsync,
        Func<Task> clearQueueAsync,
        ThreadCommandCoordinator threadCommandCoordinator,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(getPromptText);
        ArgumentNullException.ThrowIfNull(closeCurrentTabAsync);
        ArgumentNullException.ThrowIfNull(showHelpAsync);
        ArgumentNullException.ThrowIfNull(showHelpAsyncWithFilter);
        ArgumentNullException.ThrowIfNull(showCommandPaletteAsync);
        ArgumentNullException.ThrowIfNull(exitAppAsync);
        ArgumentNullException.ThrowIfNull(showOpenFolderAsync);
        ArgumentNullException.ThrowIfNull(openModelProvidersAsync);
        ArgumentNullException.ThrowIfNull(openFileEditorAsync);
        ArgumentNullException.ThrowIfNull(openSkillsAsync);
        ArgumentNullException.ThrowIfNull(focusSidebarAsync);
        ArgumentNullException.ThrowIfNull(focusPromptAsync);
        ArgumentNullException.ThrowIfNull(showSessionUsageAsync);
        ArgumentNullException.ThrowIfNull(showThreadInfoAsync);
        ArgumentNullException.ThrowIfNull(showExpandedPromptAsync);
        ArgumentNullException.ThrowIfNull(showQueueStatusAsync);
        ArgumentNullException.ThrowIfNull(selectTabLeftAsync);
        ArgumentNullException.ThrowIfNull(selectTabRightAsync);
        ArgumentNullException.ThrowIfNull(scrollToPreviousMessageAsync);
        ArgumentNullException.ThrowIfNull(scrollToNextMessageAsync);
        ArgumentNullException.ThrowIfNull(scrollToFirstMessageAsync);
        ArgumentNullException.ThrowIfNull(scrollToLastMessageAsync);
        ArgumentNullException.ThrowIfNull(clearQueueAsync);
        ArgumentNullException.ThrowIfNull(threadCommandCoordinator);
        ArgumentNullException.ThrowIfNull(setStatus);

        _router = router;
        _getPromptText = getPromptText;
        _closeCurrentTabAsync = closeCurrentTabAsync;
        _showHelpAsync = showHelpAsync;
        _showHelpAsyncWithFilter = showHelpAsyncWithFilter;
        _showCommandPaletteAsync = showCommandPaletteAsync;
        _exitAppAsync = exitAppAsync;
        _showOpenFolderAsync = showOpenFolderAsync;
        _openModelProvidersAsync = openModelProvidersAsync;
        _openFileEditorAsync = openFileEditorAsync;
        _openSkillsAsync = openSkillsAsync;
        _focusSidebarAsync = focusSidebarAsync;
        _focusPromptAsync = focusPromptAsync;
        _showSessionUsageAsync = showSessionUsageAsync;
        _showThreadInfoAsync = showThreadInfoAsync;
        _showExpandedPromptAsync = showExpandedPromptAsync;
        _showQueueStatusAsync = showQueueStatusAsync;
        _selectTabLeftAsync = selectTabLeftAsync;
        _selectTabRightAsync = selectTabRightAsync;
        _scrollToPreviousMessageAsync = scrollToPreviousMessageAsync;
        _scrollToNextMessageAsync = scrollToNextMessageAsync;
        _scrollToFirstMessageAsync = scrollToFirstMessageAsync;
        _scrollToLastMessageAsync = scrollToLastMessageAsync;
        _clearQueueAsync = clearQueueAsync;
        _threadCommandCoordinator = threadCommandCoordinator;
        _setStatus = setStatus;
    }

    public Task SubmitCurrentPromptAsync(bool steer, CancellationToken cancellationToken = default)
        => HandleInputAsync(_getPromptText(), steer, cancellationToken);

    public Task SubmitCurrentDelegationAsync(CancellationToken cancellationToken = default)
        => ExecuteIntentAsync(new DelegateThreadIntent(_getPromptText()?.Trim() ?? string.Empty), cancellationToken);

    public Task AbortSelectedThreadAsync(CancellationToken cancellationToken = default)
        => ExecuteIntentAsync(new AbortThreadIntent(), cancellationToken);

    public Task CompactSelectedThreadAsync(CancellationToken cancellationToken = default)
        => ExecuteIntentAsync(new CompactThreadIntent(), cancellationToken);

    public Task ShowHelpAsync(string? filterText = null, CancellationToken cancellationToken = default)
        => ExecuteIntentAsync(new OpenHelpIntent(filterText), cancellationToken);

    public Task ShowQueueStatusAsync(CancellationToken cancellationToken = default)
        => ExecuteIntentAsync(new QueueStatusIntent(), cancellationToken);

    public Task CloseCurrentTabAsync(CancellationToken cancellationToken = default)
        => ExecuteIntentAsync(new CloseTabIntent(), cancellationToken);

    public Task HandleAcceptedPromptAsync(string? rawInput, CancellationToken cancellationToken = default)
        => HandleInputAsync(rawInput, steer: false, cancellationToken);

    public async Task HandleInputAsync(
        string? rawInput,
        bool steer,
        CancellationToken cancellationToken = default)
    {
        var intent = _router.Route(rawInput, steer);
        if (intent is EmptyShellInputIntent && !_threadCommandCoordinator.IsCurrentPromptEmpty())
        {
            await _threadCommandCoordinator.SendPromptAsync(rawInput, steer, cancellationToken);
            return;
        }

        await ExecuteIntentAsync(intent, cancellationToken);
    }

    private async Task ExecuteIntentAsync(ShellInputIntent intent, CancellationToken cancellationToken)
    {
        switch (intent)
        {
            case EmptyShellInputIntent:
                return;

            case SendPromptIntent send:
                await _threadCommandCoordinator.SendPromptAsync(send.PromptText, steer: false, cancellationToken);
                return;

            case SteerPromptIntent steerIntent:
                await _threadCommandCoordinator.SendPromptAsync(steerIntent.PromptText, steer: true, cancellationToken);
                return;

            case DelegateThreadIntent delegateIntent:
                await _threadCommandCoordinator.DelegateThreadAsync(delegateIntent.PromptText, cancellationToken);
                return;

            case AbortThreadIntent:
                await _threadCommandCoordinator.AbortSelectedThreadAsync();
                return;

            case CompactThreadIntent:
                await _threadCommandCoordinator.CompactSelectedThreadAsync();
                return;

            case CloseTabIntent:
                await _closeCurrentTabAsync();
                return;

            case TabLeftIntent:
                await _selectTabLeftAsync();
                return;

            case TabRightIntent:
                await _selectTabRightAsync();
                return;

            case MessagePreviousIntent:
                await _scrollToPreviousMessageAsync();
                return;

            case MessageNextIntent:
                await _scrollToNextMessageAsync();
                return;

            case MessageFirstIntent:
                await _scrollToFirstMessageAsync();
                return;

            case MessageLastIntent:
                await _scrollToLastMessageAsync();
                return;

            case QueueStatusIntent:
                await _showQueueStatusAsync();
                return;

            case OpenHelpIntent help:
                if (string.IsNullOrWhiteSpace(help.FilterText))
                {
                    await _showHelpAsync();
                    return;
                }

                await _showHelpAsyncWithFilter(help.FilterText);
                return;

            case OpenCommandPaletteIntent:
                await _showCommandPaletteAsync();
                return;

            case ExitAppIntent:
                await _exitAppAsync();
                return;

            case OpenFolderIntent openFolder:
                await _showOpenFolderAsync(openFolder.InitialPath);
                return;

            case OpenModelProvidersIntent:
                await _openModelProvidersAsync();
                return;

            case OpenFileEditorIntent:
                await _openFileEditorAsync();
                return;

            case OpenSkillsIntent:
                await _openSkillsAsync();
                return;

            case FocusSidebarIntent:
                await _focusSidebarAsync();
                return;

            case FocusPromptIntent:
                await _focusPromptAsync();
                return;

            case OpenSessionUsageIntent:
                await _showSessionUsageAsync();
                return;

            case OpenThreadInfoIntent:
                await _showThreadInfoAsync();
                return;

            case OpenExpandedPromptIntent:
                await _showExpandedPromptAsync();
                return;

            case UnknownTextCommandIntent unknown:
                _setStatus($"Unknown command '/{unknown.CommandName}'. Press F1 or type /help.", false, StatusTone.Warning);
                return;

            case ClearQueueIntent:
                await _clearQueueAsync();
                return;

            default:
                throw new InvalidOperationException($"Unsupported shell input intent: {intent.GetType().Name}");
        }
    }
}
