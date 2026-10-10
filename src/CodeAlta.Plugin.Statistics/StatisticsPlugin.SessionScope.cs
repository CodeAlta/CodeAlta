using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Statistics;

// The canvas of one session and its sub-agents: its key, the line of the menu of a session, the button of the card of a turn,
// and the command both run.
public sealed partial class StatisticsPlugin
{
    /// <summary>
    /// The name of the command that opens the canvas for one session and its sub-agents. The line of the menu of a session runs it for
    /// the session of the row, and the button of the card of a turn for the session of the card.
    /// </summary>
    internal const string SessionCommandName = "statistics-session";

    /// <summary>The start of the key of a canvas limited to a session and its sub-agents: <c>session:&lt;id&gt;</c>.</summary>
    internal const string SessionKeyPrefix = "session:";

    /// <summary>The words of the button at the foot of the card of a turn.</summary>
    internal const string SessionLinkText = "Session statistics";

    /// <summary>The most characters of the title of a session in the title of its tab.</summary>
    internal const int MaximumSessionTitleLength = 48;

    /// <summary>The most characters of the details of a card that get the form with the button: a longer one is shown as it was.</summary>
    internal const int MaximumLinkedDetailsLength = 12_000;

    /// <summary>Gets the session a key of a canvas names (<c>session:&lt;id&gt;</c>), or null.</summary>
    internal static string? SessionOfKey(string? key)
        => key is not null && key.StartsWith(SessionKeyPrefix, StringComparison.Ordinal) && key.Length > SessionKeyPrefix.Length ? key[SessionKeyPrefix.Length..] : null;

    // The line of the menu of a session row: the host gives the command the session of the row, whatever is selected.
    private static PluginButtonContribution SessionMenuButton()
        => PluginUi.Button(PluginButtonPlace.SessionMenu, SessionCommandName, "chart-column", "Statistics of this session") with
        {
            Command = SessionCommandName,
        };

    private PluginCommandContribution SessionCommand()
        => Command.Shell(SessionCommandName, "Opens the Statistics canvas for one session and its sub-agents.", SessionCommandAsync) with
        {
            Label = "Statistics of the session",
            Availability = PluginCommandAvailability.SessionSelected,
            ShowInCommandPalette = false,
            ShowInCommandBar = false,
            ShowInHelp = false,
        };

    private async ValueTask<PluginCommandResult> SessionCommandAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.SessionId))
        {
            return PluginCommandResult.Message("Choose a session first.");
        }

        var result = await Services.Canvases.OpenAsync(CanvasId, new PluginCanvasOpenOptions { Key = SessionKeyPrefix + context.SessionId.Trim() }, cancellationToken).ConfigureAwait(false);
        return result.Requested ? PluginCommandResult.Handled : PluginCommandResult.Message("The Statistics canvas cannot be shown here.");
    }

    // The title of the tab of a session: its title when the statistics have read it, the start of its identifier otherwise.
    private async ValueTask<string> SessionTabTitleAsync(string sessionId, CancellationToken cancellationToken)
    {
        string? title = null;
        if (_engine is { } engine)
        {
            try
            {
                await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
                title = await engine.Queries.SessionTitleAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The tab opens under the identifier: its questions say what is wrong with the store.
                Logger.Debug($"The title of a session could not be read for its Statistics tab: {exception.Message}");
            }
        }

        return "Statistics: " + SessionLabel(title, sessionId);
    }

    /// <summary>The name a session is shown under: its title, cut to <see cref="MaximumSessionTitleLength"/> characters, or the start of its identifier.</summary>
    internal static string SessionLabel(string? title, string sessionId)
    {
        var text = string.IsNullOrWhiteSpace(title) ? null : string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text is null)
        {
            return sessionId.Length > 8 ? sessionId[..8] : sessionId;
        }

        if (text.Length <= MaximumSessionTitleLength)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[MaximumSessionTitleLength - 2]) ? MaximumSessionTitleLength - 2 : MaximumSessionTitleLength - 1;
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>
    /// The details of the card of a turn as CodeAlta Desktop shows them: the same Markdown, drawn by the window, then the button
    /// that opens the statistics of the session of the card. The fragment has no script: the button names a command, which the
    /// window runs for the session whose timeline shows the card.
    /// </summary>
    /// <param name="markdown">The Markdown of the details.</param>
    /// <returns>The fragment; null when the details are too long to be given twice, and stay Markdown.</returns>
    internal static string? LinkedDetailsHtml(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (markdown.Length > MaximumLinkedDetailsLength)
        {
            return null;
        }

        return $"<div class=\"{PluginHtml.ColumnClass}\"><div class=\"{PluginHtml.MarkdownClass}\">{PluginHtml.Encode(markdown)}</div>"
            + $"<div class=\"{PluginHtml.RowClass}\">{PluginHtml.CommandButton(SessionCommandName, SessionLinkText)}</div></div>";
    }
}
