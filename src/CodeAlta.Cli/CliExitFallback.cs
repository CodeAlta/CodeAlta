namespace CodeAlta.Cli;

/// <summary>
/// The last resort of a run asked to end (a signal, its timeout): the abort and the shutdown of the host are bounded, but
/// should the run still not have returned once its bound has passed, the result is written (the JSONL stream keeps the
/// first result only), the output is flushed, and the process exits with the code of the result. Disarmed once the run
/// has returned and disposed its host.
/// </summary>
internal sealed class CliExitFallback : IDisposable
{
    private readonly CliOutput _output;
    private readonly CliJsonlWriter? _jsonl;
    private readonly Action<int>? _exit;
    private readonly Lock _gate = new();
    private readonly List<Timer> _timers = [];
    private bool _done;
    // The result of a run that ended, which wins over the one an arming foresaw.
    private CliRunReport? _settled;

    /// <summary>Creates the fallback of a run.</summary>
    /// <param name="output">The output of the run.</param>
    /// <param name="jsonl">The JSONL stream of the run; null in text mode.</param>
    /// <param name="exit">What ends the process; null for no fallback.</param>
    public CliExitFallback(CliOutput output, CliJsonlWriter? jsonl, Action<int>? exit)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _jsonl = jsonl;
        _exit = exit;
    }

    /// <summary>Ends the process with a result once a delay has passed, unless the run has returned by then.</summary>
    public void Arm(TimeSpan delay, CliRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (_exit is null) return;
        lock (_gate)
        {
            if (_done) return;
            _timers.Add(new Timer(_ => Fire(report), null, delay, Timeout.InfiniteTimeSpan));
        }
    }

    /// <summary>Records the result of the run once it is known: a fallback that fires after it exits with its code.</summary>
    public void Settle(CliRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate) _settled = report;
    }

    private void Fire(CliRunReport armed)
    {
        CliRunReport report;
        lock (_gate)
        {
            if (_done) return;
            _done = true;
            report = _settled ?? armed;
        }

        _output.Error($"altacli: {report.Status}: {report.FailureMessage ?? "the run did not end in time."} Exiting.");
        _jsonl?.WriteResult(report);
        _output.Flush();
        _exit!(report.ExitCode);
    }

    /// <summary>Disarms the fallback: the run has returned.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _done = true;
            foreach (var timer in _timers) timer.Dispose();
            _timers.Clear();
        }
    }
}
