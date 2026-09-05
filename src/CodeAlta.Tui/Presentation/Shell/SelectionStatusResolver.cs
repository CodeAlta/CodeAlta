using CodeAlta.Tui.Models;

namespace CodeAlta.Tui.Presentation.Shell;

internal static class SelectionStatusResolver
{
    public static StatusSnapshot Resolve(
        string readyMessage,
        bool hasSessionStatus,
        string? sessionStatusMessage,
        bool sessionStatusBusy,
        StatusTone sessionStatusTone,
        bool promptEdited,
        bool promptUnavailable,
        string? promptUnavailableMessage,
        StatusTone promptUnavailableTone)
    {
        if (hasSessionStatus && !string.IsNullOrWhiteSpace(sessionStatusMessage))
        {
            return new StatusSnapshot(sessionStatusMessage!, sessionStatusBusy, sessionStatusTone);
        }

        if (promptUnavailable && !string.IsNullOrWhiteSpace(promptUnavailableMessage))
        {
            return new StatusSnapshot(promptUnavailableMessage!, Busy: false, promptUnavailableTone);
        }

        if (promptEdited)
        {
            return new StatusSnapshot(
                StatusVisualFormatter.BuildPromptEditedStatusText(),
                Busy: false,
                StatusTone.Info,
                StatusVisualFormatter.BuildPromptEditedIconMarkup());
        }

        return new StatusSnapshot(readyMessage, Busy: false, StatusTone.Ready);
    }
}
