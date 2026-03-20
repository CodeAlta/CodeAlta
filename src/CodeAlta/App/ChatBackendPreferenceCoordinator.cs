using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class ChatBackendPreferenceCoordinator
{
    private readonly CodeAltaConfigStore _configStore;
    private readonly Logger _logger;

    public ChatBackendPreferenceCoordinator(CodeAltaConfigStore configStore, Logger logger)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(logger);

        _configStore = configStore;
        _logger = logger;
    }

    public void ApplyDraftBackendPreference(ChatBackendState backendState, string? draftProjectRoot)
    {
        ArgumentNullException.ThrowIfNull(backendState);

        var scopeKey = BuildDraftScopeKey(draftProjectRoot);
        var defaults = _configStore.GetEffectiveBackendPreference(backendState.BackendId, draftProjectRoot);
        var preserveCurrentSelection = string.Equals(backendState.DraftScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase);
        var preferredModelId = preserveCurrentSelection
            ? backendState.SelectedModelId ?? defaults.Model
            : defaults.Model;

        backendState.SelectedModelId = ChatBackendPresentation.ResolvePreferredModelId(backendState.Models, preferredModelId);
        var selectedModel = FindModel(backendState.Models, backendState.SelectedModelId);
        var preferredReasoningEffort = preserveCurrentSelection
            ? backendState.SelectedReasoningEffort ?? defaults.ReasoningEffort
            : defaults.ReasoningEffort;

        backendState.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(selectedModel, preferredReasoningEffort);
        backendState.DraftScopeKey = scopeKey;
    }

    public void ApplyThreadPreference(
        OpenThreadState tab,
        WorkThreadViewState viewState,
        string? threadProjectRoot,
        IReadOnlyDictionary<string, ChatBackendState> chatBackendStates)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(chatBackendStates);

        viewState.ThreadPreferences.TryGetValue(tab.Thread.ThreadId, out var persistedPreference);
        var defaults = _configStore.GetEffectiveBackendPreference(tab.BackendId, threadProjectRoot);
        tab.ModelId ??= persistedPreference?.ModelId ?? defaults.Model;
        tab.ReasoningEffort ??= persistedPreference?.ReasoningEffort ?? defaults.ReasoningEffort;
        tab.AutoScroll = persistedPreference?.AutoScroll ?? true;

        if (!chatBackendStates.TryGetValue(tab.BackendId.Value, out var backendState))
        {
            return;
        }

        tab.ModelId = ChatBackendPresentation.ResolvePreferredModelId(
            backendState.Models,
            tab.ModelId);

        var selectedModel = FindModel(backendState.Models, tab.ModelId);
        tab.ReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(
            selectedModel,
            tab.ReasoningEffort);
    }

    public void RememberGlobalBackendPreference(
        AgentBackendId backendId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort)
    {
        try
        {
            _configStore.SaveGlobalBackendPreference(backendId, modelId, reasoningEffort);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save CodeAlta backend preferences.");
        }
    }

    public void RememberThreadPreference(
        WorkThreadViewState viewState,
        string threadId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort,
        bool autoScroll)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var normalizedModel = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        if (normalizedModel is null && reasoningEffort is null && autoScroll)
        {
            viewState.ThreadPreferences.Remove(threadId);
        }
        else
        {
            viewState.ThreadPreferences[threadId] = new WorkThreadPreference
            {
                ModelId = normalizedModel,
                ReasoningEffort = reasoningEffort,
                AutoScroll = autoScroll,
            };
        }

        viewState.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static AgentModelInfo? FindModel(IReadOnlyList<AgentModelInfo> models, string? modelId)
    {
        return string.IsNullOrWhiteSpace(modelId)
            ? null
            : models.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.Ordinal));
    }

    private static string BuildDraftScopeKey(string? draftProjectRoot)
        => draftProjectRoot ?? "__global__";
}