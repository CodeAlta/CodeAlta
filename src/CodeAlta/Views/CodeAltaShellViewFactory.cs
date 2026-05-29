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
        ArgumentNullException.ThrowIfNull(options.WorkspaceChromeController);
        ArgumentNullException.ThrowIfNull(options.PromptComposerController);
        ArgumentNullException.ThrowIfNull(options.QueuedPromptController);
        ArgumentNullException.ThrowIfNull(options.ModelProviderSelectorController);
        ArgumentNullException.ThrowIfNull(options.SessionTabHostController);
        ArgumentNullException.ThrowIfNull(options.ProjectFileSearchService);
        ArgumentNullException.ThrowIfNull(options.GetPromptReferenceProjectRoot);
        ArgumentNullException.ThrowIfNull(options.GetPromptComposerSession);
        ArgumentNullException.ThrowIfNull(options.ThinkingAnimationPhase01);
        ArgumentNullException.ThrowIfNull(options.Sidebar);
        ArgumentNullException.ThrowIfNull(options.ShellCommandSurfaceCoordinator);
        var workspaceView = new SessionWorkspaceView(
            options.ShellViewModel,
            options.WorkspaceViewModel,
            options.PromptComposerViewModel,
            options.ShellCommandSurfaceCoordinator,
            options.WorkspaceChromeController,
            options.PromptComposerController,
            options.QueuedPromptController,
            options.ModelProviderSelectorController,
            options.SessionTabHostController,
            options.ProjectFileSearchService,
            options.GetPromptReferenceProjectRoot,
            options.PromptEditorContributions,
            options.GetPromptComposerSession,
            options.ThinkingAnimationPhase01,
            options.PromptImageCallbacks);
        workspaceView.SessionCommandBar.MultiLine = options.CommandBarMultiLine;

        var shellView = Create(
            options.Sidebar,
            workspaceView.Root,
            workspaceView.SessionCommandBar,
            options.ShellCommandSurfaceCoordinator,
            options.ComposePluginFooter?.Invoke(workspaceView.SessionCommandBar));

        return new CodeAltaShellSurface(shellView, workspaceView, options.Sidebar);
    }

    public static CodeAltaShellView Create(
        Visual sidebar,
        Visual sessionWorkspace,
        Visual sessionCommandBar,
        ShellCommandSurfaceCoordinator shellCommandSurfaceCoordinator,
        Visual? pluginFooter = null)
    {
        ArgumentNullException.ThrowIfNull(sidebar);
        ArgumentNullException.ThrowIfNull(sessionWorkspace);
        ArgumentNullException.ThrowIfNull(sessionCommandBar);
        ArgumentNullException.ThrowIfNull(shellCommandSurfaceCoordinator);
        var shellView = new CodeAltaShellView(
            sidebar,
            sessionWorkspace,
            pluginFooter ?? sessionCommandBar,
            CodeAltaGlobalCommandConfigurator.Configure);
        foreach (var command in shellCommandSurfaceCoordinator.CommandsFor(ShellCommandPlacement.ShellRoot))
        {
            shellView.Root.AddCommand(shellCommandSurfaceCoordinator.CreateViewCommand(command));
        }

        return shellView;
    }

}
