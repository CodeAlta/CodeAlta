using CodeAlta.Tui.App.State;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Chat;
using XenoAtom.Logging;

namespace CodeAlta.Tui.App;

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
        ModelProviderState providerState,
        SessionViewViewState viewState,
        string? draftProjectRoot,
        string? draftProjectId)
    {
        ArgumentNullException.ThrowIfNull(providerState);
        ArgumentNullException.ThrowIfNull(viewState);

        var scopeKey = BuildDraftScopeKey(draftProjectRoot);
        var projectPreferenceKey = BuildProjectPreferenceKey(draftProjectId);
        var defaults = _configStore.GetEffectiveProviderPreference(providerState.ProviderId.Value, draftProjectRoot);
        _draftPreferencesByScope.TryGetValue(scopeKey, out var draftPreference);
        viewState.ProjectPreferences.TryGetValue(projectPreferenceKey, out var projectPreference);
        var preserveCurrentSelection = string.Equals(providerState.DraftScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase);
        var matchingDraftPreference = draftPreference is not null &&
            string.Equals(draftPreference.ProviderId.Value, providerState.ProviderId.Value, StringComparison.OrdinalIgnoreCase)
                ? draftPreference
                : null;
        var matchingProjectPreference = projectPreference is not null &&
            string.Equals(projectPreference.ProviderKey, providerState.ProviderId.Value, StringComparison.OrdinalIgnoreCase)
                ? projectPreference
                : null;
        var preferredModelId = matchingDraftPreference is not null
            ? matchingDraftPreference.ModelId ?? defaults.Model
            : matchingProjectPreference is not null
                ? matchingProjectPreference.ModelId ?? defaults.Model
                : preserveCurrentSelection
                    ? providerState.SelectedModelId ?? defaults.Model
                    : defaults.Model;

        providerState.SelectedModelId = ResolveModelSelection(providerState.Models, preferredModelId);
        var selectedModel = FindModel(providerState.Models, providerState.SelectedModelId);
        var preferredReasoningEffort = matchingDraftPreference is not null
            ? matchingDraftPreference.ReasoningEffort ?? defaults.ReasoningEffort
            : matchingProjectPreference is not null
                ? matchingProjectPreference.ReasoningEffort ?? defaults.ReasoningEffort
                : preserveCurrentSelection
                    ? providerState.SelectedReasoningEffort ?? defaults.ReasoningEffort
                    : defaults.ReasoningEffort;

        providerState.SelectedReasoningEffort = ModelProviderPresentation.ResolvePreferredReasoningEffort(selectedModel, preferredReasoningEffort);
        providerState.DraftScopeKey = scopeKey;
    }

    public ModelProviderPreference? GetDraftModelProviderPreference(
        SessionViewViewState viewState,
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

    public void ApplySessionPreference(
        OpenSessionState tab,
        SessionViewViewState viewState,
        string? sessionProjectRoot,
        IReadOnlyDictionary<string, ModelProviderState> modelProviderStates)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(modelProviderStates);

        viewState.SessionPreferences.TryGetValue(tab.SessionView.SessionId, out var persistedPreference);
        var defaults = _configStore.GetEffectiveProviderPreference(tab.ProviderId.Value, sessionProjectRoot);
        modelProviderStates.TryGetValue(tab.ProviderId.Value, out var providerState);
        var preferredModelId = tab.ModelId ?? tab.Session.ModelId ?? persistedPreference?.ModelId ?? defaults.Model;
        tab.ModelId = ResolveModelSelection(providerState?.Models ?? [], preferredModelId);
        tab.ReasoningEffort ??= tab.Session.ReasoningEffort ?? persistedPreference?.ReasoningEffort ?? defaults.ReasoningEffort;

        if (providerState is null)
        {
            return;
        }

        var selectedModel = FindModel(providerState.Models, tab.ModelId);
        tab.ReasoningEffort = ModelProviderPresentation.ResolvePreferredReasoningEffort(
            selectedModel,
            tab.ReasoningEffort);
    }

    public void RememberGlobalModelProviderPreference(
        SessionViewViewState viewState,
        ModelProviderId providerId,
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
                providerId,
                normalizedModelId,
                reasoningEffort);
        }

        if (rememberProjectPreference)
        {
            var preferenceKey = BuildProjectPreferenceKey(draftProjectId);
            viewState.ProjectPreferences.TryGetValue(preferenceKey, out var existingPreference);
            viewState.ProjectPreferences[preferenceKey] = new SessionViewPreference
            {
                ProviderKey = providerId.Value,
                ModelId = normalizedModelId,
                AgentPromptId = existingPreference?.AgentPromptId,
                ReasoningEffort = reasoningEffort,
            };
            viewState.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void RememberSessionPreference(
        SessionViewViewState viewState,
        string sessionId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var normalizedModel = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        viewState.SessionPreferences.TryGetValue(sessionId, out var existingPreference);
        if (normalizedModel is null && reasoningEffort is null && string.IsNullOrWhiteSpace(existingPreference?.AgentPromptId))
        {
            viewState.SessionPreferences.Remove(sessionId);
        }
        else
        {
            viewState.SessionPreferences[sessionId] = new SessionViewPreference
            {
                ModelId = normalizedModel,
                AgentPromptId = existingPreference?.AgentPromptId,
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

        return ModelProviderPresentation.ResolvePreferredModelId(models, preferredModelId);
    }

    private static string BuildDraftScopeKey(string? draftProjectRoot)
        => draftProjectRoot ?? "__global__";

    private static string BuildProjectPreferenceKey(string? projectId)
        => string.IsNullOrWhiteSpace(projectId) ? GlobalProjectPreferenceKey : projectId.Trim();

    private sealed record DraftModelProviderPreference(
        ModelProviderId ProviderId,
        string? ModelId,
        AgentReasoningEffort? ReasoningEffort);
}
