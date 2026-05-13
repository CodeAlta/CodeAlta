using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Workspace;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Views;

internal sealed class ModelCatalogCoordinator
{
    private readonly IReadOnlyDictionary<string, ChatBackendState> _chatBackendStates;
    private readonly ModelProviderSelectorCoordinator _modelProviderSelectorCoordinator;
    private readonly Func<WorkThreadDescriptor?> _getSelectedThread;
    private readonly Func<string, OpenThreadState?> _findOpenThread;
    private readonly Func<AgentBackendId> _getPreferredModelProviderId;
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Action _focusPrompt;
    private readonly Action _focusReasoning;
    private readonly Action<string, StatusTone> _setStatus;

    public ModelCatalogCoordinator(
        IReadOnlyDictionary<string, ChatBackendState> chatBackendStates,
        ModelProviderSelectorCoordinator modelProviderSelectorCoordinator,
        Func<WorkThreadDescriptor?> getSelectedThread,
        Func<string, OpenThreadState?> findOpenThread,
        Func<AgentBackendId> getPreferredModelProviderId,
        Func<Rectangle?> getDialogBounds,
        Func<Visual?> getFocusTarget,
        Action focusPrompt,
        Action focusReasoning,
        Action<string, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(modelProviderSelectorCoordinator);
        ArgumentNullException.ThrowIfNull(getSelectedThread);
        ArgumentNullException.ThrowIfNull(findOpenThread);
        ArgumentNullException.ThrowIfNull(getPreferredModelProviderId);
        ArgumentNullException.ThrowIfNull(getDialogBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        ArgumentNullException.ThrowIfNull(focusPrompt);
        ArgumentNullException.ThrowIfNull(focusReasoning);
        ArgumentNullException.ThrowIfNull(setStatus);

        _chatBackendStates = chatBackendStates;
        _modelProviderSelectorCoordinator = modelProviderSelectorCoordinator;
        _getSelectedThread = getSelectedThread;
        _findOpenThread = findOpenThread;
        _getPreferredModelProviderId = getPreferredModelProviderId;
        _getDialogBounds = getDialogBounds;
        _getFocusTarget = getFocusTarget;
        _focusPrompt = focusPrompt;
        _focusReasoning = focusReasoning;
        _setStatus = setStatus;
    }

    public void Open()
    {
        var (providerKey, modelId) = ResolveCurrentModelSelection();
        new ModelCatalogDialog(
            _chatBackendStates,
            providerKey,
            modelId,
            SelectModelAsync,
            _getDialogBounds,
            _getFocusTarget)
            .Show();
    }

    private async Task SelectModelAsync(ModelCatalogRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var selected = await _modelProviderSelectorCoordinator.SelectProviderModelAsync(new AgentBackendId(row.ProviderKey), row.ModelId);
        if (!selected)
        {
            _setStatus($"Could not select model '{row.ModelId}' from provider '{row.ProviderKey}'.", StatusTone.Warning);
            _focusPrompt();
            return;
        }

        _setStatus($"Selected {row.ProviderDisplayName}: {row.ModelDisplayName}.", StatusTone.Ready);
        _focusReasoning();
    }

    private (string? ProviderKey, string? ModelId) ResolveCurrentModelSelection()
    {
        if (_getSelectedThread() is { } selectedThread && _findOpenThread(selectedThread.ThreadId) is { } tab)
        {
            var tabProviderKey = tab.BackendId.Value;
            var tabModelId = tab.ModelId;
            if (string.IsNullOrWhiteSpace(tabModelId) && _chatBackendStates.TryGetValue(tabProviderKey, out var tabBackendState))
            {
                tabModelId = tabBackendState.SelectedModelId;
            }

            return (tabProviderKey, tabModelId);
        }

        var providerKey = _getPreferredModelProviderId().Value;
        var modelId = _chatBackendStates.TryGetValue(providerKey, out var backendState)
            ? backendState.SelectedModelId
            : null;
        return (providerKey, modelId);
    }
}
