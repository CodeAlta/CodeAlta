using CodeAlta.Agent;
using CodeAlta.LiveTool;

namespace CodeAlta.Tui.App;

// Presentation only: these decisions never grant a response claim or settle runtime admission.
internal static class AskResponsePresentationPolicy
{
    internal static bool ShouldReconcileRejected(
        AltaAskResponseHandle? active, AltaAskResponseHandle attempted, AltaAskResponseHandle? current)
        => ReferenceEquals(active, attempted) && !ReferenceEquals(current, attempted);

    internal static bool ShouldReportBlockedState(string targetSessionId, string? selectedSessionId, string? activeSessionId)
        // The active ask identity survives form exit while dispatch awaits; it is not a foreground view.
        => string.Equals(targetSessionId, selectedSessionId, StringComparison.Ordinal)
            && (activeSessionId is null || string.Equals(targetSessionId, activeSessionId, StringComparison.Ordinal));

    internal static bool ShouldClearOptimisticRun(
        long attemptRevision, long currentRevision, DateTimeOffset? ownedStart, DateTimeOffset? currentStart, AgentRunId? currentRunId)
        => attemptRevision == currentRevision && ownedStart is not null && ownedStart == currentStart && currentRunId is null;
}
