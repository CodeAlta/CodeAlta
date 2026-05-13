namespace CodeAlta.Frontend.Commands;

internal sealed class ShellInputCoordinator
{
    private readonly ShellInputRouter _router;
    private readonly Func<string?> _getPromptText;
    private readonly Func<bool> _isCurrentPromptEmpty;
    private readonly IShellCommandDispatcher _dispatcher;

    public ShellInputCoordinator(
        ShellInputRouter router,
        Func<string?> getPromptText,
        Func<bool> isCurrentPromptEmpty,
        IShellCommandDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(getPromptText);
        ArgumentNullException.ThrowIfNull(isCurrentPromptEmpty);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _router = router;
        _getPromptText = getPromptText;
        _isCurrentPromptEmpty = isCurrentPromptEmpty;
        _dispatcher = dispatcher;
    }

    public Task SubmitCurrentPromptAsync(bool steer, CancellationToken cancellationToken = default)
        => HandleInputAsync(_getPromptText(), steer, cancellationToken);

    public Task AbortSelectedThreadAsync(CancellationToken cancellationToken = default)
        => DispatchAsync(new AbortSelectedThreadCommand(), cancellationToken);

    public Task CompactSelectedThreadAsync(CancellationToken cancellationToken = default)
        => DispatchAsync(new CompactSelectedThreadCommand(), cancellationToken);

    public Task ShowHelpAsync(string? filterText = null, CancellationToken cancellationToken = default)
        => DispatchAsync(new OpenHelpCommand(filterText), cancellationToken);

    public Task ShowQueueStatusAsync(CancellationToken cancellationToken = default)
        => DispatchAsync(new ShowQueueStatusCommand(), cancellationToken);

    public Task CloseCurrentTabAsync(CancellationToken cancellationToken = default)
        => DispatchAsync(new CloseCurrentTabCommand(), cancellationToken);

    public Task HandleAcceptedPromptAsync(string? rawInput, CancellationToken cancellationToken = default)
        => HandleInputAsync(rawInput, steer: false, cancellationToken);

    public async Task HandleInputAsync(
        string? rawInput,
        bool steer,
        CancellationToken cancellationToken = default)
    {
        var intent = _router.Route(rawInput, steer);
        if (intent is EmptyShellInputIntent && !_isCurrentPromptEmpty())
        {
            await _dispatcher.DispatchAsync(new SubmitPromptCommand(rawInput, steer), cancellationToken);
            return;
        }

        await ExecuteIntentAsync(intent, cancellationToken);
    }

    private async Task ExecuteIntentAsync(ShellInputIntent intent, CancellationToken cancellationToken)
    {
        ShellCommand? command = intent switch
        {
            EmptyShellInputIntent => null,
            SendPromptIntent send => new SubmitPromptCommand(send.PromptText, Steer: false),
            SteerPromptIntent steer => new SubmitPromptCommand(steer.PromptText, Steer: true),
            AbortThreadIntent => new AbortSelectedThreadCommand(),
            CompactThreadIntent => new CompactSelectedThreadCommand(),
            CloseTabIntent => new CloseCurrentTabCommand(),
            TabLeftIntent => new SelectRelativeTabCommand(-1),
            TabRightIntent => new SelectRelativeTabCommand(1),
            MessagePreviousIntent => new ScrollSelectedThreadMessageCommand(ThreadMessageScrollTarget.Previous),
            MessageNextIntent => new ScrollSelectedThreadMessageCommand(ThreadMessageScrollTarget.Next),
            MessageFirstIntent => new ScrollSelectedThreadMessageCommand(ThreadMessageScrollTarget.First),
            MessageLastIntent => new ScrollSelectedThreadMessageCommand(ThreadMessageScrollTarget.Last),
            QueueStatusIntent => new ShowQueueStatusCommand(),
            OpenHelpIntent help => new OpenHelpCommand(help.FilterText),
            OpenCommandPaletteIntent => new OpenCommandPaletteCommand(),
            ExitAppIntent => new ExitAppCommand(),
            ToggleCommandBarMultiLineIntent => new ToggleCommandBarMultiLineCommand(),
            OpenFolderIntent openFolder => new OpenFolderCommand(openFolder.InitialPath),
            OpenAcpManagementIntent => new OpenAcpManagementCommand(),
            OpenModelProvidersIntent => new OpenModelProvidersCommand(),
            OpenModelsIntent => new OpenModelsCommand(),
            OpenFileEditorIntent => new OpenFileEditorCommand(),
            OpenSkillsIntent => new OpenSkillsCommand(),
            OpenPluginsIntent => new OpenPluginsCommand(),
            FocusSidebarIntent => new FocusSidebarCommand(),
            FocusPromptIntent => new FocusPromptCommand(),
            FocusModelProviderIntent => new FocusModelProviderCommand(),
            OpenSessionUsageIntent => new OpenSessionUsageCommand(),
            OpenThreadInfoIntent => new OpenThreadInfoCommand(),
            OpenExpandedPromptIntent => new OpenExpandedPromptCommand(),
            UnknownTextCommandIntent unknown => new ExecutePluginTextCommand(unknown.CommandName, unknown.Arguments),
            ClearQueueIntent => new ClearSelectedThreadQueueCommand(),
            _ => throw new InvalidOperationException($"Unsupported shell input intent: {intent.GetType().Name}"),
        };

        if (command is not null)
        {
            await _dispatcher.DispatchAsync(command, cancellationToken);
        }
    }

    private async Task DispatchAsync(ShellCommand command, CancellationToken cancellationToken)
        => await _dispatcher.DispatchAsync(command, cancellationToken);
}
