using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Tui.Models;
using XenoAtom.Ansi;

namespace CodeAlta.Tui.Views;

// Runs only after the mutation returned. Presentation failures must never be labelled as
// mutation failures; the status presenter itself remains the frontend's responsibility.
internal static class ReminderPresentationFeedback
{
    public static ReminderFeedback AfterCommit(string committedMessage, AltaReminderNotificationFailure? failure,
        Action reload, Action refresh)
    {
        var message = committedMessage;
        var warning = failure is not null;
        if (failure is not null)
        {
            message += " " + DescribeFailure(failure);
        }

        TryRefresh(reload);
        TryRefresh(refresh);
        return new ReminderFeedback(message, warning ? StatusTone.Warning : StatusTone.Info);

        void TryRefresh(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                warning = true;
                string error;
                try
                {
                    error = ex.Message;
                }
                catch (Exception)
                {
                    error = SR.T("Error message unavailable.");
                }

                message += " " + SR.T("Refresh failed: {0}", error.Length > 512 ? error[..512] : error);
            }
        }
    }

    public static string HistoricalMarkup(AltaReminderNotificationFailure? failure, string sessionId)
    {
        if (failure is null || !string.Equals(failure.TargetSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var message = SR.T("Latest retained notification failure: reminder {0}, change {1}, firing count {2}. {3}",
            failure.ReminderId, failure.ChangeKind.ToString(), failure.FiredCount, DescribeFailure(failure));
        return $"[warning]{AnsiMarkup.Escape(message)}[/]";
    }

    private static string DescribeFailure(AltaReminderNotificationFailure failure)
        => SR.T("Notification failed ({0} observer(s)): {1}", failure.FailureCount, string.Join("; ", failure.Messages))
           + (failure.MessagesTruncated ? " " + SR.T("Diagnostic messages were shortened or omitted.") : string.Empty);
}

internal sealed record ReminderFeedback(string Message, StatusTone Tone)
{
    public string Markup => $"[{(Tone == StatusTone.Warning ? "warning" : "success")}]{AnsiMarkup.Escape(Message)}[/]";
}
