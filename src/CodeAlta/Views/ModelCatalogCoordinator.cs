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
    private readonly IReadOnlyDictionary<string, ModelProviderState> _modelProviderStates;
    private readonly ModelProviderSelectorCoordinator _modelProviderSelectorCoordinator;
    private readonly Func<SessionViewDescriptor?> _getSelectedSession;
    private readonly Func<string, OpenSessionState?> _findOpenSession;
    private readonly Func<ModelProviderId> _getPreferredModelProviderId;
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Action _focusPrompt;
    private readonly Action _focusReasoning;
    private readonly Action<string, StatusTone> _setStatus;

    public ModelCatalogCoordinator(
        IReadOnlyDictionary<string, ModelProviderState> modelProviderStates,
        ModelProviderSelectorCoordinator modelProviderSelectorCoordinator,
        Func<SessionViewDescriptor?> getSelectedSession,
        Func<string, OpenSessionState?> findOpenSession,
        Func<ModelProviderId> getPreferredModelProviderId,
        Func<Rectangle?> getDialogBounds,
        Func<Visual?> getFocusTarget,
        Action focusPrompt,
        Action focusReasoning,
        Action<string, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(modelProviderStates);
        ArgumentNullException.ThrowIfNull(modelProviderSelectorCoordinator);
        ArgumentNullException.ThrowIfNull(getSelectedSession);
        ArgumentNullException.ThrowIfNull(findOpenSession);
        ArgumentNullException.ThrowIfNull(getPreferredModelProviderId);
        ArgumentNullException.ThrowIfNull(getDialogBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        ArgumentNullException.ThrowIfNull(focusPrompt);
        ArgumentNullException.ThrowIfNull(focusReasoning);
        ArgumentNullException.ThrowIfNull(setStatus);

        _modelProviderStates = modelProviderStates;
        _modelProviderSelectorCoordinator = modelProviderSelectorCoordinator;
        _getSelectedSession = getSelectedSession;
        _findOpenSession = findOpenSession;
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
            _modelProviderStates,
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

        var selected = await _modelProviderSelectorCoordinator.SelectProviderModelAsync(new ModelProviderId(row.ProviderKey), row.ModelId);
        if (!selected)
        {
            _setStatus(SR.T("Could not select model '{0}' from provider '{1}'.", row.ModelId, row.ProviderKey), StatusTone.Warning);
            _focusPrompt();
            return;
        }

        _setStatus(SR.T("Selected {0}: {1}.", row.ProviderDisplayName, row.ModelDisplayName), StatusTone.Ready);
        _focusReasoning();
    }

    private (string? ProviderKey, string? ModelId) ResolveCurrentModelSelection()
    {
        if (_getSelectedSession() is { } selectedSession && _findOpenSession(selectedSession.SessionId) is { } tab)
        {
            var tabProviderKey = tab.ProviderId.Value;
            var tabModelId = tab.ModelId;
            if (string.IsNullOrWhiteSpace(tabModelId) && _modelProviderStates.TryGetValue(tabProviderKey, out var tabProviderState))
            {
                tabModelId = tabProviderState.SelectedModelId;
            }

            return (tabProviderKey, tabModelId);
        }

        var providerKey = _getPreferredModelProviderId().Value;
        var modelId = _modelProviderStates.TryGetValue(providerKey, out var providerState)
            ? providerState.SelectedModelId
            : null;
        return (providerKey, modelId);
    }
}
