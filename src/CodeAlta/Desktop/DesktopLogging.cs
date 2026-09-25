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
        LogManager.InitializeForAsync(config);
        return capture;
    }
}
