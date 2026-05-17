namespace CodeAlta.App;

internal sealed partial class ShellThreadStateCoordinator
{
    private string? _previewThemeSchemeName;

    public string? EffectiveThemeSchemeName => _previewThemeSchemeName ?? NavigatorSettings.ThemeSchemeName;

    public void PreviewNavigatorTheme(string? themeSchemeName)
        => _previewThemeSchemeName = NormalizeThemeSchemeName(themeSchemeName);

    public void ClearNavigatorThemePreview()
        => _previewThemeSchemeName = null;

    private static string? NormalizeThemeSchemeName(string? themeSchemeName)
        => string.IsNullOrWhiteSpace(themeSchemeName) ? null : themeSchemeName.Trim();
}
