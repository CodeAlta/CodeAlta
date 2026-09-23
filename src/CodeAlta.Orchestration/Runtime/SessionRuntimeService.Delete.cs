using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    // The command owner excludes new owned sends/queues while this original runs. The actor
    // rechecks coordinator state at the retirement claim and again at the journal removal.
    internal Task<string> DeleteOwnedCatalogSessionAsync(string sessionId, string? projectId, string workspacePath, string confirmedTitle)
        => AdmitAsync(() => DeleteOwnedCatalogSessionBodyAsync(sessionId, projectId, workspacePath, confirmedTitle), CancellationToken.None);

    private async Task<string> DeleteOwnedCatalogSessionBodyAsync(string sessionId, string? projectId, string workspacePath, string confirmedTitle)
    {
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        async Task<string> ValidateAsync(CancellationToken token)
        {
            var metadata = await store.GetSessionAsync(sessionId, token).ConfigureAwait(false);
            if (metadata is null || metadata.WorkspacePath != workspacePath) return "session_missing";
            var projects = await _projectCatalog.LoadAsync(token).ConfigureAwait(false);
            var session = TryCreateRecoverableSession(metadata, projects);
            if (session is null || session.SessionId != sessionId || session.ProjectRef != projectId
                || session.Kind != (projectId is null ? SessionViewKind.GlobalSession : SessionViewKind.ProjectSession))
                return "session_missing";
            var persistedTitle = (metadata.Details as RawApiSessionMetadataDetails)?.Title;
            var visibleTitle = !string.IsNullOrWhiteSpace(persistedTitle) ? persistedTitle
                : !string.IsNullOrWhiteSpace(metadata.Summary) ? metadata.Summary : metadata.SessionId;
            if (visibleTitle != confirmedTitle) return "session_missing";
            await foreach (var candidate in store.ListSessionsAsync(filter: null, cancellationToken: token).ConfigureAwait(false))
            {
                if (candidate.SessionId != sessionId &&
                    (string.Equals(candidate.ParentSessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(candidate.CreatedBySessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(candidate.ViewState?.ParentSessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
                    return "has_children";
            }
            return "ok";
        }

        var validation = await ValidateAsync(CancellationToken.None).ConfigureAwait(false);
        if (validation != "ok") return validation;
        var actor = GetActorForWork(sessionId);
        var preparation = await actor.QueryAsync(_ =>
        {
            if (_transitions.ContainsKey(sessionId)) return ValueTask.FromResult((Busy: true, Work: (Task?)null));
            if (!_entries.TryGetValue(sessionId, out var entry)) return ValueTask.FromResult((Busy: false, Work: (Task?)null));
            if (entry.HasActiveRun || entry.QueueDrainInProgress)
                return ValueTask.FromResult((Busy: true, Work: (Task?)null));
            var retirement = _forwarding.RetireAsync(entry.Attachment);
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? ticket = null;
            var work = _forwarding.RunAsync(async () =>
            {
                await launch.Task.ConfigureAwait(false);
                try { await retirement.ConfigureAwait(false); }
                finally
                {
                    await actor.QueryAsync(token =>
                    {
                        if (_entries.TryGetValue(sessionId, out var active) && ReferenceEquals(active, entry))
                            _entries.TryRemove(sessionId, out var removedEntry);
                        if (_transitions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, ticket))
                            _transitions.TryRemove(sessionId, out var removedTransition);
                        return ValueTask.FromResult(true);
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }, external: false);
            _transitions[sessionId] = work;
            ticket = work;
            launch.TrySetResult();
            return ValueTask.FromResult((Busy: false, Work: (Task?)work));
        }, CancellationToken.None).ConfigureAwait(false);
        if (preparation.Busy) return "session_in_use";
        if (preparation.Work is not null) await preparation.Work.ConfigureAwait(false);
        return await actor.QueryAsync(async token =>
        {
            if (_transitions.ContainsKey(sessionId) || _entries.ContainsKey(sessionId)) return "session_in_use";
            var checkedScope = await ValidateAsync(token).ConfigureAwait(false);
            if (checkedScope != "ok") return checkedScope;
            return await _agentSessionCatalog.DeleteSessionAsync(sessionId, token).ConfigureAwait(false)
                ? "ok" : "delete_unconfirmed";
        }, CancellationToken.None).ConfigureAwait(false);
    }
}
