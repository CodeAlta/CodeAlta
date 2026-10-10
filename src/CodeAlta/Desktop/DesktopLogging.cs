using XenoAtom.Logging;
using XenoAtom.Logging.Writers;

namespace CodeAlta.Desktop;

internal static class DesktopLogging
{
    internal static DesktopLogCapture? Initialize(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
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
        config.RootLogger.Writers.Add(new FileLogWriter(new FileLogWriterOptions(Path.Combine(directory, "codealta.log"))
        {
            AutoFlush = true, FileSizeLimitBytes = 10L * 1024 * 1024,
            RollingInterval = FileRollingInterval.Daily, RetainedFileCountLimit = 10,
            FailureMode = FileLogWriterFailureMode.Ignore,
        }));
        config.Loggers.Add("CodeAlta", LogLevel.Info);
        // A developer looks closer at some loggers, for instance what the Claude Code CLI sends
        // (CODEALTA_DEBUG_LOGGERS=CodeAlta.ClaudeCode.Protocol): they are written at the Debug level.
        foreach (var name in DebugLoggers(Environment.GetEnvironmentVariable("CODEALTA_DEBUG_LOGGERS")))
            config.Loggers.Add(name, LogLevel.Debug);
        LogManager.InitializeForAsync(config);
        return capture;
    }

    /// <summary>The names of the loggers to write at the Debug level, from a list separated by commas or semicolons.</summary>
    internal static IReadOnlyList<string> DebugLoggers(string? value)
        => string.IsNullOrWhiteSpace(value) ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
}
