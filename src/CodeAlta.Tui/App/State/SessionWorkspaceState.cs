using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tui.App.State;

internal sealed class SessionWorkspaceState
{
    public SessionWorkspaceState(SessionTabViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
    }

    public SessionTabViewModel ViewModel { get; }
}
