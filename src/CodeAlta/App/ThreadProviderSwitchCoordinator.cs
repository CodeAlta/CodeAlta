using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;

namespace CodeAlta.App;

internal sealed class ThreadProviderSwitchCoordinator
{
    private readonly IReadOnlyDictionary<string, ChatBackendState> _chatBackendStates;
    private readonly IReadOnlyDictionary<string, LocalRuntimeBackendInfo> _localRuntimeBackends;
    private readonly Func<OpenThreadState, Task> _applyThreadPreferenceAsync;
    private readonly Func<string, Task<bool>> _detachThreadSessionAsync;
    private readonly Action<WorkThreadDescriptor> _updateThreadState;
    private readonly Func<Task> _persistViewStateAsync;

    public ThreadProviderSwitchCoordinator(
        CodeAltaConfigStore configStore,
        IReadOnlyDictionary<string, ChatBackendState> chatBackendStates,
        Func<OpenThreadState, Task> applyThreadPreferenceAsync,
        Func<string, Task<bool>> detachThreadSessionAsync,
        Action<WorkThreadDescriptor> updateThreadState,
        Func<Task> persistViewStateAsync)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(applyThreadPreferenceAsync);
        ArgumentNullException.ThrowIfNull(detachThreadSessionAsync);
        ArgumentNullException.ThrowIfNull(updateThreadState);
        ArgumentNullException.ThrowIfNull(persistViewStateAsync);

        _chatBackendStates = chatBackendStates;
        _applyThreadPreferenceAsync = applyThreadPreferenceAsync;
        _detachThreadSessionAsync = detachThreadSessionAsync;
        _updateThreadState = updateThreadState;
        _persistViewStateAsync = persistViewStateAsync;
        _localRuntimeBackends = configStore.LoadGlobalProviderDefinitions(includeDisabled: true)
            .Where(static definition => TryMapProtocolFamily(definition.ProviderType, out _))
            .Select(definition =>
            {
                TryMapProtocolFamily(definition.ProviderType, out var protocolFamily);
                return new KeyValuePair<string, LocalRuntimeBackendInfo>(
                    definition.ProviderKey,
                    new LocalRuntimeBackendInfo(
                        new AgentBackendId(definition.ProviderKey),
                        definition.ProviderKey,
                        protocolFamily!));
            })
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    public bool CanSelectThreadProvider(WorkThreadDescriptor thread, OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tab);

        return !tab.StatusBusy &&
               tab.ActiveRunId is null &&
               IsSwitchableSourceBackend(new AgentBackendId(thread.BackendId));
    }

    public bool CanSwitchThreadProvider(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        AgentBackendId targetBackendId)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tab);

        return CanSelectThreadProvider(thread, tab) &&
               !string.Equals(thread.BackendId, targetBackendId.Value, StringComparison.OrdinalIgnoreCase) &&
               TryGetLocalRuntimeBackendInfo(targetBackendId, out _) &&
               _chatBackendStates.TryGetValue(targetBackendId.Value, out var targetState) &&
               targetState.Availability == ChatBackendAvailability.Ready;
    }

    public async Task<bool> SwitchThreadProviderAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        AgentBackendId targetBackendId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tab);
        cancellationToken.ThrowIfCancellationRequested();

        if (!CanSwitchThreadProvider(thread, tab, targetBackendId) ||
            !TryGetLocalRuntimeBackendInfo(targetBackendId, out var targetBackend))
        {
            return false;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var oldThreadId = thread.ThreadId;
        var oldThreadBackendId = thread.BackendId;
        var oldThreadProviderKey = thread.ProviderKey;
        var oldThreadUpdatedAt = thread.UpdatedAt;
        var oldThreadModelId = thread.ModelId;
        var oldThreadReasoningEffort = thread.ReasoningEffort;
        var oldTabBackendId = tab.BackendId;
        var oldTabModelId = tab.ModelId;
        var oldTabReasoningEffort = tab.ReasoningEffort;
        var oldTabUsage = tab.Usage;

        thread.BackendId = targetBackend.BackendId.Value;
        thread.ProviderKey = targetBackend.ProviderKey;
        thread.UpdatedAt = timestamp;

        tab.BackendId = targetBackend.BackendId;
        tab.ModelId = null;
        tab.ReasoningEffort = null;
        tab.Usage = null;

        try
        {
            await _applyThreadPreferenceAsync(tab);
            NormalizeTargetModelSelection(tab, targetBackend.BackendId);
            thread.ModelId = tab.ModelId;
            thread.ReasoningEffort = tab.ReasoningEffort;
            if (!string.IsNullOrWhiteSpace(oldThreadId))
            {
                await _detachThreadSessionAsync(oldThreadId);
            }
        }
        catch
        {
            thread.BackendId = oldThreadBackendId;
            thread.ProviderKey = oldThreadProviderKey;
            thread.UpdatedAt = oldThreadUpdatedAt;
            thread.ModelId = oldThreadModelId;
            thread.ReasoningEffort = oldThreadReasoningEffort;
            tab.BackendId = oldTabBackendId;
            tab.ModelId = oldTabModelId;
            tab.ReasoningEffort = oldTabReasoningEffort;
            tab.Usage = oldTabUsage;
            throw;
        }

        _updateThreadState(thread);
        await _persistViewStateAsync();
        return true;
    }

    private bool TryGetLocalRuntimeBackendInfo(AgentBackendId backendId, out LocalRuntimeBackendInfo backendInfo)
    {
        return _localRuntimeBackends.TryGetValue(backendId.Value, out backendInfo!);
    }

    private bool IsSwitchableSourceBackend(AgentBackendId backendId)
        => TryGetLocalRuntimeBackendInfo(backendId, out _) || IsNativeProviderBackend(backendId);

    private void NormalizeTargetModelSelection(OpenThreadState tab, AgentBackendId targetBackendId)
    {
        if (!_chatBackendStates.TryGetValue(targetBackendId.Value, out var targetState) ||
            targetState.Models.Count == 0 ||
            string.IsNullOrWhiteSpace(tab.ModelId) ||
            targetState.Models.Any(model => string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal)))
        {
            return;
        }

        tab.ModelId = ChatBackendPresentation.ResolvePreferredModelId(targetState.Models, targetState.SelectedModelId);
        var selectedModel = ModelProviderPreferenceCoordinator.FindModel(targetState.Models, tab.ModelId);
        tab.ReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(
            selectedModel,
            targetState.SelectedReasoningEffort);
    }

    private static bool IsNativeProviderBackend(AgentBackendId backendId)
        => string.Equals(backendId.Value, AgentBackendIds.Codex.Value, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(backendId.Value, AgentBackendIds.Copilot.Value, StringComparison.OrdinalIgnoreCase);

    private static bool TryMapProtocolFamily(string? providerType, out string? protocolFamily)
    {
        protocolFamily = providerType?.Trim().ToLowerInvariant() switch
        {
            "openai-chat" => "openai-chat",
            "openai-responses" => "openai-responses",
            "azure-openai" => "openai-chat",
            "codex" => "codex",
            "copilot" => "copilot",
            "xai" => "xai",
            "anthropic" => "anthropic-messages",
            "google-genai" => "google-genai",
            "vertex-ai" => "google-genai",
            _ => null,
        };

        return protocolFamily is not null;
    }

    private sealed record LocalRuntimeBackendInfo(
        AgentBackendId BackendId,
        string ProviderKey,
        string ProtocolFamily);
}
