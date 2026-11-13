using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CodeAlta.Desktop;

/// <summary>
/// Keeps the application alive through a signal that is not meant to end a windowed program.
/// </summary>
internal static class DesktopProcessSignals
{
    // Held for the life of the process, so the registration stays in force; disposing it would restore the
    // default action.
    private static PosixSignalRegistration? _hangup;

    /// <summary>
    /// Ignores <c>SIGHUP</c>. CodeAlta starts child command-line tools (an ACP agent such as the Cursor agent,
    /// the Claude Code CLI, the Codex CLI, …) in its own process group; some of them send <c>SIGHUP</c> to that
    /// group when they stop, and the default action of <c>SIGHUP</c> would end the application with it. A
    /// windowed program started from the desktop (the Dock, launchd, a desktop entry) has no terminal whose
    /// closing it should answer, and manages its own lifetime, so the hangup is ignored. A child resets the
    /// signal to its default when it is started (on <c>exec</c>), so this does not change how the children
    /// handle it; and a start from a terminal keeps leaving its session as before (<see cref="DesktopTerminalStart"/>).
    /// </summary>
    /// <remarks>Does nothing after the first call: the registration is kept for the life of the process.</remarks>
    [UnsupportedOSPlatform("windows")]
    internal static void IgnoreHangup()
    {
        _hangup ??= PosixSignalRegistration.Create(PosixSignal.SIGHUP, static context => context.Cancel = true);
    }
}
