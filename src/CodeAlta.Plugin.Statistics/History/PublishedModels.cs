namespace CodeAlta.Plugin.Statistics.History;

/// <summary>
/// Puts the rows of one session that a fleet sums on the models its provider reported, so that the facts of one run land on
/// one model: the time and the runs counted under an alias before the first request named the model it stands for (the
/// alias counts under that name from then on), and the tool calls, which the facts keep without a model.
/// </summary>
internal static class PublishedModels
{
    private const string ModelKey = "model";
    private const string ProviderKey = "provider";
    private const string ToolFamily = "tool";

    /// <summary>Gives the rows of a session with the models of its provider.</summary>
    /// <param name="rows">The rows of the session, as the store has them.</param>
    /// <param name="aliases">The names the session's provider reported for the models it was given (alias to model); null for none.</param>
    /// <returns>
    /// The rows: an alias as the model it stands for; a tool call with the model of its provider in its quarter hour, or in the
    /// session, when the session used one model of that provider there, and without one otherwise; rows that became the same
    /// added up (a measure whose name ends with <c>_max</c> keeps the larger value).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    public static IReadOnlyList<StatisticsFactRow> Attribute(IReadOnlyList<StatisticsFactRow> rows, IReadOnlyDictionary<string, string>? aliases)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var renamed = rows.Select(row => Resolve(row, aliases)).ToList();
        var byQuarter = new Dictionary<(string Provider, long Quarter), HashSet<string>>();
        var byProvider = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in renamed)
        {
            if (row.Family == ToolFamily || Find(row, ModelKey) is not { } model) continue;
            var provider = Find(row, ProviderKey) ?? string.Empty;
            Add(byQuarter, (provider, row.Quarter), model);
            Add(byProvider, provider, model);
        }

        for (var index = 0; index < renamed.Count; index++)
        {
            var row = renamed[index];
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
            renamed[index] = row with { Keys = keys };
        }

        return Merge(renamed);
    }

    private static StatisticsFactRow Resolve(StatisticsFactRow row, IReadOnlyDictionary<string, string>? aliases)
    {
        if (aliases is not { Count: > 0 } || Find(row, ModelKey) is not { } model) return row;
        var resolved = model;
        // An alias names the model it stands for, which may have been renamed again: a few steps, never a loop.
        for (var step = 0; step < 8 && aliases.TryGetValue(resolved, out var next) && !string.IsNullOrEmpty(next) && next != resolved; step++) resolved = next;
        if (resolved == model) return row;
        return row with { Keys = [.. row.Keys.Select(key => key.Key == ModelKey ? new KeyValuePair<string, string>(ModelKey, resolved) : key)] };
    }

    // Rows of the same family, quarter hour and keys are one row.
    private static List<StatisticsFactRow> Merge(List<StatisticsFactRow> rows)
    {
        var merged = new List<StatisticsFactRow>(rows.Count);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var identity = string.Join('\u001f', [row.Family, row.Quarter.ToString(System.Globalization.CultureInfo.InvariantCulture), .. row.Keys.Select(static key => key.Key + "=" + key.Value)]);
            if (!positions.TryGetValue(identity, out var position))
            {
                positions[identity] = merged.Count;
                merged.Add(row);
                continue;
            }

            var measures = merged[position].Measures.ToList();
            foreach (var measure in row.Measures)
            {
                var at = measures.FindIndex(held => held.Key == measure.Key);
                if (at < 0) measures.Add(measure);
                else measures[at] = new(measure.Key, measure.Key.EndsWith("_max", StringComparison.Ordinal) ? Math.Max(measures[at].Value, measure.Value) : measures[at].Value + measure.Value);
            }

            merged[position] = merged[position] with { Measures = measures };
        }

        return merged;
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
