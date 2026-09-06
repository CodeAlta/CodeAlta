using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Views;
using XenoAtom.Ansi;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ReminderPresentationFeedbackTests
{
    [TestMethod]
    public void CommittedCreation_ReportsObserverFailureWithoutLosingSuccess()
    {
        using var fixture = new PresentationFixture();
        fixture.Service.Changed += ThrowObserver;
        var reminder = fixture.Create(out var failure);
        var refreshes = 0;
        var committed = SR.T("Reminder created.");

        var feedback = ReminderPresentationFeedback.AfterCommit(committed, failure, () => refreshes++, () => refreshes++);

        Assert.AreEqual(2, refreshes);
        Assert.AreEqual(StatusTone.Warning, feedback.Tone);
        StringAssert.StartsWith(feedback.Message, committed);
        StringAssert.Contains(feedback.Message, "[bold]observer text[/]");
        StringAssert.Contains(feedback.Markup, AnsiMarkup.Escape("[bold]observer text[/]"));
        Assert.AreEqual(reminder.ReminderId, fixture.Service.List(null, true).Single().ReminderId);
    }

    [TestMethod]
    public void CommittedEdit_AttemptsBothRefreshesAndLabelsTheirFailuresAsFeedback()
    {
        using var fixture = new PresentationFixture();
        var reminder = fixture.Create(out _);
        Assert.IsTrue(fixture.Service.TryUpdateContent(reminder.ReminderId, "committed text", out _, out var failure));
        var refreshCalled = false;
        var committed = SR.T("Reminder message updated.");

        var feedback = ReminderPresentationFeedback.AfterCommit(committed, failure,
            () => throw new ArgumentException("reload [bold] failed"),
            () =>
            {
                refreshCalled = true;
                throw new OperationCanceledException("refresh canceled");
            });

        Assert.IsTrue(refreshCalled);
        Assert.AreEqual(StatusTone.Warning, feedback.Tone);
        StringAssert.StartsWith(feedback.Message, committed);
        StringAssert.Contains(feedback.Message, SR.T("Refresh failed: {0}", "reload [bold] failed"));
        StringAssert.Contains(feedback.Message, SR.T("Refresh failed: {0}", "refresh canceled"));
        StringAssert.Contains(feedback.Markup, AnsiMarkup.Escape("reload [bold] failed"));
        Assert.AreEqual("committed text", fixture.Service.List(null, true).Single().ContentPreview);
        Assert.IsNull(fixture.Service.GetLastNotificationFailure(), "Local refresh errors are not service observer failures.");
    }

    [TestMethod]
    public void HistoricalFailure_IsSessionScopedAndRemainsVisibleAfterDeletion()
    {
        using var fixture = new PresentationFixture();
        var reminder = fixture.Create(out _);
        fixture.Service.Changed += ThrowObserver;
        fixture.Service.TryDelete(reminder.ReminderId, out _, out var failure);

        var markup = ReminderPresentationFeedback.HistoricalMarkup(failure, "target-session");

        StringAssert.Contains(markup, reminder.ReminderId);
        StringAssert.Contains(markup, "Deleted");
        StringAssert.Contains(markup, AnsiMarkup.Escape("[bold]observer text[/]"));
        Assert.AreEqual(string.Empty, ReminderPresentationFeedback.HistoricalMarkup(failure, "other-session"));
        Assert.AreEqual(0, fixture.Service.List(null, true).Count);
    }

    [TestMethod]
    public void SuccessfulFeedback_HasInfoToneAndNoHistoricalWarning()
    {
        var feedback = ReminderPresentationFeedback.AfterCommit("committed", null, () => { }, () => { });

        Assert.AreEqual(StatusTone.Info, feedback.Tone);
        Assert.AreEqual("committed", feedback.Message);
        Assert.AreEqual("[success]committed[/]", feedback.Markup);
        Assert.AreEqual(string.Empty, ReminderPresentationFeedback.HistoricalMarkup(null, "session"));
    }

    [TestMethod]
    public void RefreshWithThrowingMessageGetter_UsesFallbackAndContinues()
    {
        var refreshed = false;
        var feedback = ReminderPresentationFeedback.AfterCommit("committed", null,
            () => throw new UnreadableMessageException(), () => refreshed = true);

        Assert.IsTrue(refreshed);
        Assert.AreEqual(StatusTone.Warning, feedback.Tone);
        StringAssert.Contains(feedback.Message, SR.T("Error message unavailable."));
    }

    private static void ThrowObserver(object? sender, EventArgs args)
        => throw new OperationCanceledException("[bold]observer text[/]");

    private sealed class UnreadableMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException();
    }

    private sealed class PresentationFixture : IDisposable
    {
        private readonly AltaReminderServiceTests.ManualClock _clock = new();

        public PresentationFixture()
        {
            // No clock advancement and no command contributors/runtime or visual construction.
            var services = new AltaServiceCollection();
            services.Add(new AltaCommandDispatcher(new AltaCommandRegistry([]), services));
            Service = new AltaReminderService(services, _clock);
        }

        public AltaReminderService Service { get; }

        public AltaReminderDescriptor Create(out AltaReminderNotificationFailure? failure)
            => Service.Create(new AltaReminderCreateRequest
            {
                TargetSessionId = "Target-Session",
                Content = "original",
                Duration = TimeSpan.FromMinutes(1),
                RepeatCount = 1,
            }, out failure);

        public void Dispose()
        {
            Service.Changed -= ThrowObserver;
            foreach (var reminder in Service.List(null, true))
            {
                Service.TryDelete(reminder.ReminderId, out _);
            }

            _clock.Dispose();
        }
    }
}
