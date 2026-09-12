namespace CodeAlta.Orchestration.Runtime;

/// <summary>Identifies an owned command attempt, including its immutable execution and acquired attachment.</summary>
/// <param name="OperationId">Owning command receipt identity.</param>
/// <param name="RuntimeInstanceId">Runtime identity captured at binding.</param>
/// <param name="AttachmentGeneration">Acquired attachment identity, not a state revision.</param>
/// <param name="Attempt">Full session/run/interaction/attempt identity.</param>
public sealed record SessionOwnedPermissionHandle(Guid OperationId, Guid RuntimeInstanceId,
    long AttachmentGeneration, SessionPermissionHandle Attempt);

/// <summary>Complete validated plain-command summary. It is not an execution acknowledgment or a reservation.</summary>
/// <param name="Handle">Owned attempt identity.</param>
/// <param name="Request">Immutable command data; never a truncated preview for owned requests.</param>
public sealed record SessionOwnedPermissionSnapshot(SessionOwnedPermissionHandle Handle, SessionPermissionSnapshot Request);

/// <summary>A bounded manual review window. Resolving entries then refreshing exposes further pending entries.</summary>
/// <param name="Entries">At most four still-pending owned command attempts for the exact selected session.</param>
/// <param name="HasMore">More pending entries existed at this observation; no durable cursor is implied.</param>
public sealed record SessionOwnedPermissionPage(IReadOnlyList<SessionOwnedPermissionSnapshot> Entries, bool HasMore);
