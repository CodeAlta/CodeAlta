using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.Views;

internal sealed record SessionWorkspaceChromeController(
    Func<Visual> BuildSessionUsageIndicatorVisual,
    Func<Visual?>? BuildPluginSessionStatusVisual,
    Action<Visual> ToggleSessionInfoPopup,
    Action OpenModelProviders,
    Func<int> GetActiveReminderCount,
    Action OpenReminders)
{
    public static SessionWorkspaceChromeController Create(
        Func<Visual> buildSessionUsageIndicatorVisual,
        Func<Visual?>? buildPluginSessionStatusVisual,
        Action<Visual> toggleSessionInfoPopup,
        Action openModelProviders,
        Func<int> getActiveReminderCount,
        Action openReminders)
    {
        ArgumentNullException.ThrowIfNull(buildSessionUsageIndicatorVisual);
        ArgumentNullException.ThrowIfNull(toggleSessionInfoPopup);
        ArgumentNullException.ThrowIfNull(openModelProviders);
        ArgumentNullException.ThrowIfNull(getActiveReminderCount);
        ArgumentNullException.ThrowIfNull(openReminders);
        return new SessionWorkspaceChromeController(buildSessionUsageIndicatorVisual, buildPluginSessionStatusVisual, toggleSessionInfoPopup, openModelProviders, getActiveReminderCount, openReminders);
    }

    public static SessionWorkspaceChromeController Empty { get; } = new(
        static () => new XenoAtom.Terminal.UI.Controls.TextBlock(string.Empty),
        null,
        static _ => { },
        static () => { },
        static () => 0,
        static () => { });
}
