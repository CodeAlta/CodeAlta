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
            // The providers of the space of the session: one assigned to other spaces is not offered.
            var space = await ResolveSpaceIdAsync(session.SpaceId, session.ProjectRef, token).ConfigureAwait(false);
            var assignments = LoadSpaceAssignments();
            return new OwnedProviderSelectionContext(sessionId, _runtimeInstanceId, state.Entry?.AttachmentGeneration,
                session.ResolvedProviderKey, session.UpdatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _agentHub.SelectionProviders.Where(provider => provider.IsEnabled
                    && assignments.IsAvailable(Catalog.SpaceResourceKind.Provider, provider.ProviderId.Value, space)).ToArray());
        }, CancellationToken.None).AsTask(), CancellationToken.None);

    internal Task<string> SelectOwnedProviderAsync(OwnedProviderSelectionContext expected, string providerKey)
        => SelectOwnedProviderAsync(expected, providerKey, carryOver: null);

    // carryOver: what the session takes on the provider (a fallback), instead of the provider's defaults.
    internal Task<string> SelectOwnedProviderAsync(OwnedProviderSelectionContext expected, string providerKey,
        Catalog.SessionViewProviderCarryOver? carryOver)
        => AdmitAsync(async () =>
        {
            if (!TryHoldForWrite(expected.SessionId, out var lease)) return "session_in_use";
            using var held = lease;
            var actor = GetActorForWork(expected.SessionId);
            Task? retirement = null;
            var result = await actor.QueryAsync(async token =>
            {
                if (_runtimeInstanceId != expected.RuntimeInstanceId || _transitions.ContainsKey(expected.SessionId)) return "stale_runtime";
                _entries.TryGetValue(expected.SessionId, out var entry);
                if (entry?.Attachment.Ordinal != expected.AttachmentGeneration) return "stale_attachment";
                if (entry is not null && (entry.HasActiveRun || entry.QueueDrainInProgress
                    || entry.Attachment.IsRetiring || !HasOwnedCommandDefaults(entry))) return "session_in_use";
                if ((await Permissions.ListAsync().ConfigureAwait(false)).Any(permission => permission.Handle.SessionId == expected.SessionId)
                    || (await Permissions.ListOwnedUserInputsAsync(expected.SessionId, token).ConfigureAwait(false)).Entries.Count != 0) return "interaction_pending";
                var session = await ResolveOwnedSessionBodyAsync(expected.SessionId, token).ConfigureAwait(false);
                if (session is null || session.Status == Catalog.SessionViewStatus.Archived) return "session_missing";
                // A session in the cloud keeps the account of Claude Code that reaches its cloud session until it is back.
                if (_cloudWork.ContainsKey(expected.SessionId) || await RunsInCloudAsync(session, token).ConfigureAwait(false)) return InCloudCode;
                if (session.ResolvedProviderKey != expected.ProviderKey
                    || session.UpdatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) != expected.Revision) return "stale_selection";
                var target = _agentHub.SelectionProviders.SingleOrDefault(provider => provider.ProviderId.Value == providerKey && provider.IsEnabled);
                if (target is null) return "provider_unavailable";
                if (target.ProviderId.Value == session.ResolvedProviderKey) return "ok";
                // A session belongs to its space: it only takes a provider of that space.
                if (!IsProviderInSpace(target.ProviderId.Value, await ResolveSpaceIdAsync(session.SpaceId, session.ProjectRef, token).ConfigureAwait(false)))
                    return ProviderOutsideSpace;
                if (entry?.PendingAgentPromptId is not null) return "pending_selection";
                // A prompt that waits in the queue cannot follow the session to another provider. One that failed or
                // was canceled is a record of work that ended, and does not hold the session.
                var local = await _sessionViewCatalog.JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt, token).ConfigureAwait(false);
                if (local?.HasPendingQueuedPrompts() == true) return Catalog.SessionViewChangeRefusedException.QueuePending;
                // The original attachment stays intact through preparation and the atomic durable decision.
                var family = await RuntimeFamilyOfKeyWrittenAsFamilyAsync(
                    await _sessionViewCatalog.JournalStore.CreateSessionStore().GetSessionSummaryAsync(session.SessionId, token).ConfigureAwait(false), token).ConfigureAwait(false);
                // A session does not leave its sandbox by changing provider, by hand or in a fallback: the work and the
                // history of a sandboxed model come with it.
                if (await KeptSandboxAsync(session, target.ProviderId.Value, carryOver?.ModelId, token).ConfigureAwait(false) is { } kept)
                    carryOver = (carryOver ?? new Catalog.SessionViewProviderCarryOver(null, null, null)) with { Sandbox = kept };
                // The journal tells what it holds that keeps the session where it is: a turn that did not end, or a
                // prompt queued since the check above.
                try { await _sessionViewCatalog.JournalStore.SelectProviderAsync(session, expected.ProviderKey, target, carryOver, family, token).ConfigureAwait(false); }
                catch (Catalog.SessionViewChangeRefusedException refused) { return refused.Code; }
                if (entry is not null)
                {
                    retirement = _forwarding.RetireAsync(entry.Attachment);
                    if (_entries.TryRemove(expected.SessionId, out _)) ReleaseEntryLeaseAfter(expected.SessionId, retirement);
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
