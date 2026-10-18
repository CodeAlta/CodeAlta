using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugin.Statistics.Query;

namespace CodeAlta.Desktop.Tests;

/// <summary>The skills a prompt lists first: ranked once a day from the uses before today, and kept by each session.</summary>
[TestClass]
public sealed class DesktopSkillUsageTests
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Source
    {
        public List<DateTimeOffset> Reads { get; } = [];

        public IReadOnlyList<SkillUsageScore>? Scores { get; set; } = [];

        public bool Settled { get; set; } = true;

        public bool Throws { get; set; }

        public DesktopSkillUsage Usage(Clock clock) => new(
            (end, _) =>
            {
                Reads.Add(end);
                if (Throws)
                {
                    throw new InvalidOperationException("the store is closed");
                }

                return Scores is { } scores ? ValueTask.FromResult(scores) : null;
            },
            () => Settled,
            clock,
            Paris);
    }

    private static PreferredSkillsRequest Session(string id, string? project = "alpha", string space = "default")
        => new(id, project is null ? null : new ProjectDescriptor { Id = project, Name = project, ProjectPath = "/p/" + project }, space);

    private static SkillUsageScore Score(string name, double score, string? project = "alpha", string? space = "default") => new(project, space, name, score, (long)Math.Ceiling(score));

    [TestMethod]
    public void TheRanking_WeighsTheProject_FullyAndTheOtherProjectsOfTheSpace_AQuarter()
    {
        IReadOnlyList<SkillUsageScore> scores =
        [
            Score("release-notes", 3), Score("dataviz", 2, project: "beta"), Score("dataviz", 0.6),
            Score("ilspy", 8, project: "beta"), Score("rare", 0.9), Score("elsewhere", 9, space: "work"), Score("chat-only", 4, project: null),
        ];

        // ilspy: 0.25 × 8 = 2; dataviz: 0.6 + 0.25 × 2 = 1.1; chat-only: 0.25 × 4 = 1; rare is below one use; a skill of another space does not count.
        CollectionAssert.AreEqual(new[] { "release-notes", "ilspy", "dataviz", "chat-only" }, DesktopSkillUsage.Rank(scores, "alpha", "default").ToArray());
        // For a session of no project, the chats are its own.
        CollectionAssert.AreEqual(new[] { "chat-only", "ilspy" }, DesktopSkillUsage.Rank(scores, null, "default").ToArray());
        CollectionAssert.AreEqual(new[] { "elsewhere" }, DesktopSkillUsage.Rank(scores, "alpha", "work").ToArray());
        // At most the preferred skills of a prompt, the best first, and the same order for the same scores.
        var many = Enumerable.Range(0, 20).Select(static i => Score($"skill-{i:00}", 2 + (i % 3))).ToArray();
        var ranked = DesktopSkillUsage.Rank(many, "alpha", "default");
        Assert.AreEqual(AgentInstructionTemplateProvider.MaxPreferredSkills, ranked.Count);
        CollectionAssert.AreEqual(ranked.ToArray(), DesktopSkillUsage.Rank([.. many.Reverse()], "alpha", "default").ToArray());
        Assert.AreEqual("skill-02", ranked[0]);
    }

    [TestMethod]
    public void TheScores_AreReadOnceADay_UpToItsStart_AndASessionKeepsItsRanking()
    {
        // 23:30 in Paris on 2026-10-18.
        var clock = new Clock(new DateTimeOffset(2026, 10, 18, 21, 30, 0, TimeSpan.Zero));
        var source = new Source { Scores = [Score("release-notes", 3)] };
        var usage = source.Usage(clock);

        CollectionAssert.AreEqual(new[] { "release-notes" }, usage.PreferredFor(Session("s1")).ToArray());
        source.Scores = [Score("dataviz", 5)];
        CollectionAssert.AreEqual(new[] { "release-notes" }, usage.PreferredFor(Session("s2")).ToArray(), "the scores of the day were read once");
        Assert.AreEqual(1, source.Reads.Count);
        // The end is the start of the local day: what was used today does not move the ranking.
        Assert.AreEqual(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.FromHours(2)), source.Reads[0]);

        // Past midnight, a new session gets the ranking of the new day; one that was there keeps its own.
        clock.Now = clock.Now.AddHours(1);
        CollectionAssert.AreEqual(new[] { "dataviz" }, usage.PreferredFor(Session("s3")).ToArray());
        CollectionAssert.AreEqual(new[] { "release-notes" }, usage.PreferredFor(Session("s1")).ToArray());
        Assert.AreEqual(2, source.Reads.Count);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.FromHours(2)), source.Reads[1]);
        // A session with no identifier yet is not kept, but ranked the same.
        CollectionAssert.AreEqual(new[] { "dataviz" }, usage.PreferredFor(Session("")).ToArray());
    }

    [TestMethod]
    public void ARankingOfAHistoryThatIsStillRead_IsReadAgainEveryTenMinutes_ForTheSessionsThatStartThen()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero));
        var source = new Source { Scores = [], Settled = false };
        var usage = source.Usage(clock);

        Assert.AreEqual(0, usage.PreferredFor(Session("s1")).Count);
        source.Scores = [Score("release-notes", 3)];
        source.Settled = true;
        clock.Now = clock.Now.AddMinutes(5);
        Assert.AreEqual(0, usage.PreferredFor(Session("s2")).Count, "a provisional ranking is kept ten minutes");
        clock.Now = clock.Now.AddMinutes(6);
        CollectionAssert.AreEqual(new[] { "release-notes" }, usage.PreferredFor(Session("s3")).ToArray());
        Assert.AreEqual(0, usage.PreferredFor(Session("s1")).Count, "a session keeps the ranking it got");
        clock.Now = clock.Now.AddHours(2);
        usage.PreferredFor(Session("s4"));
        Assert.AreEqual(2, source.Reads.Count, "a settled ranking is not read again the same day");
    }

    [TestMethod]
    public void StatisticsThatAreNotThereOrCannotAnswer_PreferNothing()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero));
        var absent = new Source { Scores = null };
        Assert.AreEqual(0, absent.Usage(clock).PreferredFor(Session("s1")).Count);
        var failing = new Source { Throws = true };
        Assert.AreEqual(0, failing.Usage(clock).PreferredFor(Session("s1")).Count);
    }
}
