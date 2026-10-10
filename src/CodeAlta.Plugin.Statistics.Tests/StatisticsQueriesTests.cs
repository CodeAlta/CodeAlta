using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Plugin.Statistics.Tests;

[TestClass]
public sealed class StatisticsQueriesTests
{
    public static IEnumerable<object[]> ZonesAndFrequencies
    {
        get
        {
            foreach (var zone in new[] { "UTC", "Europe/Paris", "Asia/Kolkata", "America/St_Johns", "Australia/Lord_Howe", "Pacific/Kiritimati" })
            {
                foreach (var frequency in new[] { StatisticsFrequency.Day, StatisticsFrequency.Week, StatisticsFrequency.Month, StatisticsFrequency.Year })
                {
                    yield return new object[] { zone, frequency };
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(ZonesAndFrequencies))]
    public async Task Series_EqualANaiveRecomputationFromTheFacts(string zone, StatisticsFrequency frequency)
    {
        await using var harness = await QueryHarness.CreateAsync(zone);
        var from = new DateOnly(2026, 4, 3);
        var to = new DateOnly(2026, 9, 28);
        var request = new StatisticsRequest { Period = "2026-04-03..2026-09-28", Frequency = frequency, WeekStart = DayOfWeek.Monday };

        foreach (var (metric, values) in new (string, Func<FactBatch, IEnumerable<(QuarterHour, double)>>)[]
                 {
                     ("active-time", QueryHarness.ActiveMs), ("runs", QueryHarness.Runs), ("tokens", QueryHarness.Tokens), ("tool-calls", QueryHarness.ToolCalls), ("your-prompts", QueryHarness.YourPrompts),
                 })
        {
            var result = await harness.Queries.SeriesAsync(request, metric);

            var naive = harness.Naive(values, frequency, from, to, DayOfWeek.Monday);
            var line = result.Series.Single();
            CollectionAssert.AreEqual(QueryHarness.Expected(result.Buckets, naive, frequency), line.Values.ToArray(), $"{metric} {zone} {frequency}");
            Assert.AreEqual(naive.Values.Sum(), line.Total, 0.001, $"{metric} total");
            Assert.AreEqual(frequency.ToString().ToLowerInvariant(), result.Query.Frequency);
            Assert.IsTrue(result.Query.Coverage.Complete);
        }
    }

    [TestMethod]
    public async Task AMonthIsTheSumOfItsDays_AndOfItsQuarterHours_InTheTwoSources()
    {
        await using var harness = await QueryHarness.CreateAsync("Europe/Paris");
        // A project filter reads the quarter hours; without it the roll-ups are read.
        harness.Directory.Projects.AddRange(Enumerable.Range(0, 3).Select(index => new ProjectInfo("project-" + index, "p" + index, "Project " + index, [])));
        var rollups = new StatisticsRequest { Period = "2026-03-01..2026-10-31", Frequency = StatisticsFrequency.Month };
        var days = rollups with { Frequency = StatisticsFrequency.Day };
        var allProjects = rollups with { Filter = new StatisticsFilter { Space = "default" } };

        var byMonth = await harness.Queries.SeriesAsync(rollups, "tokens");
        var byDay = await harness.Queries.SeriesAsync(days, "tokens");
        var fromQuarters = await harness.Queries.SeriesAsync(allProjects, "tokens");

        Assert.AreEqual(byDay.Series.Single().Total, byMonth.Series.Single().Total, 0.001);
        CollectionAssert.AreEqual(byMonth.Series.Single().Values.ToArray(), fromQuarters.Series.Single().Values.ToArray());
        for (var month = 0; month < byMonth.Buckets.Count; month++)
        {
            var prefix = byMonth.Buckets[month].Start[..7];
            var sum = byDay.Buckets.Where(bucket => bucket.Start.StartsWith(prefix, StringComparison.Ordinal)).Sum(bucket => byDay.Series.Single().Values[bucket.Index]);
            Assert.AreEqual(sum, byMonth.Series.Single().Values[month], 0.001, prefix);
        }
    }

    [TestMethod]
    public async Task TheHours_OfADayThatTheClocksChange_AddUpToTheDay()
    {
        await using var harness = await QueryHarness.CreateAsync("Europe/Paris");
        foreach (var day in new[] { "2026-03-29", "2026-10-25", "2026-06-10" })
        {
            var hours = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = day + ".." + day, Frequency = StatisticsFrequency.Hour }, "active-time");
            var whole = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = day + ".." + day, Frequency = StatisticsFrequency.Day }, "active-time");

            Assert.AreEqual(24, hours.Buckets.Count);
            Assert.AreEqual(whole.Series.Single().Total, hours.Series.Single().Total, 0.001, day);
            var naive = harness.Naive(QueryHarness.ActiveMs, StatisticsFrequency.Hour, DateOnly.Parse(day), DateOnly.Parse(day), DayOfWeek.Monday);
            CollectionAssert.AreEqual(QueryHarness.Expected(hours.Buckets, naive, StatisticsFrequency.Hour), hours.Series.Single().Values.ToArray(), day);
        }
    }

    [TestMethod]
    public async Task ASeriesByGroup_AddsUpToTheSeriesWithoutOne()
    {
        await using var harness = await QueryHarness.CreateAsync("Asia/Kolkata");
        var request = new StatisticsRequest { Period = "2026-04-01..2026-09-30", Frequency = StatisticsFrequency.Month, Limit = 100 };

        var total = await harness.Queries.SeriesAsync(request, "requests");
        foreach (var group in new[] { "provider", "model", "effort", "project", "purpose" })
        {
            var cut = await harness.Queries.SeriesAsync(request, "requests", group);

            Assert.IsGreaterThan(1, cut.Series.Count, group);
            for (var bucket = 0; bucket < total.Buckets.Count; bucket++)
            {
                Assert.AreEqual(total.Series.Single().Values[bucket], cut.Series.Sum(line => line.Values[bucket]), 0.001, $"{group} {bucket}");
            }

            Assert.IsTrue(cut.Series.Zip(cut.Series.Skip(1)).All(static pair => pair.First.Total >= pair.Second.Total || pair.Second.Key == "other"), "The largest first.");
        }

        var tools = await harness.Queries.SeriesAsync(request, "tool-calls", "tool");
        Assert.AreEqual((await harness.Queries.SeriesAsync(request, "tool-calls")).Series.Single().Total, tools.Series.Sum(static line => line.Total), 0.001);
    }

    [TestMethod]
    public async Task TheLinesBeyondTheLimit_AreAddedUpAsOther()
    {
        await using var harness = await QueryHarness.CreateAsync();
        var request = new StatisticsRequest { Period = "2026-04-01..2026-09-30", Frequency = StatisticsFrequency.Month, Limit = 2 };

        var cut = await harness.Queries.SeriesAsync(request, "requests", "model");
        var total = await harness.Queries.SeriesAsync(request with { Limit = 100 }, "requests");

        Assert.HasCount(3, cut.Series);
        Assert.AreEqual("other", cut.Series[2].Key);
        Assert.AreEqual(total.Series.Single().Total, cut.Series.Sum(static line => line.Total), 0.001);
    }

    [TestMethod]
    public async Task ACost_IsCutByUnit_AndNeverAddedAcrossUnits()
    {
        await using var harness = await QueryHarness.CreateAsync("Europe/Paris");
        var request = new StatisticsRequest { Period = "2026-04-01..2026-09-30", Frequency = StatisticsFrequency.Month };

        var cost = await harness.Queries.SeriesAsync(request, "cost");

        Assert.AreEqual(2, cost.Series.Count);
        foreach (var line in cost.Series)
        {
            var naive = harness.Naive(batch => QueryHarness.Cost(batch, line.Key), StatisticsFrequency.Month, new DateOnly(2026, 4, 1), new DateOnly(2026, 9, 30), DayOfWeek.Monday);
            var expected = QueryHarness.Expected(cost.Buckets, naive, StatisticsFrequency.Month);
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.AreEqual(expected[index], line.Values[index], 1e-6, line.Key + " " + index);
            }
        }

        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.SeriesAsync(request, "cost", "model"));
        var summary = await harness.Queries.SummaryAsync(request);
        CollectionAssert.AreEquivalent(new[] { "AI credits", "usd" }, summary.Costs.Select(static tile => tile.Id).ToArray());
    }

    [TestMethod]
    public async Task TheSummary_HasTheTotalsOfTheSeries_AndTheChangeAgainstThePreviousPeriod()
    {
        await using var harness = await QueryHarness.CreateAsync("America/St_Johns");
        var request = new StatisticsRequest { Period = "2026-06-01..2026-08-31", Frequency = StatisticsFrequency.Week, WeekStart = DayOfWeek.Monday, Comparison = StatisticsComparison.PreviousPeriod };

        var summary = await harness.Queries.SummaryAsync(request);

        Assert.AreEqual("2026-03-01", summary.Query.CompareFrom);
        Assert.AreEqual("2026-05-31", summary.Query.CompareTo);
        var tokens = summary.Tiles.Single(static tile => tile.Id == "tokens");
        var current = harness.Naive(QueryHarness.Tokens, StatisticsFrequency.Week, new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31), DayOfWeek.Monday).Values.Sum();
        var before = harness.Naive(QueryHarness.Tokens, StatisticsFrequency.Week, new DateOnly(2026, 3, 1), new DateOnly(2026, 5, 31), DayOfWeek.Monday).Values.Sum();
        Assert.AreEqual(current, tokens.Value, 0.001);
        Assert.AreEqual(before, tokens.Previous!.Value, 0.001);
        Assert.AreEqual((current - before) / before, tokens.Change!.Value, 1e-9);
        Assert.AreEqual(tokens.Value, tokens.Spark.Sum(), 0.001);
        Assert.AreEqual(summary.Buckets.Count, tokens.Spark.Count);
        var runs = summary.Tiles.Single(static tile => tile.Id == "runs");
        Assert.AreEqual(harness.Naive(QueryHarness.Runs, StatisticsFrequency.Week, new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31), DayOfWeek.Monday).Values.Sum(), runs.Value, 0.001);
        var sessions = summary.Tiles.Single(static tile => tile.Id == "sessions");
        Assert.IsTrue(sessions.Value is > 0 and <= 8);
    }

    [TestMethod]
    public async Task TheSameDatesOneYearBefore_AreTheComparison()
    {
        await using var harness = await QueryHarness.CreateAsync();

        var series = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-06-01..2026-06-30", Frequency = StatisticsFrequency.Day, Comparison = StatisticsComparison.SamePeriodLastYear }, "tokens");

        Assert.AreEqual("2025-06-01", series.Query.CompareFrom);
        Assert.AreEqual(30, series.Series.Single().Previous!.Count);
        Assert.AreEqual(0d, series.Series.Single().PreviousTotal!.Value, "Nothing happened a year before.");
    }

    [TestMethod]
    public async Task AFilter_ThatTheFactsCannotHonor_IsNamedInTheResult()
    {
        await using var harness = await QueryHarness.CreateAsync();

        var result = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-04-01..2026-09-30", Filter = new StatisticsFilter { Origin = "agent", ToolKind = "shell", Model = "gpt-5-mini" } }, "tokens");

        CollectionAssert.AreEquivalent(new[] { "origin", "toolKind" }, result.Query.IgnoredFilters.ToArray());
        var filtered = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-04-01..2026-09-30", Frequency = StatisticsFrequency.Month, Filter = new StatisticsFilter { Model = "gpt-5-mini", Provider = "claude" } }, "tokens");
        var naive = harness.Naive(batch => batch.Usage.Where(static pair => pair.Key.Model == "gpt-5-mini" && pair.Key.Provider == "claude").Select(static pair => (pair.Key.Quarter, (double)(pair.Value.InputTokens + pair.Value.OutputTokens))), StatisticsFrequency.Month, new DateOnly(2026, 4, 1), new DateOnly(2026, 9, 30), DayOfWeek.Monday);
        CollectionAssert.AreEqual(QueryHarness.Expected(filtered.Buckets, naive, StatisticsFrequency.Month), filtered.Series.Single().Values.ToArray());
    }

    [TestMethod]
    public async Task AProjectAndASpace_ReadTheSessionsOfTheProjects_AndSaySoAboutSpaces()
    {
        await using var harness = await QueryHarness.CreateAsync("Europe/Paris");
        harness.Directory.Projects.AddRange(
        [
            new ProjectInfo("project-0", "alpha", "Alpha", ["work"]),
            new ProjectInfo("project-1", "beta", "Beta", ["work", "oss"]),
            new ProjectInfo("project-2", "gamma", "Gamma", ["oss"]),
        ]);
        harness.Directory.Spaces.AddRange([new SpaceInfo("work", "Work", false), new SpaceInfo("oss", "Open source", false)]);
        var period = "2026-04-01..2026-09-30";

        double Naive(params string[] projects) => harness.Naive(QueryHarness.Tokens, StatisticsFrequency.Year, new DateOnly(2026, 4, 1), new DateOnly(2026, 9, 30), DayOfWeek.Monday, batch => projects.Contains(batch.Session!.ProjectRef)).Values.Sum();

        var alpha = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = period, Filter = new StatisticsFilter { Project = "alpha" } }, "tokens");
        var work = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = period, Filter = new StatisticsFilter { Space = "Work" } }, "tokens");
        var both = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = period, Filter = new StatisticsFilter { Space = "oss", Project = "Beta" } }, "tokens");

        Assert.AreEqual(Naive("project-0"), alpha.Series.Single().Total, 0.001);
        Assert.AreEqual(Naive("project-0", "project-1"), work.Series.Single().Total, 0.001);
        Assert.AreEqual(Naive("project-1"), both.Series.Single().Total, 0.001);
        CollectionAssert.Contains(work.Query.Notes.ToArray(), "space-membership-is-current");
        Assert.IsEmpty(alpha.Query.Notes);
        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.SeriesAsync(new StatisticsRequest { Filter = new StatisticsFilter { Space = "nothing" } }, "tokens"));

        var byProject = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = period }, "tokens", "project");
        CollectionAssert.AreEquivalent(new[] { "Alpha", "Beta", "Gamma" }, byProject.Series.Select(static line => line.Label).ToArray());
    }

    [TestMethod]
    public async Task TheSessionsActive_AreCountedInTheFacts_NotAdded()
    {
        await using var harness = await QueryHarness.CreateAsync("Asia/Kathmandu");
        var request = new StatisticsRequest { Period = "2026-03-20..2026-11-10", Frequency = StatisticsFrequency.Month };

        var active = await harness.Queries.SeriesAsync(request, "sessions-active");
        var started = await harness.Queries.SeriesAsync(request, "sessions-started");

        var naive = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var batch in harness.Batches)
        {
            foreach (var (key, value) in batch.Activity.Where(static pair => pair.Value.ActiveMs > 0 || pair.Value.RunsStarted > 0))
            {
                naive.TryAdd(harness.Local(key.Quarter).ToString("yyyy-MM"), []);
                naive[harness.Local(key.Quarter).ToString("yyyy-MM")].Add(batch.SessionId);
            }
        }

        for (var index = 0; index < active.Buckets.Count; index++)
        {
            var expected = naive.TryGetValue(active.Buckets[index].Start[..7], out var sessions) ? sessions.Count : 0;
            Assert.AreEqual(expected, active.Series.Single().Values[index], active.Buckets[index].Label);
        }

        Assert.AreEqual(harness.Batches.Count, started.Series.Single().Total, "Every session started in the period.");
        var summary = await harness.Queries.SummaryAsync(request);
        Assert.AreEqual(harness.Batches.Count, summary.Tiles.Single(static tile => tile.Id == "sessions").Value);
    }

    [TestMethod]
    public async Task TheRankings_AreByTheMeasure_WithSharesAndLines()
    {
        await using var harness = await QueryHarness.CreateAsync("Europe/Paris");
        var request = new StatisticsRequest { Period = "2026-04-01..2026-09-30", Frequency = StatisticsFrequency.Month, Limit = 3 };

        var models = await harness.Queries.TopAsync(request, "models", "tokens");

        var expected = harness.Batches.SelectMany(static batch => batch.Usage.Select(static pair => (pair.Key.Quarter, pair.Key.Model, Tokens: pair.Value.InputTokens + pair.Value.OutputTokens)))
            .Where(item => { var date = DateOnly.FromDateTime(harness.Local(item.Quarter)); return date >= new DateOnly(2026, 4, 1) && date <= new DateOnly(2026, 9, 30); })
            .GroupBy(static item => item.Model).Select(static group => (Model: group.Key, Tokens: (double)group.Sum(static item => item.Tokens)))
            .OrderByDescending(static item => item.Tokens).ToList();
        Assert.AreEqual(3, models.Rows.Count);
        Assert.AreEqual(expected.Count, models.TotalRows);
        Assert.IsTrue(models.Truncated);
        CollectionAssert.AreEqual(expected.Take(3).Select(static item => item.Model).ToArray(), models.Rows.Select(static row => row.Key).ToArray());
        CollectionAssert.AreEqual(expected.Take(3).Select(static item => item.Tokens).ToArray(), models.Rows.Select(static row => row.Value).ToArray());
        Assert.AreEqual(expected[0].Tokens / expected.Sum(static item => item.Tokens), models.Rows[0].Share, 1e-9);
        Assert.AreEqual(models.Rows[0].Value, models.Rows[0].Spark.Sum(), 0.001);

        foreach (var (kind, by) in new[] { ("tools", "calls"), ("tools", "time"), ("projects", "time"), ("projects", "tokens"), ("sessions", "calls"), ("models", "time") })
        {
            var result = await harness.Queries.TopAsync(request, kind, by);
            Assert.IsNotEmpty(result.Rows, $"{kind} by {by}");
            Assert.IsTrue(result.Rows.Zip(result.Rows.Skip(1)).All(static pair => pair.First.Value >= pair.Second.Value), $"{kind} by {by} is sorted");
        }

        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.TopAsync(request, "colors", "tokens"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.TopAsync(request, "tools", "smell"));
    }

    [TestMethod]
    public async Task TheDistribution_GivesPercentilesWithinAStep()
    {
        await using var harness = await QueryHarness.CreateAsync();
        var batch = new FactBatch("hist");
        var quarter = new QuarterHour(SyntheticFacts.SpringStart + 400);
        // 100 tool calls of 100 ms, 100 of 1000 ms, 100 of 10000 ms, of one tool.
        foreach (var value in new[] { 100L, 1000L, 10000L })
        {
            for (var index = 0; index < 100; index++)
            {
                batch.Observe(quarter, HistogramMeasure.ToolDurationMs, "shell", value);
            }
        }

        await harness.Store.Store.ApplyAsync(new Store.ApplyRequest { SessionId = "hist", Batch = batch, Cursor = new JournalCursor(1, null, new SessionFactsState()) });
        var day = harness.Local(quarter).ToString("yyyy-MM-dd");

        var result = await harness.Queries.DistributionAsync(new StatisticsRequest { Period = day + ".." + day }, "tool-duration", "shell");

        Assert.AreEqual(300, result.Count);
        Assert.AreEqual(3, result.Steps.Count);
        Assert.IsTrue(result.P50 is >= 1000 / 1.2 and <= 1000 * 1.2, $"median {result.P50}");
        Assert.IsTrue(result.P90 is >= 10000 / 1.2 and <= 10000 * 1.2, $"p90 {result.P90}");
        var tools = await harness.Queries.ToolsAsync(new StatisticsRequest { Period = day + ".." + day });
        Assert.IsNotEmpty(tools.Rows);
        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.DistributionAsync(new StatisticsRequest(), "feelings"));
    }

    [TestMethod]
    public async Task TheCalendar_AndTheWeekByHour_AddUpToTheActiveTime()
    {
        await using var harness = await QueryHarness.CreateAsync("Asia/Kolkata");
        var request = new StatisticsRequest { Period = "2026-01-01..2026-12-31", WeekStart = DayOfWeek.Sunday };
        var total = (await harness.Queries.SeriesAsync(request with { Frequency = StatisticsFrequency.Year }, "active-time")).Series.Single().Total;

        var calendar = await harness.Queries.CalendarAsync(request);
        var week = await harness.Queries.WeekHourAsync(request);

        Assert.AreEqual(total, calendar.Days.Sum(static day => day.ActiveMs), 0.001);
        Assert.AreEqual(calendar.Days.Max(static day => day.ActiveMs), calendar.MaxActiveMs);
        Assert.AreEqual(total, week.ActiveMs.Sum(static row => row.Sum()), 0.001);
        Assert.AreEqual(7, week.ActiveMs.Count);
        Assert.IsTrue(week.ActiveMs.All(static row => row.Count == 24));
        Assert.AreEqual("Sunday", week.Weekdays[0]);

        var naive = new double[7, 24];
        foreach (var (key, value) in harness.Batches.SelectMany(static batch => batch.Activity))
        {
            var local = harness.Local(key.Quarter);
            naive[(int)local.DayOfWeek, local.Hour] += value.ActiveMs;
        }

        for (var day = 0; day < 7; day++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                Assert.AreEqual(naive[day, hour], week.ActiveMs[day][hour], 0.001, $"{day} {hour}");
            }
        }
    }

    [TestMethod]
    public async Task TheCoverage_SaysWhatIsNotReadYet()
    {
        await using var notChosen = await QueryHarness.CreateAsync(chosen: false);
        var pending = await notChosen.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-04-01..2026-09-30" }, "tokens");
        Assert.IsFalse(pending.Query.Coverage.Complete);
        Assert.AreEqual("needs-choice", pending.Query.Coverage.HistoryState);

        await using var partial = await QueryHarness.CreateAsync();
        await partial.Store.Store.SetMetaAsync(new Dictionary<string, string?> { ["history.done"] = null, ["history.complete_from_day"] = "20260701" });
        var before = await partial.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-04-01..2026-09-30" }, "tokens");
        var after = await partial.Queries.SeriesAsync(new StatisticsRequest { Period = "2026-07-01..2026-09-30" }, "tokens");
        Assert.IsFalse(before.Query.Coverage.Complete);
        Assert.AreEqual("reading", before.Query.Coverage.HistoryState);
        Assert.AreEqual("2026-07-01", before.Query.Coverage.CompleteFrom);
        Assert.IsTrue(after.Query.Coverage.Complete);
    }

    [TestMethod]
    public async Task ThePeriods_AreReadFromText()
    {
        await using var harness = await QueryHarness.CreateAsync();
        var today = new DateOnly(2026, 10, 9);

        async Task<(string From, string To, string Frequency)> Resolve(string period, StatisticsFrequency frequency = StatisticsFrequency.Auto)
        {
            var result = await harness.Queries.SeriesAsync(new StatisticsRequest { Period = period, Frequency = frequency, WeekStart = DayOfWeek.Monday }, "runs");
            return (result.Query.From, result.Query.To, result.Query.Frequency);
        }

        Assert.AreEqual(("2026-10-09", "2026-10-09", "hour"), await Resolve("today"));
        Assert.AreEqual(("2026-10-03", "2026-10-09", "day"), await Resolve("7d"));
        Assert.AreEqual(("2026-07-12", "2026-10-09", "day"), await Resolve("90d"));
        Assert.AreEqual(("2026-10-01", "2026-10-09", "day"), await Resolve("month"));
        Assert.AreEqual(("2026-09-01", "2026-09-30", "day"), await Resolve("last-month"));
        Assert.AreEqual(("2026-01-01", "2026-10-09", "week"), await Resolve("year"));
        Assert.AreEqual(("2026-10-05", "2026-10-09", "day"), await Resolve("week"));
        var first = harness.Batches.SelectMany(static batch => batch.Activity.Keys.Select(static key => key.Quarter)).Concat(harness.Batches.SelectMany(static batch => batch.Usage.Keys.Select(static key => key.Quarter)))
            .Concat(harness.Batches.SelectMany(static batch => batch.Content.Keys.Select(static key => key.Quarter))).Concat(harness.Batches.SelectMany(static batch => batch.Tools.Keys.Select(static key => key.Quarter)))
            .Min(static quarter => quarter.Index);
        Assert.AreEqual((harness.Local(new QuarterHour(first)).ToString("yyyy-MM-dd"), "2026-10-09", "week"), await Resolve("all"));
        Assert.AreEqual(("2026-05-01", "2026-10-09", "week"), await Resolve("2026-05-01.."));
        Assert.AreEqual(("2026-05-01", "2026-05-31", "month"), await Resolve("2026-05-01..2026-05-31", StatisticsFrequency.Month));
        Assert.AreEqual(today, new DateOnly(2026, 10, 9));
        foreach (var bad in new[] { "", "soon", "0d", "2026-05-01..2026-04-01", "2026-13-01..2026-14-01", "-3d" })
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.SeriesAsync(new StatisticsRequest { Period = bad }, "runs"));
        }

        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "2020-01-01..", Frequency = StatisticsFrequency.Hour }, "runs"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.SeriesAsync(new StatisticsRequest { Period = "7d" }, "colors"));
    }

    [TestMethod]
    public async Task TheSessionsTable_HasTheNumbersOfEachSession_AndKeepsTheDeletedOnes()
    {
        await using var harness = await QueryHarness.CreateAsync("UTC");
        await harness.Store.Store.MarkDeletedAsync(["session-1"]);
        var request = new StatisticsRequest { Period = "2026-01-01..2026-12-31", Limit = 100 };

        var result = await harness.Queries.SessionsAsync(request, "tokens");

        Assert.AreEqual(8, result.TotalRows);
        var second = result.Rows.Single(static row => row.SessionId == "session-1");
        Assert.IsTrue(second.Deleted);
        Assert.AreEqual(1, result.Rows.Single(static row => row.SessionId == "session-1").SubAgents, "The sub-agent session of session-1 is session-2.");
        Assert.AreEqual("session-1", result.Rows.Single(static row => row.SessionId == "session-2").ParentSessionId);
        foreach (var row in result.Rows)
        {
            var batch = harness.Batches.Single(batch => batch.SessionId == row.SessionId);
            Assert.AreEqual(batch.Usage.Values.Sum(static value => value.InputTokens + value.OutputTokens), row.Tokens, 0.001);
            Assert.AreEqual(batch.Activity.Values.Sum(static value => value.ActiveMs), row.ActiveMs, 0.001);
            Assert.AreEqual(batch.Tools.Values.Sum(static value => value.Calls), row.ToolCalls, 0.001);
            Assert.AreEqual(batch.Activity.Values.Sum(static value => value.RunsStarted), row.Runs);
        }

        Assert.IsTrue(result.Rows.Zip(result.Rows.Skip(1)).All(static pair => pair.First.Tokens >= pair.Second.Tokens));
        var limited = await harness.Queries.SessionsAsync(request with { Limit = 3 }, "recent");
        Assert.AreEqual(3, limited.Rows.Count);
        Assert.IsTrue(limited.Truncated);
    }

    [TestMethod]
    public async Task OneSession_HasItsTotals_RunsModelsAndTools_AlsoWithItsSubAgents()
    {
        await using var harness = await QueryHarness.CreateAsync("UTC", now: new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var alone = await harness.Queries.SessionAsync("session-1");
        var withChildren = await harness.Queries.SessionAsync("session-1", withChildren: true);
        var byPrefix = await harness.Queries.SessionAsync("session-1");

        Assert.IsNotNull(alone);
        Assert.IsNotNull(withChildren);
        Assert.AreEqual("session-1", alone.Session.SessionId);
        Assert.IsEmpty(alone.Children);
        var own = harness.Batches[1].Usage.Values.Sum(static value => value.InputTokens + value.OutputTokens);
        var child = harness.Batches[2].Usage.Values.Sum(static value => value.InputTokens + value.OutputTokens);
        Assert.AreEqual(own, alone.Totals.Single(static tile => tile.Id == "tokens").Value, 0.001);
        Assert.AreEqual(own + child, withChildren.Totals.Single(static tile => tile.Id == "tokens").Value, 0.001);
        Assert.AreEqual("session-2", withChildren.Children.Single().SessionId);
        Assert.IsNotEmpty(alone.Runs);
        Assert.IsTrue(alone.Runs.All(static run => run.Outcome == "completed"));
        Assert.IsNotEmpty(alone.Models);
        Assert.IsNotEmpty(alone.Tools);
        Assert.IsNull(await harness.Queries.SessionAsync("nothing-like-it"));
        Assert.AreEqual(alone.Session.SessionId, byPrefix!.Session.SessionId);
        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.SessionAsync("session-"));
    }

    [TestMethod]
    public async Task TheRecords_AreTheLargestValuesOfThePeriod()
    {
        await using var harness = await QueryHarness.CreateAsync("Europe/Paris");
        var request = new StatisticsRequest { Period = "2026-03-01..2026-12-31" };

        var records = await harness.Queries.RecordsAsync(request);

        var longest = records.Records.Single(static record => record.Measure == "longestRun");
        var expected = harness.Batches.SelectMany(static batch => batch.Extremes).Where(static pair => pair.Key.Measure == ExtremeMeasure.LongestRunMs).Max(static pair => pair.Value.Value);
        Assert.AreEqual(expected, longest.Value);
        Assert.IsNotNull(longest.SessionId);
        Assert.IsTrue(records.Records.Any(static record => record.Measure == "busiestDay"));
        Assert.IsTrue(records.Records.Any(static record => record.Measure == "longestTool"));
        var busiest = records.Records.Single(static record => record.Measure == "busiestDay");
        var days = harness.Naive(QueryHarness.ActiveMs, StatisticsFrequency.Day, new DateOnly(2026, 3, 1), new DateOnly(2026, 12, 31), DayOfWeek.Monday);
        Assert.AreEqual(days.Values.Max(), busiest.Value, 0.001);
        Assert.AreEqual(days.OrderByDescending(static pair => pair.Value).ThenBy(static pair => pair.Key, StringComparer.Ordinal).First().Key, busiest.At);

        var one = await harness.Queries.RecordsAsync(request with { Filter = new StatisticsFilter { Model = "x", Project = "project-0" } });
        CollectionAssert.IsSubsetOf(new[] { "model" }, one.Query.IgnoredFilters.ToArray());
    }

    [TestMethod]
    public async Task TheHealth_HasErrorsFailingToolsCompactionsAndTheContext()
    {
        await using var harness = await QueryHarness.CreateAsync("UTC");
        var request = new StatisticsRequest { Period = "2026-03-01..2026-12-31", Frequency = StatisticsFrequency.Month };

        var health = await harness.Queries.HealthAsync(request);

        var errors = harness.Batches.SelectMany(static batch => batch.Activity.Values).Sum(static value => value.Errors);
        Assert.AreEqual(errors, health.Errors.Sum());
        Assert.AreEqual(harness.Batches.SelectMany(static batch => batch.Activity.Values).Sum(static value => value.RunsStarted), health.Runs);
        Assert.AreEqual(errors / (double)health.Runs, health.ErrorRate, 1e-9);
        Assert.IsTrue(health.FailedTools.All(static row => row.Failures > 0));
        Assert.IsNotEmpty(health.ContextByModel);
        Assert.AreEqual(harness.Batches.SelectMany(static batch => batch.Activity.Values).Sum(static value => value.CompactionTokensBefore), health.TokensBeforeCompaction);
        Assert.IsTrue(health.ContextByModel.All(static row => row.Highest is >= 0 and <= 1));
    }

    [TestMethod]
    public void ARequest_IsReadFromTheJsonOfAPage()
    {
        var request = StatisticsJson.ParseRequest("""{"period":"7d","frequency":"week","comparison":"previousPeriod","weekStart":"Monday","limit":5,"filter":{"project":"CodeAlta","origin":"you"}}""");

        Assert.AreEqual("7d", request.Period);
        Assert.AreEqual(StatisticsFrequency.Week, request.Frequency);
        Assert.AreEqual(StatisticsComparison.PreviousPeriod, request.Comparison);
        Assert.AreEqual(DayOfWeek.Monday, request.WeekStart);
        Assert.AreEqual(5, request.Limit);
        Assert.AreEqual("CodeAlta", request.Filter.Project);
        Assert.AreEqual("you", request.Filter.Origin);
        var empty = StatisticsJson.ParseRequest("{}");
        Assert.AreEqual("30d", empty.Period);
        Assert.IsTrue(empty.Filter.IsEmpty);
        Assert.AreEqual("30d", StatisticsJson.ParseRequest("null").Period);
        Assert.Throws<ArgumentException>(() => StatisticsJson.ParseRequest("""{"frequency":"sometimes"}"""));
    }

    [TestMethod]
    public async Task TheNamesTheFactsCount_AreRankedByList()
    {
        await using var harness = await QueryHarness.CreateAsync("Europe/Paris");
        var request = new StatisticsRequest { Period = "2026-04-01..2026-09-30", Limit = 100 };

        foreach (var (name, list) in new[] { ("shell-program", DetailList.ShellProgram), ("alta-command", DetailList.AltaCommand), ("changed-file-extension", DetailList.ChangedFileExtension) })
        {
            var result = await harness.Queries.DetailsAsync(request, name);

            var expected = harness.Batches.SelectMany(static batch => batch.Details)
                .Where(pair => pair.Key.List == list && DateOnly.FromDateTime(harness.Local(pair.Key.Quarter)) is var date && date >= new DateOnly(2026, 4, 1) && date <= new DateOnly(2026, 9, 30))
                .GroupBy(static pair => pair.Key.Name)
                .ToDictionary(static group => group.Key, static group => group.Sum(static pair => pair.Value));
            CollectionAssert.AreEquivalent(expected.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).ToArray(), result.Rows.Select(static row => row.Name).ToArray(), name);
            foreach (var row in result.Rows)
            {
                Assert.AreEqual(expected[row.Name], row.Count, $"{name} {row.Name}");
            }

            Assert.AreEqual(1d, result.Rows.Sum(static row => row.Share), 1e-9);
            Assert.IsTrue(result.Rows.Zip(result.Rows.Skip(1)).All(static pair => pair.First.Count >= pair.Second.Count));
        }

        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.DetailsAsync(request, "feelings"));
    }

    [TestMethod]
    public async Task TheRuns_AreListedWithWhatTheirPromptsBroughtBack_AndCountedByOrigin()
    {
        await using var harness = await QueryHarness.CreateAsync("Asia/Kolkata");
        var request = new StatisticsRequest { Period = "2026-04-01..2026-09-30", Frequency = StatisticsFrequency.Month, Limit = 500 };

        var runs = await harness.Queries.RunsAsync(request, "longest");
        var byOrigin = await harness.Queries.SeriesAsync(request, "runs", "origin");
        var agents = await harness.Queries.SeriesAsync(request with { Filter = new StatisticsFilter { Origin = "agent" } }, "runs");
        var yours = await harness.Queries.SummaryAsync(request with { Filter = new StatisticsFilter { Origin = "you" } });

        var expected = harness.Batches.SelectMany(static batch => batch.Runs.Values)
            .Where(run => DateOnly.FromDateTime(harness.Local(QuarterHour.Of(run.Start))) is var date && date >= new DateOnly(2026, 4, 1) && date <= new DateOnly(2026, 9, 30))
            .ToList();
        Assert.AreEqual(expected.Count, runs.TotalRows);
        Assert.AreEqual(expected.Count, runs.Runs.Count);
        Assert.IsTrue(runs.Runs.Zip(runs.Runs.Skip(1)).All(static pair => pair.First.DurationMs >= pair.Second.DurationMs));
        Assert.AreEqual(expected.Max(static run => run.Duration.TotalMilliseconds), runs.Runs[0].DurationMs);
        Assert.IsTrue(runs.Runs.All(static run => run.Origin == "you" && run.Outcome == "completed"));
        Assert.AreEqual("you", byOrigin.Series.Single().Label);
        Assert.AreEqual(expected.Count, byOrigin.Series.Single().Total);
        Assert.AreEqual(0, agents.Series.Sum(static line => line.Total));
        Assert.AreEqual(expected.Count, yours.Tiles.Single(static tile => tile.Id == "runs").Value);
        var recent = await harness.Queries.RunsAsync(request with { Limit = 3 }, "recent");
        Assert.AreEqual(3, recent.Runs.Count);
        Assert.IsTrue(recent.Truncated);
        Assert.IsTrue(string.CompareOrdinal(recent.Runs[0].Start, recent.Runs[1].Start) >= 0);
        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Queries.RunsAsync(request, "luck"));
    }

    [TestMethod]
    public async Task TheWorkOfSubAgents_IsCutOffFromTheWorkOfYourSessions()
    {
        await using var harness = await QueryHarness.CreateAsync("UTC");
        var request = new StatisticsRequest { Period = "2026-03-01..2026-12-31", Frequency = StatisticsFrequency.Year };

        var tokens = await harness.Queries.SeriesAsync(request, "tokens", "delegated");
        var started = await harness.Queries.SeriesAsync(request, "sessions-started", "delegated");
        var files = await harness.Queries.SeriesAsync(request, "prompt-files");

        double Naive(bool delegated) => harness.Batches.Where(batch => (batch.Session!.ParentSessionId is not null) == delegated).SelectMany(static batch => batch.Usage.Values).Sum(static value => value.InputTokens + value.OutputTokens);
        Assert.AreEqual(Naive(false), tokens.Series.Single(static line => line.Key == "direct").Total, 0.001);
        Assert.AreEqual(Naive(true), tokens.Series.Single(static line => line.Key == "sub-agent").Total, 0.001);
        Assert.AreEqual(harness.Batches.Count(static batch => batch.Session!.ParentSessionId is not null), started.Series.Single(static line => line.Key == "sub-agent").Total);
        Assert.AreEqual(harness.Batches.SelectMany(static batch => batch.Content).Where(static pair => pair.Key.Kind == ContentKind.Prompt).Sum(static pair => pair.Value.Files), files.Series.Single().Total);
    }

    [TestMethod]
    public async Task TheResults_AreJsonWithStableCamelCaseNames()
    {
        await using var harness = await QueryHarness.CreateAsync("UTC");
        var request = new StatisticsRequest { Period = "2026-04-01..2026-04-30", Frequency = StatisticsFrequency.Week, Comparison = StatisticsComparison.PreviousPeriod };

        var json = StatisticsJson.Serialize(await harness.Queries.SeriesAsync(request, "tokens", "model"));

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.AreEqual("tokens", root.GetProperty("metric").GetString());
        Assert.AreEqual("week", root.GetProperty("query").GetProperty("frequency").GetString());
        Assert.AreEqual("2026-04-01", root.GetProperty("query").GetProperty("from").GetString());
        Assert.IsTrue(root.GetProperty("query").GetProperty("coverage").GetProperty("complete").GetBoolean());
        Assert.IsTrue(root.GetProperty("series")[0].TryGetProperty("previous", out _));
        Assert.IsTrue(root.GetProperty("buckets")[0].TryGetProperty("label", out _));
        var summary = StatisticsJson.Serialize(await harness.Queries.SummaryAsync(request));
        StringAssert.Contains(summary, "\"tiles\"");
    }
}
