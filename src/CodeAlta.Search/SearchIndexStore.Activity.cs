using System.Globalization;

namespace CodeAlta.Search;

/// <summary>Who asked a search, as the activity counts it.</summary>
public enum SearchOrigin : byte
{
    /// <summary>The search window or the Search canvas of the window.</summary>
    Window = 0,

    /// <summary>An agent, with <c>alta search</c>.</summary>
    Agent = 1,

    /// <summary>The <c>tool_search</c> tool of a session.</summary>
    ToolSearch = 2,

    /// <summary>Anything else: <c>alta search</c> from a client outside a session.</summary>
    Other = 3,
}

/// <summary>One search, as the activity keeps it: never the text of the query.</summary>
/// <param name="At">When it was asked.</param>
/// <param name="Origin">Who asked.</param>
/// <param name="SessionId">The session that asked, or null.</param>
/// <param name="Kinds">The kinds it was limited to, or null for every kind.</param>
/// <param name="Mode">How it was asked to search.</param>
/// <param name="Hits">The hits it gave.</param>
/// <param name="WordsMilliseconds">The time of the search by words.</param>
/// <param name="MeaningMilliseconds">The time of the search by meaning, or null when there was none.</param>
/// <param name="Tokens">For tool search, an estimate of the tokens it kept out of the prompt; 0 otherwise.</param>
public sealed record SearchActivityEntry(DateTimeOffset At, SearchOrigin Origin, string? SessionId, IReadOnlySet<SearchKind>? Kinds, SearchMode Mode, int Hits,
    double WordsMilliseconds, double? MeaningMilliseconds, long Tokens = 0)
{
    /// <summary>Makes the entry of a query and of its answer.</summary>
    /// <param name="request">The query.</param>
    /// <param name="response">Its answer.</param>
    /// <param name="at">When it was asked.</param>
    /// <returns>The entry.</returns>
    public static SearchActivityEntry Of(SearchRequest request, SearchResponse response, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        var origin = request.Origin switch
        {
            "window" => SearchOrigin.Window,
            "agent" => SearchOrigin.Agent,
            "tool_search" => SearchOrigin.ToolSearch,
            _ => SearchOrigin.Other,
        };
        return new(at, origin, request.OriginSessionId, request.Scope.Kinds, request.Mode, response.Hits.Count, response.WordsMilliseconds,
            response.MeaningMilliseconds > 0 ? response.MeaningMilliseconds : null);
    }
}

/// <summary>The searches of a day, by who asked them.</summary>
/// <param name="Day">The local day.</param>
/// <param name="Window">From the window.</param>
/// <param name="Agent">From agents.</param>
/// <param name="ToolSearch">From the <c>tool_search</c> tool.</param>
/// <param name="Other">From elsewhere.</param>
/// <param name="Empty">Those that found nothing.</param>
public sealed record SearchActivityDay(DateOnly Day, int Window, int Agent, int ToolSearch, int Other, int Empty);

/// <summary>What the Activity page of the Search canvas shows.</summary>
/// <param name="Days">The days with searches, the oldest first.</param>
/// <param name="Searches">The searches.</param>
/// <param name="Empty">Those that found nothing.</param>
/// <param name="MedianWordsMilliseconds">The median time of the search by words, or null without searches.</param>
/// <param name="MedianMeaningMilliseconds">The median time of the search by meaning among the searches that had one, or null.</param>
/// <param name="Tokens">The tokens tool search kept out of prompts, estimated.</param>
public sealed record SearchActivitySummary(IReadOnlyList<SearchActivityDay> Days, int Searches, int Empty, double? MedianWordsMilliseconds,
    double? MedianMeaningMilliseconds, long Tokens);

// The activity of search: one row per search, never the text of the query, kept 90 days.
public sealed partial class SearchIndexStore
{
    /// <summary>How long the activity is kept.</summary>
    public static readonly TimeSpan ActivityRetention = TimeSpan.FromDays(90);

    // The most rows a summary reads: 90 days of a busy profile.
    private const int MaximumActivityRows = 500_000;

    /// <summary>Records a search, and forgets those older than <see cref="ActivityRetention"/>.</summary>
    /// <param name="entry">The search.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>A task.</returns>
    public async ValueTask RecordActivityAsync(SearchActivityEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var kinds = entry.Kinds is null ? 0 : entry.Kinds.Aggregate(0, static (mask, kind) => mask | (1 << (int)kind));
        await WriteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO search_activity(at, origin, session_id, kinds, mode, hits, words_ms, meaning_ms, tokens)
                VALUES ($at, $origin, $session, $kinds, $mode, $hits, $words, $meaning, $tokens);
                DELETE FROM search_activity WHERE at < $cutoff;
                """;
            command.Parameters.AddWithValue("$at", entry.At.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$origin", (int)entry.Origin);
            command.Parameters.AddWithValue("$session", (object?)entry.SessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$kinds", kinds);
            command.Parameters.AddWithValue("$mode", (int)entry.Mode);
            command.Parameters.AddWithValue("$hits", entry.Hits);
            command.Parameters.AddWithValue("$words", entry.WordsMilliseconds);
            command.Parameters.AddWithValue("$meaning", entry.MeaningMilliseconds is { } meaning ? meaning : DBNull.Value);
            command.Parameters.AddWithValue("$tokens", entry.Tokens);
            command.Parameters.AddWithValue("$cutoff", (entry.At - ActivityRetention).ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sums up the searches since a time: by day and by who asked, the share that found nothing, and the median times.</summary>
    /// <param name="since">The first time counted.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The summary.</returns>
    public async ValueTask<SearchActivitySummary> GetActivityAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync(Owner, async (connection, token) =>
        {
            var days = new SortedDictionary<DateOnly, int[]>();
            var words = new List<double>();
            var meaning = new List<double>();
            int searches = 0, empty = 0;
            long tokens = 0;
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT date(at / 1000, 'unixepoch', 'localtime'), origin, hits, words_ms, meaning_ms, tokens
                FROM search_activity WHERE at >= $since ORDER BY at LIMIT $limit
                """;
            command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$limit", MaximumActivityRows);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (!DateOnly.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, out var day)) continue;
                if (!days.TryGetValue(day, out var counts)) days[day] = counts = new int[5];
                var origin = Math.Clamp(reader.GetInt32(1), 0, 3);
                counts[origin]++;
                searches++;
                if (reader.GetInt32(2) == 0)
                {
                    counts[4]++;
                    empty++;
                }

                if (!reader.IsDBNull(3)) words.Add(reader.GetDouble(3));
                if (!reader.IsDBNull(4)) meaning.Add(reader.GetDouble(4));
                tokens += reader.GetInt64(5);
            }

            return new SearchActivitySummary([.. days.Select(static pair => new SearchActivityDay(pair.Key, pair.Value[0], pair.Value[1], pair.Value[2], pair.Value[3], pair.Value[4]))],
                searches, empty, Median(words), Median(meaning), tokens);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}
