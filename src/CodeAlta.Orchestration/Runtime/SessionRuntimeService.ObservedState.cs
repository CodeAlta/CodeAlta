using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>A noncreating runtime observation released only after bounded persisted scope verification.</summary>
/// <param name="Status">Explicit verification outcome; only ok contains state.</param>
/// <param name="State">Point-in-time actor facts, not liveness or permission.</param>
public sealed record OwnedRuntimeObservation(string Status, SessionRuntimeCurrentState? State);

public sealed partial class SessionRuntimeService
{
    /// <summary>Verifies a saved session's exact header/project scope, then observes its existing actor without activation.</summary>
    /// <remarks>Reads one bounded header, never history. Archived projects refuse. Catalog/header reads and actor observations
    /// are not atomic against external writers or other actors. Absence is unknown/not attached, never idle/completed.</remarks>
    /// <param name="sessionId">Exact saved identity.</param><param name="createdAt">Exact saved header creation time.</param>
    /// <param name="scope">Global or project scope.</param><param name="projectId">Exact project identity, otherwise null.</param>
    /// <param name="projectPath">Expected catalog path, never a root for arbitrary journal access.</param>
    /// <param name="cancellationToken">Cancels reads and caller waiting.</param>
    /// <returns>Verified observation or explicit refusal.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closed.</exception>
    public async Task<OwnedRuntimeObservation> ObserveOwnedStateAsync(string sessionId, DateTimeOffset createdAt, string scope,
        string? projectId, string? projectPath, CancellationToken cancellationToken = default)
        => await AdmitAsync(() => ObserveOwnedStateBodyAsync(sessionId, createdAt, scope, projectId, projectPath, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<OwnedRuntimeObservation> ObserveOwnedStateBodyAsync(string id, DateTimeOffset createdAt, string scope,
        string? projectId, string? projectPath, CancellationToken token)
    {
        if (scope is not ("global" or "project") || scope == "global" && (projectId is not null || projectPath is not null)
            || scope == "project" && (projectId is null || projectPath is null)) return new("invalid_scope", null);
        var persisted = await _sessionViewCatalog.JournalStore.ReadBoundedHeaderAsync(id, createdAt, token).ConfigureAwait(false);
        if (persisted.Status != BoundedSessionHeaderStatus.Found) return new("metadata_unavailable", null);
        var header = persisted.Header!;
        if (header.SchemaVersion != 1 || header.SessionId != id || header.CreatedAt != createdAt
            || (scope == "global" ? header.Kind != SessionViewKind.GlobalSession || header.ProjectRef is not null
                || header.WorkingDirectory != _projectCatalog.Options.GlobalRoot
                : header.Kind != SessionViewKind.ProjectSession || header.ProjectRef != projectId || header.WorkingDirectory != projectPath))
            return new("scope_mismatch", null);
        if (scope == "project")
        {
            var ownership = await _projectCatalog.ReadBoundedOwnershipAsync(projectId, projectPath, token).ConfigureAwait(false);
            if (ownership.Status != ProjectOwnershipStatus.Match) return new("project_unverified", null);
            if (ownership.Archived != false) return new("archived_project", null);
        }
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sessionActors.TryGet(id, out var actor)) return new("ok", new(_runtimeInstanceId, id, false, null));
        return await actor.QueryAsync(_ =>
        {
            lock (_identityGate)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_sessionActors.TryGet(id, out var current) || !ReferenceEquals(current, actor))
                    return ValueTask.FromResult(new OwnedRuntimeObservation("stale_attachment", null));
                if (_entries.TryGetValue(id, out var entry) && (entry.Kind != header.Kind || entry.ProjectId != header.ProjectRef
                    || entry.WorkingDirectory != header.WorkingDirectory || entry.CreatedAt != header.CreatedAt
                    || entry.ProviderId.Value != header.ProviderId || entry.ProviderKey != header.ProviderKey))
                    return ValueTask.FromResult(new OwnedRuntimeObservation("scope_mismatch", null));
                return ValueTask.FromResult(new OwnedRuntimeObservation("ok", CaptureCurrentState(id)));
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }
}
