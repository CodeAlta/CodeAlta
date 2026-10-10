using System.Globalization;
using System.Text.Json.Serialization;

namespace CodeAlta.Plugin.Statistics.Query;

/// <summary>The size of the buckets a series is cut into.</summary>
public enum StatisticsFrequency
{
    /// <summary>Chosen from the length of the period: hours for one day, days up to 90 days, weeks up to a year, months beyond.</summary>
    Auto,

    /// <summary>One bucket per local hour.</summary>
    Hour,

    /// <summary>One bucket per local day.</summary>
    Day,

    /// <summary>One bucket per week, from the first day of the week.</summary>
    Week,

    /// <summary>One bucket per local month.</summary>
    Month,

    /// <summary>One bucket per local year.</summary>
    Year,
}

/// <summary>What a period is compared with.</summary>
public enum StatisticsComparison
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>The period of the same length that ends the day before this one starts.</summary>
    PreviousPeriod,

    /// <summary>The same dates one year before.</summary>
    SamePeriodLastYear,
}

/// <summary>
/// The filters of a query. A filter that a number cannot honor (the origin of a prompt on the tokens of a quarter hour, for instance)
/// is not applied to it, and the result names it in <c>ignoredFilters</c>.
/// </summary>
public sealed record StatisticsFilter
{
    /// <summary>Gets the space, by id, start of id or name: the projects it has <b>today</b>.</summary>
    public string? Space { get; init; }

    /// <summary>Gets the project, by id, slug or name.</summary>
    public string? Project { get; init; }

    /// <summary>Gets the provider.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the model.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the reasoning effort.</summary>
    public string? Effort { get; init; }

    /// <summary>Gets who started the work: <c>you</c>, <c>agent</c>, <c>automation</c> or <c>reminder</c>.</summary>
    public string? Origin { get; init; }

    /// <summary>Gets the kind of tool: <c>files</c>, <c>search</c>, <c>shell</c>, <c>web</c>, <c>alta</c>, <c>mcp</c>, <c>skill</c> or <c>other</c>.</summary>
    public string? ToolKind { get; init; }

    /// <summary>
    /// Gets the session the numbers are limited to: its identifier, or the start of it when only one session matches. A session the
    /// statistics do not know gives numbers of nothing, with the note <c>session-not-found</c>, never the numbers of every session.
    /// </summary>
    public string? Session { get; init; }

    /// <summary>Gets a value indicating whether the limit to <see cref="Session"/> includes the sessions that session created, at any depth: its sub-agents.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WithChildren { get; init; }

    /// <summary>Gets a value indicating whether no filter is set.</summary>
    [JsonIgnore]
    public bool IsEmpty => Space is null && Project is null && Provider is null && Model is null && Effort is null && Origin is null && ToolKind is null && Session is null;
}

/// <summary>What a page of the statistics asks for: a period, a frequency, filters and a comparison.</summary>
public sealed record StatisticsRequest
{
    /// <summary>
    /// Gets the period, in local days: <c>today</c>, <c>yesterday</c>, <c>7d</c> (or any <c>Nd</c>: the last N days, today included),
    /// <c>week</c>, <c>month</c>, <c>last-month</c>, <c>year</c>, <c>all</c>, or <c>yyyy-MM-dd..yyyy-MM-dd</c> (an end left out is today, a start left out is the first day with data).
    /// </summary>
    public string Period { get; init; } = "30d";

    /// <summary>Gets the size of the buckets.</summary>
    public StatisticsFrequency Frequency { get; init; } = StatisticsFrequency.Auto;

    /// <summary>Gets what the period is compared with.</summary>
    public StatisticsComparison Comparison { get; init; } = StatisticsComparison.None;

    /// <summary>Gets the filters.</summary>
    public StatisticsFilter Filter { get; init; } = new();

    /// <summary>Gets the first day of the week; null for <see cref="StatisticsQueries.DefaultWeekStart"/>, the one of the regional settings of the computer.</summary>
    public DayOfWeek? WeekStart { get; init; }

    /// <summary>Gets the most rows a table or a ranking returns; null for the default of the query.</summary>
    public int? Limit { get; init; }
}

/// <summary>A period of whole local days.</summary>
/// <param name="From">The first day.</param>
/// <param name="To">The last day, included.</param>
internal readonly record struct DayRange(DateOnly From, DateOnly To)
{
    /// <summary>Gets the number of days.</summary>
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>Reads the text of a period.</summary>
internal static class PeriodParser
{
    /// <summary>Resolves a period to days.</summary>
    /// <param name="text">The text of the period.</param>
    /// <param name="today">The local date of today.</param>
    /// <param name="firstDayWithData">The first local day that has statistics; null when there are none.</param>
    /// <param name="weekStart">The first day of the week.</param>
    /// <returns>The days.</returns>
    /// <exception cref="ArgumentException">The text is not a period.</exception>
    public static DayRange Resolve(string text, DateOnly today, DateOnly? firstDayWithData, DayOfWeek weekStart)
    {
        ArgumentNullException.ThrowIfNull(text);
        var value = text.Trim().ToLowerInvariant();
        switch (value)
        {
            case "today":
                return new DayRange(today, today);
            case "yesterday":
                return new DayRange(today.AddDays(-1), today.AddDays(-1));
            case "week":
            case "this-week":
                var offset = ((int)today.DayOfWeek - (int)weekStart + 7) % 7;
                return new DayRange(today.AddDays(-offset), today);
            case "month":
            case "this-month":
                return new DayRange(new DateOnly(today.Year, today.Month, 1), today);
            case "last-month":
                var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                return new DayRange(first, first.AddMonths(1).AddDays(-1));
            case "year":
            case "this-year":
                return new DayRange(new DateOnly(today.Year, 1, 1), today);
            case "all":
                return new DayRange(firstDayWithData is { } data && data < today ? data : today, today);
        }

        if (value.Length >= 2 && value[^1] == 'd' && int.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days >= 1)
        {
            return new DayRange(today.AddDays(1 - days), today);
        }

        var split = value.IndexOf("..", StringComparison.Ordinal);
        if (split >= 0)
        {
            var fromText = value[..split].Trim();
            var toText = value[(split + 2)..].Trim();
            var from = fromText.Length == 0 ? firstDayWithData ?? today : ParseDate(fromText, text);
            var to = toText.Length == 0 ? today : ParseDate(toText, text);
            if (to < from)
            {
                throw new ArgumentException($"The period '{text}' ends before it starts.", nameof(text));
            }

            return new DayRange(from, to);
        }

        throw new ArgumentException($"'{text}' is not a period: use today, yesterday, 7d, 30d, 90d, week, month, last-month, year, all, or <from>..<to> as yyyy-MM-dd.", nameof(text));
    }

    private static DateOnly ParseDate(string value, string text)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"'{value}' in the period '{text}' is not a date as yyyy-MM-dd.", nameof(text));
}
