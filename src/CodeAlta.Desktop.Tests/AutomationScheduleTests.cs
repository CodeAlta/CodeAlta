using CodeAlta.Desktop.Automations;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class AutomationScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static AutomationSchedule Schedule(AutomationTrigger trigger)
    {
        Assert.IsTrue(AutomationSchedule.TryCreate(trigger, out var schedule, out var error), error);
        return schedule!;
    }

    private static AutomationSchedule Cron(string expression) => Schedule(new(AutomationTriggerKind.Cron) { Expression = expression });

    [TestMethod]
    public void Hourly_IsDueAtItsMinuteOfEveryFewHours()
    {
        var hourly = Schedule(new(AutomationTriggerKind.Hourly) { Minute = 15 });
        Assert.AreEqual(At(2026, 10, 6, 10, 15), hourly.Next(At(2026, 10, 6, 10, 0), Utc));
        Assert.AreEqual(At(2026, 10, 6, 11, 15), hourly.Next(At(2026, 10, 6, 10, 15), Utc), "The next time is later than the moment asked.");
        Assert.AreEqual(At(2026, 10, 7, 0, 15), hourly.Next(At(2026, 10, 6, 23, 30), Utc));

        // Every four hours counts from midnight: 0, 4, 8...
        var spaced = Schedule(new(AutomationTriggerKind.Hourly) { Minute = 0, Every = 4 });
        Assert.AreEqual(At(2026, 10, 6, 12, 0), spaced.Next(At(2026, 10, 6, 9, 30), Utc));
        Assert.AreEqual(At(2026, 10, 7, 0, 0), spaced.Next(At(2026, 10, 6, 20, 0), Utc));
    }

    [TestMethod]
    public void Daily_TakesTheNearestOfItsTimes()
    {
        var daily = Schedule(new(AutomationTriggerKind.Daily) { At = [new(9, 0), new(17, 30)] });
        Assert.AreEqual(At(2026, 10, 6, 9, 0), daily.Next(At(2026, 10, 6, 8, 59), Utc));
        Assert.AreEqual(At(2026, 10, 6, 17, 30), daily.Next(At(2026, 10, 6, 9, 0), Utc));
        Assert.AreEqual(At(2026, 10, 7, 9, 0), daily.Next(At(2026, 10, 6, 17, 30), Utc));
    }

    [TestMethod]
    public void Weekly_IsDueOnItsDaysOnly()
    {
        // The 6th of October 2026 is a Tuesday.
        var weekly = Schedule(new(AutomationTriggerKind.Weekly) { Days = [DayOfWeek.Monday, DayOfWeek.Thursday], At = [new(8, 30)] });
        Assert.AreEqual(At(2026, 10, 8, 8, 30), weekly.Next(At(2026, 10, 6, 12, 0), Utc));
        Assert.AreEqual(At(2026, 10, 12, 8, 30), weekly.Next(At(2026, 10, 8, 8, 30), Utc));
    }

    [TestMethod]
    public void Cron_ReadsListsRangesStepsAndNames()
    {
        Assert.AreEqual(At(2026, 10, 6, 10, 30), Cron("*/15 * * * *").Next(At(2026, 10, 6, 10, 16), Utc));
        Assert.AreEqual(At(2026, 10, 7, 9, 0), Cron("0 9 * * 1-5").Next(At(2026, 10, 6, 9, 0), Utc));
        Assert.AreEqual(At(2026, 10, 12, 9, 0), Cron("0 9 * * mon-fri").Next(At(2026, 10, 9, 9, 0), Utc), "Friday to Monday.");
        Assert.AreEqual(At(2026, 10, 11, 6, 5), Cron("5 6 * * 7").Next(At(2026, 10, 6, 0, 0), Utc), "Sunday is also 7.");
        Assert.AreEqual(At(2027, 1, 1, 0, 0), Cron("0 0 1 JAN *").Next(At(2026, 10, 6, 0, 0), Utc));
        Assert.AreEqual(At(2026, 10, 6, 14, 20), Cron("20,50 8-14/3 * * *").Next(At(2026, 10, 6, 11, 50), Utc));
        Assert.AreEqual(At(2028, 2, 29, 12, 0), Cron("0 12 29 2 *").Next(At(2026, 10, 6, 0, 0), Utc), "A leap day.");
        // Two restricted day fields: either is enough, as cron reads them.
        Assert.AreEqual(At(2026, 10, 9, 0, 0), Cron("0 0 13 * fri").Next(At(2026, 10, 6, 0, 0), Utc));
        Assert.AreEqual(At(2026, 10, 13, 0, 0), Cron("0 0 13 * fri").Next(At(2026, 10, 9, 0, 0), Utc));
        Assert.IsNull(Cron("0 0 30 2 *").Next(At(2026, 10, 6, 0, 0), Utc), "A day that never comes.");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("* * * *")]
    [DataRow("* * * * * *")]
    [DataRow("60 * * * *")]
    [DataRow("* 24 * * *")]
    [DataRow("* * 0 * *")]
    [DataRow("* * * 13 *")]
    [DataRow("* * * * 8")]
    [DataRow("*/0 * * * *")]
    [DataRow("5-1 * * * *")]
    [DataRow("a * * * *")]
    [DataRow("1,,2 * * * *")]
    public void Cron_RefusesWhatItCannotRead(string expression)
    {
        Assert.IsFalse(AutomationSchedule.TryCreate(new(AutomationTriggerKind.Cron) { Expression = expression }, out var schedule, out var error));
        Assert.IsNull(schedule);
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    public void Triggers_WithoutWhatTheyNeedAreRefused_AndEventsHaveNoSchedule()
    {
        Assert.IsFalse(AutomationSchedule.TryCreate(new(AutomationTriggerKind.Daily), out _, out var daily));
        Assert.IsNotNull(daily);
        Assert.IsFalse(AutomationSchedule.TryCreate(new(AutomationTriggerKind.Weekly) { At = [new(9, 0)] }, out _, out var weekly));
        Assert.IsNotNull(weekly);
        Assert.IsFalse(AutomationSchedule.TryCreate(new(AutomationTriggerKind.Hourly) { Minute = 60 }, out _, out var hourly));
        Assert.IsNotNull(hourly);
        Assert.IsFalse(AutomationSchedule.TryCreate(new(AutomationTriggerKind.Hourly) { Every = 13 }, out _, out _));
        Assert.IsFalse(AutomationSchedule.TryCreate(new(AutomationTriggerKind.Issue), out var none, out var error));
        Assert.IsNull(none);
        Assert.IsNull(error, "An event is not a schedule, and not a wrong one.");
    }

    [TestMethod]
    public void Schedule_FollowsTheClockOfItsZoneAcrossASavingTimeChange()
    {
        // Central Europe: clocks go from 02:00 to 03:00 on 2026-03-29 and from 03:00 back to 02:00 on 2026-10-25.
        var paris = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Paris");
        var daily = Schedule(new(AutomationTriggerKind.Daily) { At = [new(9, 0)] });
        Assert.AreEqual(new DateTimeOffset(2026, 3, 28, 9, 0, 0, TimeSpan.FromHours(1)), daily.Next(At(2026, 3, 27, 12, 0), paris));
        Assert.AreEqual(new DateTimeOffset(2026, 3, 29, 9, 0, 0, TimeSpan.FromHours(2)), daily.Next(At(2026, 3, 28, 12, 0), paris), "09:00 on the clock, an hour earlier in the world.");

        // A time the clock skips is due when it would have been reached; a time it shows twice is due once.
        var skipped = Schedule(new(AutomationTriggerKind.Daily) { At = [new(2, 30)] });
        Assert.AreEqual(At(2026, 3, 29, 1, 30), skipped.Next(At(2026, 3, 28, 12, 0), paris)!.Value.ToUniversalTime());
        var twice = skipped.Next(At(2026, 10, 24, 12, 0), paris)!.Value;
        Assert.AreEqual(At(2026, 10, 25, 0, 30), twice.ToUniversalTime());
        Assert.AreEqual(new DateTimeOffset(2026, 10, 26, 2, 30, 0, TimeSpan.FromHours(1)), skipped.Next(twice, paris), "The second 02:30 of that night is not another run.");
    }
}
