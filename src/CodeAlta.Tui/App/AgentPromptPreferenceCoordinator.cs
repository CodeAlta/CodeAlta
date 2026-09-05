using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Tui.App;

internal sealed class AgentPromptPreferenceCoordinator
{
    private readonly Dictionary<string, string> _draftPromptNamesByScope = new(StringComparer.OrdinalIgnoreCase);

    public string? GetDraftAgentPromptId(SessionViewViewState viewState, string? draftProjectRoot, string? draftProjectId)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        if (_draftPromptNamesByScope.TryGetValue(BuildDraftScopeKey(draftProjectRoot), out var draftPromptName))
        {
            return draftPromptName;
        }

        return viewState.ProjectPreferences.TryGetValue(BuildProjectPreferenceKey(draftProjectId), out var preference)
            ? NormalizeOptionalText(preference.AgentPromptId)
            : null;
    }

    public void RememberDraftAgentPromptId(SessionViewViewState viewState, string promptName, string? draftProjectRoot, string? draftProjectId)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptName);
        var normalizedPromptName = promptName.Trim();
        _draftPromptNamesByScope[BuildDraftScopeKey(draftProjectRoot)] = normalizedPromptName;
        var preferenceKey = BuildProjectPreferenceKey(draftProjectId);
        viewState.ProjectPreferences.TryGetValue(preferenceKey, out var existingPreference);
        viewState.ProjectPreferences[preferenceKey] = new SessionViewPreference
        {
            ProviderKey = existingPreference?.ProviderKey,
            ModelId = existingPreference?.ModelId,
            AgentPromptId = normalizedPromptName,
            ReasoningEffort = existingPreference?.ReasoningEffort,
        };
        viewState.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ApplySessionAgentPromptId(OpenSessionState tab, SessionViewViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(viewState);
        viewState.SessionPreferences.TryGetValue(tab.SessionView.SessionId, out var persistedPreference);
        tab.AgentPromptId = NormalizeOptionalText(tab.AgentPromptId)
            ?? NormalizeOptionalText(tab.SessionView.AgentPromptId)
            ?? NormalizeOptionalText(persistedPreference?.AgentPromptId)
            ?? AgentPromptCatalog.DefaultPromptName;
        tab.SessionView.AgentPromptId = tab.AgentPromptId;
    }

    public void RememberSessionAgentPromptId(SessionViewViewState viewState, OpenSessionState tab, string promptName)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptName);
        var normalizedPromptName = promptName.Trim();
        viewState.SessionPreferences.TryGetValue(tab.SessionView.SessionId, out var existingPreference);
        viewState.SessionPreferences[tab.SessionView.SessionId] = new SessionViewPreference
        {
            ProviderKey = existingPreference?.ProviderKey,
            ModelId = existingPreference?.ModelId ?? tab.ModelId,
            AgentPromptId = normalizedPromptName,
            ReasoningEffort = existingPreference?.ReasoningEffort ?? tab.ReasoningEffort,
        };
        tab.AgentPromptId = normalizedPromptName;
        tab.SessionView.AgentPromptId = normalizedPromptName;
        viewState.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string BuildDraftScopeKey(string? draftProjectRoot)
        => string.IsNullOrWhiteSpace(draftProjectRoot) ? "__global__" : draftProjectRoot.Trim();

    private static string BuildProjectPreferenceKey(string? projectId)
        => string.IsNullOrWhiteSpace(projectId) ? ModelProviderPreferenceCoordinator.GlobalProjectPreferenceKey : projectId.Trim();

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
