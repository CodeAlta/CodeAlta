using CodeAlta.Orchestration.Runtime;
using NeoAstra;
using NeoAstra.Desktop.WindowState;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>Shows indeterminate Windows taskbar progress while any runtime session is running.</summary>
internal sealed class DesktopWindowsTaskbarProgress : IAsyncDisposable
{
    private readonly Func<NeoWindowProgressState, CancellationToken, Task> _setProgress;
    private readonly SemaphoreSlim _progressGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _disposeGate = new();
    private readonly Task _observation;
    private Task? _disposal;
    private NeoWindowProgressState? _currentState;

    /// <summary>Creates a progress controller whose native setter is supplied by its owner or a test.</summary>
    internal DesktopWindowsTaskbarProgress(Func<NeoWindowProgressState, CancellationToken, Task> setProgress)
        : this(setProgress, runtime: null)
    {
    }

    private DesktopWindowsTaskbarProgress(
        Func<NeoWindowProgressState, CancellationToken, Task> setProgress,
        SessionRuntimeService? runtime)
    {
        ArgumentNullException.ThrowIfNull(setProgress);
        _setProgress = setProgress;
        _observation = runtime is null ? Task.CompletedTask : Task.Run(() => ObserveRuntimeAsync(runtime));
    }

    /// <summary>Starts observing runtime activity when the native taskbar progress API is available on Windows.</summary>
    internal static DesktopWindowsTaskbarProgress? StartIfAvailable(
        NeoWindow window,
        NeoWindowPolishService windowPolish,
        SessionRuntimeService runtime)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(windowPolish);
        ArgumentNullException.ThrowIfNull(runtime);
        if (!OperatingSystem.IsWindows() || windowPolish.ProgressSupport.SupportLevel != NeoSupportLevel.Native) return null;

        return new(async (state, cancellationToken) =>
        {
            await windowPolish.SetProgressAsync(window, state, 0, cancellationToken).ConfigureAwait(false);
        }, runtime);
    }

    /// <summary>Updates the indicator for a simulated or observed session-activity state.</summary>
    internal Task SetActivityAsync(bool hasRunningSessions, CancellationToken cancellationToken = default)
        => SetProgressStateAsync(hasRunningSessions ? NeoWindowProgressState.Indeterminate : NeoWindowProgressState.None, cancellationToken);

    /// <summary>Whether at least one runtime overview reports a run or queue drain in progress.</summary>
    internal static bool HasRunningSessions(IReadOnlyList<SessionRuntimeOverview> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return sessions.Any(static session => session.Running);
    }

    private async Task ObserveRuntimeAsync(SessionRuntimeService runtime)
    {
        try
        {
            await foreach (var _ in runtime.Display.ObserveAsync(_stop.Token).ConfigureAwait(false))
            {
                await SetActivityAsync(HasRunningSessions(runtime.ListOverview()), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Disposal stops only this observer; it never cancels session work.
        }
        catch (Exception exception)
        {
            LogManager.GetLogger("CodeAlta.Desktop").Warn($"Session activity observation for the taskbar stopped: {exception.Message}");
            await SetProgressStateAsync(NeoWindowProgressState.None, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task SetProgressStateAsync(NeoWindowProgressState state, CancellationToken cancellationToken)
    {
        await _progressGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_currentState == state) return;
            try
            {
                await _setProgress(state, cancellationToken).ConfigureAwait(false);
                _currentState = state;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogManager.GetLogger("CodeAlta.Desktop").Warn($"The Windows taskbar progress indicator could not be updated: {exception.Message}");
            }
        }
        finally
        {
            _progressGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        try { await _observation.ConfigureAwait(false); }
        catch (Exception exception) { LogManager.GetLogger("CodeAlta.Desktop").Warn($"The taskbar progress observer did not stop cleanly: {exception.Message}"); }
        await SetProgressStateAsync(NeoWindowProgressState.None, CancellationToken.None).ConfigureAwait(false);
        _stop.Dispose();
    }
}
