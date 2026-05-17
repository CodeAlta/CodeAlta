using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Presentation.Styling;

internal static class CodeAltaThemeResolver
{
    public static Theme Resolve(NavigatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Resolve(settings.ThemeSchemeName);
    }

    public static Theme Resolve(string? colorSchemeName)
    {
        if (string.IsNullOrWhiteSpace(colorSchemeName))
        {
            return CreateTheme(ColorScheme.RootLoopsDark);
        }

        var scheme = FindPredefinedScheme(colorSchemeName);
        return scheme is null
            ? CreateTheme(ColorScheme.RootLoopsDark)
            : CreateTheme(scheme);
    }

    public static IReadOnlyList<ColorScheme> GetSelectableSchemes()
        => GetResolvableSchemes()
            .Where(static scheme =>
                !string.Equals(scheme.Name, ColorScheme.RootLoopsDark.Name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scheme.Name, ColorScheme.RootLoopsLight.Name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scheme.Name, ColorScheme.Terminal.Name, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(static scheme => scheme.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static ColorScheme? FindPredefinedScheme(string colorSchemeName)
    {
        var normalizedName = colorSchemeName.Trim();
        foreach (var scheme in GetResolvableSchemes())
        {
            if (string.Equals(scheme.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                return scheme;
            }
        }

        return null;
    }

    private static IEnumerable<ColorScheme> GetResolvableSchemes()
    {
        yield return ColorScheme.RootLoopsDark;
        yield return ColorScheme.RootLoopsLight;
        yield return ColorScheme.Terminal;

        foreach (var scheme in ColorScheme.GetPredefinedSchemes())
        {
            yield return scheme;
        }
    }

    private static Theme CreateTheme(ColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        var baseTheme = Theme.FromScheme(scheme);
        if (scheme.Background is not { } schemeBackground ||
            scheme.Foreground is not { } schemeForeground ||
            schemeBackground.Kind is not (ColorKind.Rgb or ColorKind.RgbA) ||
            schemeForeground.Kind is not (ColorKind.Rgb or ColorKind.RgbA))
        {
            return baseTheme;
        }

        var background = schemeBackground.ToRgb();
        var foreground = schemeForeground.ToRgb();
        var isLight = IsLight(background, foreground);
        var accent = AdaptAccent(scheme.Blue, foreground, isLight);

        return new Theme
        {
            Scheme = scheme,
            Foreground = foreground,
            Background = background,
            Surface = MixSurface(background, foreground, isLight ? 0.022f : 0.040f),
            SurfaceAlt = MixSurface(background, foreground, isLight ? 0.040f : 0.062f),
            PopupSurface = MixSurface(background, foreground, isLight ? 0.030f : 0.070f),
            ControlFill = MixSurface(background, foreground, isLight ? 0.052f : 0.078f),
            ControlFillHover = MixSurface(background, foreground, isLight ? 0.082f : 0.112f),
            ControlFillPressed = MixSurface(background, foreground, isLight ? 0.116f : 0.152f),
            InputFill = MixSurface(background, foreground, isLight ? 0.035f : 0.026f),
            InputFillFocused = MixSurface(background, foreground, isLight ? 0.048f : 0.038f),
            Border = MixSurface(background, foreground, isLight ? 0.24f : 0.22f),
            FocusBorder = accent,
            Accent = accent,
            Selection = Color.Mix(background, accent, isLight ? 0.18f : 0.24f, ColorMixSpace.Oklab),
            Disabled = Color.Mix(foreground, background, 0.55f, ColorMixSpace.Oklab),
            Primary = AdaptAccent(scheme.Blue, foreground, isLight),
            Success = AdaptAccent(scheme.Green, foreground, isLight),
            Warning = AdaptAccent(scheme.Yellow, foreground, isLight),
            Error = AdaptAccent(scheme.Red, foreground, isLight),
            Muted = Color.Mix(foreground, background, isLight ? 0.42f : 0.36f, ColorMixSpace.Oklab),
            GradientMixSpace = baseTheme.GradientMixSpace,
            Lines = baseTheme.Lines,
            ScrollBars = baseTheme.ScrollBars,
        };
    }

    private static bool IsLight(Color background, Color foreground)
        => background.GetRelativeLuminance() > foreground.GetRelativeLuminance() &&
           background.GetRelativeLuminance() >= 0.50f;

    private static Color MixSurface(Color background, Color foreground, float amount)
        => Color.Mix(background, foreground, amount, ColorMixSpace.Oklab);

    private static Color AdaptAccent(Color color, Color foreground, bool isLight)
        => Color.Mix(color.ToRgb(), foreground, isLight ? 0.22f : 0.12f, ColorMixSpace.Oklab);
}
