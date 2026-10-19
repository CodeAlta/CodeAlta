using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// Watches what every call of the page needs: the thread of the window, through which each call enters and leaves the
/// host, and the thread pool, on which its handler runs. A heartbeat is posted to each every <see cref="Interval"/>; a
/// heartbeat that waits longer than <see cref="StallAfter"/> is a stall, logged when it starts and again with its length
/// once it ran. The checks run on a thread of their own, so a starved pool cannot hide its own stall. It only watches.
/// </summary>
internal sealed class DesktopUiWatchdog : IDisposable
{
    /// <summary>How often a heartbeat is posted.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a heartbeat may wait before it is a stall: what a loaded computer does not reach by itself.</summary>
    internal static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(2);

    private readonly Probe[] _probes;
    private readonly TimeProvider _clock;
    private readonly Action<string> _warn;
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset? _lastCheck;
    private readonly Thread? _thread;

    /// <summary>Starts watching the thread of the window, reached by <paramref name="post"/>, and the thread pool.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="post"/> is null.</exception>
    internal DesktopUiWatchdog(Action<Action> post)
        : this(
            [
                ("thread of the window", post),
                ("thread pool", static callback => ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state!)(), callback)),
            ],
            TimeProvider.System, static message => Logger.Warn(message), static message => Logger.Info(message), start: true)
    {
    }

    /// <summary>Creates a watchdog of its tests: their probes, clock and log; <paramref name="start"/> false leaves the checks to <see cref="Check"/>.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal DesktopUiWatchdog(IReadOnlyList<(string Name, Action<Action> Post)> probes, TimeProvider clock, Action<string> warn, Action<string> info, bool start)
        : this([.. probes.Select(static probe => new Probe(probe.Name, probe.Post))], clock, warn, info, start)
    {
    }

    private DesktopUiWatchdog(Probe[] probes, TimeProvider clock, Action<string> warn, Action<string> info, bool start)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(warn);
        ArgumentNullException.ThrowIfNull(info);
        _probes = probes;
        _clock = clock;
        _warn = warn;
        foreach (var probe in _probes) probe.Attach(clock, warn, info);
        if (!start) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "CodeAlta watchdog", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private static Logger Logger => LogManager.GetLogger("CodeAlta.Desktop.Watchdog");

    /// <summary>Posts the heartbeats that do not wait, and looks at those that do.</summary>
    internal void Check()
    {
        // The checks come every interval: a check that comes much later says the whole process was held (a computer
        // that is overloaded or that slept, a process suspended), the watchdog with it. Its heartbeats could not see it.
        var now = _clock.GetUtcNow();
        if (_lastCheck is { } last && now - last >= Interval + StallAfter)
        {
            try { _warn($"CodeAlta did not run for {(now - last).TotalSeconds:0.0} s: the whole process was held, the calls of the page with it."); }
            catch { /* The log never stops the watch. */ }
        }

        _lastCheck = now;
        foreach (var probe in _probes) probe.Check();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        foreach (var probe in _probes) probe.Stop();
    }

    private void Run()
    {
        while (!_stop.Token.WaitHandle.WaitOne(Interval)) Check();
    }

    private sealed class Probe(string name, Action<Action> post)
    {
        private readonly Lock _gate = new();
        private TimeProvider _clock = TimeProvider.System;
        private Action<string> _warn = static _ => { };
        private Action<string> _info = static _ => { };
        private DateTimeOffset? _posted;
        private bool _stalled;
        private bool _stopped;

        internal void Attach(TimeProvider clock, Action<string> warn, Action<string> info)
        {
            ArgumentNullException.ThrowIfNull(post);
            (_clock, _warn, _info) = (clock, warn, info);
        }

        internal void Stop()
        {
            lock (_gate) _stopped = true;
        }

        internal void Check()
        {
            lock (_gate)
            {
                if (_stopped) return;
                var now = _clock.GetUtcNow();
                if (_posted is { } posted)
                {
                    var waited = now - posted;
                    if (!_stalled && waited >= StallAfter)
                    {
                        _stalled = true;
                        Safe(_warn, $"The {name} has not run for {waited.TotalSeconds:0.0} s: the calls of the page wait for it.");
                    }

                    return;
                }

                _posted = now;
            }

            try
            {
                post(Beat);
            }
            catch (Exception exception)
            {
                // A window that is closing takes no more callbacks: nothing to watch there any more.
                lock (_gate) { _posted = null; _stopped = true; }
                Safe(_info, $"The {name} takes no heartbeat any more ({exception.GetType().Name}).");
            }
        }

        private void Beat()
        {
            lock (_gate)
            {
                if (_posted is not { } posted) return;
                var waited = _clock.GetUtcNow() - posted;
                _posted = null;
                if (_stalled)
                {
                    _stalled = false;
                    Safe(_info, $"The {name} runs again after {waited.TotalSeconds:0.0} s.");
                }
                else if (waited >= StallAfter)
                {
                    // The whole process was held (a computer that suspends it, a debugger): the heartbeat ran before a
                    // check saw it wait. It is a stall all the same.
                    Safe(_warn, $"The {name} did not run for {waited.TotalSeconds:0.0} s: the calls of the page waited for it.");
                }
            }
        }

        private static void Safe(Action<string> log, string message)
        {
            try { log(message); }
            catch { /* The log never stops the watch. */ }
        }
    }
}
