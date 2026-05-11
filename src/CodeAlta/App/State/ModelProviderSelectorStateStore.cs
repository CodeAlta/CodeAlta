using CodeAlta.Models;
using CodeAlta.Threading;
using CodeAlta.ViewModels;

namespace CodeAlta.App.State;

internal sealed class ModelProviderSelectorStateStore
{
    private readonly ThreadWorkspaceViewModel _workspaceViewModel;
    private readonly IUiDispatcher _uiDispatcher;

    public ModelProviderSelectorStateStore(
        ThreadWorkspaceViewModel workspaceViewModel,
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

    public void SetModelProviderSelection(IReadOnlyList<ChatBackendOption> items, int selectedIndex)
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
