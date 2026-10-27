using CodeAlta.Catalog.Logging;
using XenoAtom.Logging;
using XenoAtom.Logging.Writers;

namespace CodeAlta.Desktop;

internal static class DesktopLogging
{
    private const string FailureLoggerName = "CodeAlta.Desktop.Startup";

    internal static DesktopLogCapture? Initialize(string dataRoot)
        => Initialize(dataRoot, null, out _);

    internal static DesktopLogCapture? Initialize(string dataRoot, out Action? flushPending)
        => Initialize(dataRoot, null, out flushPending);

    /// <summary>Starts the log of the application: a file under <paramref name="dataRoot"/> and the rows of the log viewer.</summary>
    /// <param name="dataRoot">The folder of the data of the window.</param>
    /// <param name="settings">Reads the levels of the features (<c>[logging]</c>); null for the defaults.</param>
    /// <param name="flushPending">Writes what is waiting in the queue to the file; null when the log was started elsewhere.</param>
    /// <returns>The rows of the log viewer; null when the log was started elsewhere.</returns>
    internal static DesktopLogCapture? Initialize(string dataRoot, Func<LoggingSettings>? settings, out Action? flushPending)
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
        // The levels of the features come from [logging]; a developer also looks closer at some loggers, for instance
        // what the Claude Code CLI sends (CODEALTA_DEBUG_LOGGERS=CodeAlta.ClaudeCode.Protocol): they are written at the
        // Debug level whatever the level of their feature.
        capture.Levels = new DesktopLogLevels(config, DebugLoggers(Environment.GetEnvironmentVariable("CODEALTA_DEBUG_LOGGERS")));
        capture.Levels.Apply(ReadSettings(settings));
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
        // An exception no code catches, or a task whose failure nobody awaits, is written down: otherwise a part of
        // the application that stopped working leaves nothing to investigate.
        AppDomain.CurrentDomain.UnhandledException += static (_, args) =>
            ReportFailure(args.ExceptionObject as Exception, $"Unhandled exception (terminating: {args.IsTerminating}).");
        TaskScheduler.UnobservedTaskException += static (_, args) => ReportUnobservedTask(args.Exception);
        return capture;
    }

    // The log starts before anything else: settings that cannot be read leave the defaults, and say so once it runs.
    private static LoggingSettings ReadSettings(Func<LoggingSettings>? settings)
    {
        try { return settings?.Invoke() ?? LoggingSettings.Default; }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[CodeAlta desktop logging] The log levels could not be read: {exception.Message}");
            return LoggingSettings.Default;
        }
    }

    /// <summary>Reports the full failure, including native loader inner exceptions, to the log and stderr.</summary>
    internal static void ReportFailure(Exception? failure, string message)
    {
        TryLog(FailureLoggerName, failure, message);
        Console.Error.WriteLine(failure is null ? message : $"{message}{Environment.NewLine}{failure}");
    }

    /// <summary>Reports a task whose failure nothing observed: to the log, or to stderr once the log has ended.</summary>
    internal static void ReportUnobservedTask(AggregateException failure)
    {
        const string Message = "A task failed and nothing observed its exception.";
        if (!TryLog("CodeAlta.Desktop", failure, Message)) Console.Error.WriteLine($"{Message}{Environment.NewLine}{failure}");
    }

    // The handlers of the process outlive the log, and a finalizer reports unobserved tasks: XenoAtom.Logging throws
    // once the log has ended, and an exception there would end the process.
    private static bool TryLog(string logger, Exception? failure, string message)
    {
        if (!LogManager.IsInitialized) return false;
        try
        {
            LogManager.GetLogger(logger).Error(failure, message);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false; // The log ended in between.
        }
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

/// <summary>
/// The levels of the log while it runs: those of the features, which the user changes in Settings, and the loggers a
/// developer names in <c>CODEALTA_DEBUG_LOGGERS</c>, which stay at Debug or more.
/// </summary>
internal sealed class DesktopLogLevels(LogManagerConfig config, IReadOnlyList<string> debugLoggers)
{
    private readonly Lock _gate = new();
    private LoggingSettings _current = LoggingSettings.Default;

    /// <summary>Gets the settings the log runs with.</summary>
    internal LoggingSettings Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Sets the levels of every feature; the loggers already created take them at once.</summary>
    /// <param name="settings">The settings.</param>
    internal void Apply(LoggingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            settings.ApplyTo(config);
            foreach (var name in debugLoggers)
            {
                var level = settings.LevelOf(LogFeatures.Of(name));
                config.SetLogLevel(name, level < LogLevel.Debug ? level : LogLevel.Debug);
            }

            if (LogManager.IsInitialized) config.ApplyChanges();
            _current = settings;
        }
    }
}
