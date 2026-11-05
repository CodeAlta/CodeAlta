namespace CodeAlta.Plugin.Statistics.History;

/// <summary>
/// Gives the tool calls of one session that a fleet sums the model of their provider, so that the facts of one run land on one
/// model: the facts keep tool calls without a model. The time and the runs are on the model their own run reported already (the
/// reducer moves them there when the first request of the run names it), and the requests and costs on the model they reported.
/// </summary>
internal static class PublishedModels
{
    private const string ModelKey = "model";
    private const string ProviderKey = "provider";
    private const string ToolFamily = "tool";

    /// <summary>Gives the rows of a session with the models of its provider.</summary>
    /// <param name="rows">The rows of the session, as the store has them.</param>
    /// <returns>
    /// The rows: a tool call with the model of its provider in its quarter hour, or in the session, when the session used one model
    /// of that provider there, and without one otherwise; the other rows as they are.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    public static IReadOnlyList<StatisticsFactRow> Attribute(IReadOnlyList<StatisticsFactRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var byQuarter = new Dictionary<(string Provider, long Quarter), HashSet<string>>();
        var byProvider = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            // A row that counts nothing names no model the session used.
            if (row.Family == ToolFamily || Find(row, ModelKey) is not { } model || row.Measures.All(static measure => measure.Value == 0)) continue;
            var provider = Find(row, ProviderKey) ?? string.Empty;
            Add(byQuarter, (provider, row.Quarter), model);
            Add(byProvider, provider, model);
        }

        var attributed = rows.ToList();
        for (var index = 0; index < attributed.Count; index++)
        {
            var row = attributed[index];
            if (row.Family != ToolFamily || Find(row, ModelKey) is not null) continue;
            var provider = Find(row, ProviderKey) ?? string.Empty;
            var model = Single(byQuarter.GetValueOrDefault((provider, row.Quarter))) ?? Single(byProvider.GetValueOrDefault(provider));
            if (model is null) continue;
            var keys = new List<KeyValuePair<string, string>>(row.Keys.Count + 1);
            foreach (var key in row.Keys)
            {
                keys.Add(key);
                if (key.Key == ProviderKey) keys.Add(new(ModelKey, model));
            }

            if (keys.Count == row.Keys.Count) keys.Insert(0, new(ModelKey, model));
            attributed[index] = row with { Keys = keys };
        }

        return attributed;
    }

    private static string? Find(StatisticsFactRow row, string name)
    {
        foreach (var key in row.Keys)
        {
            if (key.Key == name) return key.Value;
        }

        return null;
    }

    private static void Add<TKey>(Dictionary<TKey, HashSet<string>> models, TKey key, string model) where TKey : notnull
    {
        if (!models.TryGetValue(key, out var set)) models[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(model);
    }

    private static string? Single(HashSet<string>? models) => models is { Count: 1 } ? models.First() : null;
}
