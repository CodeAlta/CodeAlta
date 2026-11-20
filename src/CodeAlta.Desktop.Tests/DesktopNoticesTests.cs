using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Notifications;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// What an automation whose run ended, a plugin or a script tells the user: a notification of the system while CodeAlta is in the
/// background and the setting shows them, a notice in the window otherwise, which waits there when it came in the background.
/// </summary>
[TestClass]
public sealed class DesktopNoticesTests
{
    private static readonly PluginNotification Run = new()
    {
        Title = "Nightly review", Body = "Completed in 3 min 12 s.\nTwo pull requests need a review.", Tone = PluginNotificationTone.Success,
        SessionId = "01a1a860-4ed9-7d97-bdf8-164c519a27a1",
    };

    [TestMethod]
    public async Task InTheBackground_TheSystemShowsIt_AndAClickOpensItsSession()
    {
        var surface = new FakeSurface();
        var window = new List<(PluginNotification Notification, bool Waits)>();
        var notices = new DesktopNotices(surface, (notification, waits) => window.Add((notification, waits)));

        Assert.IsTrue(await notices.NotifyAsync(Run, CancellationToken.None));
        var shown = surface.Shown.Single();
        Assert.AreEqual(("Nightly review", Run.Body, Run.SessionId), (shown.Title, shown.Body, shown.ActivationData));
        Assert.IsEmpty(window);

        Assert.IsTrue(DesktopNotices.IsNotice(new(shown.Id, null, shown.ActivationData, false), out var session));
        Assert.AreEqual(Run.SessionId, session);
        Assert.IsFalse(DesktopNotices.IsNotice(new(shown.Id, null, shown.ActivationData, Dismissed: true), out _));
        Assert.IsFalse(DesktopNotices.IsNotice(new(DesktopTurnAlerts.NotificationPrefix + "a", null, "a", false), out _), "the notification of a session that is done");
        Assert.IsNull(DesktopTurnAlerts.SessionOf(new(shown.Id, null, shown.ActivationData, false)), "the alerts of the sessions do not take it");

        // Each notice is a notification of its own: a second one does not replace the first.
        await notices.NotifyAsync(Run with { SessionId = null }, CancellationToken.None);
        Assert.AreNotEqual(surface.Shown[0].Id, surface.Shown[1].Id);
        Assert.IsTrue(DesktopNotices.IsNotice(new(surface.Shown[1].Id, null, surface.Shown[1].ActivationData, false), out session));
        Assert.IsNull(session, "without a session, a click brings CodeAlta to the front");
    }

    [TestMethod]
    public async Task InFrontOfTheWindow_OrWithoutNotificationsOfTheSystem_TheWindowShowsIt()
    {
        var surface = new FakeSurface { Foreground = true };
        var window = new List<(PluginNotification Notification, bool Waits)>();
        var notices = new DesktopNotices(surface, (notification, waits) => window.Add((notification, waits)));

        // The user sees the window: a notice there, which goes by itself.
        await notices.NotifyAsync(Run, CancellationToken.None);
        Assert.AreEqual((Run, false), window.Single());
        Assert.IsEmpty(surface.Shown);

        // In the background, with the notifications of the system turned off (the mark alone, or nothing), or a system that
        // has none: a notice that waits in the window, for a user who is not there.
        surface.Foreground = false;
        foreach (var (mode, status) in new[] { (DesktopTurnAlertMode.Mark, NeoDesktopStatus.Success), (DesktopTurnAlertMode.Off, NeoDesktopStatus.Success),
            (DesktopTurnAlertMode.All, NeoDesktopStatus.Unsupported) })
        {
            (surface.Mode, surface.NotifyStatus) = (mode, status);
            await notices.NotifyAsync(Run, CancellationToken.None);
            Assert.AreEqual((Run, true), window[^1], mode.ToString());
        }

        Assert.AreEqual(4, window.Count);
        Assert.IsEmpty(surface.Warnings, "what the platform does not have is no failure");

        // A system that fails is said once in the log, and the window shows each notice.
        surface.NotifyStatus = NeoDesktopStatus.Denied;
        await notices.NotifyAsync(Run, CancellationToken.None);
        await notices.NotifyAsync(Run, CancellationToken.None);
        Assert.AreEqual(6, window.Count);
        Assert.AreEqual(1, surface.Warnings.Count, string.Join(Environment.NewLine, surface.Warnings));
    }

    [TestMethod]
    public void TheNotification_KeepsWithinWhatEverySystemShows_AndOpensOnlyASessionItCanName()
    {
        var notification = DesktopNotices.NotificationOf(new PluginNotification { Title = new string('t', 100) + "\nsecond line", Body = new string('b', 1000), SessionId = "a b" }, 7);
        Assert.AreEqual(DesktopNotices.NotificationPrefix + "7", notification.Id);
        Assert.AreEqual(PluginNotification.MaximumTitleLength, notification.Title.Length);
        Assert.EndsWith("…", notification.Title);
        Assert.AreEqual(PluginNotification.MaximumBodyLength, notification.Body.Length);
        Assert.IsNull(notification.ActivationData, "an identity a notification cannot take opens nothing");
    }

    [TestMethod]
    public async Task APlugin_NotifiesOnTheSameChannel_AndItsMessageStaysAsItWas()
    {
        using var ui = new DesktopPluginUi();
        var events = new List<CodeAlta.Desktop.Rpc.PluginUiEvent>();
        using var watch = ui.Watch(events.Add);
        var plugin = ui.ForPlugin("plugin:sample");

        // Before the window has its notifications, and for a message without a title: a notice in the window.
        await plugin.NotifyAsync(Run with { Tone = PluginNotificationTone.Danger });
        await plugin.NotifyAsync("Plain message.");
        Assert.AreEqual(("notify", "Nightly review", Run.Body, "danger", Run.SessionId, false), (events[0].Kind, events[0].Title, events[0].Message, events[0].Tone, events[0].SessionId, events[0].Waits));
        Assert.AreEqual(("notify", (string?)null, "Plain message.", (string?)null), (events[1].Kind, events[1].Title, events[1].Message, events[1].Tone));

        // Once it has them, a plugin's notification goes where the others go: the system while CodeAlta is in the background.
        var surface = new FakeSurface();
        ui.Notices = new DesktopNotices(surface, ui.ShowNotice);
        await plugin.NotifyAsync(Run);
        Assert.AreEqual("Nightly review", surface.Shown.Single().Title);
        Assert.AreEqual(2, events.Count);
        surface.Foreground = true;
        await plugin.NotifyAsync(Run with { Tone = PluginNotificationTone.Info, Body = null });
        Assert.AreEqual(("Nightly review", (string?)null, (string?)null), (events[2].Title, events[2].Message, events[2].Tone));

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await plugin.NotifyAsync(Run with { Title = " " }));
    }

    private sealed class FakeSurface : IDesktopTurnAlertSurface
    {
        public DesktopTurnAlertMode Mode { get; set; } = DesktopTurnAlertMode.All;
        public bool Foreground { get; set; }
        public NeoDesktopStatus NotifyStatus { get; set; } = NeoDesktopStatus.Success;
        public List<NeoNotificationRequest> Shown { get; } = [];
        public List<string> Warnings { get; } = [];

        public bool IsForeground => Foreground;
        public IReadOnlyList<SessionRuntimeOverview> ReadSessions() => [];
        public ValueTask<IReadOnlyCollection<string>> ReadWaitingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyCollection<string>>([]);

        public ValueTask<NeoDesktopStatus> NotifyAsync(NeoNotificationRequest request, CancellationToken cancellationToken)
        {
            if (NotifyStatus == NeoDesktopStatus.Success) Shown.Add(request);
            return ValueTask.FromResult(NotifyStatus);
        }

        public ValueTask<NeoDesktopStatus> RemoveNotificationAsync(string id, CancellationToken cancellationToken) => ValueTask.FromResult(NeoDesktopStatus.Success);
        public ValueTask<NeoDesktopStatus> SetBadgeAsync(string? badge, CancellationToken cancellationToken) => ValueTask.FromResult(NeoDesktopStatus.Success);
        public ValueTask<NeoDesktopStatus> RequestAttentionAsync(CancellationToken cancellationToken) => ValueTask.FromResult(NeoDesktopStatus.Success);
        public void Warn(string message) => Warnings.Add(message);
    }
}
