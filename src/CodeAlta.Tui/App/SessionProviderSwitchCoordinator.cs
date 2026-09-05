using CodeAlta.Agent;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Chat;

namespace CodeAlta.Tui.App;

internal sealed class SessionProviderSwitchCoordinator
{
    private readonly IReadOnlyDictionary<string, ModelProviderState> _modelProviderStates;
    private readonly Func<OpenSessionState, Task> _applySessionPreferenceAsync;
    private readonly Func<string, Task<bool>> _detachRuntimeSessionAsync;
    private readonly Action<SessionViewDescriptor> _updateSessionState;
    private readonly Func<Task> _persistViewStateAsync;

    public SessionProviderSwitchCoordinator(
        IReadOnlyDictionary<string, ModelProviderState> modelProviderStates,
        Func<OpenSessionState, Task> applySessionPreferenceAsync,
        Func<string, Task<bool>> detachRuntimeSessionAsync,
        Action<SessionViewDescriptor> updateSessionState,
        Func<Task> persistViewStateAsync)
    {
        ArgumentNullException.ThrowIfNull(modelProviderStates);
        ArgumentNullException.ThrowIfNull(applySessionPreferenceAsync);
        ArgumentNullException.ThrowIfNull(detachRuntimeSessionAsync);
        ArgumentNullException.ThrowIfNull(updateSessionState);
        ArgumentNullException.ThrowIfNull(persistViewStateAsync);

        _modelProviderStates = modelProviderStates;
        _applySessionPreferenceAsync = applySessionPreferenceAsync;
        _detachRuntimeSessionAsync = detachRuntimeSessionAsync;
        _updateSessionState = updateSessionState;
        _persistViewStateAsync = persistViewStateAsync;
    }

    public bool CanSelectSessionProvider(SessionViewDescriptor session, OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);

        return !tab.StatusBusy &&
               tab.ActiveRunId is null;
    }

    public bool CanSwitchSessionProvider(
        SessionViewDescriptor session,
        OpenSessionState tab,
        ModelProviderId targetProviderId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);

        return CanSelectSessionProvider(session, tab) &&
               !string.Equals(session.ProviderId, targetProviderId.Value, StringComparison.OrdinalIgnoreCase) &&
               _modelProviderStates.TryGetValue(targetProviderId.Value, out var targetState) &&
               targetState.Availability == ModelProviderAvailability.Ready;
    }

    public async Task<bool> SwitchSessionProviderAsync(
        SessionViewDescriptor session,
        OpenSessionState tab,
        ModelProviderId targetProviderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);
        cancellationToken.ThrowIfCancellationRequested();

        if (!CanSwitchSessionProvider(session, tab, targetProviderId))
        {
            return false;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var oldSessionId = session.SessionId;
        var oldSessionProviderId = session.ProviderId;
        var oldSessionProviderKey = session.ProviderKey;
        var oldSessionUpdatedAt = session.UpdatedAt;
        var oldSessionModelId = session.ModelId;
        var oldSessionReasoningEffort = session.ReasoningEffort;
        var oldTabProviderId = tab.ProviderId;
        var oldTabModelId = tab.ModelId;
        var oldTabReasoningEffort = tab.ReasoningEffort;
        var oldTabUsage = tab.Usage;

        session.ProviderId = targetProviderId.Value;
        session.ProviderKey = targetProviderId.Value;
        session.UpdatedAt = timestamp;

        tab.ProviderId = targetProviderId;
        tab.ModelId = null;
        tab.ReasoningEffort = null;
        tab.Usage = null;

        try
        {
            await _applySessionPreferenceAsync(tab);
            NormalizeTargetModelSelection(tab, targetProviderId);
            session.ModelId = tab.ModelId;
            session.ReasoningEffort = tab.ReasoningEffort;
            if (!string.IsNullOrWhiteSpace(oldSessionId))
            {
                await _detachRuntimeSessionAsync(oldSessionId);
            }
        }
        catch
        {
            session.ProviderId = oldSessionProviderId;
            session.ProviderKey = oldSessionProviderKey;
            session.UpdatedAt = oldSessionUpdatedAt;
            session.ModelId = oldSessionModelId;
            session.ReasoningEffort = oldSessionReasoningEffort;
            tab.ProviderId = oldTabProviderId;
            tab.ModelId = oldTabModelId;
            tab.ReasoningEffort = oldTabReasoningEffort;
            tab.Usage = oldTabUsage;
            throw;
        }

        _updateSessionState(session);
        await _persistViewStateAsync();
        return true;
    }

    private void NormalizeTargetModelSelection(OpenSessionState tab, ModelProviderId targetProviderId)
    {
        if (!_modelProviderStates.TryGetValue(targetProviderId.Value, out var targetState))
        {
            return;
        }

        if (targetState.Models.Count == 0)
        {
            tab.ModelId = string.IsNullOrWhiteSpace(targetState.SelectedModelId)
                ? null
                : targetState.SelectedModelId.Trim();
            tab.ReasoningEffort = targetState.SelectedReasoningEffort;
            return;
        }

        if (string.IsNullOrWhiteSpace(tab.ModelId) ||
            targetState.Models.Any(model => string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal)))
        {
            return;
        }

        tab.ModelId = ModelProviderPresentation.ResolvePreferredModelId(targetState.Models, targetState.SelectedModelId);
        var selectedModel = ModelProviderPreferenceCoordinator.FindModel(targetState.Models, tab.ModelId);
        tab.ReasoningEffort = ModelProviderPresentation.ResolvePreferredReasoningEffort(
            selectedModel,
            targetState.SelectedReasoningEffort);
    }

}
