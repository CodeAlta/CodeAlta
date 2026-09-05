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

    [TestMethod]
    public void Source_InvalidatesReminderBadgeMarkupWhenReminderServiceChanges()
    {
        var source = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ReminderUiCoordinator.cs"));

        StringAssert.Contains(source, "private readonly State<int> _reminderVersion = new(0);");
        StringAssert.Contains(source, "var _ = _reminderVersion.Value;");
        StringAssert.Contains(source, "_reminderVersion.Value++;");
    }

    private static string GetCodeAltaSourceRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(directory, "CodeAlta.Tui", "CodeAlta.Tui.csproj");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find CodeAlta.Tui source root.");
    }
}
