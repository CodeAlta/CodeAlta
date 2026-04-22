using CodeAlta.Frontend.Commands;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Views;

internal static class CodeAltaShellViewFactory
{
    public static CodeAltaShellView Create(
        Visual sidebar,
        Visual threadWorkspace,
        Visual threadCommandBar,
        ShellCommandSurfaceCoordinator shellCommandSurfaceCoordinator,
        Action openAcpManager,
        Action toggleTerminalLoopCallback,
        Action focusSidebar,
        Action focusPromptEditor,
        Func<bool> canUseCommandPalette)
    {
        ArgumentNullException.ThrowIfNull(sidebar);
        ArgumentNullException.ThrowIfNull(threadWorkspace);
        ArgumentNullException.ThrowIfNull(threadCommandBar);
        ArgumentNullException.ThrowIfNull(shellCommandSurfaceCoordinator);
        ArgumentNullException.ThrowIfNull(openAcpManager);
        ArgumentNullException.ThrowIfNull(toggleTerminalLoopCallback);
        ArgumentNullException.ThrowIfNull(focusSidebar);
        ArgumentNullException.ThrowIfNull(focusPromptEditor);
        ArgumentNullException.ThrowIfNull(canUseCommandPalette);

        var shellView = new CodeAltaShellView(
            sidebar,
            threadWorkspace,
            threadCommandBar,
            CodeAltaGlobalCommandConfigurator.Configure);
        shellView.Root.AddCommand(new Command
        {
            Id = "CodeAlta.Diagnostics.ToggleTerminalLoop",
            LabelMarkup = "Loop",
            DescriptionMarkup = "Toggle per-frame loop work.",
            Gesture = new KeyGesture(TerminalKey.F4),
            Presentation = CommandPresentation.CommandBar,
            Execute = _ => toggleTerminalLoopCallback(),
        });
        var commandPaletteMetadata = ShellCommandCatalog.Get("CodeAlta.Shell.CommandPalette");
        shellView.Root.AddCommand(new Command
        {
            Id = commandPaletteMetadata.Id,
            LabelMarkup = commandPaletteMetadata.DisplayLabelMarkup,
            Name = commandPaletteMetadata.CommandName,
            SearchText = commandPaletteMetadata.CommandSearchText,
            DescriptionMarkup = commandPaletteMetadata.DescriptionMarkup,
            Gesture = commandPaletteMetadata.Gesture,
            Sequence = commandPaletteMetadata.Sequence,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ => shellCommandSurfaceCoordinator.ShowCommandPalette(),
            CanExecute = _ => canUseCommandPalette(),
            IsVisible = _ => canUseCommandPalette(),
            ConsumesGestureWhenUnavailable = false,
        });
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Project.OpenFolder"),
            () => _ = shellCommandSurfaceCoordinator.ShowOpenFolderDialogAsync(),
            CommandPresentation.CommandPalette));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.File.Edit"),
            () => _ = shellCommandSurfaceCoordinator.OpenFileEditorAsync()));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Acp.Manage"),
            openAcpManager));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Shell.FocusSidebar"),
            focusSidebar));
        shellView.Root.AddCommand(ShellCommandViewFactory.Create(
            ShellCommandCatalog.Get("CodeAlta.Shell.FocusPrompt"),
            focusPromptEditor));
        return shellView;
    }
}
