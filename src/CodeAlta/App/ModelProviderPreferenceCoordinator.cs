using CodeAlta.App.State;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class ModelProviderPreferenceCoordinator
{
    public const string GlobalProjectPreferenceKey = "4b03e143-8c55-4bcb-a08d-6fc2f99f1b2a";

    private readonly CodeAltaConfigStore _configStore;
    private readonly Logger _logger;
    private readonly Dictionary<string, DraftModelProviderPreference> _draftPreferencesByScope = new(StringComparer.OrdinalIgnoreCase);

    public ModelProviderPreferenceCoordinator(CodeAltaConfigStore configStore, Logger logger)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(logger);

        _configStore = configStore;
        _logger = logger;
    }

    public void ApplyDraftModelProviderPreference(
        ChatBackendState backendState,
        WorkThreadViewState viewState,
        string? draftProjectRoot,
        string? draftProjectId)
    {
        ArgumentNullException.ThrowIfNull(backendState);
        ArgumentNullException.ThrowIfNull(viewState);

        var scopeKey = BuildDraftScopeKey(draftProjectRoot);
        var projectPreferenceKey = BuildProjectPreferenceKey(draftProjectId);
        var defaults = _configStore.GetEffectiveProviderPreference(backendState.BackendId.Value, draftProjectRoot);
        _draftPreferencesByScope.TryGetValue(scopeKey, out var draftPreference);
        viewState.ProjectPreferences.TryGetValue(projectPreferenceKey, out var projectPreference);
        var preserveCurrentSelection = string.Equals(backendState.DraftScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase);
        var matchingDraftPreference = draftPreference is not null &&
            string.Equals(draftPreference.BackendId.Value, backendState.BackendId.Value, StringComparison.OrdinalIgnoreCase)
                ? draftPreference
                : null;
        var matchingProjectPreference = projectPreference is not null &&
            string.Equals(projectPreference.ProviderKey, backendState.BackendId.Value, StringComparison.OrdinalIgnoreCase)
                ? projectPreference
                : null;
        var preferredModelId = matchingDraftPreference is not null
            ? matchingDraftPreference.ModelId ?? defaults.Model
            : matchingProjectPreference is not null
                ? matchingProjectPreference.ModelId ?? defaults.Model
                : preserveCurrentSelection
                    ? backendState.SelectedModelId ?? defaults.Model
                    : defaults.Model;

        backendState.SelectedModelId = ResolveModelSelection(backendState.Models, preferredModelId);
        var selectedModel = FindModel(backendState.Models, backendState.SelectedModelId);
        var preferredReasoningEffort = matchingDraftPreference is not null
            ? matchingDraftPreference.ReasoningEffort ?? defaults.ReasoningEffort
            : matchingProjectPreference is not null
                ? matchingProjectPreference.ReasoningEffort ?? defaults.ReasoningEffort
                : preserveCurrentSelection
                    ? backendState.SelectedReasoningEffort ?? defaults.ReasoningEffort
                    : defaults.ReasoningEffort;

        backendState.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(selectedModel, preferredReasoningEffort);
        backendState.DraftScopeKey = scopeKey;
    }

    public ModelProviderPreference? GetDraftModelProviderPreference(
        WorkThreadViewState viewState,
        string? draftProjectId)
    {
        ArgumentNullException.ThrowIfNull(viewState);

        return viewState.ProjectPreferences.TryGetValue(BuildProjectPreferenceKey(draftProjectId), out var preference) &&
            !string.IsNullOrWhiteSpace(preference.ProviderKey)
                ? new ModelProviderPreference(
                    new ModelProviderId(preference.ProviderKey.Trim()),
                    preference.ModelId,
                    preference.ReasoningEffort)
                : null;
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
        var defaults = _configStore.GetEffectiveProviderPreference(tab.BackendId.Value, threadProjectRoot);
        chatBackendStates.TryGetValue(tab.BackendId.Value, out var backendState);
        var preferredModelId = tab.ModelId ?? tab.Thread.ModelId ?? persistedPreference?.ModelId ?? defaults.Model;
        tab.ModelId = ResolveModelSelection(backendState?.Models ?? [], preferredModelId);
        tab.ReasoningEffort ??= tab.Thread.ReasoningEffort ?? persistedPreference?.ReasoningEffort ?? defaults.ReasoningEffort;

        if (backendState is null)
        {
            return;
        }

        var selectedModel = FindModel(backendState.Models, tab.ModelId);
        tab.ReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(
            selectedModel,
            tab.ReasoningEffort);
    }

    public void RememberGlobalModelProviderPreference(
        WorkThreadViewState viewState,
        AgentBackendId backendId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort,
        string? draftProjectRoot = null,
        string? draftProjectId = null,
        bool rememberDraftScope = false)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        var normalizedModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        var rememberProjectPreference = rememberDraftScope || !string.IsNullOrWhiteSpace(draftProjectId);
        if (rememberDraftScope)
        {
            _draftPreferencesByScope[BuildDraftScopeKey(draftProjectRoot)] = new DraftModelProviderPreference(
                backendId,
                normalizedModelId,
                reasoningEffort);
        }

        if (rememberProjectPreference)
        {
            viewState.ProjectPreferences[BuildProjectPreferenceKey(draftProjectId)] = new WorkThreadPreference
            {
                ProviderKey = backendId.Value,
                ModelId = normalizedModelId,
                ReasoningEffort = reasoningEffort,
            };
            viewState.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void RememberThreadPreference(
        WorkThreadViewState viewState,
        string threadId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var normalizedModel = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        if (normalizedModel is null && reasoningEffort is null)
        {
            viewState.ThreadPreferences.Remove(threadId);
        }
        else
        {
            viewState.ThreadPreferences[threadId] = new WorkThreadPreference
            {
                ModelId = normalizedModel,
                ReasoningEffort = reasoningEffort,
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

    private static string? ResolveModelSelection(IReadOnlyList<AgentModelInfo> models, string? preferredModelId)
    {
        ArgumentNullException.ThrowIfNull(models);

        if (!string.IsNullOrWhiteSpace(preferredModelId))
        {
            return preferredModelId.Trim();
        }

        return ChatBackendPresentation.ResolvePreferredModelId(models, preferredModelId);
    }

    private static string BuildDraftScopeKey(string? draftProjectRoot)
        => draftProjectRoot ?? "__global__";

    private static string BuildProjectPreferenceKey(string? projectId)
        => string.IsNullOrWhiteSpace(projectId) ? GlobalProjectPreferenceKey : projectId.Trim();

    private sealed record DraftModelProviderPreference(
        AgentBackendId BackendId,
        string? ModelId,
        AgentReasoningEffort? ReasoningEffort);
}
