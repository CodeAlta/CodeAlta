using CodeAlta.Catalog;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Figlet;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Presentation.Shell;

internal static class WelcomePaneFactory
{
    private static readonly Lazy<FigletFont> WelcomeFigletFont = new(LoadWelcomeFigletFont);

    public static Visual Build(ProjectDescriptor? selectedProject, bool globalScopeSelected)
    {
        var guidanceLines = ShellTextFormatter.BuildWelcomeGuidanceLines(selectedProject, globalScopeSelected);
        return new Center(
            new VStack(
            [
                BuildWelcomeLogo(),
                new TextBlock(ShellTextFormatter.BuildWelcomeSubtitle(selectedProject, globalScopeSelected))
                    {
                        Wrap = true,
                        IsSelectable = false,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = Align.Stretch,
                    }
                    .Style(TextBlockStyle.Default with
                    {
                        Foreground = UiPalette.WelcomeSubtitleColor,
                        TextStyle = TextStyle.Bold,
                    }),
                new TextBlock(guidanceLines[0])
                    {
                        Wrap = true,
                        IsSelectable = false,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = Align.Stretch,
                    }
                    .Style(TextBlockStyle.Default with
                    {
                        Foreground = UiPalette.WelcomeGuidanceColor,
                    }),
                new TextBlock($"{NerdFont.MdArrowRightThinCircleOutline} {guidanceLines[1]}\n{NerdFont.MdTabPlus} {guidanceLines[2]}")
                    {
                        Wrap = true,
                        IsSelectable = false,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = Align.Stretch,
                    }
                    .Style(TextBlockStyle.Default with
                    {
                        Foreground = UiPalette.WelcomeGuidanceColor,
                    }),
            ])
            {
                Spacing = 1,
                HorizontalAlignment = Align.Center,
                VerticalAlignment = Align.Center,
                MaxWidth = 76,
            });
    }

    public static FigletFont GetWelcomeFigletFont()
    {
        return WelcomeFigletFont.Value;
    }

    public static float ComputeLoopAnimationPhase(long ticks, long cycleTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycleTicks);

        var normalizedTicks = ((ticks % cycleTicks) + cycleTicks) % cycleTicks;
        return (float)(normalizedTicks / (double)cycleTicks);
    }

    private static FigletFont LoadWelcomeFigletFont()
    {
        using var stream = typeof(WelcomePaneFactory).Assembly.GetManifestResourceStream("CodeAlta.Assets.3d.flf");
        if (stream is null)
        {
            throw new InvalidOperationException("Unable to load embedded welcome FIGlet font 'CodeAlta.Assets.3d.flf'.");
        }

        using var reader = new StreamReader(stream);
        return FigletFont.Parse(reader.ReadToEnd(), new FigletFontInfo("3-D", "Daniel Henninger"));
    }

    private static Visual BuildWelcomeLogo()
    {
        var font = GetWelcomeFigletFont();
        return new Center(
            new HStack(
            [
                new TextFiglet("Code")
                    .Font(font)
                    .LetterSpacing(1)
                    .TrimTrailingSpaces(true)
                    .TextAlignment(TextAlignment.Left),
                new TextFiglet("Alta")
                    .Font(font)
                    .LetterSpacing(1)
                    .TrimTrailingSpaces(true)
                    .TextAlignment(TextAlignment.Left)
                    .Style(BuildWelcomeAltaFigletStyle),
            ])
            {
                Spacing = 2,
                HorizontalAlignment = Align.Center,
            });
    }

    private static TextFigletStyle BuildWelcomeAltaFigletStyle()
    {
        var phase = ComputeLoopAnimationPhase(DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 6L);
        return TextFigletStyle.Default with
        {
            ForegroundBrush = UiPalette.BuildWelcomeAltaBrush(phase),
        };
    }
}