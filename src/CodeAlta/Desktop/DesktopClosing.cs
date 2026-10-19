namespace CodeAlta.Desktop;

/// <summary>
/// Whether the host of the window is closing, since when, and the step of its close it waits for. The page reads it
/// with <c>boot.status</c>: a host that closes keeps its window until its close ends, and without this the page only
/// sees its calls fail. After <see cref="ForceExitDelay"/> the page may end the process (<see cref="CanForceExit"/>).
/// </summary>
internal sealed class DesktopClosing
{
    /// <summary>How long a close runs before the page offers to end the process.</summary>
    internal static readonly TimeSpan ForceExitDelay = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private DateTimeOffset? _since;
    private string? _step;

    /// <summary>Creates the state of a host that is not closing.</summary>
    internal DesktopClosing() : this(TimeProvider.System) { }

    /// <summary>Creates the state of a host that is not closing, with the clock its tests choose.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
    internal DesktopClosing(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>The close was asked for: the first call keeps its time.</summary>
    internal void Begin()
    {
        lock (_gate) _since ??= _clock.GetUtcNow();
    }

    /// <summary>The step of the close that runs now, such as <c>sessions</c>; null once the close has nothing left to wait for.</summary>
    internal void Step(string? step)
    {
        lock (_gate)
        {
            _since ??= _clock.GetUtcNow();
            _step = step;
        }
    }

    /// <summary>Whether the close was asked for.</summary>
    internal bool IsClosing { get { lock (_gate) return _since is not null; } }

    /// <summary>Whether the close has run long enough for the page to offer ending the process.</summary>
    internal bool CanForceExit
    {
        get
        {
            lock (_gate) return _since is { } since && _clock.GetUtcNow() - since >= ForceExitDelay;
        }
    }

    /// <summary>What the page is told: null while the host is not closing.</summary>
    internal Rpc.BootClosing? Snapshot()
    {
        lock (_gate)
        {
            if (_since is not { } since) return null;
            var elapsed = _clock.GetUtcNow() - since;
            return new(_step, (int)Math.Max(0, Math.Floor(elapsed.TotalSeconds)), elapsed >= ForceExitDelay);
        }
    }
}
