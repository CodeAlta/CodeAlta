using CodeAlta.Desktop;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopUnreadSessionsTests
{
    [TestMethod]
    public void ASessionWhoseTurnEnds_IsMarked_UnlessTheUserLooksAtIt_AndLosesItsMarkOnceLookedAt()
    {
        var inView = new List<string> { "shown" };
        var unread = new DesktopUnreadSessions(() => inView);
        var changes = 0;
        unread.Changed += () => changes++;

        unread.Observe([Alert("done", DesktopTurnAlertKind.Done), Alert("failed", DesktopTurnAlertKind.Failed), Alert("waits", DesktopTurnAlertKind.Waiting),
            Alert("shown", DesktopTurnAlertKind.Done)]);
        CollectionAssert.AreEqual(new[] { "done", "failed", "waits" }, unread.Sessions.ToArray(), "the session the user looks at gets no mark");
        Assert.AreEqual(1, changes, "one reading, one change");

        unread.Observe([]);
        unread.Look();
        Assert.AreEqual(1, changes, "nothing new: nothing to tell");

        // The user puts the session in front of a pane of a window that has the focus.
        inView.Add("FAILED");
        unread.Look();
        CollectionAssert.AreEqual(new[] { "done", "waits" }, unread.Sessions.ToArray());
        Assert.AreEqual(2, changes);

        // A reading clears what the user looks at too, whatever told the view.
        inView.Add("done");
        unread.Observe([]);
        CollectionAssert.AreEqual(new[] { "waits" }, unread.Sessions.ToArray());
        Assert.AreEqual(3, changes);
    }

    [TestMethod]
    public void ASubSession_GetsNoMark_ItsEndGoesToItsCreator()
    {
        var unread = new DesktopUnreadSessions(() => []);
        unread.Observe([Alert("child", DesktopTurnAlertKind.Waiting, parent: "parent"), Alert("child-done", DesktopTurnAlertKind.Done, parent: "parent")]);
        Assert.IsEmpty(unread.Sessions);
    }

    [TestMethod]
    public void ASessionMarkedAgain_KeepsOneMark_AndPastTheMostMarks_TheOldestGoes()
    {
        var unread = new DesktopUnreadSessions(() => []);
        for (var index = 0; index < DesktopUnreadSessions.MaximumSessions; index++) unread.Observe([Alert($"s{index}", DesktopTurnAlertKind.Done)]);
        // Marked again, a session that is not the oldest takes no second place: the oldest keeps its own.
        unread.Observe([Alert("S5", DesktopTurnAlertKind.Done)]);
        Assert.HasCount(DesktopUnreadSessions.MaximumSessions, unread.Sessions);
        Assert.AreEqual("s0", unread.Sessions[0], "no mark went for a session that already had one");
        Assert.AreEqual("S5", unread.Sessions[^1], "its turn ended last");
        Assert.AreEqual(1, unread.Sessions.Count(static id => string.Equals(id, "s5", StringComparison.OrdinalIgnoreCase)));

        unread.Observe([Alert("new", DesktopTurnAlertKind.Done)]);
        Assert.HasCount(DesktopUnreadSessions.MaximumSessions, unread.Sessions);
        Assert.DoesNotContain("s0", unread.Sessions, "the oldest mark went");
        Assert.AreEqual("s1", unread.Sessions[0]);
        Assert.AreEqual("new", unread.Sessions[^1]);
    }

    [TestMethod]
    public void ADeletedSession_LosesItsMark_AndTellsItOnce()
    {
        var unread = new DesktopUnreadSessions(() => []);
        unread.Observe([Alert("kept", DesktopTurnAlertKind.Done), Alert("deleted", DesktopTurnAlertKind.Failed)]);
        var changes = 0;
        unread.Changed += () => changes++;

        unread.Forget("DELETED");
        CollectionAssert.AreEqual(new[] { "kept" }, unread.Sessions.ToArray());
        unread.Forget("deleted");
        unread.Forget("never-marked");
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void ALookThatComesWhileAReadingMarksASession_ClearsItsMark()
    {
        // The reading asks what the user looks at, and is told a view without the session. The user shows the session at
        // that moment: the look waits for the reading to end, then finds the mark and clears it. It used to run between the
        // view of the reading and its mark, found nothing, and the mark stayed on a session the user was looking at.
        DesktopUnreadSessions? unread = null;
        Task? look = null;
        var reading = true;
        unread = new DesktopUnreadSessions(() =>
        {
            if (!reading) return ["shown"];
            reading = false;
            look = Task.Run(() => unread!.Look());
            // Time for the look to run, where nothing holds it back.
            Thread.Sleep(200);
            return [];
        });

        unread.Observe([Alert("shown", DesktopTurnAlertKind.Done), Alert("other", DesktopTurnAlertKind.Done)]);
        Assert.IsTrue(look!.Wait(TestTime.Seconds(30)));
        CollectionAssert.AreEqual(new[] { "other" }, unread.Sessions.ToArray());
    }

    private static DesktopTurnAlert Alert(string id, DesktopTurnAlertKind kind, string? parent = null) => new(id, "Title of " + id, kind, parent);
}
