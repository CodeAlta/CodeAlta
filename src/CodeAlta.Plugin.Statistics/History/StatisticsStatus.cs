using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Plugin.Statistics.History;

/// <summary>Writes a <see cref="HistoryState"/> as camelCase text: <c>needsChoice</c>, <c>stoppedHere</c>.</summary>
public sealed class HistoryStateConverter : JsonStringEnumConverter<HistoryState>
{
    /// <summary>Initializes the converter.</summary>
    public HistoryStateConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}

/// <summary>Where the reading of the history stands.</summary>
[JsonConverter(typeof(HistoryStateConverter))]
public enum HistoryState
{
    /// <summary>The engine is starting: the tables are being prepared.</summary>
    Starting,

    /// <summary>The user has not chosen how much history to read; nothing is read until the choice is made.</summary>
    NeedsChoice,

    /// <summary>Sessions are being read.</summary>
    Reading,

    /// <summary>The user paused the reading; the flow goes on.</summary>
    Paused,

    /// <summary>The user stopped the reading here: the statistics start at the date reached.</summary>
    StoppedHere,

    /// <summary>Every session the choice covers is read.</summary>
    Done,

    /// <summary>The engine could not start; <see cref="StatisticsStatus.Error"/> says why.</summary>
    Failed,
}

/// <summary>How much history the user asked to read.</summary>
/// <param name="Kind">The kind of choice.</param>
/// <param name="Days">For <see cref="HistoryChoiceKind.Days"/>: the number of days, today included.</param>
public sealed record HistoryChoice(HistoryChoiceKind Kind, int Days = 0)
{
    /// <summary>Gets the choice to read every session.</summary>
    public static HistoryChoice All { get; } = new(HistoryChoiceKind.All);

    /// <summary>Gets the choice to read nothing of the past: the statistics start with today.</summary>
    public static HistoryChoice FromToday { get; } = new(HistoryChoiceKind.FromToday);

    /// <summary>Gets the choice to read the sessions with activity in the last days.</summary>
    /// <param name="days">The number of days, today included; at least 1.</param>
    /// <returns>The choice.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="days"/> is below 1.</exception>
    public static HistoryChoice LastDays(int days)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        return new HistoryChoice(HistoryChoiceKind.Days, days);
    }

    /// <summary>Gets the text the choice is saved as: <c>all</c>, <c>days:30</c> or <c>from-today</c>.</summary>
    /// <returns>The text.</returns>
    public string ToText() => Kind switch
    {
        HistoryChoiceKind.All => "all",
        HistoryChoiceKind.Days => "days:" + Days.ToString(CultureInfo.InvariantCulture),
        _ => "from-today",
    };

    /// <summary>Reads a choice from its saved text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="choice">The choice, when the text is one.</param>
    /// <returns><see langword="true"/> when the text is a choice.</returns>
    public static bool TryParse(string? text, out HistoryChoice choice)
    {
        choice = FromToday;
        if (string.Equals(text, "all", StringComparison.Ordinal))
        {
            choice = All;
            return true;
        }

        if (string.Equals(text, "from-today", StringComparison.Ordinal))
        {
            choice = FromToday;
            return true;
        }

        if (text is not null && text.StartsWith("days:", StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days >= 1)
        {
            choice = LastDays(days);
            return true;
        }

        return false;
    }
}

/// <summary>The kinds of <see cref="HistoryChoice"/>.</summary>
public enum HistoryChoiceKind
{
    /// <summary>Every session.</summary>
    All,

    /// <summary>The sessions with activity in the last days.</summary>
    Days,

    /// <summary>Nothing of the past.</summary>
    FromToday,
}

/// <summary>A session the history could not read.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Reason">Why, in a few words.</param>
public sealed record SkippedSession(string SessionId, string Reason);

/// <summary>
/// A snapshot of the reading of the history: what the bar of the page and <c>alta statistics status</c> show. It is immutable; the
/// engine publishes a new one each time something changes.
/// </summary>
public sealed record StatisticsStatus
{
    /// <summary>Gets where the reading stands.</summary>
    public HistoryState State { get; init; } = HistoryState.Starting;

    /// <summary>Gets what the reading is for: <c>first-read</c> (the first choice), <c>facts-improved</c> (a new version computes more),
    /// <c>extended</c> (the user asked to read more), <c>catch-up</c> (sessions changed while CodeAlta was closed or written by another application).</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the choice the user made, as text (<c>all</c>, <c>days:30</c>, <c>from-today</c>); null before the choice.</summary>
    public string? Choice { get; init; }

    /// <summary>Gets the first local day the statistics keep (<c>yyyymmdd</c>); null when the whole history is read.</summary>
    public int? FloorDay { get; init; }

    /// <summary>Gets the number of sessions the reading has to go through.</summary>
    public int SessionsTotal { get; init; }

    /// <summary>Gets the number of those sessions that are read.</summary>
    public int SessionsDone { get; init; }

    /// <summary>Gets the number of bytes of journal the reading has to go through.</summary>
    public long BytesTotal { get; init; }

    /// <summary>Gets the number of those bytes that are read.</summary>
    public long BytesDone { get; init; }

    /// <summary>Gets the oldest local day the reading has reached (<c>yyyymmdd</c>); null before the first session.</summary>
    public int? OldestDateReached { get; init; }

    /// <summary>
    /// Gets the local day from which the numbers are complete (<c>yyyymmdd</c>): the sessions are read from the most recent, so what
    /// is before this day may be missing. Null when everything is read.
    /// </summary>
    public int? CompleteFromDay { get; init; }

    /// <summary>Gets the reading speed in bytes per second, since the reading began; null before it has a measure.</summary>
    public double? BytesPerSecond { get; init; }

    /// <summary>Gets the time left in seconds, from the speed so far; null when it cannot be told.</summary>
    public double? EtaSeconds { get; init; }

    /// <summary>Gets the session being read.</summary>
    public string? CurrentSessionId { get; init; }

    /// <summary>Gets the number of sessions that could not be read.</summary>
    public int SkippedCount { get; init; }

    /// <summary>Gets the first sessions that could not be read, with the reason; at most fifty.</summary>
    public IReadOnlyList<SkippedSession> Skipped { get; init; } = [];

    /// <summary>Gets the number of sessions the flow still has to catch up.</summary>
    public int PendingFlow { get; init; }

    /// <summary>Gets the number of changes to the numbers so far; it grows each time a catch-up is saved.</summary>
    public long Revision { get; init; }

    /// <summary>Gets the reason the engine failed to start, when <see cref="State"/> is <see cref="HistoryState.Failed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>Gets a value indicating whether the numbers are complete for every day the statistics keep.</summary>
    public bool IsComplete => State is HistoryState.Done && CompleteFromDay is null;
}

/// <summary>What changed when a catch-up was saved, for the pages that show the numbers.</summary>
/// <param name="Revision">The new revision.</param>
/// <param name="FromDay">The first local day that changed (<c>yyyymmdd</c>).</param>
/// <param name="ToDay">The last local day that changed.</param>
/// <param name="SessionIds">The sessions that changed.</param>
public sealed record StatisticsDataChange(long Revision, int FromDay, int ToDay, IReadOnlyList<string> SessionIds);
