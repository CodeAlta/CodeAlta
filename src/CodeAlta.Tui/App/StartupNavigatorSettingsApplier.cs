using CodeAlta.Tui.Views;
using XenoAtom.Logging;

namespace CodeAlta.Tui.App;

internal static class StartupNavigatorSettingsApplier
{
    public static void Apply(ShellSessionStateCoordinator sessionStateCoordinator, Logger logger)
    {
        ArgumentNullException.ThrowIfNull(sessionStateCoordinator);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var settings = sessionStateCoordinator.LoadNavigatorSettingsAsync(CancellationToken.None).GetAwaiter().GetResult();
            sessionStateCoordinator.ApplyNavigatorSettingsSnapshot(settings);
            UiTheme.ApplyLanguage(settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error(ex, "Failed to load navigator settings before UI startup.");
            UiTheme.ApplyLanguage(sessionStateCoordinator.NavigatorSettings);
        }
    }
}
