namespace CodeAlta.Tui;

internal static class CodeAltaTaskMonitor
{
    public static void Observe(Task task, string source)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (task.IsCompleted)
        {
            ReportIfFaulted(task, source);
            return;
        }

        _ = task.ContinueWith(
            static (completedTask, state) => ReportIfFaulted(completedTask, (string)state!),
            source,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ReportIfFaulted(Task task, string source)
    {
        if (!task.IsFaulted || task.Exception is null)
        {
            return;
        }

        CodeAltaCrashReporter.ReportFatalTaskException(source, task.Exception.Flatten());
    }
}
