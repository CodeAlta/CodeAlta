using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.History;
using Spent = CodeAlta.Plugin.Statistics.Tests.SessionForkFactsTests.Spent;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// A session moved to another machine takes its whole journal there, and the new owner appends the move record
/// (<c>codealta.sessionMoved</c>). Every machine counts its own (<c>doc/fleet/statistics.md</c>): the part of a journal a machine
/// wrote is counted by that machine only, so the sums of the fleet are the same before and after a move.
/// </summary>
[TestClass]
public sealed class SessionMoveFactsTests
{
    private const string TargetProject = "77777777-7777-7777-7777-777777777777";

    private static DateTimeOffset Time(int minute, double seconds = 0) => JournalBuilder.Time(minute, seconds);

    // The journal a session has on the machine it moved to, as the install writes it: its header written again for the project
    // of that machine, the records it had, a new state, then the move record and the note of the new folder.
    private static JournalBuilder MovedTo(JournalBuilder journal, string moveId, string? from, string? to, DateTimeOffset at, string? fromDevice = null, string? toDevice = null)
    {
        var moved = new JournalBuilder(journal.SessionId, journal.Provider);
        moved.Header(Time(0), projectRef: TargetProject, title: "A source");
        foreach (var line in journal.Lines.Skip(1))
        {
            moved.Add(line);
        }

        moved.State(at);
        return Move(moved, moveId, from, to, at, fromDevice, toDevice);
    }

    // The record the install writes: the machines by name for the people, and by device id for the machines.
    private static JournalBuilder Move(JournalBuilder journal, string moveId, string? from, string? to, DateTimeOffset at, string? fromDevice = null, string? toDevice = null)
    {
        static string Name(string? name) => name is null ? "null" : JournalBuilder.Text(name);
        var devices = (fromDevice is null ? "" : $"\"fromDevice\":{Name(fromDevice)},") + (toDevice is null ? "" : $"\"toDevice\":{Name(toDevice)},");
        return journal.AddRaw("codealta.sessionMoved",
                $"{{\"moveId\":\"{moveId}\",\"from\":{Name(from)},\"to\":{Name(to)},{devices}\"oldWorkingFolder\":\"C:\\\\a\",\"newWorkingFolder\":\"C:\\\\b\",\"epoch\":2,\"time\":\"{JournalBuilder.Json(at)}\"}}",
                at)
            .Add($"{{\"$type\":\"sessionUpdate\",\"kind\":\"Info\",\"message\":\"moved\",{journal.Envelope(at.AddSeconds(1), null)}");
    }

    // The first lines of a journal: what a reader finds while the rest is not written yet.
    private static JournalBuilder Head(JournalBuilder journal, int lines)
    {
        var head = new JournalBuilder(journal.SessionId, journal.Provider);
        foreach (var line in journal.Lines.Take(lines))
        {
            head.Add(line);
        }

        return head;
    }

    private static int Origins(FactBatch batch) => batch.Details.Where(static pair => pair.Key.List == DetailList.SessionOrigin).Sum(static pair => (int)pair.Value);

    [TestMethod]
    public void TheMachineASessionMovedTo_CountsTheWorkAfterTheMoveOnly()
    {
        var source = SessionForkFactsTests.Source();
        var onSource = Spent.Of(SessionFactsReducerTests.Reduce(source));
        var target = MovedTo(source, "m1", "Desk", "Mac", Time(30));
        SessionForkFactsTests.Turn(target, "b1", 40, 8000, 2);

        var result = SessionFactsReducerTests.CatchUp(target);

        var batch = result.Batch;
        CollectionAssert.AreEqual(new[] { "b1" }, batch.Runs.Keys.ToArray(), "The turns made on the source are counted there.");
        Assert.AreEqual(new Spent(1, 1, 1, 18_000, 1, 8000, 800, 2, 1, 2, 1, 1, 1, 22, 1, 5), Spent.Of(batch));
        Assert.AreEqual(0, Origins(batch), "The session was created on the source, and counted there.");
        // The session is the one of this machine: its project, and its title from the header.
        Assert.AreEqual((TargetProject, "A source"), (batch.Session!.ProjectRef, batch.Session.Title));
        Assert.AreEqual("gpt-6.1-sol", batch.Runs["b1"].Model, "The choices in force are followed through what it imported.");

        // The source counted three turns, the target one: four turns were made.
        var never = SessionForkFactsTests.Source();
        SessionForkFactsTests.Turn(never, "b1", 40, 8000, 2);
        Assert.AreEqual(Spent.Of(SessionFactsReducerTests.Reduce(never)), onSource.Plus(Spent.Of(batch)));
    }

    [TestMethod]
    public void AJournalReadBeforeItsMoveRecord_IsReadAgainOnceTheRecordIsWritten()
    {
        var source = SessionForkFactsTests.Source();
        var target = MovedTo(source, "m1", "Desk", "Mac", Time(30));
        SessionForkFactsTests.Turn(target, "b1", 40, 8000, 2);
        // The journal is in place a moment before its move record.
        var copied = Head(target, source.Lines.Count + 1);

        var first = SessionFactsReducerTests.CatchUp(copied);
        Assert.AreEqual(3, first.Batch.Runs.Count, "Nothing tells yet that the journal came from another machine.");

        using var catchUp = new SessionCatchUp();
        using var stream = target.ToStream();
        var second = catchUp.CatchUp(target.SessionId, stream, first.Cursor, long.MaxValue, CancellationToken.None);

        Assert.IsTrue(second.Restarted, "What was counted of the journal is replaced.");
        var whole = SessionFactsReducerTests.Reduce(target);
        Assert.AreEqual(whole.ToCanonicalText(), second.Batch.ToCanonicalText());
        Assert.AreEqual(whole.ToCanonicalRunsText(), second.Batch.ToCanonicalRunsText());
        CollectionAssert.AreEqual(new[] { "b1" }, second.Batch.Runs.Keys.ToArray());
    }

    [TestMethod]
    public void ASessionThatComesBack_CountsOnEachMachineWhatThatMachineDid()
    {
        var source = SessionForkFactsTests.Source();
        var onDesk = SessionFactsReducerTests.CatchUp(source);
        var onMac = MovedTo(source, "m1", "Desk", "Mac", Time(30));
        SessionForkFactsTests.Turn(onMac, "b1", 40, 8000, 2);
        var back = MovedTo(onMac, "m2", "Mac", "Desk", Time(50));
        SessionForkFactsTests.Turn(back, "a4", 60, 16000, 4);

        // The journal it had on the first machine is replaced by the one it comes back with.
        using var catchUp = new SessionCatchUp();
        using var stream = back.ToStream();
        var again = catchUp.CatchUp(back.SessionId, stream, onDesk.Cursor, long.MaxValue, CancellationToken.None);
        var mac = SessionFactsReducerTests.Reduce(onMac);

        Assert.IsTrue(again.Restarted);
        CollectionAssert.AreEqual(new[] { "a4", "r1", "r2", "r3" }, again.Batch.Runs.Keys.Order().ToArray(), "What it did before it left is still its own.");
        Assert.AreEqual(1, Origins(again.Batch), "The session was created there.");
        Assert.AreEqual(SessionFactsReducerTests.Reduce(back).ToCanonicalText(), again.Batch.ToCanonicalText(), "Read at once, the journal gives the same facts.");
        CollectionAssert.AreEqual(new[] { "b1" }, mac.Runs.Keys.ToArray());

        // Five turns were made, each counted once in the fleet.
        var never = SessionForkFactsTests.Source();
        SessionForkFactsTests.Turn(never, "b1", 40, 8000, 2);
        SessionForkFactsTests.Turn(never, "a4", 60, 16000, 4);
        Assert.AreEqual(Spent.Of(SessionFactsReducerTests.Reduce(never)), Spent.Of(again.Batch).Plus(Spent.Of(mac)));

        // On a third machine, only its own turn.
        var onStudio = MovedTo(onMac, "m2", "Mac", "Studio", Time(50));
        SessionForkFactsTests.Turn(onStudio, "c1", 60, 16000, 4);
        CollectionAssert.AreEqual(new[] { "c1" }, SessionFactsReducerTests.Reduce(onStudio).Runs.Keys.ToArray());
    }

    [TestMethod]
    public void ASessionThatComesBackToARenamedMachine_CountsThereWhatThatMachineDidBeforeItLeft()
    {
        // The desk is "Desk" when the session leaves it and "Studio" when it comes back: the device is the same.
        var source = SessionForkFactsTests.Source();
        var onDesk = SessionFactsReducerTests.CatchUp(source);
        var onMac = MovedTo(source, "m1", "Desk", "Mac", Time(30), "desk-device", "mac-device");
        SessionForkFactsTests.Turn(onMac, "b1", 40, 8000, 2);
        var back = MovedTo(onMac, "m2", "Mac", "Studio", Time(50), "mac-device", "desk-device");
        SessionForkFactsTests.Turn(back, "a4", 60, 16000, 4);

        using var catchUp = new SessionCatchUp();
        using var stream = back.ToStream();
        var again = catchUp.CatchUp(back.SessionId, stream, onDesk.Cursor, long.MaxValue, CancellationToken.None);

        Assert.IsTrue(again.Restarted);
        CollectionAssert.AreEqual(new[] { "a4", "r1", "r2", "r3" }, again.Batch.Runs.Keys.Order().ToArray(), "What it did before it left is still its own, whatever it was called.");
        Assert.AreEqual(1, Origins(again.Batch), "The session was created there.");
        CollectionAssert.AreEqual(new[] { "b1" }, SessionFactsReducerTests.Reduce(onMac).Runs.Keys.ToArray());
    }

    [TestMethod]
    public void AMachineRenamedBetweenTwoMoves_StillCountsOnlyItsOwnParts()
    {
        // Desk -> Mac -> Studio, and the Mac is renamed before it gives the session away: the Studio counts its own turn only.
        var onMac = MovedTo(SessionForkFactsTests.Source(), "m1", "Desk", "Mac", Time(30), "desk-device", "mac-device");
        SessionForkFactsTests.Turn(onMac, "b1", 40, 8000, 2);
        var onStudio = MovedTo(onMac, "m2", "Mac mini", "Studio", Time(50), "mac-device", "studio-device");
        SessionForkFactsTests.Turn(onStudio, "c1", 60, 16000, 4);
        CollectionAssert.AreEqual(new[] { "c1" }, SessionFactsReducerTests.Reduce(onStudio).Runs.Keys.ToArray());

        // And back on the Mac, under the name it has now: its turn counts, the Desk's and the Studio's do not.
        var backOnMac = MovedTo(onStudio, "m3", "Studio", "Mac mini", Time(70), "studio-device", "mac-device");
        SessionForkFactsTests.Turn(backOnMac, "b2", 80, 8000, 2);
        CollectionAssert.AreEqual(new[] { "b1", "b2" }, SessionFactsReducerTests.Reduce(backOnMac).Runs.Keys.Order().ToArray());
    }

    [TestMethod]
    public void MovesThatNameTheMachinesOnlyByName_AreReadAsTheyWere()
    {
        // A journal written before the device ids were recorded: names still tell the parts apart.
        var onMac = MovedTo(SessionForkFactsTests.Source(), "m1", "Desk", "Mac", Time(30));
        SessionForkFactsTests.Turn(onMac, "b1", 40, 8000, 2);
        // The next move was written by a build that records the ids: the first one's names are all there is to follow.
        var back = MovedTo(onMac, "m2", "Mac", "Desk", Time(50), "mac-device", "desk-device");
        SessionForkFactsTests.Turn(back, "a4", 60, 16000, 4);

        CollectionAssert.AreEqual(new[] { "a4", "r1", "r2", "r3" }, SessionFactsReducerTests.Reduce(back).Runs.Keys.Order().ToArray());
        CollectionAssert.AreEqual(new[] { "b1" }, SessionFactsReducerTests.Reduce(onMac).Runs.Keys.ToArray());
    }

    [TestMethod]
    public void ASessionMovedMoreThanTwoHundredAndFiftySixTimes_StillCountsOnlyTheLastMachinesParts()
    {
        // Two machines pass the session to each other 300 times; each part holds a turn. The Desk holds it at the end (an even count).
        var journal = SessionForkFactsTests.Source();
        const int moves = 300;
        for (var move = 1; move <= moves; move++)
        {
            var toMac = move % 2 == 1;
            journal = MovedTo(journal, "m" + move, toMac ? "Desk" : "Mac", toMac ? "Mac" : "Desk", Time(30 + move), toMac ? "desk-device" : "mac-device", toMac ? "mac-device" : "desk-device");
            SessionForkFactsTests.Turn(journal, "t" + move, 30 + move, 1000, 0.1);
        }

        var runs = SessionFactsReducerTests.Reduce(journal).Runs.Keys.ToHashSet();

        // The Desk wrote the turns before the first move and those after the even moves; the Mac wrote the others.
        var expected = new HashSet<string> { "r1", "r2", "r3" };
        for (var move = 2; move <= moves; move += 2)
        {
            expected.Add("t" + move);
        }

        CollectionAssert.AreEquivalent(expected.ToArray(), runs.ToArray());
    }

    [TestMethod]
    public void AMoveBetweenMachinesWithoutNames_CountsTheWorkAfterTheLastMove()
    {
        var target = MovedTo(SessionForkFactsTests.Source(), "m1", null, null, Time(30));
        SessionForkFactsTests.Turn(target, "b1", 40, 8000, 2);

        CollectionAssert.AreEqual(new[] { "b1" }, SessionFactsReducerTests.Reduce(target).Runs.Keys.ToArray());
    }

    [TestMethod]
    public void ReadingAMovedJournalInTwoParts_GivesTheFactsOfReadingItAtOnce()
    {
        var onMac = MovedTo(SessionForkFactsTests.Source(), "m1", "Desk", "Mac", Time(30));
        SessionForkFactsTests.Turn(onMac, "b1", 40, 8000, 2);
        var back = MovedTo(onMac, "m2", "Mac", "Desk", Time(50));
        SessionForkFactsTests.Turn(back, "a4", 60, 16000, 4);

        foreach (var journal in new[] { onMac, back })
        {
            var whole = SessionFactsReducerTests.CatchUp(journal);
            for (var split = 0; split <= journal.Lines.Count; split++)
            {
                var head = Head(journal, split);
                using var catchUp = new SessionCatchUp();
                using var firstStream = head.ToStream();
                var first = catchUp.CatchUp(journal.SessionId, firstStream, null, long.MaxValue, CancellationToken.None);
                using var fullStream = journal.ToStream();
                var second = catchUp.CatchUp(journal.SessionId, fullStream, first.Cursor, long.MaxValue, CancellationToken.None);

                // A read that met a move it did not know replaces what was counted; otherwise it adds to it.
                var facts = second.Batch;
                if (!second.Restarted)
                {
                    facts = new FactBatch(journal.SessionId);
                    facts.Merge(first.Batch);
                    facts.Merge(second.Batch);
                }

                Assert.AreEqual(whole.Batch.ToCanonicalText(), facts.ToCanonicalText(), $"facts differ when the journal is cut after {split} lines");
                Assert.AreEqual(whole.Batch.ToCanonicalRunsText(), facts.ToCanonicalRunsText(), $"runs differ when the journal is cut after {split} lines");
                Assert.AreEqual(whole.Cursor.State.ToJson(), second.Cursor.State.ToJson(), $"state differs when the journal is cut after {split} lines");
                Assert.AreEqual(whole.Cursor.Offset, second.Cursor.Offset);
            }
        }
    }

    [TestMethod]
    public async Task WhatTwoMachinesPublish_SumsToTheSameBeforeAndAfterAMove()
    {
        await using var desk = await EngineHarness.CreateAsync();
        await using var mac = await EngineHarness.CreateAsync();
        var source = SessionForkFactsTests.Source();
        desk.Journals.Set(source, Time(21));
        await desk.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await mac.Engine.ChooseHistoryAsync(HistoryChoice.All);
        await desk.Engine.DrainAsync();
        var before = await RequestsAsync(desk.Engine, source.SessionId);
        Assert.AreEqual(3L, before);

        // The journal arrives on the other machine, then its move record: what that machine publishes is what it did.
        var target = MovedTo(source, "m1", "Desk", "Mac", Time(30));
        mac.Journals.Set(Head(target, source.Lines.Count + 1), Time(30));
        mac.Engine.Signal(target.SessionId);
        await mac.Engine.DrainAsync();
        mac.Journals.Set(target, Time(30, 1));
        mac.Engine.Signal(target.SessionId);
        await mac.Engine.DrainAsync();

        Assert.AreEqual(before, await RequestsAsync(desk.Engine, source.SessionId) + await RequestsAsync(mac.Engine, target.SessionId),
            "A move adds nothing to the sums of the fleet.");

        SessionForkFactsTests.Turn(target, "b1", 40, 8000, 2);
        mac.Journals.Set(target, Time(40, 21));
        mac.Engine.Signal(target.SessionId);
        await mac.Engine.DrainAsync();
        Assert.AreEqual(1L, await RequestsAsync(mac.Engine, target.SessionId));
        Assert.AreEqual(3L, await RequestsAsync(desk.Engine, source.SessionId));
    }

    private static async Task<long> RequestsAsync(IStatisticsPublication publication, string sessionId)
        => (await publication.ReadRowsAsync(sessionId, long.MinValue)).Where(static row => row.Family == "usage")
            .Sum(static row => row.Measures.Where(static measure => measure.Key == "requests").Sum(static measure => measure.Value));
}
