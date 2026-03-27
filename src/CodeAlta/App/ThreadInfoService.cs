using CodeAlta.Agent;
using CodeAlta.App.Context;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Threads;

namespace CodeAlta.App;

internal sealed class ThreadInfoService
{
    private readonly AgentHub _agentHub;
    private readonly ThreadSelectionContext _threadSelection;
    private readonly IReadOnlyDictionary<string, ChatBackendState> _chatBackendStates;

    public ThreadInfoService(
        AgentHub agentHub,
        ThreadSelectionContext threadSelection,
        IReadOnlyDictionary<string, ChatBackendState> chatBackendStates)
    {
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(chatBackendStates);

        _agentHub = agentHub;
        _threadSelection = threadSelection;
        _chatBackendStates = chatBackendStates;
    }

    public async Task<ThreadInfoReport?> LoadSelectedThreadReportAsync(CancellationToken cancellationToken = default)
    {
        var thread = _threadSelection.GetSelectedThread();
        if (thread is null)
        {
            return null;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        var backendState = _chatBackendStates.TryGetValue(thread.BackendId, out var resolvedBackendState)
            ? resolvedBackendState
            : new ChatBackendState(new AgentBackendId(thread.BackendId), thread.BackendId);

        IReadOnlyList<AgentEvent>? history = tab.HistoryEvents;
        if (!tab.HistoryLoaded || history is null)
        {
            try
            {
                await _threadSelection.EnsureThreadHistoryLoadedAsync(thread, cancellationToken).ConfigureAwait(false);
                history = tab.HistoryEvents;
            }
            catch (InvalidOperationException)
            {
                history = tab.HistoryEvents;
            }
        }

        AgentSessionMetadata? metadata = null;
        try
        {
            var sessions = await _agentHub
                .ListSessionsAsync(new AgentBackendId(thread.BackendId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            metadata = sessions.FirstOrDefault(
                session => string.Equals(session.SessionId, thread.BackendSessionId, StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            metadata = null;
        }

        return ThreadInfoReportBuilder.Build(
            thread,
            backendState.DisplayName,
            tab.ModelId ?? backendState.SelectedModelId,
            tab.ReasoningEffort,
            metadata,
            history,
            DateTimeOffset.Now);
    }
}
