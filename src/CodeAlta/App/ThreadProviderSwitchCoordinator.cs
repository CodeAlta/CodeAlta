using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;

namespace CodeAlta.App;

internal sealed class ThreadProviderSwitchCoordinator
{
    private readonly CatalogOptions _catalogOptions;
    private readonly WorkThreadCatalog _threadCatalog;
    private readonly IReadOnlyDictionary<string, ChatBackendState> _chatBackendStates;
    private readonly IReadOnlyDictionary<string, LocalRuntimeBackendInfo> _localRuntimeBackends;
    private readonly Func<OpenThreadState, Task> _applyThreadPreferenceAsync;
    private readonly Func<string, Task<bool>> _detachThreadSessionAsync;
    private readonly Action<string, WorkThreadDescriptor> _rekeyThreadIdentity;
    private readonly Func<Task> _persistViewStateAsync;

    public ThreadProviderSwitchCoordinator(
        CatalogOptions catalogOptions,
        WorkThreadCatalog threadCatalog,
        CodeAltaConfigStore configStore,
        IReadOnlyDictionary<string, ChatBackendState> chatBackendStates,
        Func<OpenThreadState, Task> applyThreadPreferenceAsync,
        Func<string, Task<bool>> detachThreadSessionAsync,
        Action<string, WorkThreadDescriptor> rekeyThreadIdentity,
        Func<Task> persistViewStateAsync)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(threadCatalog);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(applyThreadPreferenceAsync);
        ArgumentNullException.ThrowIfNull(detachThreadSessionAsync);
        ArgumentNullException.ThrowIfNull(rekeyThreadIdentity);
        ArgumentNullException.ThrowIfNull(persistViewStateAsync);

        _catalogOptions = catalogOptions;
        _threadCatalog = threadCatalog;
        _chatBackendStates = chatBackendStates;
        _applyThreadPreferenceAsync = applyThreadPreferenceAsync;
        _detachThreadSessionAsync = detachThreadSessionAsync;
        _rekeyThreadIdentity = rekeyThreadIdentity;
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
               TryGetLocalRuntimeBackendInfo(new AgentBackendId(thread.BackendId), out _);
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

        if (!CanSwitchThreadProvider(thread, tab, targetBackendId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(thread.BackendSessionId))
        {
            return false;
        }

        if (!TryGetLocalRuntimeBackendInfo(new AgentBackendId(thread.BackendId), out var sourceBackend) ||
            !TryGetLocalRuntimeBackendInfo(targetBackendId, out var targetBackend))
        {
            return false;
        }

        var store = CreateSessionStore();
        var sessionId = thread.BackendSessionId;
        var sourceSummary = await store.GetSessionAsync(
                sourceBackend.ProtocolFamily,
                sourceBackend.ProviderKey,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"The local-runtime session '{thread.BackendSessionId}' was not found for provider '{sourceBackend.ProviderKey}'.");
        var sourceState = await store.GetStateAsync(
                sourceBackend.ProtocolFamily,
                sourceBackend.ProviderKey,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);

        var timestamp = DateTimeOffset.UtcNow;

        var oldThreadId = thread.ThreadId;
        var oldInternalDirectory = thread.Kind == WorkThreadKind.InternalThread &&
                                   !string.IsNullOrWhiteSpace(thread.SourcePath)
            ? Path.GetDirectoryName(thread.SourcePath)
            : null;

        try
        {
            await _detachThreadSessionAsync(oldThreadId).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }

        thread.BackendId = targetBackend.BackendId.Value;
        thread.ProviderKey = targetBackend.ProviderKey;
        thread.ThreadId = Orchestration.Runtime.WorkThreadRuntimeService.CreateThreadId(targetBackend.BackendId, sessionId);
        thread.UpdatedAt = timestamp;

        tab.BackendId = targetBackend.BackendId;
        tab.ModelId = null;
        tab.ReasoningEffort = null;
        tab.Usage = null;

        _rekeyThreadIdentity(oldThreadId, thread);
        await _applyThreadPreferenceAsync(tab).ConfigureAwait(false);

        var targetSummary = sourceSummary with
        {
            BackendId = targetBackend.BackendId,
            ProtocolFamily = targetBackend.ProtocolFamily,
            ProviderKey = targetBackend.ProviderKey,
            ModelId = tab.ModelId,
            Usage = null,
            UpdatedAt = timestamp,
        };

        var targetState = (sourceState ?? new LocalAgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = sourceBackend.ProtocolFamily,
            ProviderKey = sourceBackend.ProviderKey,
            UpdatedAt = sourceSummary.UpdatedAt,
        }) with
        {
            ProtocolFamily = targetBackend.ProtocolFamily,
            ProviderKey = targetBackend.ProviderKey,
            ProviderSessionId = null,
            ProviderState = null,
            Usage = null,
            UpdatedAt = timestamp,
        };

        await store.UpsertSessionAsync(targetSummary, cancellationToken).ConfigureAwait(false);
        await store.UpsertStateAsync(targetState, cancellationToken).ConfigureAwait(false);
        await store.AppendEventsAsync(
                targetBackend.ProtocolFamily,
                targetBackend.ProviderKey,
                sessionId,
                [
                    new AgentSessionUpdateEvent(
                        targetBackend.BackendId,
                        sessionId,
                        timestamp,
                        null,
                        AgentSessionUpdateKind.ModelChanged,
                        $"Provider switched from {sourceBackend.ProviderKey} to {targetBackend.ProviderKey}."),
                ],
                cancellationToken)
            .ConfigureAwait(false);

        if (thread.Kind == WorkThreadKind.InternalThread)
        {
            await _threadCatalog.SaveInternalAsync(thread, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(oldInternalDirectory) &&
                !string.Equals(oldInternalDirectory, Path.GetDirectoryName(thread.SourcePath), StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(oldInternalDirectory))
            {
                Directory.Delete(oldInternalDirectory, recursive: true);
            }
        }

        await _persistViewStateAsync().ConfigureAwait(false);
        return true;
    }

    private FileSystemLocalAgentSessionStore CreateSessionStore()
    {
        return new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(_catalogOptions.GlobalRoot));
    }

    private bool TryGetLocalRuntimeBackendInfo(AgentBackendId backendId, out LocalRuntimeBackendInfo backendInfo)
    {
        return _localRuntimeBackends.TryGetValue(backendId.Value, out backendInfo!);
    }

    private static bool TryMapProtocolFamily(string? providerType, out string? protocolFamily)
    {
        protocolFamily = providerType?.Trim().ToLowerInvariant() switch
        {
            "openai-chat" => "openai-chat",
            "openai-responses" => "openai-responses",
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
