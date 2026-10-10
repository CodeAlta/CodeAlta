using System.Globalization;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Query;

/// <summary>One bucket of a plan.</summary>
/// <param name="Index">The position of the bucket.</param>
/// <param name="Start">The local start of the bucket.</param>
/// <param name="Label">A short label.</param>
internal sealed record BucketSpec(int Index, DateTime Start, string Label);

/// <summary>
/// The buckets a period is cut into, and which bucket a local day, month, year or quarter hour belongs to. Every bucket is a run of
/// whole local days, or a local hour: a month is never cut by a week.
/// </summary>
internal sealed class BucketPlan
{
    /// <summary>The most buckets a plan has; a longer period needs a coarser frequency.</summary>
    public const int MaxBuckets = 5000;

    private readonly LocalDays _days;
    private readonly DateOnly _firstWeekStart;
    private readonly int _fromMonthIndex;
    private readonly int _fromYear;

    private BucketPlan(StatisticsFrequency frequency, bool isSingle, DayRange range, DayOfWeek weekStart, LocalDays days, IReadOnlyList<BucketSpec> buckets, bool isWeekHour = false)
    {
        Frequency = frequency;
        IsSingle = isSingle;
        IsWeekHour = isWeekHour;
        Range = range;
        WeekStart = weekStart;
        Buckets = buckets;
        _days = days;
        _firstWeekStart = range.From.AddDays(-(((int)range.From.DayOfWeek - (int)weekStart + 7) % 7));
        _fromMonthIndex = (range.From.Year * 12) + range.From.Month - 1;
        _fromYear = range.From.Year;
    }

    /// <summary>Gets the frequency; never <see cref="StatisticsFrequency.Auto"/>.</summary>
    public StatisticsFrequency Frequency { get; }

    /// <summary>Gets a value indicating whether the plan has one bucket for the whole period.</summary>
    public bool IsSingle { get; }

    /// <summary>Gets a value indicating whether the buckets are the 168 hours of a week, not dates.</summary>
    public bool IsWeekHour { get; }

    /// <summary>Gets the days of the period.</summary>
    public DayRange Range { get; }

    /// <summary>Gets the first day of the week.</summary>
    public DayOfWeek WeekStart { get; }

    /// <summary>Gets the buckets.</summary>
    public IReadOnlyList<BucketSpec> Buckets { get; }

    /// <summary>Gets the name of the frequency as the results say it.</summary>
    public string FrequencyName => IsSingle ? "all" : IsWeekHour ? "weekhour" : Frequency.ToString().ToLowerInvariant();

    /// <summary>Picks the frequency of a period when the request leaves it to the application.</summary>
    /// <param name="range">The period.</param>
    /// <returns>Hours for one day, days up to 90 days, weeks up to a year, months beyond.</returns>
    public static StatisticsFrequency AutoFrequency(DayRange range) => range.Days switch
    {
        <= 1 => StatisticsFrequency.Hour,
        <= 90 => StatisticsFrequency.Day,
        <= 366 => StatisticsFrequency.Week,
        _ => StatisticsFrequency.Month,
    };

    /// <summary>Cuts a period into buckets.</summary>
    /// <param name="range">The period.</param>
    /// <param name="requested">The frequency asked for.</param>
    /// <param name="weekStart">The first day of the week.</param>
    /// <param name="days">The local days of the time zone.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentException">The frequency would make too many buckets.</exception>
    public static BucketPlan Create(DayRange range, StatisticsFrequency requested, DayOfWeek weekStart, LocalDays days)
    {
        var frequency = requested == StatisticsFrequency.Auto ? AutoFrequency(range) : requested;
        var buckets = new List<BucketSpec>();
        switch (frequency)
        {
            case StatisticsFrequency.Hour:
                CheckCount(range.Days * 24L, frequency);
                for (var day = range.From; day <= range.To; day = day.AddDays(1))
                {
                    for (var hour = 0; hour < 24; hour++)
                    {
                        var start = day.ToDateTime(new TimeOnly(hour, 0));
                        buckets.Add(new BucketSpec(buckets.Count, start, start.ToString("HH:00", CultureInfo.InvariantCulture)));
                    }
                }

                break;
            case StatisticsFrequency.Day:
                CheckCount(range.Days, frequency);
                for (var day = range.From; day <= range.To; day = day.AddDays(1))
                {
                    buckets.Add(new BucketSpec(buckets.Count, day.ToDateTime(TimeOnly.MinValue), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                }

                break;
            case StatisticsFrequency.Week:
                var first = range.From.AddDays(-(((int)range.From.DayOfWeek - (int)weekStart + 7) % 7));
                CheckCount(((range.To.DayNumber - first.DayNumber) / 7) + 1, frequency);
                for (var start = first; start <= range.To; start = start.AddDays(7))
                {
                    buckets.Add(new BucketSpec(buckets.Count, start.ToDateTime(TimeOnly.MinValue), start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                }

                break;
            case StatisticsFrequency.Month:
                for (var month = new DateOnly(range.From.Year, range.From.Month, 1); month <= range.To; month = month.AddMonths(1))
                {
                    buckets.Add(new BucketSpec(buckets.Count, month.ToDateTime(TimeOnly.MinValue), month.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
                    CheckCount(buckets.Count, frequency);
                }

                break;
            default:
                for (var year = range.From.Year; year <= range.To.Year; year++)
                {
                    buckets.Add(new BucketSpec(buckets.Count, new DateTime(year, 1, 1), year.ToString(CultureInfo.InvariantCulture)));
                }

                break;
        }

        return new BucketPlan(frequency, false, range, weekStart, days, buckets);
    }

    /// <summary>Gets a plan with one bucket for the whole period, for totals and rankings.</summary>
    /// <param name="range">The period.</param>
    /// <param name="weekStart">The first day of the week.</param>
    /// <param name="days">The local days of the time zone.</param>
    /// <returns>The plan.</returns>
    public static BucketPlan Single(DayRange range, DayOfWeek weekStart, LocalDays days)
        => new(StatisticsFrequency.Day, true, range, weekStart, days, [new BucketSpec(0, range.From.ToDateTime(TimeOnly.MinValue), range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]);

    /// <summary>Gets a plan whose 168 buckets are the hours of a week: bucket <c>7 * d + h</c> is hour <c>h</c> of the day <c>d</c> after the first day of the week.</summary>
    /// <param name="range">The period.</param>
    /// <param name="weekStart">The first day of the week.</param>
    /// <param name="days">The local days of the time zone.</param>
    /// <returns>The plan.</returns>
    public static BucketPlan WeekHour(DayRange range, DayOfWeek weekStart, LocalDays days)
    {
        var buckets = new List<BucketSpec>(168);
        for (var day = 0; day < 7; day++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                buckets.Add(new BucketSpec(buckets.Count, DateTime.MinValue.AddHours(hour), $"{(DayOfWeek)(((int)weekStart + day) % 7)} {hour:00}:00"));
            }
        }

        return new BucketPlan(StatisticsFrequency.Hour, false, range, weekStart, days, buckets, isWeekHour: true);
    }

    /// <summary>Gets the bucket of a local day.</summary>
    /// <param name="day">The day as <c>yyyymmdd</c>.</param>
    /// <returns>The index; -1 when the day is outside the period.</returns>
    public int OfDay(int day)
    {
        DateOnly date;
        try
        {
            date = LocalDays.ToDate(day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return -1;
        }

        if (date < Range.From || date > Range.To)
        {
            return -1;
        }

        if (IsSingle)
        {
            return 0;
        }

        return Frequency switch
        {
            StatisticsFrequency.Day => date.DayNumber - Range.From.DayNumber,
            StatisticsFrequency.Week => (date.DayNumber - _firstWeekStart.DayNumber) / 7,
            StatisticsFrequency.Month => (date.Year * 12) + date.Month - 1 - _fromMonthIndex,
            StatisticsFrequency.Year => date.Year - _fromYear,
            _ => -1,
        };
    }

    /// <summary>Gets the bucket of a month that is wholly inside the period.</summary>
    /// <param name="month">The month as <c>yyyymm</c>.</param>
    /// <returns>The index; -1 when the month is not a bucket of the plan.</returns>
    public int OfMonth(int month)
    {
        if (IsSingle)
        {
            return 0;
        }

        var index = ((month / 100) * 12) + (month % 100) - 1 - _fromMonthIndex;
        return Frequency switch
        {
            StatisticsFrequency.Month => index >= 0 && index < Buckets.Count ? index : -1,
            StatisticsFrequency.Year => month / 100 - _fromYear is var year && year >= 0 && year < Buckets.Count ? year : -1,
            _ => -1,
        };
    }

    /// <summary>Gets the bucket of a year that is wholly inside the period.</summary>
    /// <param name="year">The year.</param>
    /// <returns>The index; -1 when the year is not a bucket of the plan.</returns>
    public int OfYear(int year)
        => IsSingle ? 0 : Frequency == StatisticsFrequency.Year && year - _fromYear is var index && index >= 0 && index < Buckets.Count ? index : -1;

    /// <summary>Gets the bucket of a quarter hour.</summary>
    /// <param name="quarter">The quarter hour index.</param>
    /// <returns>The index; -1 when the quarter is outside the period.</returns>
    public int OfQuarter(int quarter)
    {
        var local = _days.LocalTimeOf(quarter);
        if (IsWeekHour)
        {
            var date = DateOnly.FromDateTime(local);
            return date < Range.From || date > Range.To ? -1 : (((int)local.DayOfWeek - (int)WeekStart + 7) % 7 * 24) + local.Hour;
        }

        if (Frequency == StatisticsFrequency.Hour && !IsSingle)
        {
            var date = DateOnly.FromDateTime(local);
            if (date < Range.From || date > Range.To)
            {
                return -1;
            }

            return ((date.DayNumber - Range.From.DayNumber) * 24) + local.Hour;
        }

        return OfDay(LocalDays.ToDay(DateOnly.FromDateTime(local)));
    }

    private static void CheckCount(long count, StatisticsFrequency frequency)
    {
        if (count > MaxBuckets)
        {
            throw new ArgumentException($"A {frequency.ToString().ToLowerInvariant()} frequency over this period makes {count} buckets (at most {MaxBuckets}): choose a coarser frequency.");
        }
    }
}
