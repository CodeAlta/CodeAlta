using CodeAlta.Tui.Models;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tui.App.State;

internal sealed class ModelProviderSelectorStateStore
{
    private readonly SessionWorkspaceViewModel _workspaceViewModel;
    private readonly IUiDispatcher _uiDispatcher;

    public ModelProviderSelectorStateStore(
        SessionWorkspaceViewModel workspaceViewModel,
        IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(uiDispatcher);

        _workspaceViewModel = workspaceViewModel;
        _uiDispatcher = uiDispatcher;
    }

    public int? GetSelectedModelProviderIndex()
        => _workspaceViewModel.SelectedModelProviderIndex >= 0 ? _workspaceViewModel.SelectedModelProviderIndex : null;

    public int? GetSelectedModelIndex()
        => _workspaceViewModel.SelectedModelIndex >= 0 ? _workspaceViewModel.SelectedModelIndex : null;

    public int? GetSelectedReasoningIndex()
        => _workspaceViewModel.SelectedReasoningIndex >= 0 ? _workspaceViewModel.SelectedReasoningIndex : null;

    public void SetModelProviderSelection(IReadOnlyList<ModelProviderOption> items, int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(items);
        _workspaceViewModel.ModelProviderOptions = items;
        _workspaceViewModel.SelectedModelProviderIndex = selectedIndex;
    }

    public void SetSelectedModelProviderIndex(int selectedIndex)
        => _workspaceViewModel.SelectedModelProviderIndex = selectedIndex;

    public void SetModelSelection(IReadOnlyList<ChatModelOption> items, int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(items);
        _workspaceViewModel.ModelOptions = items;
        _workspaceViewModel.SelectedModelIndex = selectedIndex;
    }

    public void SetReasoningSelection(IReadOnlyList<ChatReasoningOption> items, int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(items);
        _workspaceViewModel.ReasoningOptions = items;
        _workspaceViewModel.SelectedReasoningIndex = selectedIndex;
    }

    public IUiDispatcher GetUiDispatcher()
        => _uiDispatcher;

    public void VerifyBindableAccess()
        => _uiDispatcher.VerifyAccess();
}
