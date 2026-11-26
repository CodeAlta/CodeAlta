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
        unread.Observe([Alert("s0", DesktopTurnAlertKind.Done)]);
        Assert.HasCount(DesktopUnreadSessions.MaximumSessions, unread.Sessions);
        Assert.AreEqual("s0", unread.Sessions[^1], "its turn ended last");

        unread.Observe([Alert("new", DesktopTurnAlertKind.Done)]);
        Assert.HasCount(DesktopUnreadSessions.MaximumSessions, unread.Sessions);
        Assert.DoesNotContain("s1", unread.Sessions, "the oldest mark went");
        Assert.AreEqual("s2", unread.Sessions[0]);
        Assert.AreEqual("new", unread.Sessions[^1]);
    }

    private static DesktopTurnAlert Alert(string id, DesktopTurnAlertKind kind, string? parent = null) => new(id, "Title of " + id, kind, parent);
}
