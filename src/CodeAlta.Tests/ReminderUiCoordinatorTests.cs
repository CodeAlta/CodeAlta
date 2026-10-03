using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Tui.Models;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ReminderUiCoordinatorTests
{
    [TestMethod]
    public void SelectedSessionReminderCountReflectsDeletedReminder()
    {
        var service = new AltaReminderService(new AltaServiceCollection());
        var session = new SessionViewDescriptor { SessionId = "session-test", Title = "Session" };
        var refreshCount = 0;
        var dispatchCount = 0;
        using var coordinator = new ReminderUiCoordinator(
            service,
            new ReminderUiCoordinatorPort
            {
                GetSelectedSession = () => session,
                GetDialogBounds = static () => (Rectangle?)null,
                GetFocusTarget = static () => null,
                SetStatus = static (_, _) => { },
                DispatchToUi = action =>
                {
                    dispatchCount++;
                    action();
                },
                RefreshProjection = () => refreshCount++,
            });

        var reminder = service.Create(new AltaReminderCreateRequest
        {
            TargetSessionId = session.SessionId,
            Content = "follow up",
            Duration = TimeSpan.FromHours(1),
            RepeatCount = 1,
        });

        Assert.AreEqual(1, coordinator.GetSelectedSessionReminderCount());
        Assert.IsTrue(coordinator.HasActiveReminder(session.SessionId));

        Assert.IsTrue(service.TryDelete(reminder.ReminderId, out _));

        Assert.AreEqual(0, coordinator.GetSelectedSessionReminderCount());
        Assert.IsFalse(coordinator.HasActiveReminder(session.SessionId));
        Assert.AreEqual(2, dispatchCount);
        Assert.AreEqual(2, refreshCount);
    }
}
