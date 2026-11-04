using CodeAlta.Desktop;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Notifications;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopTurnAlertsTests
{
    private static readonly IReadOnlySet<string> NoneWaits = new HashSet<string>();

    [TestMethod]
    public void Tracker_TellsOfASessionThatFinishedItsWork_NotOfOneThatWasAlreadyIdle()
    {
        var tracker = new DesktopTurnAlertTracker();
        Assert.IsEmpty(tracker.Observe([Session("a", running: true), Session("b")], NoneWaits), "the first reading only sets what the next ones compare with");
        Assert.IsEmpty(tracker.Observe([Session("a", running: true), Session("b")], NoneWaits));

        var alerts = tracker.Observe([Session("a"), Session("b")], NoneWaits);
        Assert.AreEqual(new DesktopTurnAlert("a", "Title of a", DesktopTurnAlertKind.Done), alerts.Single());
        Assert.IsEmpty(tracker.Observe([Session("a"), Session("b")], NoneWaits), "told once");
    }

    [TestMethod]
    public void Tracker_ASessionWithABackgroundTask_IsDoneOnceTheTaskEnds()
    {
        var tracker = new DesktopTurnAlertTracker();
        tracker.Observe([Session("a", running: true)], NoneWaits);
        Assert.IsEmpty(tracker.Observe([Session("a", background: 1)], NoneWaits), "a job whose result starts another turn is still work");
        Assert.AreEqual(DesktopTurnAlertKind.Done, tracker.Observe([Session("a")], NoneWaits).Single().Kind);
    }

    [TestMethod]
    public void Tracker_AFailedRun_IsToldAsAFailure()
    {
        var tracker = new DesktopTurnAlertTracker();
        tracker.Observe([Session("a", running: true)], NoneWaits);
        Assert.AreEqual(DesktopTurnAlertKind.Failed, tracker.Observe([Session("a", failed: true)], NoneWaits).Single().Kind);
    }

    [TestMethod]
    public void Tracker_ASubSession_IsDoneForItsCreator_ButItsQuestionsAreTheUsers()
    {
        var tracker = new DesktopTurnAlertTracker();
        tracker.Observe([Session("child", running: true, parent: "parent")], NoneWaits);
        Assert.IsEmpty(tracker.Observe([Session("child", parent: "parent")], NoneWaits));

        tracker.Observe([Session("child", running: true, parent: "parent")], NoneWaits);
        var waiting = tracker.Observe([Session("child", running: true, parent: "parent")], new HashSet<string> { "child" });
        Assert.AreEqual(new DesktopTurnAlert("child", "Title of child", DesktopTurnAlertKind.Waiting), waiting.Single());
    }

    [TestMethod]
    public void Tracker_ASessionThatWaits_IsToldOnce_AndIsDoneWhenItEndsAfterTheAnswer()
    {
        var tracker = new DesktopTurnAlertTracker();
        var waits = new HashSet<string>(StringComparer.Ordinal) { "a" };
        tracker.Observe([Session("a", running: true)], NoneWaits);
        Assert.AreEqual(DesktopTurnAlertKind.Waiting, tracker.Observe([Session("a", running: true)], waits).Single().Kind);
        Assert.IsEmpty(tracker.Observe([Session("a", running: true)], waits));
        Assert.IsEmpty(tracker.Observe([Session("a", running: true)], NoneWaits), "answered: it works again");
        Assert.AreEqual(DesktopTurnAlertKind.Done, tracker.Observe([Session("a")], NoneWaits).Single().Kind);
    }

    [TestMethod]
    public void Tracker_ASessionThatAppearsWaiting_IsTold_AndOneThatAppearsIdle_IsNot()
    {
        var tracker = new DesktopTurnAlertTracker();
        tracker.Observe([], NoneWaits);
        var alerts = tracker.Observe([Session("a", running: true), Session("b"), Session("c")], new HashSet<string> { "c" });
        Assert.AreEqual(new DesktopTurnAlert("c", "Title of c", DesktopTurnAlertKind.Waiting), alerts.Single());
    }

    [TestMethod]
    public async Task InTheBackground_ASessionDone_IsNotified_Badged_AndAsksForAttention_UntilAWindowHasTheFocus()
    {
        var surface = new FakeSurface();
        await using var alerts = new DesktopTurnAlerts(surface);
        surface.Sessions = [Session("a", running: true), Session("b", running: true)];
        await alerts.RefreshAsync();

        surface.Sessions = [Session("a"), Session("b", running: true)];
        await alerts.RefreshAsync();
        Assert.AreEqual(DesktopTurnAlerts.NotificationPrefix + "a", surface.Shown.Single().Id);
        Assert.AreEqual("a", surface.Shown.Single().ActivationData);
        Assert.AreEqual("Title of a", surface.Shown.Single().Title);
        CollectionAssert.AreEqual(new[] { "1" }, surface.Badges);
        Assert.AreEqual(1, surface.Attention);

        surface.Sessions = [Session("a"), Session("b")];
        await alerts.RefreshAsync();
        CollectionAssert.AreEqual(new[] { "a", "b" }, alerts.Unseen.ToArray());
        CollectionAssert.AreEqual(new[] { "1", "2" }, surface.Badges);
        Assert.AreEqual(2, surface.Attention);

        await alerts.RefreshAsync();
        Assert.HasCount(2, surface.Badges, "nothing new: nothing is shown again");

        surface.Foreground = true;
        await alerts.RefreshAsync();
        Assert.IsEmpty(alerts.Unseen);
        CollectionAssert.AreEqual(new[] { "1", "2", null }, surface.Badges);
        CollectionAssert.AreEquivalent(new[] { DesktopTurnAlerts.NotificationPrefix + "a", DesktopTurnAlerts.NotificationPrefix + "b" }, surface.Removed);

        await alerts.RefreshAsync();
        Assert.HasCount(3, surface.Badges, "a badge that is clear is not cleared again");
        Assert.IsEmpty(surface.Warnings);
    }

    [TestMethod]
    public async Task WithTheFocus_NothingIsShown()
    {
        var surface = new FakeSurface { Foreground = true, Sessions = [Session("a", running: true)] };
        await using var alerts = new DesktopTurnAlerts(surface);
        await alerts.RefreshAsync();
        surface.Sessions = [Session("a")];
        await alerts.RefreshAsync();
        Assert.IsEmpty(surface.Shown);
        Assert.IsEmpty(surface.Badges);
        Assert.AreEqual(0, surface.Attention);
    }

    [TestMethod]
    public async Task TheMarkAlone_ShowsNoNotification_AndOff_ShowsNothing_NorWhatHappenedMeanwhile()
    {
        var surface = new FakeSurface { Mode = DesktopTurnAlertMode.Mark, Sessions = [Session("a", running: true)] };
        await using var alerts = new DesktopTurnAlerts(surface);
        await alerts.RefreshAsync();
        surface.Sessions = [Session("a")];
        await alerts.RefreshAsync();
        Assert.IsEmpty(surface.Shown);
        CollectionAssert.AreEqual(new[] { "1" }, surface.Badges);
        Assert.AreEqual(1, surface.Attention);

        surface.Mode = DesktopTurnAlertMode.Off;
        surface.Sessions = [Session("a", running: true)];
        await alerts.RefreshAsync();
        CollectionAssert.AreEqual(new[] { "1", null }, surface.Badges, "turned off, the mark goes at once");
        surface.Sessions = [Session("a")];
        await alerts.RefreshAsync();

        surface.Mode = DesktopTurnAlertMode.All;
        await alerts.RefreshAsync();
        Assert.IsEmpty(surface.Shown, "turned on again, what happened while it was off is not told");
        Assert.AreEqual(1, surface.Attention);
    }

    [TestMethod]
    public async Task WhatThePlatformDoesNotHave_IsNoFailure_AndAFailureIsLoggedOnce()
    {
        var surface = new FakeSurface { BadgeStatus = NeoDesktopStatus.Unsupported, NotifyStatus = NeoDesktopStatus.Denied, Sessions = [Session("a", running: true), Session("b", running: true)] };
        await using var alerts = new DesktopTurnAlerts(surface);
        await alerts.RefreshAsync();
        surface.Sessions = [Session("a"), Session("b", running: true)];
        await alerts.RefreshAsync();
        surface.Sessions = [Session("a"), Session("b")];
        await alerts.RefreshAsync();
        Assert.AreEqual(1, surface.Warnings.Count, string.Join(Environment.NewLine, surface.Warnings));
        StringAssert.Contains(surface.Warnings[0], "notification");

        surface.Foreground = true;
        await alerts.RefreshAsync();
        Assert.IsEmpty(surface.Removed, "a notification that was not shown is not removed");
    }

    [TestMethod]
    public async Task Disposal_ClearsTheBadge()
    {
        var surface = new FakeSurface { Sessions = [Session("a", running: true)] };
        var alerts = new DesktopTurnAlerts(surface);
        await alerts.RefreshAsync();
        surface.Sessions = [Session("a")];
        await alerts.RefreshAsync();
        await alerts.DisposeAsync();
        CollectionAssert.AreEqual(new[] { "1", null }, surface.Badges);
    }

    [TestMethod]
    public void TheNotification_OpensItsSession_AndKeepsWithinWhatEverySystemShows()
    {
        var notification = DesktopTurnAlerts.NotificationOf(new("01a1a860-4ed9-7d97-bdf8-164c519a27a1", new string('t', 100), DesktopTurnAlertKind.Waiting))!;
        Assert.AreEqual(63, notification.Title.Length);
        Assert.EndsWith("…", notification.Title);
        Assert.IsLessThanOrEqualTo(255, notification.Body.Length);
        Assert.AreEqual("01a1a860-4ed9-7d97-bdf8-164c519a27a1", DesktopTurnAlerts.SessionOf(new(notification.Id, null, notification.ActivationData, false)));
        Assert.IsNull(DesktopTurnAlerts.SessionOf(new(notification.Id, null, notification.ActivationData, Dismissed: true)));
        Assert.IsNull(DesktopTurnAlerts.SessionOf(new("other.notification", null, "a", false)));

        Assert.AreEqual("CodeAlta", DesktopTurnAlerts.NotificationOf(new("a", " ", DesktopTurnAlertKind.Done))!.Title);
        Assert.AreEqual("Two lines", DesktopTurnAlerts.NotificationOf(new("a", "Two\nlines", DesktopTurnAlertKind.Done))!.Title.Replace("  ", " "));
        Assert.IsNull(DesktopTurnAlerts.NotificationOf(new("a b", "Title", DesktopTurnAlertKind.Done)), "an identity a notification cannot take");
    }

    [TestMethod]
    public void OnMacOS_NotificationsNeedAnApplicationBundle()
    {
        // The notification center of macOS ends a process that is not a bundle, as alta started from a terminal is.
        Assert.AreEqual(!OperatingSystem.IsMacOS(), DesktopTurnAlertSurface.CanNotify(null));
        Assert.AreEqual(!OperatingSystem.IsMacOS(), DesktopTurnAlertSurface.CanNotify(""));
        Assert.IsTrue(DesktopTurnAlertSurface.CanNotify("org.codealta.desktop"));
    }

    [TestMethod]
    public void ThePreference_IsAllUnlessTheFileSaysOtherwise()
    {
        var root = Directory.CreateTempSubdirectory("codealta-turn-alerts-").FullName;
        try
        {
            var file = Path.Combine(root, "preferences.json");
            Assert.AreEqual(DesktopTurnAlertMode.All, DesktopPreferences.Default.TurnAlerts);
            Assert.AreEqual(DesktopTurnAlertMode.All, DesktopPreferences.Load(root).TurnAlerts);

            Assert.IsTrue((DesktopPreferences.Default with { TurnAlerts = DesktopTurnAlertMode.Mark }).Save(root));
            Assert.AreEqual("""{"onClose":"ask","turnAlerts":"mark"}""", File.ReadAllText(file));
            Assert.AreEqual(DesktopTurnAlertMode.Mark, DesktopPreferences.Load(root).TurnAlerts);

            Assert.IsTrue(DesktopPreferences.Default.Save(root));
            Assert.AreEqual("""{"onClose":"ask"}""", File.ReadAllText(file), "the default is not written");

            foreach (var (text, expected) in new[]
            {
                ("""{"onClose":"keep","turnAlerts":"off"}""", DesktopTurnAlertMode.Off),
                ("""{"closeToTray":true,"turnAlerts":"off"}""", DesktopTurnAlertMode.Off),
                ("""{"turnAlerts":"mark"}""", DesktopTurnAlertMode.Mark),
                ("""{"turnAlerts":"loud"}""", DesktopTurnAlertMode.All),
                ("""{"turnAlerts":false}""", DesktopTurnAlertMode.All),
            })
            {
                File.WriteAllText(file, text);
                Assert.AreEqual(expected, DesktopPreferences.Load(root).TurnAlerts, text);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SessionRuntimeOverview Session(string id, bool running = false, int background = 0, bool failed = false, string? parent = null)
        => new(id, "project", "Title of " + id, running, background, failed) { ParentSessionId = parent };

    private sealed class FakeSurface : IDesktopTurnAlertSurface
    {
        public DesktopTurnAlertMode Mode { get; set; } = DesktopTurnAlertMode.All;
        public bool Foreground { get; set; }
        public IReadOnlyList<SessionRuntimeOverview> Sessions { get; set; } = [];
        public IReadOnlyCollection<string> Waiting { get; set; } = [];
        public NeoDesktopStatus NotifyStatus { get; set; } = NeoDesktopStatus.Success;
        public NeoDesktopStatus BadgeStatus { get; set; } = NeoDesktopStatus.Success;
        public List<NeoNotificationRequest> Shown { get; } = [];
        public List<string> Removed { get; } = [];
        public List<string?> Badges { get; } = [];
        public int Attention { get; private set; }
        public List<string> Warnings { get; } = [];

        public bool IsForeground => Foreground;
        public IReadOnlyList<SessionRuntimeOverview> ReadSessions() => Sessions;
        public ValueTask<IReadOnlyCollection<string>> ReadWaitingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Waiting);

        public ValueTask<NeoDesktopStatus> NotifyAsync(NeoNotificationRequest request, CancellationToken cancellationToken)
        {
            if (NotifyStatus == NeoDesktopStatus.Success) Shown.Add(request);
            return ValueTask.FromResult(NotifyStatus);
        }

        public ValueTask<NeoDesktopStatus> RemoveNotificationAsync(string id, CancellationToken cancellationToken)
        {
            Removed.Add(id);
            return ValueTask.FromResult(NeoDesktopStatus.Success);
        }

        public ValueTask<NeoDesktopStatus> SetBadgeAsync(string? badge, CancellationToken cancellationToken)
        {
            if (BadgeStatus == NeoDesktopStatus.Success) Badges.Add(badge);
            return ValueTask.FromResult(BadgeStatus);
        }

        public ValueTask<NeoDesktopStatus> RequestAttentionAsync(CancellationToken cancellationToken)
        {
            Attention++;
            return ValueTask.FromResult(NeoDesktopStatus.Success);
        }

        public void Warn(string message) => Warnings.Add(message);
    }
}
