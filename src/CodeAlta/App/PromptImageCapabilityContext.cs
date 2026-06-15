using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;

namespace CodeAlta.App;

internal sealed class PromptImageCapabilityContext
{
    private readonly Func<SessionViewDescriptor?> _getSelectedSession;
    private readonly Func<string, OpenSessionState?> _findOpenSession;
    private readonly Func<ModelProviderId> _getPreferredProviderId;
    private readonly IReadOnlyDictionary<string, ModelProviderState> _modelProviderStates;

    public PromptImageCapabilityContext(
        Func<SessionViewDescriptor?> getSelectedSession,
        Func<string, OpenSessionState?> findOpenSession,
        Func<ModelProviderId> getPreferredProviderId,
        IReadOnlyDictionary<string, ModelProviderState> modelProviderStates)
    {
        ArgumentNullException.ThrowIfNull(getSelectedSession);
        ArgumentNullException.ThrowIfNull(findOpenSession);
        ArgumentNullException.ThrowIfNull(getPreferredProviderId);
        ArgumentNullException.ThrowIfNull(modelProviderStates);

        _getSelectedSession = getSelectedSession;
        _findOpenSession = findOpenSession;
        _getPreferredProviderId = getPreferredProviderId;
        _modelProviderStates = modelProviderStates;
    }

    public bool CurrentPromptModelSupportsImageInput()
    {
        var (providerId, model) = ResolveCurrentPromptModel();
        return AgentModelCapabilityHelper.SupportsImageInput(providerId, model);
    }

    public string BuildCurrentPromptImageUnsupportedMessage()
    {
        var (providerId, model) = ResolveCurrentPromptModel();
        var modelName = model?.DisplayName ?? model?.Id ?? SR.T("the selected model");
        return SR.T("Image paste is not available because {0} on {1} does not advertise image input support.", modelName, providerId.Value);
    }

    private (ModelProviderId ProviderId, AgentModelInfo? Model) ResolveCurrentPromptModel()
    {
        var selectedSession = _getSelectedSession();
        var selectedTab = selectedSession is null ? null : _findOpenSession(selectedSession.SessionId);
        var providerId = selectedTab?.ProviderId ?? (selectedSession is { } session
            ? new ModelProviderId(session.ResolvedProviderKey)
            : _getPreferredProviderId());
        if (!_modelProviderStates.TryGetValue(providerId.Value, out var providerState))
        {
            return (providerId, null);
        }

        var modelId = selectedTab?.ModelId ?? providerState.SelectedModelId;
        var selectedModel = !string.IsNullOrWhiteSpace(modelId)
            ? providerState.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.Ordinal))
            : null;
        selectedModel ??= ModelProviderPresentation.GetSelectedModel(providerState) ??
            (providerState.Models.Count == 1 ? providerState.Models[0] : null);
        return (providerId, selectedModel);
    }
}
