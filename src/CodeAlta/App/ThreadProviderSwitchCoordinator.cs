using System.Text.Json;
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
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<AgentEvent>>> _getThreadHistoryAsync;

    public ThreadProviderSwitchCoordinator(
        CatalogOptions catalogOptions,
        WorkThreadCatalog threadCatalog,
        CodeAltaConfigStore configStore,
        IReadOnlyDictionary<string, ChatBackendState> chatBackendStates,
        Func<OpenThreadState, Task> applyThreadPreferenceAsync,
        Func<string, Task<bool>> detachThreadSessionAsync,
        Action<string, WorkThreadDescriptor> rekeyThreadIdentity,
        Func<Task> persistViewStateAsync,
        Func<string, CancellationToken, Task<IReadOnlyList<AgentEvent>>>? getThreadHistoryAsync = null)
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
        _getThreadHistoryAsync = getThreadHistoryAsync ?? ((_, _) => Task.FromResult<IReadOnlyList<AgentEvent>>([]));
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
               TryGetSwitchableSourceBackendInfo(new AgentBackendId(thread.BackendId), out _);
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

        if (!TryGetSwitchableSourceBackendInfo(new AgentBackendId(thread.BackendId), out var sourceBackend) ||
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
            cancellationToken);

        if (sourceSummary is null && !sourceBackend.IsLocalRuntime)
        {
            sourceSummary = await MirrorNativeSessionAsync(
                store,
                sourceBackend,
                thread,
                tab,
                sessionId,
                cancellationToken);
        }

        if (sourceSummary is null)
        {
            throw new InvalidOperationException(
                $"The local-runtime session '{thread.BackendSessionId}' was not found for provider '{sourceBackend.ProviderKey}'.");
        }

        var sourceState = await store.GetStateAsync(
                sourceBackend.ProtocolFamily,
                sourceBackend.ProviderKey,
                sessionId,
                cancellationToken)
            ;

        var timestamp = DateTimeOffset.UtcNow;

        var oldThreadId = thread.ThreadId;
        var oldInternalDirectory = thread.Kind == WorkThreadKind.InternalThread &&
                                   !string.IsNullOrWhiteSpace(thread.SourcePath)
            ? Path.GetDirectoryName(thread.SourcePath)
            : null;

        try
        {
            await _detachThreadSessionAsync(oldThreadId);
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
        await _applyThreadPreferenceAsync(tab);

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

        await store.UpsertSessionAsync(targetSummary, cancellationToken);
        await store.UpsertStateAsync(targetState, cancellationToken);
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
            ;

        if (thread.Kind == WorkThreadKind.InternalThread)
        {
            await _threadCatalog.SaveInternalAsync(thread, cancellationToken);

            if (!string.IsNullOrWhiteSpace(oldInternalDirectory) &&
                !string.Equals(oldInternalDirectory, Path.GetDirectoryName(thread.SourcePath), StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(oldInternalDirectory))
            {
                Directory.Delete(oldInternalDirectory, recursive: true);
            }
        }

        await _persistViewStateAsync();
        return true;
    }

    private FileSystemLocalAgentSessionStore CreateSessionStore()
    {
        return new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(_catalogOptions.GlobalRoot));
    }

    private async Task<LocalAgentSessionSummary> MirrorNativeSessionAsync(
        FileSystemLocalAgentSessionStore store,
        RuntimeBackendInfo sourceBackend,
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        string sessionId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentEvent> history;
        try
        {
            history = await _getThreadHistoryAsync(thread.ThreadId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            history = [];
        }
        catch (KeyNotFoundException)
        {
            history = [];
        }

        var timestamp = history.Count == 0
            ? DateTimeOffset.UtcNow
            : history.Max(static @event => @event.Timestamp);
        var createdAt = thread.StartedAt ?? thread.CreatedAt;
        var summary = new LocalAgentSessionSummary
        {
            SessionId = sessionId,
            BackendId = sourceBackend.BackendId,
            ProtocolFamily = sourceBackend.ProtocolFamily,
            ProviderKey = sourceBackend.ProviderKey,
            ModelId = tab.ModelId,
            WorkingDirectory = thread.WorkingDirectory,
            Title = thread.Title,
            Summary = thread.LatestSummary,
            Usage = tab.Usage,
            CreatedAt = createdAt,
            UpdatedAt = timestamp > createdAt ? timestamp : createdAt,
        };

        await store.UpsertSessionAsync(summary, cancellationToken);
        await store.UpsertStateAsync(
            new LocalAgentSessionState
            {
                SessionId = sessionId,
                ProtocolFamily = sourceBackend.ProtocolFamily,
                ProviderKey = sourceBackend.ProviderKey,
                ProviderSessionId = sessionId,
                Usage = tab.Usage,
                UpdatedAt = summary.UpdatedAt,
            },
            cancellationToken);

        var mirroredHistory = BuildLocalReplayEvents(sourceBackend.BackendId, sessionId, history);
        if (mirroredHistory.Count > 0)
        {
            await store.AppendEventsAsync(
                sourceBackend.ProtocolFamily,
                sourceBackend.ProviderKey,
                sessionId,
                mirroredHistory,
                cancellationToken);
        }

        return summary;
    }

    private static IReadOnlyList<AgentEvent> BuildLocalReplayEvents(
        AgentBackendId backendId,
        string sessionId,
        IReadOnlyList<AgentEvent> history)
    {
        if (history.Count == 0)
        {
            return [];
        }

        var events = new List<AgentEvent>(history.Count * 2);
        foreach (var @event in history)
        {
            if (@event is AgentContentCompletedEvent completed)
            {
                if (TryCreateReplayRawEvent(backendId, sessionId, completed, out var replayEvent))
                {
                    events.Add(replayEvent);
                }
            }

            events.Add(@event);
        }

        return events;
    }

    private static bool TryCreateReplayRawEvent(
        AgentBackendId backendId,
        string sessionId,
        AgentContentCompletedEvent completed,
        out AgentRawEvent replayEvent)
    {
        replayEvent = null!;
        if (string.IsNullOrWhiteSpace(completed.Content))
        {
            return false;
        }

        switch (completed.Kind)
        {
            case AgentContentKind.User:
            {
                var input = new AgentInput([new AgentInputItem.Text(completed.Content)]);
                replayEvent = new AgentRawEvent(
                    backendId,
                    sessionId,
                    completed.Timestamp,
                    "local.userMessage",
                    JsonDocument.Parse(input.ToJson()).RootElement.Clone(),
                    completed.RunId);
                return true;
            }

            case AgentContentKind.Assistant:
            {
                var message = new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Assistant,
                    [new LocalAgentMessagePart.Text(completed.Content)]);
                replayEvent = new AgentRawEvent(
                    backendId,
                    sessionId,
                    completed.Timestamp,
                    "local.assistantMessage",
                    JsonDocument.Parse(message.ToJson()).RootElement.Clone(),
                    completed.RunId);
                return true;
            }

            case AgentContentKind.Reasoning:
            {
                var message = new LocalAgentConversationMessage(
                    LocalAgentConversationRole.Assistant,
                    [new LocalAgentMessagePart.Reasoning(completed.Content)]);
                replayEvent = new AgentRawEvent(
                    backendId,
                    sessionId,
                    completed.Timestamp,
                    "local.assistantMessage",
                    JsonDocument.Parse(message.ToJson()).RootElement.Clone(),
                    completed.RunId);
                return true;
            }

            default:
                return false;
        }
    }

    private bool TryGetLocalRuntimeBackendInfo(AgentBackendId backendId, out LocalRuntimeBackendInfo backendInfo)
    {
        return _localRuntimeBackends.TryGetValue(backendId.Value, out backendInfo!);
    }

    private bool TryGetSwitchableSourceBackendInfo(AgentBackendId backendId, out RuntimeBackendInfo backendInfo)
    {
        if (TryGetLocalRuntimeBackendInfo(backendId, out var localBackend))
        {
            backendInfo = new RuntimeBackendInfo(
                localBackend.BackendId,
                localBackend.ProviderKey,
                localBackend.ProtocolFamily,
                IsLocalRuntime: true);
            return true;
        }

        if (IsNativeProviderBackend(backendId))
        {
            backendInfo = new RuntimeBackendInfo(
                backendId,
                backendId.Value,
                backendId.Value,
                IsLocalRuntime: false);
            return true;
        }

        backendInfo = null!;
        return false;
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
            "openai-codex-subscription" => "openai-codex-subscription",
            "github-copilot-direct" => "github-copilot-direct",
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

    private sealed record RuntimeBackendInfo(
        AgentBackendId BackendId,
        string ProviderKey,
        string ProtocolFamily,
        bool IsLocalRuntime);
}
