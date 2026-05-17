using CodeAlta.App;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal static class UiTheme
{
    public static CodeAltaShellView Set(CodeAltaShellView shellView, ShellThreadStateCoordinator stateCoordinator)
    {
        ArgumentNullException.ThrowIfNull(shellView);
        ArgumentNullException.ThrowIfNull(stateCoordinator);

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
}
