namespace CodeAlta.Server.Storage;

/// <summary>The on-call rotation of a team: its people in turn, each for a shift of hours, from a start.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="TeamId">The team.</param>
/// <param name="People">Its people, in the order of their turns.</param>
/// <param name="StartsAt">When the first turn starts.</param>
/// <param name="ShiftHours">How many hours a turn lasts.</param>
/// <param name="UpdatedAt">When it was last set.</param>
/// <param name="UpdatedByPersonId">Who set it.</param>
public sealed record OnCallRotationRecord(string OrganizationId, string TeamId, IReadOnlyList<string> People, DateTimeOffset StartsAt, int ShiftHours, DateTimeOffset UpdatedAt,
    string UpdatedByPersonId);

/// <summary>The on-call rotations of the teams (migration 0018).</summary>
public interface IOnCallStore
{
    /// <summary>Gets the rotation of a team.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The rotation, or null.</returns>
    ValueTask<OnCallRotationRecord?> GetOnCallRotationAsync(string organizationId, string teamId, CancellationToken cancellationToken = default);

    /// <summary>Lists the rotations of an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The rotations.</returns>
    ValueTask<IReadOnlyList<OnCallRotationRecord>> ListOnCallRotationsAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Sets the rotation of a team, with its audit record.</summary>
    /// <param name="rotation">The rotation.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes once it is stored.</returns>
    ValueTask PutOnCallRotationAsync(OnCallRotationRecord rotation, AuditEntry audit, CancellationToken cancellationToken = default);

    /// <summary>Takes the rotation of a team away, with its audit record.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="audit">The audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it had one.</returns>
    ValueTask<bool> DeleteOnCallRotationAsync(string organizationId, string teamId, AuditEntry audit, CancellationToken cancellationToken = default);
}
