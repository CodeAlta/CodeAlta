using System.Globalization;
using CodeAlta.Plugin.Statistics.History;

namespace CodeAlta.Plugin.Statistics.Store;

internal sealed partial class StatisticsStore
{
    /// <summary>The families a fleet publishes: what its queries add up (time, runs, tokens, cost, tool calls).</summary>
    internal static IReadOnlyList<FactTable> PublishedFamilies { get; } = [FactTables.Activity, FactTables.Usage, FactTables.Cost, FactTables.Tools];

    /// <summary>
    /// The version of what a publisher makes of the rows, part of each revision: a new one has every session published again once.
    /// Version 2 puts the rows of a session on the models its provider reported (<see cref="PublishedModels"/>); version 3 never
    /// relabels a model a request reported.
    /// </summary>
    internal const int PublicationVersion = 3;

    /// <summary>
    /// Gets the revision of the facts of every session read: where its reading stopped, the version of the facts, the floor and the
    /// first line it was read from, and the version of the publication. A saved catch-up changes it; reading the rows again only when
    /// it changed keeps a publisher cheap.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The revisions by session.</returns>
    public ValueTask<IReadOnlyDictionary<string, string>> ListRevisionsAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyDictionary<string, string>>(sql =>
        {
            var revisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, revision) in sql.Query(
                $"SELECT session_id, read_offset, facts_version, floor_q, first_line_hash, deleted FROM {Table("journal")}",
                static reader => (reader.GetString(0), string.Create(CultureInfo.InvariantCulture,
                    $"{reader.GetInt64(1)}:{reader.GetInt64(2)}:{reader.GetInt64(3)}:{(reader.IsDBNull(4) ? 0 : reader.GetInt64(4))}:{reader.GetInt64(5)}:p{PublicationVersion}"))))
            {
                revisions[id] = revision;
            }

            return revisions;
        }, cancellationToken);

    /// <summary>Reads the rows of the published families of a session, from a quarter hour on.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="fromQuarter">The first quarter hour.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The rows, by family, then quarter, then keys.</returns>
    public ValueTask<IReadOnlyList<StatisticsFactRow>> ReadPublishedRowsAsync(string sessionId, long fromQuarter, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var floor = (long)Math.Clamp(fromQuarter, int.MinValue, int.MaxValue);
        return ReadAsync<IReadOnlyList<StatisticsFactRow>>(sql =>
        {
            var rows = new List<StatisticsFactRow>();
            foreach (var family in PublishedFamilies)
            {
                var keys = family.Keys;
                var measures = family.Measures;
                var columns = string.Join(", ", keys.Select(static key => key.Name).Concat(measures.Select(static measure => measure.Name)));
                var order = string.Join(", ", new[] { "q" }.Concat(keys.Select(static key => key.Name)));
                // A row a later correction took back to zero (a run's start moved from its alias to the model its request named) says nothing:
                // it is not published, and its model is not one the session used.
                var counted = string.Join(" OR ", measures.Select(static measure => $"{measure.Name} <> 0"));
                rows.AddRange(sql.Query($"SELECT q, {columns} FROM {family.TableName(Prefix, RollupLevel.Quarter)} WHERE session_id = @p0 AND q >= @p1 AND ({counted}) ORDER BY {order}",
                    reader =>
                    {
                        var keyValues = new KeyValuePair<string, string>[keys.Length];
                        for (var i = 0; i < keys.Length; i++)
                        {
                            var value = keys[i].IsText ? reader.GetString(1 + i) : reader.GetInt64(1 + i).ToString(CultureInfo.InvariantCulture);
                            keyValues[i] = new(keys[i].Name, value);
                        }

                        var measureValues = new KeyValuePair<string, long>[measures.Length];
                        for (var i = 0; i < measures.Length; i++) measureValues[i] = new(measures[i].Name, reader.GetInt64(1 + keys.Length + i));
                        return new StatisticsFactRow(family.Name, reader.GetInt64(0), keyValues, measureValues);
                    }, sessionId, floor));
            }

            return rows;
        }, cancellationToken);
    }
}
