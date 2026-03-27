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
        private static readonly Color UserBorder = Color.FromOklch(0.74f, 0.16f, 315f);
        private static readonly Color AssistantBorder = Color.FromOklch(0.82f, 0.08f, 245f);
        private static readonly Color ReasoningBorder = Color.FromOklch(0.56f, 0.10f, 255f);
        private static readonly Color ActivityBorder = Color.FromOklch(0.68f, 0.02f, 255f);
        private static readonly Color NoticeBorder = Color.FromOklch(0.80f, 0.08f, 155f);
        private static readonly Color InteractionBorder = Color.FromOklch(0.84f, 0.10f, 85f);

        private static readonly Color StatusReady = Color.FromOklch(0.78f, 0.09f, 152f);
        private static readonly Color StatusWarning = Color.FromOklch(0.82f, 0.10f, 85f);
        private static readonly Color StatusError = Color.FromOklch(0.70f, 0.15f, 28f);
        private static readonly Color StatusInfo = Color.FromOklch(0.77f, 0.09f, 245f);
        private static readonly Color StatusMuted = Color.FromOklch(0.72f, 0.02f, 255f);
        internal static readonly Color WelcomeAccent0 = Color.Rgb(0x00, 0xD1, 0xFF);
        internal static readonly Color WelcomeAccent1 = Color.Rgb(0x4F, 0x46, 0xE5);
        internal static readonly Color WelcomeAccent2 = Color.Rgb(0xA8, 0x55, 0xF7);
        internal static readonly Color WelcomeAccentBright0 = Color.Rgb(0x7A, 0xE8, 0xFF);
        private static readonly Color SidebarGlobal = Color.FromOklch(0.82f, 0.10f, 85f);
        private static readonly Color SidebarProjects = Color.FromOklch(0.79f, 0.08f, 245f);
        private static readonly Color SidebarProjectThread = Color.FromOklch(0.78f, 0.08f, 152f);
        private static readonly Color SidebarInternalThread = Color.FromOklch(0.73f, 0.10f, 310f);
        private static readonly Color SidebarCopilotThread = Color.FromOklch(0.76f, 0.14f, 300f);
        private static readonly Color SidebarFallback = Color.FromOklch(0.90f, 0.01f, 255f);
        private static readonly Color PromptPlaceholder = Color.FromOklch(0.65f, 0.02f, 255f);

        private static readonly Color ToolChipText = Color.FromOklch(0.90f, 0.01f, 255f);
        private static readonly Color ToolChipNeutral = Color.FromOklch(0.56f, 0.02f, 255f);
        private static readonly Color ToolGroupBorder = Color.Mix(ActivityBorder, AssistantBorder, 0.30f, ColorMixSpace.Oklch);
        private static readonly Color ToolGroupBackground = Color.Mix(Colors.Black, ToolGroupBorder, 0.48f, ColorMixSpace.Oklch).WithOpacity(0.28f);
        private static readonly Color QueuedPromptBackground = Color.Mix(Colors.Black, UserBorder, 0.34f, ColorMixSpace.Oklch).WithOpacity(0.36f);
        private static readonly Color PendingSteerBackground = Color.Mix(Colors.Black, StatusInfo, 0.36f, ColorMixSpace.Oklch).WithOpacity(0.40f);

        internal static GroupStyle GetChatGroupStyle(ChatTimelineTone tone)
        {
            var border = tone switch
            {
                ChatTimelineTone.User => UserBorder,
                ChatTimelineTone.Assistant => AssistantBorder,
                ChatTimelineTone.Reasoning => ReasoningBorder,
                ChatTimelineTone.Activity => ActivityBorder,
                ChatTimelineTone.Notice => NoticeBorder,
                ChatTimelineTone.Interaction => InteractionBorder,
                _ => AssistantBorder,
            };

            var background = tone switch
            {
                ChatTimelineTone.User => border.WithOpacity(0.08f),
                ChatTimelineTone.Assistant => border.WithOpacity(0.06f),
                ChatTimelineTone.Reasoning => border.WithOpacity(0.04f),
                ChatTimelineTone.Activity => border.WithOpacity(0.06f),
                ChatTimelineTone.Notice => border.WithOpacity(0.08f),
                ChatTimelineTone.Interaction => border.WithOpacity(0.10f),
                _ => border.WithOpacity(0.06f),
            };

            return GroupStyle.Rounded with
            {
                BorderCellStyle = Style.None.WithForeground(border),
                FocusedBorderCellStyle = Style.None.WithForeground(border.Lighten(0.06f)) | TextStyle.Bold,
                BackgroundStyle = Style.None.WithBackground(background),
            };
        }

        internal static GroupStyle GetToolCallGroupStyle()
        {
            return GroupStyle.Rounded with
            {
                BorderCellStyle = Style.None.WithForeground(ToolGroupBorder),
                FocusedBorderCellStyle = Style.None.WithForeground(ToolGroupBorder.Lighten(0.08f)) | TextStyle.Bold,
                BackgroundStyle = Style.None.WithBackground(ToolGroupBackground),
            };
        }

        internal static ButtonStyle GetToolChipButtonStyle(ToolCallDisplayStatus status)
        {
            var accent = GetToolStatusColor(status);
            var background = Color.Mix(Colors.Black, accent, 0.12f, ColorMixSpace.Oklch).WithOpacity(0.28f);
            var hover = Color.Mix(Colors.Black, accent, 0.18f, ColorMixSpace.Oklch).WithOpacity(0.34f);
            var pressed = Color.Mix(Colors.Black, accent, 0.24f, ColorMixSpace.Oklch).WithOpacity(0.40f);

            return new ButtonStyle
            {
                Padding = new Thickness(1, 0, 1, 0),
                ShowBorder = false,
                Normal = Style.None.WithForeground(ToolChipText).WithBackground(background),
                Hovered = Style.None.WithForeground(ToolChipText).WithBackground(hover),
                Pressed = Style.None.WithForeground(ToolChipText).WithBackground(pressed) | TextStyle.Bold,
                Focused = Style.None.WithForeground(ToolChipText).WithBackground(hover) | TextStyle.Underline,
            };
        }

        internal static string GetToolStatusMarkup(ToolCallDisplayStatus status)
            => GetMarkupColor(GetToolStatusColor(status));

        internal static string GetStatusToneMarkup(StatusTone tone)
        {
            return GetMarkupColor(GetStatusToneColor(tone));
        }

        internal static Color GetStatusToneColor(StatusTone tone)
        {
            return tone switch
            {
                StatusTone.Ready => StatusReady,
                StatusTone.Warning => StatusWarning,
                StatusTone.Error => StatusError,
                _ => StatusInfo,
            };
        }

        internal static Style GetSidebarIconStyle(SidebarAccent accent)
        {
            var color = accent switch
            {
                SidebarAccent.Global => SidebarGlobal,
                SidebarAccent.Projects => SidebarProjects,
                SidebarAccent.ProjectThread => SidebarProjectThread,
                SidebarAccent.InternalThread => SidebarInternalThread,
                SidebarAccent.CopilotThread => SidebarCopilotThread,
                _ => SidebarFallback,
            };

            return Style.None.WithForeground(color);
        }

        internal static Color QueuedPromptBackgroundColor => QueuedPromptBackground;

        internal static Color PendingSteerBackgroundColor => PendingSteerBackground;

        internal static string MutedMarkup => GetMarkupColor(StatusMuted);

        internal static Color PromptPlaceholderColor => PromptPlaceholder;

        internal static Color WelcomeSubtitleColor => Color.Mix(StatusMuted, Colors.White, 0.28f, ColorMixSpace.Oklab);

        internal static Color WelcomeGuidanceColor => Color.Mix(StatusMuted, Colors.White, 0.14f, ColorMixSpace.Oklab);

        internal static GradientStop[] BuildWelcomeAltaGradientStops()
        {
            var edgeColor = Color.Mix(WelcomeAccent0, WelcomeAccentBright0, 0.34f, ColorMixSpace.Oklab)
                .WithOpacity(0.82f);
            var targetColor = Color.Mix(WelcomeAccent1, WelcomeAccent2, 0.54f, ColorMixSpace.Oklab);
            var shoulderColor = Color.Mix(edgeColor, targetColor, 0.58f, ColorMixSpace.Oklab);
            var centerColor = Color.Mix(targetColor, Colors.White, 0.18f, ColorMixSpace.Oklab);
            var pulseColor = Color.Mix(centerColor, Colors.White, 0.30f, ColorMixSpace.Oklab);
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

        internal static Brush BuildWelcomeAltaBrush(float phase)
        {
            var start = new GradientPoint(-0.55f + phase, -0.08f + phase);
            var end = new GradientPoint(0.45f + phase, 0.92f + phase);
            return Brush.LinearGradient(
                start,
                end,
                BuildWelcomeAltaGradientStops(),
                tileMode: BrushTileMode.Repeat,
                mixSpaceOverride: ColorMixSpace.Oklab);
        }

        private static Color GetToolStatusColor(ToolCallDisplayStatus status)
        {
            return status switch
            {
                ToolCallDisplayStatus.Running => StatusInfo,
                ToolCallDisplayStatus.Completed => StatusReady,
                ToolCallDisplayStatus.Failed => StatusError,
                ToolCallDisplayStatus.Canceled => StatusWarning,
                _ => ToolChipNeutral,
            };
        }

        private static string GetMarkupColor(Color color)
            => color.ToRgb().ToHexString();
    }
}
