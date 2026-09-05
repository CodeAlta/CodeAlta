using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.Tui.Presentation.Styling;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tui.Views;

internal static class UiTheme
{
    public static CodeAltaShellView Set(CodeAltaShellView shellView, ShellSessionStateCoordinator stateCoordinator)
    {
        ArgumentNullException.ThrowIfNull(shellView);
        ArgumentNullException.ThrowIfNull(stateCoordinator);

        ApplyLanguage(stateCoordinator.NavigatorSettings);

        string? cachedSchemeName = null;
        var cachedTheme = CodeAltaThemeResolver.Resolve(stateCoordinator.NavigatorSettings);
        Theme ResolveTheme()
        {
            var schemeName = stateCoordinator.EffectiveThemeSchemeName;
            if (!string.Equals(cachedSchemeName, schemeName, StringComparison.Ordinal))
            {
                cachedSchemeName = schemeName;
                cachedTheme = CodeAltaThemeResolver.Resolve(schemeName);
                shellView.Root.App?.RequestFullRender();
            }

            return cachedTheme;
        }

        shellView.Root.Style(ResolveTheme);
        if (shellView.Root is CodeAltaRootView rootView)
        {
            rootView.AddAttachedToAppCallback((_, app) =>
            {
                if (!ReferenceEquals(app.Root, shellView.Root))
                {
                    app.Root.Style(ResolveTheme);
                }
            });
        }

        return shellView;
    }

    public static void ApplyLanguage(NavigatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplyLanguage(settings.LanguageName);
    }

    private static void ApplyLanguage(string? languageName)
    {
        if (!string.IsNullOrWhiteSpace(languageName))
        {
            SR.Language = languageName;
            return;
        }

        SR.AutoDetect();
    }
}
