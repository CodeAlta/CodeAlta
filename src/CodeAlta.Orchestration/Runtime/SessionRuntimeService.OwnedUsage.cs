using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>One actor-observed usage event after an exact, bounded persisted ownership check. No history is inferred.</summary>
/// <param name="Status">Explicit ownership/attachment/observation outcome; only ok releases an observation.</param>
/// <param name="RuntimeInstanceId">Exact instance that performed the read.</param>
/// <param name="SessionId">Exact requested existing session.</param>
/// <param name="AttachmentGeneration">Ordinal of the revalidated attachment, only for ok or no_observation.</param>
/// <param name="Usage">Last admitted event, only for ok; null is never zero.</param>
/// <param name="OmittedUsageEvents">Rejected identity-mismatched events on this attachment, not a history completeness measure.</param>
public sealed record OwnedUsageReadResult(string Status, Guid RuntimeInstanceId, string SessionId,
    long? AttachmentGeneration, SessionRuntimeUsageObservation? Usage, long OmittedUsageEvents);

public sealed partial class SessionRuntimeService
{
    /// <summary>Reads the last admitted typed usage on an existing actor only after bounded catalog and exact persisted header checks.</summary>
    /// <remarks>Caller must first validate its admitted host epoch. This method neither activates a session nor provides
    /// an atomic snapshot against external catalog/journal writers. Only the original attachment can release data.</remarks>
    /// <param name="sessionId">Known exact session ID.</param>
    /// <param name="scope">Explicit requested scope: project or global.</param>
    /// <param name="projectId">Known project ID only for project scope.</param>
    /// <param name="expectedProjectPath">Host-matched normalized project path only for project scope.</param>
    /// <param name="cancellationToken">Cancels the read and the caller's wait.</param>
    /// <returns>Explicit result or refusal without paths, provider details or history.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closed.</exception>
    public async Task<OwnedUsageReadResult> ReadOwnedUsageAsync(string sessionId, string scope, string? projectId,
        string? expectedProjectPath, CancellationToken cancellationToken = default)
        => await ReadOwnedUsageWithReadersAsync(sessionId, scope, projectId, expectedProjectPath,
            _sessionViewCatalog.JournalStore.ReadBoundedHeaderAsync, _projectCatalog.ReadBoundedOwnershipAsync,
            cancellationToken).ConfigureAwait(false);

    // Only test fixtures replace the two bounded reads to hold the asynchronous ownership boundary.
    internal async Task<OwnedUsageReadResult> ReadOwnedUsageWithReadersAsync(string sessionId, string scope, string? projectId,
        string? expectedProjectPath,
        Func<string?, DateTimeOffset, CancellationToken, Task<BoundedSessionHeaderResult>> readHeader,
        Func<string?, string?, CancellationToken, Task<ProjectOwnershipResult>> readCatalog,
        CancellationToken cancellationToken = default)
        => await AdmitAsync(() => ReadOwnedUsageBodyAsync(sessionId, scope, projectId, expectedProjectPath,
            readHeader, readCatalog, cancellationToken),
            cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task<OwnedUsageReadResult> ReadOwnedUsageBodyAsync(string sessionId, string scope, string? projectId,
        string? expectedProjectPath,
        Func<string?, DateTimeOffset, CancellationToken, Task<BoundedSessionHeaderResult>> readHeader,
        Func<string?, string?, CancellationToken, Task<ProjectOwnershipResult>> readCatalog, CancellationToken token)
    {
        OwnedUsageReadResult fail(string status) => new(status, _runtimeInstanceId, sessionId, null, null, 0);
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256 || scope is not ("project" or "global")
            || scope == "global" && (projectId is not null || expectedProjectPath is not null)
            || scope == "project" && (string.IsNullOrWhiteSpace(projectId) || expectedProjectPath is null))
            return fail("invalid_request");
        if (!_sessionActors.TryGet(sessionId, out var actor)) return fail("missing_session");
        var capture = await actor.QueryAsync(_ =>
        {
            if (!_entries.TryGetValue(sessionId, out var entry))
                return ValueTask.FromResult<(string, RuntimeSessionEntry?)>(("missing_session", null));
            if (_transitions.ContainsKey(sessionId) || entry.Attachment.IsRetiring || entry.IsTerminated)
                return ValueTask.FromResult<(string, RuntimeSessionEntry?)>(("transition", null));
            var scopeMatches = scope == "global"
                ? entry.Kind == SessionViewKind.GlobalSession && entry.ProjectId is null
                : entry.Kind == SessionViewKind.ProjectSession && entry.ProjectId == projectId
                    && entry.WorkingDirectory == expectedProjectPath;
            return ValueTask.FromResult<(string, RuntimeSessionEntry?)>(scopeMatches
                ? ("ok", entry) : ("scope_mismatch", null));
        }, CancellationToken.None).ConfigureAwait(false);
        if (capture.Item2 is not { } original) return fail(capture.Item1);
        var persisted = await readHeader(sessionId, original.CreatedAt, token)
            .ConfigureAwait(false);
        if (persisted.Status != BoundedSessionHeaderStatus.Found)
            return fail(persisted.Status switch
            {
                BoundedSessionHeaderStatus.Missing => "metadata_missing",
                BoundedSessionHeaderStatus.Incomplete => "metadata_incomplete",
                BoundedSessionHeaderStatus.Invalid => "metadata_invalid",
                _ => "read_failed",
            });
        var header = persisted.Header!;
        if (header.SchemaVersion != 1 || header.Kind != original.Kind || header.ProjectRef != original.ProjectId
            || header.WorkingDirectory != original.WorkingDirectory || header.ProviderId != original.ProviderId.Value
            || header.ProviderKey != original.ProviderKey || header.SessionId != original.SessionId
            || header.CreatedAt != original.CreatedAt)
            return fail("metadata_mismatch");
        if (scope == "project")
        {
            var catalog = await readCatalog(original.ProjectId, original.WorkingDirectory, token)
                .ConfigureAwait(false);
            if (catalog.Status != ProjectOwnershipStatus.Match)
                return fail(catalog.Status switch
                {
                    ProjectOwnershipStatus.Missing => "missing_project",
                    ProjectOwnershipStatus.Ambiguous => "ambiguous_project",
                    ProjectOwnershipStatus.Incomplete => "incomplete_project",
                    ProjectOwnershipStatus.Invalid => "invalid_project",
                    _ => "read_failed",
                });
            if (catalog.Archived != false) return fail("archived_project");
        }
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sessionActors.TryGet(sessionId, out var currentActor) || !ReferenceEquals(actor, currentActor)) return fail("stale_attachment");
        return await actor.QueryAsync(_ =>
        {
            // Serialize the release boundary with host closure; the pre-query check cannot fence a queued callback.
            lock (_identityGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_sessionActors.TryGet(sessionId, out var owner) || !ReferenceEquals(actor, owner)
                    || !_entries.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, original)
                    || !ReferenceEquals(current.Attachment, original.Attachment))
                    return ValueTask.FromResult(fail("stale_attachment"));
                if (_transitions.ContainsKey(sessionId) || current.Attachment.IsRetiring || current.IsTerminated)
                    return ValueTask.FromResult(fail("transition"));
                var observation = current.LastObservedUsage;
                return ValueTask.FromResult(new OwnedUsageReadResult(observation is null ? "no_observation" : "ok",
                    _runtimeInstanceId, sessionId, current.Attachment.Ordinal, observation, current.OmittedUsageEvents));
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }
}
