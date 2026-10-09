using CodeAlta.Orchestration.Runtime;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.WindowState;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>Shows indeterminate progress on the Windows taskbar button while a session runs.</summary>
/// <remarks>
/// The activity is read from the runtime at a short interval rather than observed: a session that runs, or that
/// sends its queued prompts, is seen whatever started it, and nothing is computed for each event of a turn.
/// </remarks>
internal sealed class DesktopWindowsTaskbarProgress : IAsyncDisposable
{
    /// <summary>How often the activity of the sessions is read.</summary>
    internal static readonly TimeSpan Period = TimeSpan.FromMilliseconds(500);

    private readonly Func<NeoWindowProgressState, CancellationToken, ValueTask<NeoDesktopStatus>> _setProgress;
    private readonly Func<bool> _hasRunningSessions;
    private readonly Func<bool> _isWindowShown;
    private readonly Action<string> _reportFailure;
    private readonly SemaphoreSlim _progressGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _disposeGate = new();
    private readonly Task _observation;
    private Task? _disposal;
    private NeoWindowProgressState? _appliedState;
    private bool _windowWasShown;
    private bool _failureLogged;

    /// <summary>Creates an indicator that reads the activity and sets the progress as its owner or a test says.</summary>
    /// <param name="setProgress">Sets the progress of the taskbar button and returns what the desktop answered.</param>
    /// <param name="hasRunningSessions">Reads whether a session runs right now.</param>
    /// <param name="isWindowShown">Reads whether the window is shown, and so has a taskbar button.</param>
    /// <param name="reportFailure">Receives the message of a progress that could not be set, once until it is set again.</param>
    /// <param name="period">How often the activity is read; null when the caller reads it with <see cref="RefreshAsync"/>.</param>
    internal DesktopWindowsTaskbarProgress(
        Func<NeoWindowProgressState, CancellationToken, ValueTask<NeoDesktopStatus>> setProgress,
        Func<bool> hasRunningSessions,
        Func<bool> isWindowShown,
        Action<string> reportFailure,
        TimeSpan? period = null)
    {
        ArgumentNullException.ThrowIfNull(setProgress);
        ArgumentNullException.ThrowIfNull(hasRunningSessions);
        ArgumentNullException.ThrowIfNull(isWindowShown);
        ArgumentNullException.ThrowIfNull(reportFailure);
        _setProgress = setProgress;
        _hasRunningSessions = hasRunningSessions;
        _isWindowShown = isWindowShown;
        _reportFailure = reportFailure;
        _windowWasShown = isWindowShown();
        _observation = period is { } interval ? Task.Run(() => ObserveAsync(interval)) : Task.CompletedTask;
    }

    /// <summary>Starts the indicator of a window when the desktop has a taskbar progress, which is on Windows.</summary>
    /// <returns>The indicator, to dispose with the window; null when the desktop has none.</returns>
    internal static DesktopWindowsTaskbarProgress? StartIfAvailable(
        NeoWindow window,
        NeoWindowPolishService windowPolish,
        SessionRuntimeService runtime)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(windowPolish);
        ArgumentNullException.ThrowIfNull(runtime);
        if (!OperatingSystem.IsWindows() || windowPolish.ProgressSupport.SupportLevel != NeoSupportLevel.Native) return null;

        return new(
            // A closed window has no taskbar button left to set.
            (state, cancellationToken) => window.IsClosed
                ? ValueTask.FromResult(NeoDesktopStatus.Success)
                : windowPolish.SetProgressAsync(window, state, 0, cancellationToken),
            () => HasRunningSessions(runtime.ListOverview()),
            () => window.IsVisible,
            static message => LogManager.GetLogger("CodeAlta.Desktop").Warn(message),
            Period);
    }

    /// <summary>Whether a session has a run in flight or sends its queued prompts. Background tasks alone do not count.</summary>
    internal static bool HasRunningSessions(IReadOnlyList<SessionRuntimeOverview> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return sessions.Any(static session => session.Running);
    }

    /// <summary>Reads the activity once and shows it. A state the desktop did not accept is set again at the next reading.</summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var state = _hasRunningSessions() ? NeoWindowProgressState.Indeterminate : NeoWindowProgressState.None;
        await _progressGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A window that is hidden (closed to the notification area) loses its taskbar button, and the progress with
            // it: the button it gets when it is shown again starts without one. That button may not exist yet at the
            // reading that sees the window shown, so the progress is set at the next one.
            var shown = _isWindowShown();
            var settled = shown && _windowWasShown;
            _windowWasShown = shown;
            if (!settled)
            {
                _appliedState = null;
                return;
            }

            await SetProgressStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _progressGate.Release();
        }
    }

    private async Task ObserveAsync(TimeSpan period)
    {
        try
        {
            using var timer = new PeriodicTimer(period);
            do
            {
                await RefreshAsync(_stop.Token).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Disposal stops the readings only; it never cancels the work of a session.
        }
        catch (Exception exception)
        {
            _reportFailure($"The taskbar no longer follows the activity of the sessions: {exception.Message}");
        }
    }

    // Called with the gate held.
    private async Task SetProgressStateAsync(NeoWindowProgressState state, CancellationToken cancellationToken)
    {
        if (_appliedState == state) return;
        string? failure;
        try
        {
            var status = await _setProgress(state, cancellationToken).ConfigureAwait(false);
            failure = status == NeoDesktopStatus.Success ? null : status.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
        }

        if (failure is null)
        {
            _appliedState = state;
            _failureLogged = false;
            return;
        }

        // What the taskbar shows is unknown now: the state is set again at the next reading. Logged once, not at each reading.
        _appliedState = null;
        if (_failureLogged) return;
        _failureLogged = true;
        _reportFailure($"The Windows taskbar progress could not be set to {state}: {failure}");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        await _observation.ConfigureAwait(false);
        await _progressGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // The window may outlive its sessions for a moment: it is not left with a progress that nothing clears.
            if (_appliedState == NeoWindowProgressState.Indeterminate)
                await SetProgressStateAsync(NeoWindowProgressState.None, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _progressGate.Release();
        }

        _stop.Dispose();
    }
}
