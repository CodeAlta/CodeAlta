namespace CodeAlta.Desktop;

/// <summary>
/// When the window shows the application: the work of the start the first page does not need (the providers, the
/// search, the workers) waits for it, so that it does not slow the start-up screen down.
/// </summary>
/// <remarks>
/// The page leaves the start-up screen once it has the workspace: the first workspace snapshot it is served marks the
/// moment, with a short delay for the page to draw it. A page that never asks opens the gate after a fallback delay.
/// </remarks>
internal sealed class DesktopStartupGate
{
    private readonly TaskCompletionSource _shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _time;
    private int _served;

    /// <summary>The time the page takes to draw the workspace it was served.</summary>
    internal static readonly TimeSpan DrawDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the gate waits for a page that does not ask for its workspace.</summary>
    internal static readonly TimeSpan Fallback = TimeSpan.FromSeconds(15);

    internal DesktopStartupGate(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Completes once the window shows the application, or the fallback passed.</summary>
    internal Task Shown => _shown.Task;

    /// <summary>The page was served its first workspace: the gate opens once the page had time to draw it.</summary>
    internal void WorkspaceServed()
    {
        if (Interlocked.Exchange(ref _served, 1) == 0) Open(DrawDelay);
    }

    /// <summary>The application document is loading: the gate opens after <see cref="Fallback"/> whatever the page does.</summary>
    internal void ApplicationLoading() => Open(Fallback);

    private void Open(TimeSpan delay)
        => _ = Task.Delay(delay, _time).ContinueWith(_ => _shown.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
