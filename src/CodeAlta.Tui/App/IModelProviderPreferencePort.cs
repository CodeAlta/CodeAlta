using CodeAlta.Tui.App.State;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Chat;

namespace CodeAlta.Tui.App;

internal sealed record ModelProviderPreference(
    ModelProviderId ModelProviderId,
    string? ModelId = null,
    AgentReasoningEffort? ReasoningEffort = null)
{
    public ModelProviderPreference Normalize()
        => this with { ModelId = string.IsNullOrWhiteSpace(ModelId) ? null : ModelId };
}

internal interface IModelProviderPreferencePort
{
    ModelProviderId GetPreferredModelProviderId(ProjectId projectId);

    bool IsModelProviderReady(ModelProviderId modelProviderId);

    void ApplyDraftPreference(PromptSessionBinding promptSession, ModelProviderPreference preference);

    void ApplySessionPreference(string sessionId, ModelProviderPreference preference);

    void RememberProjectPreference(ProjectId projectId, ModelProviderPreference preference);

    void RememberSessionPreference(string sessionId, ModelProviderPreference preference, bool persistNow);

    void ApplyDraftModelProviderState(ModelProviderState modelProviderState);

    void ApplySessionModelProviderState(OpenSessionState sessionState);

    void RememberGlobalPreference(ModelProviderPreference preference);
}

internal sealed class DelegatingModelProviderPreferencePort : IModelProviderPreferencePort
{
    private readonly Func<ProjectId, ModelProviderId> _getPreferredModelProviderId;
    private readonly Func<ModelProviderId, bool> _isModelProviderReady;
    private readonly Action<PromptSessionBinding, ModelProviderPreference> _applyDraftPreference;
    private readonly Action<string, ModelProviderPreference> _applySessionPreference;
    private readonly Action<ProjectId, ModelProviderPreference> _rememberProjectPreference;
    private readonly Action<string, ModelProviderPreference, bool> _rememberSessionPreference;
    private readonly Action<ModelProviderState> _applyDraftModelProviderState;
    private readonly Action<OpenSessionState> _applySessionModelProviderState;
    private readonly Action<ModelProviderPreference> _rememberGlobalPreference;

    public DelegatingModelProviderPreferencePort(
        Func<ProjectId, ModelProviderId> getPreferredModelProviderId,
        Func<ModelProviderId, bool> isModelProviderReady,
        Action<PromptSessionBinding, ModelProviderPreference> applyDraftPreference,
        Action<string, ModelProviderPreference> applySessionPreference,
        Action<ProjectId, ModelProviderPreference> rememberProjectPreference,
        Action<string, ModelProviderPreference, bool> rememberSessionPreference,
        Action<ModelProviderState>? applyDraftModelProviderState = null,
        Action<OpenSessionState>? applySessionModelProviderState = null,
        Action<ModelProviderPreference>? rememberGlobalPreference = null)
    {
        ArgumentNullException.ThrowIfNull(getPreferredModelProviderId);
        ArgumentNullException.ThrowIfNull(isModelProviderReady);
        ArgumentNullException.ThrowIfNull(applyDraftPreference);
        ArgumentNullException.ThrowIfNull(applySessionPreference);
        ArgumentNullException.ThrowIfNull(rememberProjectPreference);
        ArgumentNullException.ThrowIfNull(rememberSessionPreference);

        _getPreferredModelProviderId = getPreferredModelProviderId;
        _isModelProviderReady = isModelProviderReady;
        _applyDraftPreference = applyDraftPreference;
        _applySessionPreference = applySessionPreference;
        _rememberProjectPreference = rememberProjectPreference;
        _rememberSessionPreference = rememberSessionPreference;
        _applyDraftModelProviderState = applyDraftModelProviderState ?? (static _ => { });
        _applySessionModelProviderState = applySessionModelProviderState ?? (static _ => { });
        _rememberGlobalPreference = rememberGlobalPreference ?? (static _ => { });
    }

    public ModelProviderId GetPreferredModelProviderId(ProjectId projectId)
    {
        if (projectId == default)
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }

        return _getPreferredModelProviderId(projectId);
    }

    public bool IsModelProviderReady(ModelProviderId modelProviderId)
    {
        EnsureModelProviderId(modelProviderId);
        return _isModelProviderReady(modelProviderId);
    }

    public void ApplyDraftPreference(PromptSessionBinding promptSession, ModelProviderPreference preference)
    {
        ArgumentNullException.ThrowIfNull(promptSession);
        EnsurePreference(preference);
        _applyDraftPreference(promptSession, preference.Normalize());
    }

    public void ApplySessionPreference(string sessionId, ModelProviderPreference preference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsurePreference(preference);
        _applySessionPreference(sessionId, preference.Normalize());
    }

    public void RememberProjectPreference(ProjectId projectId, ModelProviderPreference preference)
    {
        if (projectId == default)
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }

        EnsurePreference(preference);
        _rememberProjectPreference(projectId, preference.Normalize());
    }

    public void RememberSessionPreference(string sessionId, ModelProviderPreference preference, bool persistNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsurePreference(preference);
        _rememberSessionPreference(sessionId, preference.Normalize(), persistNow);
    }

    public void ApplyDraftModelProviderState(ModelProviderState modelProviderState)
    {
        ArgumentNullException.ThrowIfNull(modelProviderState);
        _applyDraftModelProviderState(modelProviderState);
    }

    public void ApplySessionModelProviderState(OpenSessionState sessionState)
    {
        ArgumentNullException.ThrowIfNull(sessionState);
        _applySessionModelProviderState(sessionState);
    }

    public void RememberGlobalPreference(ModelProviderPreference preference)
    {
        EnsurePreference(preference);
        _rememberGlobalPreference(preference.Normalize());
    }

    private static void EnsurePreference(ModelProviderPreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        EnsureModelProviderId(preference.ModelProviderId);
    }

    private static void EnsureModelProviderId(ModelProviderId modelProviderId)
    {
        if (modelProviderId.IsEmpty)
        {
            throw new ArgumentException("Model provider id cannot be empty.", nameof(modelProviderId));
        }
    }
}

internal sealed class FrontendModelProviderPreferencePort : IModelProviderPreferencePort
{
    private readonly Action<ModelProviderState> _applyDraftModelProviderState;
    private readonly Action<OpenSessionState> _applySessionModelProviderState;
    private readonly Action<ModelProviderId, string?, AgentReasoningEffort?> _rememberGlobalPreference;
    private readonly Action<string, string?, AgentReasoningEffort?, bool> _rememberSessionPreference;

    public FrontendModelProviderPreferencePort(
        Action<ModelProviderState> applyDraftModelProviderState,
        Action<OpenSessionState> applySessionModelProviderState,
        Action<ModelProviderId, string?, AgentReasoningEffort?> rememberGlobalPreference,
        Action<string, string?, AgentReasoningEffort?, bool> rememberSessionPreference)
    {
        ArgumentNullException.ThrowIfNull(applyDraftModelProviderState);
        ArgumentNullException.ThrowIfNull(applySessionModelProviderState);
        ArgumentNullException.ThrowIfNull(rememberGlobalPreference);
        ArgumentNullException.ThrowIfNull(rememberSessionPreference);

        _applyDraftModelProviderState = applyDraftModelProviderState;
        _applySessionModelProviderState = applySessionModelProviderState;
        _rememberGlobalPreference = rememberGlobalPreference;
        _rememberSessionPreference = rememberSessionPreference;
    }

    public ModelProviderId GetPreferredModelProviderId(ProjectId projectId)
        => throw new NotSupportedException("The frontend preference adapter does not own project preference lookup yet.");

    public bool IsModelProviderReady(ModelProviderId modelProviderId)
        => throw new NotSupportedException("The frontend preference adapter does not own provider readiness lookup yet.");

    public void ApplyDraftPreference(PromptSessionBinding promptSession, ModelProviderPreference preference)
        => ApplyDraftModelProviderState(CreateLegacyModelProviderState(preference));

    public void ApplySessionPreference(string sessionId, ModelProviderPreference preference)
        => throw new NotSupportedException("Applying session preference still requires the open session projection during frontend migration.");

    public void RememberProjectPreference(ProjectId projectId, ModelProviderPreference preference)
        => RememberGlobalPreference(preference);

    public void RememberSessionPreference(string sessionId, ModelProviderPreference preference, bool persistNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsurePreference(preference);
        var normalized = preference.Normalize();
        _rememberSessionPreference(sessionId, normalized.ModelId, normalized.ReasoningEffort, persistNow);
    }

    public void ApplyDraftModelProviderState(ModelProviderState modelProviderState)
    {
        ArgumentNullException.ThrowIfNull(modelProviderState);
        _applyDraftModelProviderState(modelProviderState);
    }

    public void ApplySessionModelProviderState(OpenSessionState sessionState)
    {
        ArgumentNullException.ThrowIfNull(sessionState);
        _applySessionModelProviderState(sessionState);
    }

    public void RememberGlobalPreference(ModelProviderPreference preference)
    {
        EnsurePreference(preference);
        var normalized = preference.Normalize();
        _rememberGlobalPreference(normalized.ModelProviderId, normalized.ModelId, normalized.ReasoningEffort);
    }

    private static ModelProviderState CreateLegacyModelProviderState(ModelProviderPreference preference)
    {
        EnsurePreference(preference);
        return new ModelProviderState(preference.ModelProviderId, preference.ModelProviderId.Value)
        {
            SelectedModelId = preference.ModelId,
            SelectedReasoningEffort = preference.ReasoningEffort,
        };
    }

    private static void EnsurePreference(ModelProviderPreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        if (preference.ModelProviderId.IsEmpty)
        {
            throw new ArgumentException("Model provider id cannot be empty.", nameof(preference));
        }
    }
}
