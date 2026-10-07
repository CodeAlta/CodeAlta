using System.Globalization;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.LiveTool;

/// <summary>
/// The <c>alta appearance</c> commands: how a session is shown in the window. The settings of the window are the
/// user's; what a command changes is the view of one session, the calling one by default.
/// </summary>
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly AltaCommandPolicy[] AppearancePolicies =
    [
        Read("appearance get"),
        Mutating("appearance set"),
    ];

    private static Command CreateAppearanceCommand(AltaCommandContext context)
    {
        var group = Group("appearance", "Read and change how a session is shown in the window: the width of its conversation.");
        string? shownSession = null;
        var get = Leaf("get", "Show how a session is shown, and the user's own settings.");
        get.Add("session=", "The session. Defaults to the calling session.", value => shownSession = value);
        get.Add((_, _) => ValueTask.FromResult(HandleAppearanceGet(context, shownSession)));
        group.Add(get);

        string? sessionWidth = null, changedSession = null;
        var set = Leaf("set", "Change how one session is shown. It applies at once, to that session only, and leaves the user's settings as they are.");
        set.Add("session=", "The session. Defaults to the calling session.", value => changedSession = value);
        set.Add("session-width=", $"The width of the conversation (timeline and prompt), centered in the space of the session: a percentage from {IAltaAppearance.MinimumSessionWidth} to {IAltaAppearance.DefaultSessionWidth}, or `default` to follow the user's setting again.",
            value => sessionWidth = value);
        set.Add((_, _) => ValueTask.FromResult(HandleAppearanceSet(context, changedSession, sessionWidth)));
        group.Add(set);

        AddHelpText(
            group,
            "A change is for the view of one session, until the application exits or the user resizes that session. The settings of the window are the user's: they change them in Settings > Appearance, or by dragging an edge of the prompt.",
            "Change the view of a session only when the user asks for it.",
            "Examples: `alta appearance get`; `alta appearance set --session-width 70`; `alta appearance set --session-width default`.");
        return group;
    }

    private static int HandleAppearanceGet(AltaCommandContext context, string? session)
    {
        if (!TryGetAppearance(context, out var appearance)) return AltaExitCodes.ServiceUnavailable;
        WriteAppearance(context, appearance, AppearanceSession(context, session), changed: null);
        return AltaExitCodes.Success;
    }

    private static int HandleAppearanceSet(AltaCommandContext context, string? session, string? sessionWidth)
    {
        const string Command = "alta appearance set";
        const string Range = "--session-width is a percentage from 40 to 100, or `default`.";
        if (!TryGetAppearance(context, out var appearance)) return AltaExitCodes.ServiceUnavailable;
        if (AppearanceSession(context, session) is not { } sessionId)
        {
            return UsageError(context, "usage.missingSession", "Name the session to change with --session <id>: this caller is not a session.", Command);
        }

        var text = NormalizeOptionalText(sessionWidth);
        if (text is null) return UsageError(context, "usage.missingSetting", "Name what to change: --session-width <percent>.", Command);
        var percent = text.TrimEnd('%').Trim();
        int? width;
        if (string.Equals(percent, "default", StringComparison.OrdinalIgnoreCase) || string.Equals(percent, "reset", StringComparison.OrdinalIgnoreCase))
        {
            width = null;
        }
        else if (int.TryParse(percent, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value is >= IAltaAppearance.MinimumSessionWidth and <= IAltaAppearance.DefaultSessionWidth)
        {
            width = value;
        }
        else
        {
            return UsageError(context, "usage.invalidSessionWidth", Range, Command);
        }

        if (!appearance.SetSessionWidth(sessionId, width)) return UsageError(context, "usage.invalidSessionWidth", Range, Command);
        WriteAppearance(context, appearance, sessionId, changed: ["sessionWidth"]);
        return AltaExitCodes.Success;
    }

    // The session a command is about: the one it names, or the one that calls.
    private static string? AppearanceSession(AltaCommandContext context, string? session)
        => NormalizeOptionalText(session) ?? NormalizeOptionalText(context.Caller.SourceSessionId);

    private static bool TryGetAppearance(AltaCommandContext context, out IAltaAppearance appearance)
    {
        appearance = context.Services.Get<IAltaAppearance>()!;
        if (appearance is not null) return true;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'IAltaAppearance' is unavailable.");
        return false;
    }

    private static void WriteAppearance(AltaCommandContext context, IAltaAppearance appearance, string? sessionId, string[]? changed)
    {
        var own = sessionId is null ? null : appearance.GetSessionWidth(sessionId);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = changed is null ? "alta.appearance" : "alta.appearance.changed", version = 1, correlationId = context.CorrelationId, sessionId,
            // What the session is shown with, where it comes from, and the user's own setting.
            sessionWidth = own ?? appearance.SessionWidth, sessionWidthSource = own is null ? "user" : "session", userSessionWidth = appearance.SessionWidth,
            sessionWidthMinimum = IAltaAppearance.MinimumSessionWidth, sessionWidthMaximum = IAltaAppearance.DefaultSessionWidth, sessionWidthUnit = "percent", changed,
        });
    }
}
