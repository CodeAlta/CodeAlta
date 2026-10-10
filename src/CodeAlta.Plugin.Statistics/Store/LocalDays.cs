using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Store;

/// <summary>
/// Maps quarter hours (UTC) to local days of a time zone, with the rules the zone had on each date. A day is the number
/// <c>yyyymmdd</c>, a month <c>yyyymm</c> and a year <c>yyyy</c>, so that months and years are read off a day by integer division.
/// </summary>
/// <remarks>
/// A quarter hour belongs to the local day its start falls in. Every offset in use is a whole number of quarter hours, so a
/// quarter never spans two local days; the day of a quarter is nevertheless computed from the zone, never from a fixed offset,
/// so that a daylight-saving change or a zone whose offset changed in the past gives the right day.
/// </remarks>
internal sealed class LocalDays
{
    private const long QuarterMs = QuarterHour.Milliseconds;

    /// <summary>Initializes the mapping for a time zone.</summary>
    /// <param name="timeZone">The time zone of the user.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeZone"/> is null.</exception>
    public LocalDays(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        TimeZone = timeZone;
    }

    /// <summary>Gets the time zone.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>Gets the local day a quarter hour belongs to.</summary>
    /// <param name="quarter">The quarter hour index.</param>
    /// <returns>The day as <c>yyyymmdd</c>.</returns>
    public int DayOf(int quarter) => ToDay(LocalDateOf(quarter));

    /// <summary>Gets the local date a quarter hour belongs to.</summary>
    /// <param name="quarter">The quarter hour index.</param>
    /// <returns>The date.</returns>
    public DateOnly LocalDateOf(int quarter)
    {
        var utc = DateTime.UnixEpoch.AddMilliseconds(quarter * QuarterMs);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZone));
    }

    /// <summary>Gets the local date and time of a quarter hour.</summary>
    /// <param name="quarter">The quarter hour index.</param>
    /// <returns>The local date and time of its start.</returns>
    public DateTime LocalTimeOf(int quarter)
    {
        var utc = DateTime.UnixEpoch.AddMilliseconds(quarter * QuarterMs);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZone);
    }

    /// <summary>Gets the local day of a moment.</summary>
    /// <param name="time">The moment.</param>
    /// <returns>The day as <c>yyyymmdd</c>.</returns>
    public int DayOf(DateTimeOffset time)
        => ToDay(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(time.UtcDateTime, TimeZone)));

    /// <summary>Gets the first quarter hour of a local date; the first one that exists when midnight does not.</summary>
    /// <param name="date">The date.</param>
    /// <returns>The quarter hour index.</returns>
    public int FirstQuarterOf(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (TimeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(local, TimeZone);
        return (int)Math.Floor((utc - DateTime.UnixEpoch).TotalMilliseconds / QuarterMs);
    }

    /// <summary>
    /// Gets the quarter hours of a local day as intervals. One interval in every zone but those that set their clocks back
    /// across midnight, which give two.
    /// </summary>
    /// <param name="day">The day as <c>yyyymmdd</c>.</param>
    /// <returns>The intervals, in order; the end of each is excluded.</returns>
    public IReadOnlyList<(int From, int To)> QuarterIntervalsOf(int day)
    {
        var date = ToDate(day);
        var anchor = (int)Math.Floor((date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalMilliseconds / QuarterMs);
        // A local day lies within 14 hours before and 12 hours after the same day in UTC.
        var from = anchor - (14 * 4) - 4;
        var to = anchor + (24 * 4) + (12 * 4) + 4;
        var intervals = new List<(int, int)>(1);
        var start = -1;
        for (var quarter = from; quarter < to; quarter++)
        {
            if (DayOf(quarter) == day)
            {
                if (start < 0)
                {
                    start = quarter;
                }
            }
            else if (start >= 0)
            {
                intervals.Add((start, quarter));
                start = -1;
            }
        }

        if (start >= 0)
        {
            intervals.Add((start, to));
        }

        return intervals;
    }

    /// <summary>Gets the quarter hour that starts a local day, and the one that starts the next, as a single range.</summary>
    /// <param name="firstDay">The first day, as <c>yyyymmdd</c>.</param>
    /// <param name="lastDay">The last day, included.</param>
    /// <returns>The first quarter of the first day and the first quarter after the last day.</returns>
    public (int From, int To) QuarterRangeOfDays(int firstDay, int lastDay)
    {
        var first = QuarterIntervalsOf(firstDay);
        var last = QuarterIntervalsOf(lastDay);
        return (first.Count > 0 ? first[0].From : FirstQuarterOf(ToDate(firstDay)), last.Count > 0 ? last[^1].To : FirstQuarterOf(ToDate(lastDay).AddDays(1)));
    }

    /// <summary>Converts a date to a day number.</summary>
    /// <param name="date">The date.</param>
    /// <returns><c>yyyymmdd</c>.</returns>
    public static int ToDay(DateOnly date) => (date.Year * 10000) + (date.Month * 100) + date.Day;

    /// <summary>Converts a day number to a date.</summary>
    /// <param name="day">The day as <c>yyyymmdd</c>.</param>
    /// <returns>The date.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="day"/> is not a valid day.</exception>
    public static DateOnly ToDate(int day)
    {
        var year = day / 10000;
        var month = day / 100 % 100;
        var dayOfMonth = day % 100;
        if (year < 1 || year > 9999 || month is < 1 or > 12 || dayOfMonth < 1 || dayOfMonth > DateTime.DaysInMonth(year, month))
        {
            throw new ArgumentOutOfRangeException(nameof(day), day, "Not a day in the form yyyymmdd.");
        }

        return new DateOnly(year, month, dayOfMonth);
    }

    /// <summary>Gets the month a day belongs to.</summary>
    /// <param name="day">The day as <c>yyyymmdd</c>.</param>
    /// <returns><c>yyyymm</c>.</returns>
    public static int MonthOf(int day) => day / 100;

    /// <summary>Gets the year a day belongs to.</summary>
    /// <param name="day">The day as <c>yyyymmdd</c>.</param>
    /// <returns><c>yyyy</c>.</returns>
    public static int YearOf(int day) => day / 10000;
}
