using System.Runtime.InteropServices;

namespace CodeAlta.Desktop;

/// <summary>
/// SIGTERM and SIGINT of an automated start, on macOS and Linux: the exit another program asks for, which waits for the
/// sessions at work instead of ending the process at once. On Windows a windowed application gets no such signal: a
/// program closes its window (WM_CLOSE) or runs <c>alta --exit</c>.
/// </summary>
internal sealed class DesktopExitSignals : IDisposable
{
    private readonly List<PosixSignalRegistration> _registrations = [];

    private DesktopExitSignals()
    {
    }

    /// <summary>Has the two signals ask <paramref name="exit"/>; null on Windows, or where signals cannot be handled.</summary>
    /// <param name="exit">Asks the application to exit; called on a thread of the pool, once per signal.</param>
    internal static DesktopExitSignals? Start(Action exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        if (OperatingSystem.IsWindows()) return null;
        var signals = new DesktopExitSignals();
        try
        {
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
            {
                signals._registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    // The process does not end here: the exit ends it, once the sessions have.
                    context.Cancel = true;
                    exit();
                }));
            }

            return signals;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
        {
            signals.Dispose();
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
    }
}
