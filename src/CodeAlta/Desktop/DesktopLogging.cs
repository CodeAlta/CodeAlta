using XenoAtom.Logging;
using XenoAtom.Logging.Writers;

namespace CodeAlta.Desktop;

internal static class DesktopLogging
{
    private const string FailureLoggerName = "CodeAlta.Desktop.Startup";

    internal static DesktopLogCapture? Initialize(string dataRoot)
        => Initialize(dataRoot, out _);

    internal static DesktopLogCapture? Initialize(string dataRoot, out Action? flushPending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        flushPending = null;
        if (LogManager.IsInitialized) return null;
        var directory = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(directory);
        var config = new LogManagerConfig
        {
            AsyncErrorHandler = static failure => Console.Error.WriteLine($"[CodeAlta desktop logging] {failure.Message}"),
        };
        config.RootLogger.MinimumLevel = LogLevel.Warn;
        var capture = new DesktopLogCapture();
        config.RootLogger.Writers.Add(new DesktopCaptureLogWriter(capture));
        var file = new FileLogWriter(new FileLogWriterOptions(Path.Combine(directory, "codealta.log"))
        {
            AutoFlush = true, FileSizeLimitBytes = 10L * 1024 * 1024,
            RollingInterval = FileRollingInterval.Daily, RetainedFileCountLimit = 10,
            FailureMode = FileLogWriterFailureMode.Ignore,
        });
        config.RootLogger.Writers.Add(file);
        config.Loggers.Add("CodeAlta", LogLevel.Info);
        // A developer looks closer at some loggers, for instance what the Claude Code CLI sends
        // (CODEALTA_DEBUG_LOGGERS=CodeAlta.ClaudeCode.Protocol): they are written at the Debug level.
        foreach (var name in DebugLoggers(Environment.GetEnvironmentVariable("CODEALTA_DEBUG_LOGGERS")))
            config.Loggers.Add(name, LogLevel.Debug);
        // A failure ending the run must not be dropped if retained work has filled the ordinary queue.
        config.GetLoggerConfig(FailureLoggerName).OverflowMode = LoggerOverflowMode.Block;

        // XenoAtom.Logging has no non-disposing queue flush. A private marker passes through the same
        // async queue after the preceding diagnostics, then flushes the file without retiring its writers.
        var flush = new PendingLogFlush(file);
        var flushConfig = config.GetLoggerConfig(PendingLogFlush.LoggerName);
        flushConfig.MinimumLevel = LogLevel.Fatal;
        flushConfig.IncludeParentWriters = false; // The marker is not a diagnostic or a captured row.
        flushConfig.OverflowMode = LoggerOverflowMode.Block;
        flushConfig.Writers.Add(flush);
        LogManager.InitializeForAsync(config);
        flushPending = flush.Wait;
        return capture;
    }

    /// <summary>Reports the full failure, including native loader inner exceptions, to the log and stderr.</summary>
    internal static void ReportFailure(Exception? failure, string message)
    {
        LogManager.GetLogger(FailureLoggerName).Error(failure, message);
        Console.Error.WriteLine(failure is null ? message : $"{message}{Environment.NewLine}{failure}");
    }

    /// <summary>The names of the loggers to write at the Debug level, from a list separated by commas or semicolons.</summary>
    internal static IReadOnlyList<string> DebugLoggers(string? value)
        => string.IsNullOrWhiteSpace(value) ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();

    private sealed class PendingLogFlush(FileLogWriter file) : LogWriter
    {
        internal const string LoggerName = "CodeAlta.Desktop.LogFlush";

        internal void Wait()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LogManager.GetLogger(LoggerName).Fatal(completion, string.Empty);
            if (!completion.Task.Wait(TimeSpan.FromSeconds(5)))
                Console.Error.WriteLine("[CodeAlta desktop logging] Pending diagnostics could not be flushed within five seconds; logging remains available.");
        }

        protected override void Log(LogMessage message)
        {
            if (message.Attachment is not TaskCompletionSource completion) return;
            file.Flush();
            completion.TrySetResult();
        }
    }
}
