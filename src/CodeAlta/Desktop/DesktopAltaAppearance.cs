using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// What the <c>alta appearance</c> commands read and change of the window: the user's setting is read, and one
/// session is given a width of its own.
/// </summary>
internal sealed class DesktopAltaAppearance : IAltaAppearance
{
    private readonly DesktopShell _shell;

    /// <exception cref="ArgumentNullException"><paramref name="shell"/> is null.</exception>
    internal DesktopAltaAppearance(DesktopShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
    }

    /// <inheritdoc />
    public int SessionWidth => _shell.SessionWidth;

    /// <inheritdoc />
    public int? GetSessionWidth(string sessionId) => _shell.SessionWidthOf(sessionId);

    /// <inheritdoc />
    public bool SetSessionWidth(string sessionId, int? percent) => _shell.SetSessionWidthOf(sessionId, percent);
}
