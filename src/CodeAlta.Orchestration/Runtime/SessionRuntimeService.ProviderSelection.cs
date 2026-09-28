using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Exact observation required for an idle provider selection; not send authority.</summary>
public sealed record OwnedProviderSelectionContext(string SessionId, Guid RuntimeInstanceId, long? AttachmentGeneration,
    string ProviderKey, string Revision, IReadOnlyList<ModelProviderDescriptor> Providers);

public sealed partial class SessionRuntimeService
{
    internal Task<OwnedProviderSelectionContext?> ReadProviderSelectionAsync(string sessionId)
        => AdmitAsync(() => GetActorForWork(sessionId).QueryAsync<OwnedProviderSelectionContext?>(async token =>
        {
            var session = await ResolveOwnedSessionBodyAsync(sessionId, token).ConfigureAwait(false);
            if (session is null || session.Status == Catalog.SessionViewStatus.Archived) return null;
            var state = CaptureCurrentState(sessionId);
            if (state.CoordinatorTransitionInProgress || state.Entry is { ActiveRunId: not null } or { QueueDrainInProgress: true }
                or { IsRetiring: true }) return null;
            return new OwnedProviderSelectionContext(sessionId, _runtimeInstanceId, state.Entry?.AttachmentGeneration,
                session.ResolvedProviderKey, session.UpdatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _agentHub.SelectionProviders.Where(provider => provider.IsEnabled).ToArray());
        }, CancellationToken.None).AsTask(), CancellationToken.None);

    internal Task<string> SelectOwnedProviderAsync(OwnedProviderSelectionContext expected, string providerKey)
        => AdmitAsync(async () =>
        {
            var actor = GetActorForWork(expected.SessionId);
            Task? retirement = null;
            var result = await actor.QueryAsync(async token =>
            {
                if (_runtimeInstanceId != expected.RuntimeInstanceId || _transitions.ContainsKey(expected.SessionId)) return "stale_runtime";
                _entries.TryGetValue(expected.SessionId, out var entry);
                if (entry?.Attachment.Ordinal != expected.AttachmentGeneration) return "stale_attachment";
                if (entry is not null && (entry.HasActiveRun || entry.QueueDrainInProgress || entry.OwnedQueue is not null
                    || entry.Attachment.IsRetiring || !HasOwnedCommandDefaults(entry))) return "session_in_use";
                if ((await Permissions.ListAsync().ConfigureAwait(false)).Any(permission => permission.Handle.SessionId == expected.SessionId)
                    || (await Permissions.ListOwnedUserInputsAsync(expected.SessionId, token).ConfigureAwait(false)).Entries.Count != 0) return "interaction_pending";
                var session = await ResolveOwnedSessionBodyAsync(expected.SessionId, token).ConfigureAwait(false);
                if (session is null || session.Status == Catalog.SessionViewStatus.Archived) return "session_missing";
                if (session.ResolvedProviderKey != expected.ProviderKey
                    || session.UpdatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) != expected.Revision) return "stale_selection";
                var target = _agentHub.SelectionProviders.SingleOrDefault(provider => provider.ProviderId.Value == providerKey && provider.IsEnabled);
                if (target is null) return "provider_unavailable";
                if (target.ProviderId.Value == session.ResolvedProviderKey) return "ok";
                if (entry?.PendingAgentPromptId is not null) return "pending_selection";
                // The original attachment stays intact through preparation and the atomic durable decision.
                await _sessionViewCatalog.JournalStore.SelectProviderAsync(session, expected.ProviderKey, target, token).ConfigureAwait(false);
                if (entry is not null)
                {
                    retirement = _forwarding.RetireAsync(entry.Attachment);
                    _entries.TryRemove(expected.SessionId, out _);
                    _transitions[expected.SessionId] = retirement;
                }
                return "ok";
            }, CancellationToken.None).ConfigureAwait(false);
            if (result != "ok") return result;
            // Publish the committed selection/transition even if old attachment cleanup is retained.
            PublishSessionLifecycleEvent(expected.SessionId);
            var changed = await ResolveOwnedSessionBodyAsync(expected.SessionId, CancellationToken.None).ConfigureAwait(false);
            if (changed is not null) PublishSessionCatalogEvent(changed);
            // Retirement happens after commit. Its failure must not be reported as a rollback.
            if (retirement is not null)
            {
                try { await retirement.ConfigureAwait(false); }
                catch { return "selected_cleanup_required"; }
                await actor.QueryAsync(_ =>
                {
                    if (_transitions.TryGetValue(expected.SessionId, out var current) && ReferenceEquals(current, retirement))
                        _transitions.TryRemove(expected.SessionId, out var removed);
                    return ValueTask.FromResult(true);
                }, CancellationToken.None).ConfigureAwait(false);
            }
            PublishSessionLifecycleEvent(expected.SessionId);
            return "ok";
        }, CancellationToken.None);
}
