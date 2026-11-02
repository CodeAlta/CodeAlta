using System.Runtime.InteropServices;

namespace CodeAlta.Node;

/// <summary>
/// SIGINT (Ctrl+C) and SIGTERM: the first stops the node gracefully, the second ends the wait for running turns, the
/// third ends the process at once.
/// </summary>
internal sealed class NodeSignals : IDisposable
{
    private readonly List<PosixSignalRegistration> _registrations = [];
    private readonly Action _stop;
    private readonly Action _hurry;
    private readonly Action _exit;
    private int _count;

    /// <summary>Handles the signals of the process.</summary>
    /// <param name="stop">Stops the node, at the first signal.</param>
    /// <param name="hurry">Ends the wait for running turns, at the second.</param>
    /// <param name="exit">Ends the process, at the third.</param>
    public NodeSignals(Action stop, Action hurry, Action exit)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(hurry);
        ArgumentNullException.ThrowIfNull(exit);
        _stop = stop;
        _hurry = hurry;
        _exit = exit;
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM })
        {
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, Handle));
            }
            catch (PlatformNotSupportedException)
            {
                // A platform without the signal: the others still stop the node.
            }
        }
    }

    private void Handle(PosixSignalContext context)
    {
        // The default action (the end of the process) is replaced by a graceful stop.
        context.Cancel = true;
        Signal();
    }

    /// <summary>Counts a signal: the first stops the node, the second hurries the stop, the third exits.</summary>
    internal void Signal()
    {
        switch (Interlocked.Increment(ref _count))
        {
            case 1: _stop(); break;
            case 2: _hurry(); break;
            default: _exit(); break;
        }
    }

    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
    }
}
