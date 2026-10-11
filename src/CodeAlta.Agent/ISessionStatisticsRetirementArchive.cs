namespace CodeAlta.Agent;

/// <summary>A journal generation, identified by its length and write time, not the incremental reader's first-line fingerprint.</summary>
/// <param name="SessionId">The verified session identity.</param>
/// <param name="Path">The journal path resolved by the owning session catalog.</param>
/// <param name="Length">The complete source length.</param>
/// <param name="LastWriteUtc">The source write time.</param>
/// <param name="Sha256">The SHA-256 of every source byte; null when the source was described without being read, as at retention.</param>
public sealed record SessionStatisticsSource(string SessionId, string Path, long Length, DateTimeOffset LastWriteUtc, string? Sha256);

/// <summary>Evidence that a particular source generation has a durable Statistics archive.</summary>
/// <remarks>The runtime saves Statistics while it holds the source against writers, then deletes it whatever the outcome.</remarks>
/// <param name="Source">The exact source generation.</param>
/// <param name="Generation">The immutable archive generation.</param>
/// <param name="PayloadSha256">The digest of the saved statistics.</param>
public sealed record SessionStatisticsRetirementReceipt(SessionStatisticsSource Source, string Generation, string PayloadSha256);

/// <summary>The outcome of saving Statistics before a retirement.</summary>
/// <param name="Receipt">The receipt of the saved copy; null when nothing was saved.</param>
/// <param name="FailureReason">An explanation when nothing was saved.</param>
public sealed record SessionStatisticsRetirementResult(SessionStatisticsRetirementReceipt? Receipt, string? FailureReason);

/// <summary>The optional archival participant of the active Statistics engine, without any UI dependency.</summary>
/// <remarks>Saving is best effort: no participant, a null receipt, an exception or no answer in time never keeps an expired
/// session, whose facts Statistics keeps anyway once it finds the journal gone. Calls are made under the runtime's
/// retirement ownership, which excludes writers until deletion finishes. Implementations do not read or delete session
/// artifacts: retention never reads an old journal.</remarks>
public interface ISessionStatisticsRetirementArchive
{
    /// <summary>Durably saves the facts Statistics already holds, when it has read the whole journal as it is now.</summary>
    /// <param name="sessionId">The session resolved by the owning catalog, never a renderer-supplied path.</param>
    /// <param name="cancellationToken">Cancels preparation; no receipt is returned on cancellation.</param>
    /// <returns>The receipt of the saved copy, or the reason nothing was saved.</returns>
    ValueTask<SessionStatisticsRetirementResult> PrepareAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Records confirmed source removal and rehydrates its saved projection without deleting any artifacts.</summary>
    /// <param name="receipt">The prepared source-bound receipt.</param>
    /// <param name="artifactsRemaining">Whether runtime-owned attachment/trace cleanup remains incomplete.</param>
    /// <param name="cancellationToken">Cancels reconciliation.</param>
    /// <returns>False if the source reappeared, the participant is unavailable, or its generation cannot be verified.</returns>
    ValueTask<bool> ReconcileDeletedAsync(SessionStatisticsRetirementReceipt receipt, bool artifactsRemaining, CancellationToken cancellationToken = default);
}
