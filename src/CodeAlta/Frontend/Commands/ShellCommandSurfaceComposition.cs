using CodeAlta.App;
using CodeAlta.ViewModels;

namespace CodeAlta.Frontend.Commands;

internal static class ShellCommandSurfaceComposition
{
    public static ShellCommandSurfaceCoordinator Create(
        PromptComposerViewModel promptComposerViewModel,
        SessionWorkspaceViewModel sessionWorkspaceViewModel,
        SessionCommandCoordinator sessionCommandCoordinator,
        IShellPromptInputService promptInputService,
        IShellSessionCommandService sessionCommandService,
        IShellDialogCommandService dialogCommandService,
        IShellNavigationCommandService navigationCommandService,
        IShellTabCommandService tabCommandService,
        IShellStatusService statusService,
        IPluginCommandService pluginCommandService,
        Action toggleTerminalLoop,
        Func<bool> canUseCommandPalette,
        Func<bool> isCommandBarMultiLine)
    {
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(sessionWorkspaceViewModel);
        ArgumentNullException.ThrowIfNull(sessionCommandCoordinator);
        ArgumentNullException.ThrowIfNull(promptInputService);
        ArgumentNullException.ThrowIfNull(sessionCommandService);
        ArgumentNullException.ThrowIfNull(dialogCommandService);
        ArgumentNullException.ThrowIfNull(navigationCommandService);
        ArgumentNullException.ThrowIfNull(tabCommandService);
        ArgumentNullException.ThrowIfNull(statusService);
        ArgumentNullException.ThrowIfNull(pluginCommandService);
        ArgumentNullException.ThrowIfNull(toggleTerminalLoop);
        ArgumentNullException.ThrowIfNull(canUseCommandPalette);
        ArgumentNullException.ThrowIfNull(isCommandBarMultiLine);

        ShellCommandRegistry? registry = null;
        var presenter = new ShellCommandPalettePresenter(dialogCommandService);
        var context = new ShellCommandContext
        {
            PromptInput = promptInputService,
            PromptDispatch = new DelegatingShellPromptDispatchService(sessionCommandCoordinator.SendPromptAsync),
            Sessions = sessionCommandService,
            Dialogs = dialogCommandService,
            Navigation = navigationCommandService,
            Tabs = tabCommandService,
            Status = statusService,
            Availability = new DelegatingShellCommandAvailabilityService(
                canUseCommandPalette,
                () => promptComposerViewModel.IsEnabled,
                () => promptComposerViewModel.CanSend,
                () => promptComposerViewModel.CanSteer,
                () => promptComposerViewModel.CanAbort,
                () => promptComposerViewModel.CanClearQueue,
                () => promptComposerViewModel.CanCompact,
                () => promptComposerViewModel.CanCloseTab,
                () => sessionWorkspaceViewModel.CanShowSessionInfo),
            Presenter = presenter,
            Plugins = pluginCommandService,
            SessionActions = new DelegatingShellSessionActionService(
                sessionCommandCoordinator.AbortSelectedSessionAsync,
                sessionCommandCoordinator.CompactSelectedSessionAsync,
                sessionCommandCoordinator.ClearSelectedSessionQueueAsync),
            Diagnostics = new DelegatingShellDiagnosticsCommandService(toggleTerminalLoop),
            GetCommands = () => registry?.Commands ?? [],
            IsCommandBarMultiLine = isCommandBarMultiLine,
        };
        registry = new ShellCommandRegistry(BuiltinShellCommands.Enumerate().Concat(PluginShellCommandAdapter.CreateCommands(pluginCommandService)));
        var runner = new UiCommandRunner(statusService.SetStatus);
        return new ShellCommandSurfaceCoordinator(context, registry, runner, presenter);
    }
}
