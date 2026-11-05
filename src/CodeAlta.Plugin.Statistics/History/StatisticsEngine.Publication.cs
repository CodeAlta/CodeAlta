namespace CodeAlta.Plugin.Statistics.History;

// The tables exist once the engine started: before, a publisher reads nothing rather than prepare them.
internal sealed partial class StatisticsEngine : IStatisticsPublication
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, string>> ReadRevisionsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInitialized()) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return await _store.ListRevisionsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<StatisticsFactRow>> ReadRowsAsync(string sessionId, long fromQuarter, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (!IsInitialized()) return [];
        var rows = await _store.ReadPublishedRowsAsync(sessionId, fromQuarter, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return rows;
        // The names the provider reported for the models the session was given, which the state of its facts keeps.
        var journal = await _store.GetJournalAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var aliases = journal is { State.Length: > 0 } ? Facts.SessionFactsState.FromUtf8Json(journal.State).ModelAliases : null;
        return PublishedModels.Attribute(rows, aliases);
    }
}
