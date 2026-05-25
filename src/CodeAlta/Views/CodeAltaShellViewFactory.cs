using CodeAlta.Frontend.Commands;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Views;

internal static class CodeAltaShellViewFactory
{
    public static CodeAltaShellSurface CreateSurface(CodeAltaShellSurfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ShellViewModel);
        ArgumentNullException.ThrowIfNull(options.WorkspaceViewModel);
        ArgumentNullException.ThrowIfNull(options.PromptComposerViewModel);
        ArgumentNullException.ThrowIfNull(options.WorkspaceCommandBindings);
        ArgumentNullException.ThrowIfNull(options.WorkspaceChromeController);
        ArgumentNullException.ThrowIfNull(options.PromptComposerController);
        ArgumentNullException.ThrowIfNull(options.QueuedPromptController);
        ArgumentNullException.ThrowIfNull(options.ModelProviderSelectorController);
        ArgumentNullException.ThrowIfNull(options.ThreadTabHostController);
        ArgumentNullException.ThrowIfNull(options.ProjectFileSearchService);
        ArgumentNullException.ThrowIfNull(options.GetPromptReferenceProjectRoot);
        ArgumentNullException.ThrowIfNull(options.GetPromptComposerSession);
        ArgumentNullException.ThrowIfNull(options.ThinkingAnimationPhase01);
        ArgumentNullException.ThrowIfNull(options.Sidebar);
        ArgumentNullException.ThrowIfNull(options.ShellCommandSurfaceCoordinator);
        ArgumentNullException.ThrowIfNull(options.OpenAcpManager);
        ArgumentNullException.ThrowIfNull(options.ToggleTerminalLoopCallback);
        ArgumentNullException.ThrowIfNull(options.ToggleNavigator);
        ArgumentNullException.ThrowIfNull(options.CanUseCommandPalette);

        var workspaceView = new ThreadWorkspaceView(
            options.ShellViewModel,
            options.WorkspaceViewModel,
            options.PromptComposerViewModel,
            options.WorkspaceCommandBindings,
            options.WorkspaceChromeController,
            options.PromptComposerController,
            options.QueuedPromptController,
            options.ModelProviderSelectorController,
            options.ThreadTabHostController,
            options.ProjectFileSearchService,
            options.GetPromptReferenceProjectRoot,
            options.PromptEditorContributions,
            options.GetPromptComposerSession,
            options.ThinkingAnimationPhase01,
            options.PromptImageCallbacks);
        workspaceView.ThreadCommandBar.MultiLine = options.CommandBarMultiLine;

        var shellView = Create(
            options.Sidebar,
            workspaceView.Root,
            workspaceView.ThreadCommandBar,
            options.ShellCommandSurfaceCoordinator,
            options.OpenAcpManager,
            options.ToggleTerminalLoopCallback,
            options.ToggleNavigator,
            options.CanUseCommandPalette,
            () => workspaceView.ThreadCommandBar.MultiLine,
            options.ComposePluginFooter?.Invoke(workspaceView.ThreadCommandBar));

        return new CodeAltaShellSurface(shellView, workspaceView, options.Sidebar);
    }

    public static CodeAltaShellView Create(
        Visual sidebar,
        Visual threadWorkspace,
        Visual threadCommandBar,
        ShellCommandSurfaceCoordinator shellCommandSurfaceCoordinator,
        Action openAcpManager,
        Action toggleTerminalLoopCallback,
        Action toggleNavigator,
        Func<bool> canUseCommandPalette,
        Func<bool> isCommandBarMultiLine,
        Visual? pluginFooter = null)
    {
        ArgumentNullException.ThrowIfNull(sidebar);
        ArgumentNullException.ThrowIfNull(threadWorkspace);
        ArgumentNullException.ThrowIfNull(threadCommandBar);
        ArgumentNullException.ThrowIfNull(shellCommandSurfaceCoordinator);
        ArgumentNullException.ThrowIfNull(openAcpManager);
        ArgumentNullException.ThrowIfNull(toggleTerminalLoopCallback);
        ArgumentNullException.ThrowIfNull(toggleNavigator);
        ArgumentNullException.ThrowIfNull(canUseCommandPalette);
        ArgumentNullException.ThrowIfNull(isCommandBarMultiLine);

        var shellView = new CodeAltaShellView(
            sidebar,
            threadWorkspace,
            pluginFooter ?? threadCommandBar,
            CodeAltaGlobalCommandConfigurator.Configure);
        shellView.Root.AddCommand(new XenoAtom.Terminal.UI.Commands.Command
        {
            Id = "CodeAlta.Diagnostics.ToggleTerminalLoop",
            LabelMarkup = "Loop",
            DescriptionMarkup = "Toggle per-frame loop work.",
            Presentation = CommandPresentation.CommandBar,
            Execute = _ => toggleTerminalLoopCallback(),
        });
        var commandPaletteMetadata = ShellCommandCatalog.Get("CodeAlta.Shell.CommandPalette");
        shellView.Root.AddCommand(new XenoAtom.Terminal.UI.Commands.Command
        {
            Id = commandPaletteMetadata.Id,
            LabelMarkup = commandPaletteMetadata.DisplayLabelMarkup,
            Name = commandPaletteMetadata.CommandName,
            SearchText = commandPaletteMetadata.CommandSearchText,
            DescriptionMarkup = commandPaletteMetadata.DescriptionMarkup,
            Gesture = commandPaletteMetadata.Gesture,
            Sequence = commandPaletteMetadata.Sequence,
            Presentation = CommandPresentation.CommandBar,
            Execute = command => { _ = shellCommandSurfaceCoordinator.ShowCommandPaletteAsync(); },
            CanExecute = _ => canUseCommandPalette(),
            IsVisible = _ => canUseCommandPalette(),
            ConsumesGestureWhenUnavailable = false,
        });
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Project.OpenFolder"),
            () => _ = shellCommandSurfaceCoordinator.ShowOpenFolderDialogAsync(),
            CommandPresentation.CommandPalette));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Providers.Manage"),
            () => _ = shellCommandSurfaceCoordinator.OpenModelProvidersAsync()));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Shell.About"),
            () => _ = shellCommandSurfaceCoordinator.OpenAboutAsync()));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.ApplicationLogs.Open"),
            () => _ = shellCommandSurfaceCoordinator.OpenApplicationLogsAsync()));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.File.Edit"),
            () => _ = shellCommandSurfaceCoordinator.OpenFileEditorAsync()));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Skills.Manage"),
            () => _ = shellCommandSurfaceCoordinator.OpenSkillsAsync()));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Plugins.Manage"),
            () => _ = shellCommandSurfaceCoordinator.OpenPluginsAsync()));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Workspace.Settings"),
            () => _ = shellCommandSurfaceCoordinator.OpenWorkspaceSettingsAsync()));
        // ACP frontend command registration is intentionally disabled until the
        // TUI integration is exercised and validated.
        // shellView.Root.AddCommand(ShellCommandViewFactory.Create(
        //     ShellCommandCatalog.Get("CodeAlta.Acp.Manage"),
        //     openAcpManager));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Shell.FocusSidebar"),
            () => _ = shellCommandSurfaceCoordinator.FocusSidebarAsync()));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Shell.ToggleNavigator"),
            toggleNavigator));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Shell.FocusPrompt"),
            () => _ = shellCommandSurfaceCoordinator.FocusPromptAsync()));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Shell.FocusModelProvider"),
            () => _ = shellCommandSurfaceCoordinator.FocusModelProviderAsync()));
        shellView.Root.AddCommand(CreateToggleCommandBarMultiLineCommand(ToggleCommandBarMultiLine, isCommandBarMultiLine()));
        return shellView;

        void ToggleCommandBarMultiLine()
        {
            shellCommandSurfaceCoordinator.ToggleCommandBarMultiLine();
            shellView.Root.AddCommand(CreateToggleCommandBarMultiLineCommand(ToggleCommandBarMultiLine, isCommandBarMultiLine()));
        }
    }

    internal static Command CreateToggleCommandBarMultiLineCommand(Action toggleCommandBarMultiLine, bool commandBarMultiLine)
    {
        ArgumentNullException.ThrowIfNull(toggleCommandBarMultiLine);

        var metadata = ShellCommandCatalog.Get("CodeAlta.Shell.ToggleCommandBarMultiLine");
        return ShellCommandViewFactory.Create(
            metadata,
            toggleCommandBarMultiLine,
            labelMarkup: commandBarMultiLine ? "Show Less Shortcuts" : metadata.DisplayLabelMarkup);
    }

}
