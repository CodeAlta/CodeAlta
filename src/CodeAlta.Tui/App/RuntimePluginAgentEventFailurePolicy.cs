using System.Runtime.ExceptionServices;

namespace CodeAlta.Tui.App;

// Explicitly preserves the former task-monitor reporting decision, not the new operation's TaskStatus.
internal static class RuntimePluginAgentEventFailurePolicy
{
    internal static ValueTask ReportAsync(string sessionId, Exception original,
        Action<string, Exception> log, Action<string, AggregateException> reportFatal)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(reportFatal);
        return CoreAsync();

        async ValueTask CoreAsync()
        {
            // An async operation captures synchronous callback failure without changing its identity.
            await Task.CompletedTask.ConfigureAwait(false);
            try { log($"Plugin agent event observer failed for session {sessionId}", original); }
            catch (OperationCanceledException) { throw; }
            catch (Exception reportingFailure)
            {
                try
                {
                    reportFatal($"Plugin agent event observer for session {sessionId}",
                        new AggregateException(reportingFailure).Flatten());
                }
                catch (Exception fatalFailure) { throw new AggregateException(reportingFailure, fatalFailure); }
                ExceptionDispatchInfo.Capture(reportingFailure).Throw();
            }
        }
    }
}
