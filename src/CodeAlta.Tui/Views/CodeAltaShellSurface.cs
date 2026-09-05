using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.Views;

internal sealed class CodeAltaShellSurface
{
    private readonly Visual _dialogFallback;

    public CodeAltaShellSurface(CodeAltaShellView shellView, SessionWorkspaceView workspaceView, Visual dialogFallback)
    {
        ArgumentNullException.ThrowIfNull(shellView);
        ArgumentNullException.ThrowIfNull(workspaceView);
        ArgumentNullException.ThrowIfNull(dialogFallback);

        ShellView = shellView;
        WorkspaceView = workspaceView;
        _dialogFallback = dialogFallback;
    }

    public CodeAltaShellView ShellView { get; }

    public SessionWorkspaceView WorkspaceView { get; }

    public Visual Root => ShellView.Root;

    public Visual DialogAnchor => PromptFocusTarget ?? _dialogFallback;

    public Visual? PromptFocusTarget => WorkspaceView.SessionInput;
}
