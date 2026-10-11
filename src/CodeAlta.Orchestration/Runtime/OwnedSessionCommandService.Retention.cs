using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    // Retirement reserves the exclusive slot for one inactive session only: commands for every other session go on.
    internal Task<string> RetireCatalogSessionAsync(AgentSessionMetadata candidate,
        DateTimeOffset cutoff, SessionRetentionProtection protection, ISessionStatisticsRetirementArchive? archive,
        SessionRetentionPass? pass, CancellationToken cancellationToken)
    {
        TaskCompletionSource<string> completion;
        lock (_gate)
        {
            if (_closed || _retained) return Task.FromResult("closed");
            if (_deleteWork is not null || _active.ContainsKey(candidate.SessionId) || _steering.Contains(candidate.SessionId)
                || _compacting.Contains(candidate.SessionId) || _abortingRuns.Contains(candidate.SessionId)
                || _queueing.Contains(candidate.SessionId) || _remoteControlling.ContainsKey(candidate.SessionId)
                || _operations.Values.Any(operation => !operation.Released && string.Equals(operation.SessionId, candidate.SessionId, StringComparison.OrdinalIgnoreCase))
                || _queues.Values.Any(operation => !operation.Released && string.Equals(operation.Request.SessionId, candidate.SessionId, StringComparison.OrdinalIgnoreCase))
                || Asks.List(candidate.SessionId).Head is not null)
                return Task.FromResult("busy");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteWork = completion.Task;
            _retiringSessionId = candidate.SessionId;
            _ = RetireCatalogSessionOwnedAsync(candidate, cutoff, protection, archive, pass, cancellationToken, completion);
        }
        return completion.Task;
    }

    private async Task RetireCatalogSessionOwnedAsync(AgentSessionMetadata candidate,
        DateTimeOffset cutoff, SessionRetentionProtection protection, ISessionStatisticsRetirementArchive? archive,
        SessionRetentionPass? pass, CancellationToken cancellationToken, TaskCompletionSource<string> completion)
    {
        try
        {
            completion.TrySetResult(await _runtime.RetireOwnedCatalogSessionAsync(candidate, cutoff, protection, archive,
                pass, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { completion.TrySetResult("canceled"); }
        catch (Exception) { completion.TrySetResult("retirement_unconfirmed"); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_deleteWork, completion.Task)) { _deleteWork = null; _retiringSessionId = null; }
            }
        }
    }

    // Called under the gate. A deletion or a provider change holds every session; a retirement holds only its own.
    private bool Reserved(string sessionId)
        => _deleteWork is not null && (_retiringSessionId is null || string.Equals(_retiringSessionId, sessionId, StringComparison.OrdinalIgnoreCase));
}
