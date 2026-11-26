using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    /// <summary>The most labels a session has.</summary>
    public const int MaximumSessionLabels = 32;

    /// <summary>
    /// Gives labels to a session and takes others from it, also while it runs: one state record of its journal. The labels
    /// are identifiers of labels for sessions (<see cref="LabelDescriptor.Target"/>), which the caller checks; the session
    /// keeps the ones it has, in their order, and gets the new ones after them.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="add">The labels the session gets.</param>
    /// <param name="remove">The labels the session loses.</param>
    /// <param name="cancellationToken">Stops waiting for admission; what was started is completed.</param>
    /// <returns>The labels of the session after the change, or <see langword="null"/> for a session that is not known.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank, or the session would have more than
    /// <see cref="MaximumSessionLabels"/> labels.</exception>
    /// <exception cref="ObjectDisposedException">The runtime is closing.</exception>
    /// <exception cref="SessionInUseException">Another process holds the session.</exception>
    public async Task<IReadOnlyList<string>?> SetSessionLabelsAsync(string sessionId, IReadOnlyList<string>? add, IReadOnlyList<string>? remove,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var adding = LabelDescriptor.NormalizeIds(add);
        var removing = LabelDescriptor.NormalizeIds(remove);
        var id = sessionId.Trim();
        return await AdmitAsync<IReadOnlyList<string>?>(async () =>
        {
            ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
            using var lease = HoldForWrite(id);
            SessionViewDescriptor? changed = null;
            var labels = await GetActorForWork(id).QueryAsync<IReadOnlyList<string>?>(async token =>
            {
                if (await ResolveOwnedSessionBodyAsync(id, token).ConfigureAwait(false) is not { } session) return null;
                var localState = await ReadLatestLocalStateAsync(id, session.CreatedAt, token).ConfigureAwait(false);
                var current = LabelDescriptor.NormalizeIds(localState?.Labels);
                var next = LabelDescriptor.NormalizeIds(current.Where(label => !removing.Contains(label, StringComparer.Ordinal)).Concat(adding));
                if (next.Count > MaximumSessionLabels)
                    throw new ArgumentException($"A session has at most {MaximumSessionLabels} labels.", nameof(add));
                if (next.SequenceEqual(current, StringComparer.Ordinal)) return current;
                if (localState is null)
                {
                    localState = new SessionViewLocalState();
                    CopySessionMetadata(session, localState);
                }

                localState.Labels = next.Count == 0 ? null : next;
                await _sessionViewCatalog.JournalStore.AppendStateAsync(session, localState, token).ConfigureAwait(false);
                changed = session;
                return next;
            }, CancellationToken.None).ConfigureAwait(false);
            if (changed is not null)
            {
                // The labels are written: the lists read them again at their next refresh when telling them fails.
                try
                {
                    PublishSessionCatalogEvent(changed);
                    await _agentSessionCatalog.NotifySessionUpdatedAsync(id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                }
            }

            return labels;
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the labels of a session, as the last state record of its journal names them.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The identifiers of its labels, in their order; empty for none.</returns>
    public async Task<IReadOnlyList<string>> GetSessionLabelsAsync(SessionViewDescriptor session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.CachedViewState is { } cached) return cached.Labels ?? [];
        var localState = await ReadLatestLocalStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false);
        return LabelDescriptor.NormalizeIds(localState?.Labels);
    }
}
