namespace CodeAlta.Desktop;

/// <summary>
/// How the computer is kept from going to sleep on its own: an assertion of the operating system that holds until
/// it is released.
/// </summary>
internal interface IDesktopKeepAwakeBackend
{
    /// <summary>
    /// Asks the system not to sleep while the computer is idle. The display may still turn off and the screen lock;
    /// closing the lid or asking for sleep still sleeps.
    /// </summary>
    /// <param name="reason">Why, as the system's tools show it.</param>
    /// <returns>What releases the assertion; null when the system has none or refused it.</returns>
    IDisposable? Acquire(string reason);
}

/// <summary>
/// Keeps the computer from going to idle sleep while CodeAlta Desktop runs and the user's setting is on, so that
/// automations, reminders, background jobs and running sessions keep going with the window hidden. The setting
/// applies at once; the assertion is released when it is turned off and when the application exits, and the
/// operating system releases it if the process ends first.
/// </summary>
internal sealed class DesktopKeepAwake : IDisposable
{
    /// <summary>What the system's tools (<c>pmset -g assertions</c>, <c>powercfg /requests</c>) show.</summary>
    internal const string Reason = "CodeAlta keeps the computer awake for its sessions, automations and reminders";

    private readonly IDesktopKeepAwakeBackend _backend;
    private readonly string _reason;
    private readonly Action<string> _warn;
    private readonly Lock _gate = new();
    private IDisposable? _assertion;
    private bool _failed;
    private bool _disposed;

    /// <param name="backend">The assertions of the operating system.</param>
    /// <param name="warn">Writes a warning to the log.</param>
    /// <param name="reason">Why, as the system's tools show it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="backend"/> or <paramref name="warn"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is blank.</exception>
    internal DesktopKeepAwake(IDesktopKeepAwakeBackend backend, Action<string> warn, string reason = Reason)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(warn);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _backend = backend;
        _warn = warn;
        _reason = reason;
    }

    /// <summary>The assertions of the operating system the application runs on.</summary>
    internal static IDesktopKeepAwakeBackend PlatformBackend()
        => new SystemBackend();

    /// <summary>Whether the computer is kept awake now.</summary>
    internal bool IsActive { get { lock (_gate) return _assertion is not null; } }

    /// <summary>
    /// Keeps the computer awake (<paramref name="on"/>) or lets it sleep again. A failure is logged once and leaves
    /// the computer as the system decides; it never stops the application.
    /// </summary>
    internal void Set(bool on)
    {
        IDisposable? released = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (!on)
            {
                (released, _assertion) = (_assertion, null);
            }
            else if (_assertion is null)
            {
                try
                {
                    _assertion = _backend.Acquire(_reason);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _assertion = null;
                    if (!_failed) _warn($"The computer cannot be kept awake: {exception.Message}");
                    _failed = true;
                }
            }
        }

        Release(released);
    }

    /// <summary>Releases the assertion: the computer sleeps again as the system decides.</summary>
    public void Dispose()
    {
        IDisposable? released;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            (released, _assertion) = (_assertion, null);
        }

        Release(released);
    }

    private void Release(IDisposable? assertion)
    {
        try
        {
            assertion?.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _warn($"The computer could not be let sleep again: {exception.Message}");
        }
    }

    // The assertions of the operating system, shared with the model servers of a node.
    private sealed class SystemBackend : IDesktopKeepAwakeBackend
    {
        public IDisposable? Acquire(string reason) => CodeAlta.Core.Power.SleepAssertion.Acquire(reason);
    }
}