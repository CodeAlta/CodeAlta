using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;

namespace CodeAlta.App;

internal sealed class PromptImageCapabilityContext
{
    private readonly Func<WorkThreadDescriptor?> _getSelectedThread;
    private readonly Func<string, OpenThreadState?> _findOpenThread;
    private readonly Func<AgentBackendId> _getPreferredBackendId;
    private readonly IReadOnlyDictionary<string, ChatBackendState> _chatBackendStates;

    public PromptImageCapabilityContext(
        Func<WorkThreadDescriptor?> getSelectedThread,
        Func<string, OpenThreadState?> findOpenThread,
        Func<AgentBackendId> getPreferredBackendId,
        IReadOnlyDictionary<string, ChatBackendState> chatBackendStates)
    {
        ArgumentNullException.ThrowIfNull(getSelectedThread);
        ArgumentNullException.ThrowIfNull(findOpenThread);
        ArgumentNullException.ThrowIfNull(getPreferredBackendId);
        ArgumentNullException.ThrowIfNull(chatBackendStates);

        _getSelectedThread = getSelectedThread;
        _findOpenThread = findOpenThread;
        _getPreferredBackendId = getPreferredBackendId;
        _chatBackendStates = chatBackendStates;
    }

    public bool CurrentPromptModelSupportsImageInput()
    {
        var (backendId, model) = ResolveCurrentPromptModel();
        return AgentModelCapabilityHelper.SupportsImageInput(backendId, model);
    }

    public string BuildCurrentPromptImageUnsupportedMessage()
    {
        var (backendId, model) = ResolveCurrentPromptModel();
        var modelName = model?.DisplayName ?? model?.Id ?? "the selected model";
        return $"Image paste is not available because {modelName} on {backendId.Value} does not advertise image input support.";
    }

    private (AgentBackendId BackendId, AgentModelInfo? Model) ResolveCurrentPromptModel()
    {
        var selectedThread = _getSelectedThread();
        var selectedTab = selectedThread is null ? null : _findOpenThread(selectedThread.ThreadId);
        var backendId = selectedTab?.BackendId ?? (selectedThread is { } thread
            ? new AgentBackendId(thread.BackendId)
            : _getPreferredBackendId());
        if (!_chatBackendStates.TryGetValue(backendId.Value, out var backendState))
        {
            return (backendId, null);
        }

        var modelId = selectedTab?.ModelId ?? backendState.SelectedModelId;
        var selectedModel = !string.IsNullOrWhiteSpace(modelId)
            ? backendState.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.Ordinal))
            : null;
        selectedModel ??= ChatBackendPresentation.GetSelectedModel(backendState) ??
            (backendState.Models.Count == 1 ? backendState.Models[0] : null);
        return (backendId, selectedModel);
    }
}
