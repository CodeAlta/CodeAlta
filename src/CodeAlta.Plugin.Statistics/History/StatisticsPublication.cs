namespace CodeAlta.Plugin.Statistics.History;

/// <summary>
/// One row of the facts of a session as the store keeps it, for a host that publishes them (a machine of a fleet): its family,
/// its quarter hour, its keys and its measures, by column name, in the order of its table. Names and counts, never content.
/// </summary>
/// <param name="Family">The family: <c>activity</c>, <c>usage</c>, <c>cost</c> or <c>tool</c>.</param>
/// <param name="Quarter">The quarter hour, as an index since the Unix epoch in UTC.</param>
/// <param name="Keys">The keys after the session and the quarter (<c>provider</c>, <c>model</c>, ...), integers written in invariant digits.</param>
/// <param name="Measures">The measures; those whose name ends with <c>_max</c> merge with the larger value, the others add.</param>
public sealed record StatisticsFactRow(
    string Family,
    long Quarter,
    IReadOnlyList<KeyValuePair<string, string>> Keys,
    IReadOnlyList<KeyValuePair<string, long>> Measures);

/// <summary>What a host that publishes the statistics of its sessions reads of the store (<see cref="IStatisticsService"/>).</summary>
public interface IStatisticsPublication
{
    /// <summary>
    /// Gets the revision of the facts of every session read: a text that changes when a catch-up of the session is saved, so
    /// that a publisher reads again only the rows of the sessions that changed.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The revisions by session; empty while the engine is not ready.</returns>
    ValueTask<IReadOnlyDictionary<string, string>> ReadRevisionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the rows of the families a fleet publishes, of a session, from a quarter hour on.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="fromQuarter">The first quarter hour, as an index since the Unix epoch in UTC.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The rows, by family, quarter and keys.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    ValueTask<IReadOnlyList<StatisticsFactRow>> ReadRowsAsync(string sessionId, long fromQuarter, CancellationToken cancellationToken = default);
}
