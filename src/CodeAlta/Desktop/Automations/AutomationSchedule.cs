using System.Globalization;

namespace CodeAlta.Desktop.Automations;

/// <summary>
/// When a trigger is due: a set of minutes, hours, days and months on the clock of the machine. A cron
/// expression is one such set; the hourly, daily and weekly triggers are one set per time of day.
/// </summary>
internal sealed class AutomationSchedule
{
    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private readonly Rule[] _rules;

    private AutomationSchedule(Rule[] rules) => _rules = rules;

    /// <summary>Builds the schedule of a trigger.</summary>
    /// <param name="trigger">The trigger.</param>
    /// <param name="schedule">The schedule; null for an event trigger or an invalid one.</param>
    /// <param name="error">What is wrong with the trigger; null for an event trigger.</param>
    /// <returns>Whether the trigger is a valid schedule.</returns>
    internal static bool TryCreate(AutomationTrigger trigger, out AutomationSchedule? schedule, out string? error)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        (schedule, error) = (null, null);
        switch (trigger.Kind)
        {
            case AutomationTriggerKind.Hourly:
                if (trigger.Minute is < 0 or > 59) error = "The minute of an hourly trigger is between 0 and 59.";
                else if (trigger.Every is < 1 or > 12) error = "An hourly trigger runs every 1 to 12 hours.";
                else schedule = new([new(Bits(trigger.Minute), Every(24, trigger.Every), AllDays, AllMonths, AllWeekDays, false)]);
                break;
            case AutomationTriggerKind.Daily:
                if (trigger.At.Count == 0) error = "A daily trigger has at least one time.";
                else schedule = new([.. trigger.At.Select(static time => new Rule(Bits(time.Minute), Bits(time.Hour), AllDays, AllMonths, AllWeekDays, false))]);
                break;
            case AutomationTriggerKind.Weekly:
                if (trigger.Days.Count == 0) error = "A weekly trigger has at least one day.";
                else if (trigger.At.Count == 0) error = "A weekly trigger has at least one time.";
                else
                {
                    var days = trigger.Days.Aggregate(0UL, static (bits, day) => bits | 1UL << (int)day);
                    schedule = new([.. trigger.At.Select(time => new Rule(Bits(time.Minute), Bits(time.Hour), AllDays, AllMonths, days, false))]);
                }

                break;
            case AutomationTriggerKind.Cron:
                if (TryParseCron(trigger.Expression, out var rule, out error)) schedule = new([rule]);
                break;
        }

        return schedule is not null;
    }

    /// <summary>The first time the schedule is due after a moment.</summary>
    /// <param name="after">The moment; the result is later than it.</param>
    /// <param name="zone">The time zone whose clock the schedule reads.</param>
    /// <returns>The next time, or null when there is none in the next eight years (the 30th of February).</returns>
    internal DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTimeOffset? best = null;
        foreach (var rule in _rules)
        {
            if (Next(rule, after, zone) is { } next && (best is null || next < best)) best = next;
        }

        return best;
    }

    private static DateTimeOffset? Next(Rule rule, DateTimeOffset after, TimeZoneInfo zone)
    {
        // Walk the clock of the zone, a field at a time, from the minute that follows.
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var limit = local.AddYears(8);
        while (local < limit)
        {
            if ((rule.Months & 1UL << local.Month) == 0)
            {
                local = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
            }
            else if (!rule.MatchesDay(local))
            {
                local = local.Date.AddDays(1);
            }
            else if ((rule.Hours & 1UL << local.Hour) == 0)
            {
                local = local.Date.AddHours(local.Hour + 1);
            }
            else if ((rule.Minutes & 1UL << local.Minute) == 0)
            {
                local = local.AddMinutes(1);
            }
            else
            {
                // A time the clock skips when it moves forward is due at the moment it would have been reached; a
                // time it shows twice when it moves back is due the first time.
                var instant = zone.IsInvalidTime(local)
                    ? new DateTimeOffset(local, zone.GetUtcOffset(local.AddHours(-1)))
                    : new DateTimeOffset(local, zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local));
                if (instant > after) return instant;
                local = local.AddMinutes(1);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a cron expression of five fields: minute, hour, day of the month, month and day of the week. A field is
    /// <c>*</c>, a value, a range <c>a-b</c>, a list <c>a,b</c>, or any of them with a step <c>/n</c>; months and
    /// days also have their three-letter English names, and Sunday is 0 or 7. When both day fields are restricted, a
    /// day that matches either is due, as cron does.
    /// </summary>
    private static bool TryParseCron(string? expression, out Rule rule, out string? error)
    {
        rule = default;
        var fields = (expression ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "A cron expression has five fields: minute, hour, day of the month, month and day of the week.";
            return false;
        }

        if (!TryParseField(fields[0], 0, 59, null, "minute", out var minutes, out error)
            || !TryParseField(fields[1], 0, 23, null, "hour", out var hours, out error)
            || !TryParseField(fields[2], 1, 31, null, "day of the month", out var days, out error)
            || !TryParseField(fields[3], 1, 12, MonthNames, "month", out var months, out error)
            || !TryParseField(fields[4], 0, 7, AutomationTrigger.DayNames, "day of the week", out var weekDays, out error))
            return false;
        // Sunday is written 0 or 7.
        if ((weekDays & 1UL << 7) != 0) weekDays = weekDays & ~(1UL << 7) | 1UL;
        rule = new(minutes, hours, days, months, weekDays, !fields[2].StartsWith('*') && !fields[4].StartsWith('*'));
        return true;
    }

    private static bool TryParseField(string field, int minimum, int maximum, string[]? names, string what, out ulong bits, out string? error)
    {
        bits = 0;
        foreach (var part in field.Split(','))
        {
            var range = part;
            var step = 1;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                range = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1 || step > maximum)
                    return Fail(out error);
            }

            int first, last;
            if (range == "*")
            {
                (first, last) = (minimum, names == AutomationTrigger.DayNames ? 6 : maximum);
            }
            else
            {
                var dash = range.IndexOf('-');
                if (!TryParseValue(dash < 0 ? range : range[..dash], names, minimum, out first)) return Fail(out error);
                if (dash < 0) last = slash >= 0 ? maximum : first;
                else if (!TryParseValue(range[(dash + 1)..], names, minimum, out last)) return Fail(out error);
            }

            if (first < minimum || last > maximum || first > last) return Fail(out error);
            for (var value = first; value <= last; value += step) bits |= 1UL << value;
        }

        error = null;
        return bits != 0 || Fail(out error);

        bool Fail(out string? message)
        {
            message = $"'{field}' is not a {what} of a cron expression.";
            return false;
        }
    }

    private static bool TryParseValue(string text, string[]? names, int minimum, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return true;
        var index = names is null ? -1 : Array.IndexOf(names, text.ToLowerInvariant());
        // The names of the months start at 1, those of the days at 0.
        value = index + (names == MonthNames ? 1 : 0);
        return index >= 0 && value >= minimum;
    }

    private const ulong AllDays = 0xFFFFFFFEUL; // 1 to 31
    private const ulong AllMonths = 0x1FFEUL; // 1 to 12
    private const ulong AllWeekDays = 0x7FUL; // Sunday (0) to Saturday (6)

    private static ulong Bits(int value) => 1UL << value;

    private static ulong Every(int count, int step)
    {
        var bits = 0UL;
        for (var value = 0; value < count; value += step) bits |= 1UL << value;
        return bits;
    }

    // Either: a day of the month or a day of the week is enough, which is how cron reads two restricted day fields.
    private readonly record struct Rule(ulong Minutes, ulong Hours, ulong Days, ulong Months, ulong WeekDays, bool Either)
    {
        public bool MatchesDay(DateTime date)
        {
            var day = (Days & 1UL << date.Day) != 0;
            var weekDay = (WeekDays & 1UL << (int)date.DayOfWeek) != 0;
            return Either ? day || weekDay : day && weekDay;
        }
    }
}
