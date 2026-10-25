using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tui.App;

/// <summary>
/// Reads the sessions again when another process listed, changed or released one (the command line): at most once a
/// second, a burst of changes taken in one reading.
/// </summary>
internal sealed class ExternalSessionRefresh : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    private readonly ExternalSessionFollower _follower;
    private readonly Func<CancellationToken, Task> _refresh;
    private readonly CancellationTokenSource _stop = new();
    private int _scheduled;

    internal ExternalSessionRefresh(ExternalSessionFollower follower, Func<CancellationToken, Task> refresh)
    {
        ArgumentNullException.ThrowIfNull(follower);
        ArgumentNullException.ThrowIfNull(refresh);
        _follower = follower;
        _refresh = refresh;
        follower.Changed += OnChanged;
    }

    public void Dispose()
    {
        _follower.Changed -= OnChanged;
        _stop.Cancel();
    }

    private void OnChanged(object? sender, ExternalSessionChange change)
    {
        if (Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        var token = _stop.Token;
        var task = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Delay, token).ConfigureAwait(false);
                Volatile.Write(ref _scheduled, 0);
                await _refresh(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);
        CodeAltaTaskMonitor.Observe(task, "Refresh of the sessions another process writes");
    }
}
