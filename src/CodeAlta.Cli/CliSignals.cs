using System.Runtime.InteropServices;

namespace CodeAlta.Cli;

/// <summary>
/// SIGINT (Ctrl+C) and SIGTERM: the first aborts the run, which ends with the reason <c>signal</c> and exit code 3; a
/// second one ends the process at once, with the same code.
/// </summary>
internal sealed class CliSignals : IDisposable
{
    private readonly List<PosixSignalRegistration> _registrations = [];
    private readonly Action _abort;
    private readonly Action _exit;
    private int _count;

    /// <summary>Handles the signals of the process.</summary>
    /// <param name="abort">Aborts the run, at the first signal.</param>
    /// <param name="exit">Ends the process, at the second.</param>
    public CliSignals(Action abort, Action exit)
    {
        ArgumentNullException.ThrowIfNull(abort);
        ArgumentNullException.ThrowIfNull(exit);
        _abort = abort;
        _exit = exit;
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM })
        {
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, Handle));
            }
            catch (PlatformNotSupportedException)
            {
                // A platform without the signal (SIGTERM on some Windows hosts): the others still abort the run.
            }
        }
    }

    /// <summary>Handles one signal: the default action of the process (its end) is replaced by the abort of the run.</summary>
    internal void Handle(PosixSignalContext context)
    {
        context.Cancel = true;
        Signal();
    }

    /// <summary>Counts a signal: the first aborts the run, the second ends the process.</summary>
    internal void Signal()
    {
        if (Interlocked.Increment(ref _count) == 1) _abort();
        else _exit();
    }

    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
    }
}
