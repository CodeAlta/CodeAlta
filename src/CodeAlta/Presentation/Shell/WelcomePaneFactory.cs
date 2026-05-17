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

    public static Visual Build(ProjectDescriptor? selectedProject, bool globalScopeSelected, State<float> welcomeAnimationPhase01)
    {
        ArgumentNullException.ThrowIfNull(welcomeAnimationPhase01);

        var guidanceLines = ShellTextFormatter.BuildWelcomeGuidanceLines(selectedProject, globalScopeSelected);
        TextBlock? subtitle = null;
        subtitle = new TextBlock(ShellTextFormatter.BuildWelcomeSubtitle(selectedProject, globalScopeSelected))
            {
                Wrap = true,
                IsSelectable = false,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = Align.Stretch,
            }
            .Style(() => TextBlockStyle.Default with
            {
                Foreground = UiPalette.GetWelcomeSubtitleColor(subtitle!.GetTheme()),
                TextStyle = TextStyle.Bold,
            });
        TextBlock? firstGuidance = null;
        firstGuidance = new TextBlock(guidanceLines[0])
            {
                Wrap = true,
                IsSelectable = false,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = Align.Stretch,
            }
            .Style(() => TextBlockStyle.Default with
            {
                Foreground = UiPalette.GetWelcomeGuidanceColor(firstGuidance!.GetTheme()),
            });
        TextBlock? nextGuidance = null;
        nextGuidance = new TextBlock($"{NerdFont.MdArrowRightThinCircleOutline} {guidanceLines[1]}\n{NerdFont.MdTabPlus} {guidanceLines[2]}")
            {
                Wrap = true,
                IsSelectable = false,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = Align.Stretch,
            }
            .Style(() => TextBlockStyle.Default with
            {
                Foreground = UiPalette.GetWelcomeGuidanceColor(nextGuidance!.GetTheme()),
            });

        return new Center(
            new VStack(
            [
                BuildWelcomeLogo(welcomeAnimationPhase01),
                subtitle,
                firstGuidance,
                nextGuidance,
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

    private static Visual BuildWelcomeLogo(State<float> welcomeAnimationPhase01)
    {
        ArgumentNullException.ThrowIfNull(welcomeAnimationPhase01);

        var font = GetWelcomeFigletFont();
        return new Center(
            new HStack(
            [
                new TextFiglet("Code")
                    .Font(font)
                    .LetterSpacing(1)
                    .TrimTrailingSpaces(true)
                    .TextAlignment(TextAlignment.Left),
                CreateWelcomeAltaFiglet(welcomeAnimationPhase01, font),
            ])
            {
                Spacing = 2,
                HorizontalAlignment = Align.Center,
            });
    }

    private static TextFiglet CreateWelcomeAltaFiglet(State<float> welcomeAnimationPhase01, FigletFont font)
    {
        TextFiglet? altaFiglet = null;
        altaFiglet = new TextFiglet("Alta")
                    .Font(font)
                    .LetterSpacing(1)
                    .TrimTrailingSpaces(true)
                    .TextAlignment(TextAlignment.Left)
                    .Style(() => BuildWelcomeAltaFigletStyle(altaFiglet!.GetTheme(), welcomeAnimationPhase01.Value));
        return altaFiglet;
    }

    private static TextFigletStyle BuildWelcomeAltaFigletStyle(Theme theme, float phase)
    {
        return TextFigletStyle.Default with
        {
            ForegroundBrush = UiPalette.BuildWelcomeAltaBrush(theme, phase),
        };
    }
}
