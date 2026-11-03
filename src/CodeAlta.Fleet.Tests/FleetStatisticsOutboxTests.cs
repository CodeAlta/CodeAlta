using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The outbox of the statistics of a server link (<c>doc/fleet/statistics.md</c>, publication): rows, not increments; a set
/// per session with a version that follows its rows, never sent again once held, resent after a restore, and a new generation
/// when the outbox is lost.
/// </summary>
[TestClass]
public sealed class FleetStatisticsOutboxTests
{
    [TestMethod]
    public void ASetIsANewVersionOnlyWhenItsRowsChange_AndWhatTheServerHoldsIsNotSentAgain()
    {
        using var folder = new TestFolder();
        var outbox = FleetStatisticsOutbox.Load(Path.Combine(folder.Path, "statistics.json"));
        outbox.Resume(0);

        Assert.IsTrue(outbox.NeedsRows("s1", "r1", 0, null), "a session never read");
        var first = outbox.Record("s1", "r1", 0, null, [Row(100, 1200)]);
        Assert.AreEqual(1L, first!.Version);
        outbox.Acknowledged(new FleetStatisticsPublishResult(outbox.Batch([first]).Sequence, 1, 0, []));
        Assert.IsFalse(outbox.NeedsRows("s1", "r1", 0, null), "held, and its facts did not change");

        // A catch-up that changes no row (a new revision, the same rows) makes no new version.
        Assert.IsTrue(outbox.NeedsRows("s1", "r2", 0, null));
        Assert.IsNull(outbox.Record("s1", "r2", 0, null, [Row(100, 1200)]));
        Assert.IsFalse(outbox.NeedsRows("s1", "r2", 0, null));

        // The current value of the row, never an increment: the set is the rows as they are now.
        var second = outbox.Record("s1", "r3", 0, null, [Row(100, 1500), Row(101, 10)]);
        Assert.AreEqual((2L, 2), (second!.Version, second.Rows.Count));

        // Its epoch and its project are part of the set.
        Assert.IsTrue(outbox.NeedsRows("s1", "r3", 1, null));
        Assert.IsTrue(outbox.NeedsRows("s1", "r3", 0, "project-key"));
    }

    [TestMethod]
    public void ASessionWithoutRowsPublishesNothing_AndOneWhoseRowsWentAwaySendsAnEmptySet()
    {
        using var folder = new TestFolder();
        var outbox = FleetStatisticsOutbox.Load(Path.Combine(folder.Path, "statistics.json"));
        outbox.Resume(0);
        Assert.IsNull(outbox.Record("old", "r1", 0, null, []), "a session of before the link has nothing to say");
        Assert.IsFalse(outbox.NeedsRows("old", "r1", 0, null), "and is not read again until it changes");

        var set = outbox.Record("s1", "r1", 0, null, [Row(100, 1)]);
        outbox.Acknowledged(new FleetStatisticsPublishResult(outbox.Batch([set!]).Sequence, 1, 0, []));
        var empty = outbox.Record("s1", "r2", 0, null, []);
        Assert.AreEqual((2L, 0), (empty!.Version, empty.Rows.Count), "the rows the server holds are taken back");
    }

    [TestMethod]
    public void ABatchWhoseAnswerWasLost_IsAcknowledgedByTheCursor_AndARestoredServerGetsEverythingAgain()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "statistics.json");
        var outbox = FleetStatisticsOutbox.Load(path);
        outbox.Resume(0);
        var batch = outbox.Batch([outbox.Record("s1", "r1", 0, null, [Row(100, 1)])!]);

        // The connection dropped before the answer; the server holds the batch and says so in its welcome.
        var reloaded = FleetStatisticsOutbox.Load(path);
        Assert.AreEqual(outbox.Generation, reloaded.Generation, "the generation is kept with the outbox");
        reloaded.Resume(batch.Sequence);
        Assert.IsFalse(reloaded.NeedsRows("s1", "r1", 0, null));

        // A server restored from an older backup has a lower cursor: every set is sent again, with the versions it knows.
        reloaded.Resume(batch.Sequence - 1);
        Assert.IsTrue(reloaded.NeedsRows("s1", "r1", 0, null));
        Assert.AreEqual(1L, reloaded.Record("s1", "r1", 0, null, [Row(100, 1)])!.Version);
    }

    [TestMethod]
    public void ALostOutbox_StartsANewGeneration()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "statistics.json");
        var first = FleetStatisticsOutbox.Load(path);
        first.Resume(0);
        first.Batch([first.Record("s1", "r1", 0, null, [Row(100, 1)])!]);
        File.Delete(path);

        var again = FleetStatisticsOutbox.Load(path);
        Assert.AreNotEqual(first.Generation, again.Generation, "the server replaces what the machine published before");
        File.WriteAllText(path, "{ not json");
        Assert.AreNotEqual(again.Generation, FleetStatisticsOutbox.Load(path).Generation);
    }

    [TestMethod]
    public void ASessionNoLongerPublished_IsWithdrawnOnceItWasPublished_AndSentAgainWhenPublishedAgain()
    {
        using var folder = new TestFolder();
        var outbox = FleetStatisticsOutbox.Load(Path.Combine(folder.Path, "statistics.json"));
        outbox.Resume(0);
        var set = outbox.Record("s1", "r1", 0, null, [Row(100, 1)]);
        outbox.Record("never", "r1", 0, null, []);
        outbox.Acknowledged(new FleetStatisticsPublishResult(outbox.Batch([set!]).Sequence, 1, 0, []));

        CollectionAssert.AreEqual(new[] { "s1" }, outbox.Withdrawals(["s1", "never"], 100).ToArray(), "only what the server may hold is withdrawn");
        // Published again before the answer: sent again, whether the server deleted the set or still holds it.
        var again = outbox.Record("s1", "r1", 0, null, [Row(100, 1)]);
        Assert.AreEqual(1L, again!.Version);
        Assert.AreEqual(0, outbox.Withdrawals([], 100).Count);

        Assert.AreEqual(1, outbox.Withdrawals(["s1"], 100).Count);
        outbox.Withdrawn(["s1"]);
        Assert.AreEqual(0, outbox.Withdrawals(["s1"], 100).Count);
        Assert.AreEqual(1L, outbox.Record("s1", "r1", 0, null, [Row(100, 1)])!.Version, "a withdrawn session starts its versions again");
    }

    private static FleetStatisticsRow Row(long quarter, long inputTokens)
        => new(FleetStatisticsFamilies.Usage, quarter, "anthropic", "claude-opus-5-5", ["high", "", "0"], new Dictionary<string, long> { ["requests"] = 1, ["input_tokens"] = inputTokens });
}
