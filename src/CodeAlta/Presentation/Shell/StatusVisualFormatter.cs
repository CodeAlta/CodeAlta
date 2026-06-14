using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Presentation.Shell;

internal static class StatusVisualFormatter
{
    public static string BuildThinkingStatusText() => SR.T("Thinking...");

    public static string BuildThinkingStatusText(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return SR.T("Thinking...");
        }

        return SR.T("Thinking for {0}...", FormatElapsedTime(elapsed));
    }

    public static bool IsThinkingStatusText(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           (string.Equals(message, SR.T("Thinking..."), StringComparison.Ordinal) ||
            string.Equals(message, "Thinking...", StringComparison.Ordinal) ||
            message.StartsWith("Thinking for ", StringComparison.Ordinal) ||
            message.StartsWith("已思考 ", StringComparison.Ordinal));

    public static string BuildPromptEditedStatusText() => SR.T("Draft edited...");

    public static string BuildStatusIconMarkup(StatusTone tone)
    {
        return tone switch
        {
            StatusTone.Ready => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Ready)}]{TerminalIcons.MdCheckCircleOutline}[/]",
            StatusTone.Warning => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Warning)}]{TerminalIcons.MdAlertOutline}[/]",
            StatusTone.Error => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Error)}]{TerminalIcons.MdAlertCircleOutline}[/]",
            _ => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Info)}]{TerminalIcons.OctInfo}[/]",
        };
    }

    public static string BuildPromptEditedIconMarkup()
        => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Info)}]{TerminalIcons.MdSquareEditOutline}[/]";

    public static TextBlockStyle BuildStatusTextStyle(Theme theme, string message, bool busy, StatusTone tone, float thinkingAnimationPhase01)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(message);

        if (busy && IsThinkingStatusText(message))
        {
            var sweepBrush = Brush.LinearGradient(
                new GradientPoint(-0.55f + (0.75f * thinkingAnimationPhase01), 0f),
                new GradientPoint(0.20f + (0.75f * thinkingAnimationPhase01), 0f),
                BuildThinkingGradientStops(theme),
                tileMode: BrushTileMode.Repeat,
                mixSpaceOverride: theme.GradientMixSpace);
            return TextBlockStyle.Default with { ForegroundBrush = sweepBrush };
        }

        return TextBlockStyle.Default with { Foreground = UiPalette.GetStatusToneColor(theme, tone) };
    }

    public static GradientStop[] BuildThinkingGradientStops(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var baseColor = UiPalette.GetStatusToneColor(theme, StatusTone.Info);
        var foreground = theme.Foreground?.ToRgb() ?? baseColor;
        var glowColor = Color.Mix(baseColor, foreground, 0.26f, ColorMixSpace.Oklab);
        var highlightColor = Color.Mix(baseColor, foreground, 0.52f, ColorMixSpace.Oklab);
        return
        [
            new GradientStop(0.00f, baseColor.WithOpacity(0.50f)),
            new GradientStop(0.12f, glowColor.WithOpacity(0.62f)),
            new GradientStop(0.22f, highlightColor),
            new GradientStop(0.34f, glowColor.WithOpacity(0.66f)),
            new GradientStop(0.48f, baseColor.WithOpacity(0.56f)),
            new GradientStop(0.62f, glowColor.WithOpacity(0.64f)),
            new GradientStop(0.74f, foreground),
            new GradientStop(0.86f, glowColor.WithOpacity(0.68f)),
            new GradientStop(1.00f, baseColor.WithOpacity(0.50f)),
        ];
    }

    private static string FormatElapsedTime(TimeSpan elapsed)
    {
        var totalSeconds = Math.Max(1, (int)Math.Floor(elapsed.TotalSeconds));
        var hours = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;

        var parts = new List<string>(capacity: 3);
        if (hours > 0)
        {
            parts.Add(FormatUnit(hours, "hour"));
        }

        if (minutes > 0)
        {
            parts.Add(FormatUnit(minutes, "minute"));
        }

        if ((hours == 0 && seconds > 0) || parts.Count == 0)
        {
            parts.Add(FormatUnit(seconds, "second"));
        }

        return string.Join(' ', parts);
    }

    private static string FormatUnit(int value, string unit)
        => value == 1 ? $"1 {unit}" : $"{value} {unit}s";
}
