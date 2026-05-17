using CodeAlta.Models;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Presentation.Styling
{
    internal enum SidebarAccent
    {
        Global,
        Projects,
        ProjectThread,
        InternalThread,
        CopilotThread,
        Fallback,
    }

    internal static class UiPalette
    {
        private static readonly Color WelcomeAltaAccent0 = Color.Rgb(0x00, 0xD1, 0xFF);
        private static readonly Color WelcomeAltaAccent1 = Color.Rgb(0x4F, 0x46, 0xE5);
        private static readonly Color WelcomeAltaAccent2 = Color.Rgb(0xA8, 0x55, 0xF7);
        private static readonly Color WelcomeAltaAccentBright0 = Color.Rgb(0x7A, 0xE8, 0xFF);
        private static readonly Color WelcomeAltaHighlight = Color.Rgb(0xFF, 0xFF, 0xFF);

        internal static GroupStyle GetChatGroupStyle(Theme theme, ChatTimelineTone tone)
        {
            ArgumentNullException.ThrowIfNull(theme);

            var border = GetChatToneColor(theme, tone);
            var focusBorder = MixTowardForeground(theme, border, 0.12f);
            var isEmphasizedTone = tone is ChatTimelineTone.Interaction or ChatTimelineTone.Notice;
            var background = GetToneOverlay(
                theme,
                border,
                IsLightTheme(theme) ? (isEmphasizedTone ? 0.245f : 0.225f) : (isEmphasizedTone ? 0.034f : 0.024f));

            return GroupStyle.Rounded with
            {
                BorderCellStyle = Style.None.WithForeground(border),
                FocusedBorderCellStyle = Style.None.WithForeground(focusBorder) | TextStyle.Bold,
                BackgroundStyle = Style.None.WithBackground(background),
            };
        }

        internal static GroupStyle GetToolCallGroupStyle(Theme theme)
        {
            ArgumentNullException.ThrowIfNull(theme);

            var muted = GetThemeColor(theme.Muted, GetThemeColor(theme.Border, GetThemeColor(theme.Foreground, Color.Default)));
            var border = Color.Mix(muted, GetThemeColor(theme.Foreground, muted), IsLightTheme(theme) ? 0.08f : 0.12f, ColorMixSpace.Oklab);
            var focusedBorder = MixTowardForeground(theme, border, 0.18f);
            var background = GetNeutralOverlay(theme, IsLightTheme(theme) ? 0.105f : 0.032f);

            return GroupStyle.Rounded with
            {
                BorderCellStyle = Style.None.WithForeground(border),
                FocusedBorderCellStyle = Style.None.WithForeground(focusedBorder) | TextStyle.Bold,
                BackgroundStyle = Style.None.WithBackground(background),
            };
        }

        internal static GroupStyle GetSidebarGroupStyle(Theme theme)
        {
            ArgumentNullException.ThrowIfNull(theme);

            var border = GetThemeColor(theme.Border, GetThemeColor(theme.Muted, Color.Default));
            var focusedBorder = MixTowardForeground(theme, border, 0.10f);
            var surface = GetThemeColor(theme.Surface, GetThemeColor(theme.Background, Color.Default));
            return GroupStyle.Rounded with
            {
                BorderCellStyle = Style.None.WithForeground(border),
                FocusedBorderCellStyle = Style.None.WithForeground(focusedBorder) | TextStyle.Bold,
                BackgroundStyle = Style.None.WithBackground(surface),
            };
        }

        internal static ButtonStyle GetToolChipButtonStyle(Theme theme, ToolCallDisplayStatus status)
        {
            ArgumentNullException.ThrowIfNull(theme);

            var foreground = GetThemeColor(theme.Foreground, Color.Default);
            var accent = GetToolStatusColor(theme, status);
            var background = GetNeutralOverlay(theme, IsLightTheme(theme) ? 0.115f : 0.048f);
            var hover = Color.Mix(background, accent, 0.16f, ColorMixSpace.Oklab);
            var pressed = Color.Mix(background, accent, 0.24f, ColorMixSpace.Oklab);

            return new ButtonStyle
            {
                Padding = new Thickness(1, 0, 1, 0),
                ShowBorder = false,
                Normal = Style.None.WithForeground(foreground).WithBackground(background),
                Hovered = Style.None.WithForeground(foreground).WithBackground(hover),
                Pressed = Style.None.WithForeground(foreground).WithBackground(pressed) | TextStyle.Bold,
                Focused = Style.None.WithForeground(foreground).WithBackground(hover) | TextStyle.Underline,
            };
        }

        internal static string GetToolStatusMarkup(ToolCallDisplayStatus status)
        {
            return status switch
            {
                ToolCallDisplayStatus.Running => "primary",
                ToolCallDisplayStatus.Completed => "success",
                ToolCallDisplayStatus.Failed => "error",
                ToolCallDisplayStatus.Canceled => "warning",
                _ => "muted",
            };
        }

        internal static string GetStatusToneMarkup(StatusTone tone)
        {
            return tone switch
            {
                StatusTone.Ready => "success",
                StatusTone.Warning => "warning",
                StatusTone.Error => "error",
                _ => "primary",
            };
        }

        internal static string GetSidebarAccentMarkup(SidebarAccent accent)
        {
            return accent switch
            {
                SidebarAccent.Global => "warning",
                SidebarAccent.Projects => "primary",
                SidebarAccent.ProjectThread => "success",
                SidebarAccent.InternalThread => "accent",
                SidebarAccent.CopilotThread => "accent",
                _ => "muted",
            };
        }

        internal static Color GetStatusToneColor(Theme theme, StatusTone tone)
        {
            ArgumentNullException.ThrowIfNull(theme);

            return tone switch
            {
                StatusTone.Ready => GetSchemeAccent(theme, static scheme => scheme.Green, GetThemeColor(theme.Success, Color.Default)),
                StatusTone.Warning => GetSchemeAccent(theme, static scheme => scheme.Yellow, GetThemeColor(theme.Warning, Color.Default)),
                StatusTone.Error => GetSchemeAccent(theme, static scheme => scheme.Red, GetThemeColor(theme.Error, Color.Default)),
                _ => GetSchemeAccent(theme, static scheme => scheme.Blue, GetThemeColor(theme.Primary, GetThemeColor(theme.Accent, GetThemeColor(theme.Foreground, Color.Default)))),
            };
        }

        internal static Style GetSidebarIconStyle(SidebarAccent accent)
        {
            return Style.None.WithForeground(GetSidebarAccentColor(accent));
        }

        internal static Color GetQueuedPromptBackgroundColor(Theme theme)
        {
            ArgumentNullException.ThrowIfNull(theme);
            return GetToneOverlay(
                theme,
                GetSchemeAccent(theme, static scheme => scheme.Purple, GetThemeColor(theme.Accent, GetThemeColor(theme.Primary, Color.Default))),
                IsLightTheme(theme) ? 0.145f : 0.028f);
        }

        internal static Color GetPendingSteerBackgroundColor(Theme theme)
        {
            ArgumentNullException.ThrowIfNull(theme);
            return GetToneOverlay(theme, GetStatusToneColor(theme, StatusTone.Info), IsLightTheme(theme) ? 0.155f : 0.032f);
        }

        internal static string MutedMarkup => "muted";

        internal static Color GetPromptPlaceholderColor(Theme theme)
        {
            ArgumentNullException.ThrowIfNull(theme);
            return GetThemeColor(theme.Muted, GetThemeColor(theme.Foreground, Color.Default));
        }

        internal static Color GetWelcomeSubtitleColor(Theme theme)
        {
            ArgumentNullException.ThrowIfNull(theme);
            return MixTowardForeground(theme, GetThemeColor(theme.Muted, GetThemeColor(theme.Foreground, Color.Default)), 0.18f);
        }

        internal static Color GetWelcomeGuidanceColor(Theme theme)
        {
            ArgumentNullException.ThrowIfNull(theme);
            return GetThemeColor(theme.Muted, GetThemeColor(theme.Foreground, Color.Default));
        }

        internal static Brush BuildWelcomeAltaBrush(Theme theme, float phase)
        {
            ArgumentNullException.ThrowIfNull(theme);

            var start = new GradientPoint(-0.55f + phase, -0.08f + phase);
            var end = new GradientPoint(0.45f + phase, 0.92f + phase);
            return Brush.LinearGradient(
                start,
                end,
                BuildWelcomeAltaGradientStops(theme),
                tileMode: BrushTileMode.Repeat,
                mixSpaceOverride: ColorMixSpace.Oklab);
        }

        internal static GradientStop[] BuildWelcomeAltaGradientStops(Theme theme)
        {
            ArgumentNullException.ThrowIfNull(theme);
            _ = theme;

            var edgeColor = Color.Mix(WelcomeAltaAccent0, WelcomeAltaAccentBright0, 0.34f, ColorMixSpace.Oklab)
                .WithOpacity(0.82f);
            var targetColor = Color.Mix(WelcomeAltaAccent1, WelcomeAltaAccent2, 0.54f, ColorMixSpace.Oklab);
            var shoulderColor = Color.Mix(edgeColor, targetColor, 0.58f, ColorMixSpace.Oklab);
            var centerColor = Color.Mix(targetColor, WelcomeAltaHighlight, 0.18f, ColorMixSpace.Oklab);
            var pulseColor = Color.Mix(centerColor, WelcomeAltaHighlight, 0.30f, ColorMixSpace.Oklab);
            return
            [
                new GradientStop(0.00f, edgeColor),
                new GradientStop(0.10f, shoulderColor),
                new GradientStop(0.18f, pulseColor),
                new GradientStop(0.30f, shoulderColor),
                new GradientStop(0.42f, centerColor),
                new GradientStop(0.50f, pulseColor),
                new GradientStop(0.58f, centerColor),
                new GradientStop(0.70f, shoulderColor),
                new GradientStop(0.82f, pulseColor),
                new GradientStop(0.90f, shoulderColor),
                new GradientStop(1.00f, edgeColor),
            ];
        }

        private static Color GetToolStatusColor(Theme theme, ToolCallDisplayStatus status)
        {
            return status switch
            {
                ToolCallDisplayStatus.Running => GetStatusToneColor(theme, StatusTone.Info),
                ToolCallDisplayStatus.Completed => GetStatusToneColor(theme, StatusTone.Ready),
                ToolCallDisplayStatus.Failed => GetStatusToneColor(theme, StatusTone.Error),
                ToolCallDisplayStatus.Canceled => GetStatusToneColor(theme, StatusTone.Warning),
                _ => GetThemeColor(theme.Muted, GetThemeColor(theme.Foreground, Color.Default)),
            };
        }

        internal static Color GetSidebarAccentColor(SidebarAccent accent)
        {
            return accent switch
            {
                SidebarAccent.Global => ConsoleColor.DarkYellow,
                SidebarAccent.Projects => ConsoleColor.Blue,
                SidebarAccent.ProjectThread => ConsoleColor.Green,
                SidebarAccent.InternalThread => ConsoleColor.Magenta,
                SidebarAccent.CopilotThread => ConsoleColor.Magenta,
                _ => ConsoleColor.DarkGray,
            };
        }

        private static Color GetChatToneColor(Theme theme, ChatTimelineTone tone)
        {
            return tone switch
            {
                ChatTimelineTone.User => GetSchemeAccent(theme, static scheme => scheme.Purple, GetThemeColor(theme.Accent, Color.Default)),
                ChatTimelineTone.Assistant => GetSchemeAccent(theme, static scheme => scheme.Blue, GetThemeColor(theme.Primary, Color.Default)),
                ChatTimelineTone.Reasoning => GetSchemeAccent(theme, static scheme => Color.Mix(scheme.Blue, scheme.BrightBlack, 0.78f, ColorMixSpace.Oklab), GetThemeColor(theme.Border, Color.Default)),
                ChatTimelineTone.Activity => GetSchemeAccent(theme, static scheme => scheme.BrightBlack, GetThemeColor(theme.Muted, Color.Default)),
                ChatTimelineTone.Notice => GetSchemeAccent(theme, static scheme => scheme.Green, GetThemeColor(theme.Success, Color.Default)),
                ChatTimelineTone.Interaction => GetSchemeAccent(theme, static scheme => scheme.Yellow, GetThemeColor(theme.Warning, Color.Default)),
                _ => GetSchemeAccent(theme, static scheme => scheme.Blue, GetThemeColor(theme.Primary, Color.Default)),
            };
        }

        private static Color GetSchemeAccent(Theme theme, Func<ColorScheme, Color> selector, Color fallback)
        {
            if (theme.Scheme is not { } scheme)
            {
                return fallback;
            }

            var foreground = GetThemeColor(theme.Foreground, fallback);
            return Color.Mix(selector(scheme).ToRgb(), foreground, IsLightTheme(theme) ? 0.22f : 0.12f, ColorMixSpace.Oklab);
        }

        private static Color GetNeutralOverlay(Theme theme, float amount)
        {
            var background = GetThemeColor(theme.Background, Color.Default);
            var foreground = GetThemeColor(theme.Foreground, background);
            return Color.Mix(background, foreground, amount, ColorMixSpace.Oklab);
        }

        private static Color GetToneOverlay(Theme theme, Color color, float amount)
        {
            if (IsLightTheme(theme) && theme.Background is { } themeBackground)
            {
                var background = themeBackground.ToRgb();
                if (background.Kind is ColorKind.Rgb or ColorKind.RgbA)
                {
                    return Color.Mix(background, color.ToRgb(), amount, ColorMixSpace.Oklab);
                }
            }

            return color.WithOpacity(amount);
        }

        private static bool IsLightTheme(Theme theme)
        {
            if (theme.Background is not { } background || theme.Foreground is not { } foreground)
            {
                return false;
            }

            return background.GetRelativeLuminance() > foreground.GetRelativeLuminance();
        }

        private static Color MixTowardForeground(Theme theme, Color color, float amount)
            => Color.Mix(color, GetThemeColor(theme.Foreground, color), amount, ColorMixSpace.Oklab);

        private static Color GetThemeColor(Color? color, Color fallback)
            => color?.ToRgb() ?? fallback;
    }
}
