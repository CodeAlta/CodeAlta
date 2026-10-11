namespace CodeAlta.Orchestration.Runtime;

/// <summary>One startup pass's replayable immutable outcome.</summary>
/// <param name="State">not_started, running, completed, partial, skipped or failed.</param>
/// <param name="Examined">Candidates considered.</param>
/// <param name="Deleted">Confirmed removed journals.</param>
/// <param name="Skipped">Candidates retained.</param>
/// <param name="Reason">Bounded status code; never journal content or private paths.</param>
public sealed record SessionRetentionStatus(string State, int Examined, int Deleted, int Skipped, string? Reason);
