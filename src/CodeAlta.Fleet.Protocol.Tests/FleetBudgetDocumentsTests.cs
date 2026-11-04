using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Protocol.Tests;

/// <summary>
/// The signed budgets as documents (<c>doc/fleet/observability.md</c>, budgets and alerts): what reads, the periods in the time
/// zone of a budget, the scope a run is in, and what a statistics row counts.
/// </summary>
[TestClass]
public sealed class FleetBudgetDocumentsTests
{
    [TestMethod]
    public void ABudget_ReadsBack_AndOneThatDoesNotHoldIsRefused()
    {
        var budget = Budget();
        Assert.IsTrue(FleetBudgetDocuments.TryRead(FleetBudgetDocuments.Serialize(budget), out var read, out var reason), reason);
        Assert.AreEqual(budget, read);

        foreach (var (wrong, why) in new (FleetBudgetDefinition, string)[]
        {
            (budget with { Limit = 0 }, "a limit from 1"),
            (budget with { Measure = "requests" }, "tokens or a cost"),
            (budget with { Period = "year" }, "per day, week or month"),
            (budget with { Mode = "strict" }, "advisory or enforced"),
            (budget with { ScopeKind = FleetBudgetScopeKinds.Machine, ScopeId = null }, "a machine names its id"),
            (budget with { ScopeId = "someone" }, "the person names no id"),
            (budget with { TimeZone = "Mars/Olympus" }, "a time zone of the machine"),
            (budget with { Name = " padded " }, "a name without spaces around"),
        })
        {
            Assert.IsFalse(FleetBudgetDocuments.TryRead(FleetBudgetDocuments.Serialize(wrong), out _, out _), why);
        }

        // A Continue anyway is valid a day at most after it is signed.
        var time = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var decision = new FleetBudgetContinue(1, "org", "hold", "budget", 1, 7, "machine", "run", "nonce", "person", "signer", time, time.AddHours(1));
        Assert.IsTrue(FleetBudgetDocuments.TryReadContinue(FleetBudgetDocuments.Serialize(decision), out _, out _));
        Assert.IsFalse(FleetBudgetDocuments.TryReadContinue(FleetBudgetDocuments.Serialize(decision with { Expires = time.AddDays(2) }), out _, out _));
        Assert.IsFalse(FleetBudgetDocuments.GetSignedBytes("{}").AsSpan().SequenceEqual(FleetBudgetDocuments.GetContinueSignedBytes("{}")), "a budget never reads as a Continue anyway");
    }

    [TestMethod]
    public void APeriod_StartsAtMidnightOfItsZone_TheWeekOnMonday()
    {
        var now = new DateTimeOffset(2026, 11, 4, 22, 30, 0, TimeSpan.Zero); // A Wednesday.
        var day = FleetBudgetDocuments.PeriodOf(FleetBudgetPeriods.Day, "UTC", now);
        Assert.AreEqual((new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero)), (day.Start, day.End));
        Assert.AreEqual(96, day.EndQuarter - day.StartQuarter, "a day of UTC is 96 quarter hours");

        var week = FleetBudgetDocuments.PeriodOf(FleetBudgetPeriods.Week, "UTC", now);
        Assert.AreEqual((DayOfWeek.Monday, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)), (week.Start.DayOfWeek, week.Start));
        Assert.AreEqual(week.Start.AddDays(7), week.End);

        var month = FleetBudgetDocuments.PeriodOf(FleetBudgetPeriods.Month, "UTC", now);
        Assert.AreEqual((new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)), (month.Start, month.End));

        // In Tokyo it is already Thursday: the day of the budget is the local one.
        var tokyo = FleetBudgetDocuments.PeriodOf(FleetBudgetPeriods.Day, "Asia/Tokyo", now);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.FromHours(9)), tokyo.Start);

        // A day that loses an hour to daylight saving time is 92 quarter hours long.
        var spring = FleetBudgetDocuments.PeriodOf(FleetBudgetPeriods.Day, "Europe/Paris", new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero));
        Assert.AreEqual(92, spring.EndQuarter - spring.StartQuarter);
        Assert.ThrowsExactly<ArgumentException>(() => FleetBudgetDocuments.PeriodOf("year", "UTC", now));
    }

    [TestMethod]
    public void ABudget_CoversTheRunsOfItsScope_AndCountsItsMeasure()
    {
        var run = new FleetBudgetRunScope("machine-1", "person-1", "space-key", "project-key");
        Assert.IsTrue(FleetBudgetDocuments.Covers(Budget(), run), "the person's budget covers every machine of the person");
        Assert.IsFalse(FleetBudgetDocuments.Covers(Budget() with { PersonId = "person-2" }, run));
        Assert.IsTrue(FleetBudgetDocuments.Covers(Budget() with { ScopeKind = FleetBudgetScopeKinds.Machine, ScopeId = "machine-1" }, run));
        Assert.IsFalse(FleetBudgetDocuments.Covers(Budget() with { ScopeKind = FleetBudgetScopeKinds.Machine, ScopeId = "machine-2" }, run));
        Assert.IsTrue(FleetBudgetDocuments.Covers(Budget() with { ScopeKind = FleetBudgetScopeKinds.Space, ScopeId = "space-key" }, run));
        Assert.IsTrue(FleetBudgetDocuments.Covers(Budget() with { ScopeKind = FleetBudgetScopeKinds.Project, ScopeId = "project-key" }, run));
        Assert.IsFalse(FleetBudgetDocuments.Covers(Budget() with { ScopeKind = FleetBudgetScopeKinds.Project, ScopeId = "project-key" }, run with { ProjectKey = null }),
            "a chat is in no project");
        Assert.IsFalse(FleetBudgetDocuments.Covers(Budget() with { ScopeKind = FleetBudgetScopeKinds.Team, ScopeId = "team" }, run), "teams come with their roles");
        Assert.IsFalse(FleetBudgetDocuments.Covers(Budget() with { Removed = true }, run));

        var usage = new FleetStatisticsRow(FleetStatisticsFamilies.Usage, 1, "p", "m", null, new Dictionary<string, long> { ["input_tokens"] = 1200, ["output_tokens"] = 300, ["requests"] = 2 });
        var cost = new FleetStatisticsRow(FleetStatisticsFamilies.Cost, 1, "p", "m", ["usd"], new Dictionary<string, long> { ["total_micro"] = 45_000 });
        var credits = cost with { Keys = ["ai_credits"] };
        Assert.AreEqual((1500L, 0L), (FleetBudgetDocuments.Count(FleetBudgetMeasures.Tokens, usage), FleetBudgetDocuments.Count(FleetBudgetMeasures.Tokens, cost)));
        Assert.AreEqual((45_000L, 0L, 0L), (FleetBudgetDocuments.Count(FleetBudgetMeasures.Cost, cost), FleetBudgetDocuments.Count(FleetBudgetMeasures.Cost, credits),
            FleetBudgetDocuments.Count(FleetBudgetMeasures.Cost, usage)), "units never mix: AI credits are not dollars");
        Assert.AreEqual((1L, 10L), (FleetBudgetDocuments.RunReserve(Budget() with { Limit = 50 }), FleetBudgetDocuments.RunReserve(Budget() with { Limit = 1000 })));
    }

    private static FleetBudgetDefinition Budget() => new(FleetBudgetDefinition.CurrentVersion, "org", "person-1", "budget-1", "Monthly tokens", FleetBudgetScopeKinds.Person, null,
        FleetBudgetMeasures.Tokens, 1_000_000, FleetBudgetPeriods.Month, "UTC", FleetBudgetModes.Enforced, FleetBudgetReachedActions.Hold, 1, false, "signer",
        DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
}
