namespace CodeAlta.Agent;

/// <summary>An exact journal generation, not the incremental reader's first-line fingerprint.</summary>
/// <param name="SessionId">The verified session identity.</param>
/// <param name="Path">The journal path resolved by the owning session catalog.</param>
/// <param name="Length">The complete source length.</param>
/// <param name="LastWriteUtc">The source write time.</param>
/// <param name="Sha256">The SHA-256 of every source byte.</param>
public sealed record SessionStatisticsSource(string SessionId, string Path, long Length, DateTimeOffset LastWriteUtc, string Sha256);

/// <summary>Evidence that a particular source generation has a durable, verified Statistics archive.</summary>
/// <remarks>This is not deletion authorization. The caller must retain session retirement ownership and revalidate age,
/// activity, protection, lineage, paths and this receipt immediately before deleting the exact source.</remarks>
/// <param name="Source">The exact source generation.</param>
/// <param name="Generation">The immutable archive generation.</param>
/// <param name="PayloadSha256">The digest of the saved statistics.</param>
public sealed record SessionStatisticsRetirementReceipt(SessionStatisticsSource Source, string Generation, string PayloadSha256);

/// <summary>The fail-closed outcome of preparing Statistics for retirement.</summary>
/// <param name="Receipt">A verified receipt; null when preservation could not be proved.</param>
/// <param name="FailureReason">An explanation when no receipt is issued.</param>
public sealed record SessionStatisticsRetirementResult(SessionStatisticsRetirementReceipt? Receipt, string? FailureReason);

/// <summary>The optional archival participant of the active Statistics engine, without any UI dependency.</summary>
/// <remarks>No participant or a null receipt means automatic deletion is forbidden. Calls must be made under the runtime's
/// retirement ownership, which excludes writers until deletion finishes. Implementations do not delete session artifacts.</remarks>
public interface ISessionStatisticsRetirementArchive
{
    /// <summary>Durably preserves the full verified source, without overriding disabled or limited history.</summary>
    /// <param name="sessionId">The session resolved by the owning catalog, never a renderer-supplied path.</param>
    /// <param name="cancellationToken">Cancels preparation; no receipt is returned on cancellation.</param>
    /// <returns>The verified receipt or an explicit reason to skip retirement.</returns>
    ValueTask<SessionStatisticsRetirementResult> PrepareAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Reopens the durable archive and hashes the current source, rejecting changed or missing generations.</summary>
    /// <param name="receipt">The receipt issued during preparation.</param>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <returns>Whether the archive and current source still match.</returns>
    ValueTask<bool> ValidateAsync(SessionStatisticsRetirementReceipt receipt, CancellationToken cancellationToken = default);

    /// <summary>Records confirmed source removal and rehydrates its saved projection without deleting any artifacts.</summary>
    /// <param name="receipt">The prepared source-bound receipt.</param>
    /// <param name="artifactsRemaining">Whether runtime-owned attachment/trace cleanup remains incomplete.</param>
    /// <param name="cancellationToken">Cancels reconciliation.</param>
    /// <returns>False if the source reappeared, the participant is unavailable, or its generation cannot be verified.</returns>
    ValueTask<bool> ReconcileDeletedAsync(SessionStatisticsRetirementReceipt receipt, bool artifactsRemaining, CancellationToken cancellationToken = default);
}
