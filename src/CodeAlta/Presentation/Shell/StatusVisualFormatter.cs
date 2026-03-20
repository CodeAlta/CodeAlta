using CodeAlta.Models;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Presentation.Shell;

internal static class StatusVisualFormatter
{
    private const string ThinkingStatusMessage = "Thinking...";

    public static string BuildThinkingStatusText() => ThinkingStatusMessage;

    public static string BuildStatusIconMarkup(StatusTone tone)
    {
        return tone switch
        {
            StatusTone.Ready => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Ready)}]{NerdFont.MdCheckCircleOutline}[/]",
            StatusTone.Warning => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Warning)}]{NerdFont.MdAlertOutline}[/]",
            StatusTone.Error => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Error)}]{NerdFont.MdAlertCircleOutline}[/]",
            _ => $"[{UiPalette.GetStatusToneMarkup(StatusTone.Info)}]{NerdFont.OctInfo}[/]",
        };
    }

    public static TextBlockStyle BuildStatusTextStyle(string message, bool busy, StatusTone tone)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (busy && string.Equals(message, ThinkingStatusMessage, StringComparison.Ordinal))
        {
            var phase = WelcomePaneFactory.ComputeLoopAnimationPhase(DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 5L);
            var sweepBrush = Brush.LinearGradient(
                new GradientPoint(-0.55f + (0.75f * phase), 0f),
                new GradientPoint(0.20f + (0.75f * phase), 0f),
                BuildThinkingGradientStops(),
                tileMode: BrushTileMode.Repeat,
                mixSpaceOverride: ColorMixSpace.Oklab);
            return TextBlockStyle.Default with { ForegroundBrush = sweepBrush };
        }

        return TextBlockStyle.Default with { Foreground = UiPalette.GetStatusToneColor(tone) };
    }

    public static GradientStop[] BuildThinkingGradientStops()
    {
        var baseColor = UiPalette.GetStatusToneColor(StatusTone.Info);
        var glowColor = Color.Mix(baseColor, Colors.White, 0.26f, ColorMixSpace.Oklab);
        var highlightColor = Color.Mix(baseColor, Colors.White, 0.52f, ColorMixSpace.Oklab);
        return
        [
            new GradientStop(0.00f, baseColor.WithOpacity(0.50f)),
            new GradientStop(0.12f, glowColor.WithOpacity(0.62f)),
            new GradientStop(0.22f, highlightColor),
            new GradientStop(0.34f, glowColor.WithOpacity(0.66f)),
            new GradientStop(0.48f, baseColor.WithOpacity(0.56f)),
            new GradientStop(0.62f, glowColor.WithOpacity(0.64f)),
            new GradientStop(0.74f, Colors.White),
            new GradientStop(0.86f, glowColor.WithOpacity(0.68f)),
            new GradientStop(1.00f, baseColor.WithOpacity(0.50f)),
        ];
    }
}