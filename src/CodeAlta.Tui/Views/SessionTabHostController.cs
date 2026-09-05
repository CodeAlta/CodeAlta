namespace CodeAlta.Tui.Views;

internal sealed record SessionTabHostController(Action<int> SelectTab)
{
    public static SessionTabHostController Create(Action<int> selectTab)
    {
        ArgumentNullException.ThrowIfNull(selectTab);
        return new SessionTabHostController(selectTab);
    }
}
