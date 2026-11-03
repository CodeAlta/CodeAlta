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
        return await _store.ReadPublishedRowsAsync(sessionId, fromQuarter, cancellationToken).ConfigureAwait(false);
    }
}
