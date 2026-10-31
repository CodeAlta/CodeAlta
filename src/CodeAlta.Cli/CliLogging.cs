using XenoAtom.Logging;
using XenoAtom.Logging.Writers;

namespace CodeAlta.Cli;

/// <summary>
/// The logging of <c>altacli</c>: a file, <c>logs/altacli.log</c> of the state root (information and above of CodeAlta,
/// warnings of the libraries), and standard error for the errors, or the warnings too with <c>--verbose</c>. Never
/// standard output, which carries the protocol. An exception no code catches, or a task whose failure nobody observes,
/// is written to both.
/// </summary>
internal static class CliLogging
{
    /// <summary>The name of the log file, in the <c>logs</c> folder of the state root.</summary>
    public const string FileName = "altacli.log";

    private const string FailureLoggerName = "CodeAlta.Cli.Failure";

    /// <summary>Sets up the logging of the process, once; false when it was set up already.</summary>
    public static bool Initialize(string stateRoot, CliOutput output, bool verbose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentNullException.ThrowIfNull(output);
        if (LogManager.IsInitialized) return false;
        var directory = Path.Combine(stateRoot, "logs");
        Directory.CreateDirectory(directory);
        var config = new LogManagerConfig
        {
            AsyncErrorHandler = failure => output.Error($"altacli: logging: {failure.Message}"),
        };
        config.RootLogger.MinimumLevel = LogLevel.Warn;
        config.RootLogger.Writers.Add(CreateFileWriter(Path.Combine(directory, FileName)));
        config.RootLogger.Writers.Add(new StderrLogWriter(output, verbose ? LogLevel.Warn : LogLevel.Error));
        config.Loggers.Add("CodeAlta", LogLevel.Info);
        // A failure that ends the run is never dropped from a full queue.
        config.GetLoggerConfig(FailureLoggerName).OverflowMode = LoggerOverflowMode.Block;
        LogManager.InitializeForAsync(config);
        AppDomain.CurrentDomain.UnhandledException += static (_, args) =>
            LogManager.GetLogger(FailureLoggerName).Error(args.ExceptionObject as Exception, $"Unhandled exception (terminating: {args.IsTerminating}).");
        TaskScheduler.UnobservedTaskException += static (_, args) =>
            LogManager.GetLogger("CodeAlta.Cli").Error(args.Exception, "A task failed and nothing observed its exception.");
        return true;
    }

    /// <summary>
    /// Creates the writer of the log file. Runs of altacli on the same state root share the file: a run appends to it, and
    /// does not archive it when it starts, which fails while another run has it open.
    /// </summary>
    internal static FileLogWriter CreateFileWriter(string path)
        => new(new FileLogWriterOptions(path)
        {
            AutoFlush = true, FileSizeLimitBytes = 10L * 1024 * 1024,
            RollingInterval = FileRollingInterval.Daily, RetainedFileCountLimit = 10,
            RollOnStartup = false,
            FailureMode = FileLogWriterFailureMode.Ignore,
        });

    /// <summary>Writes the failure that ends the run to the log.</summary>
    public static void ReportFailure(Exception failure, string message)
        => LogManager.GetLogger(FailureLoggerName).Error(failure, message);

    /// <summary>Shuts down the logging the run set up: the pending messages are written.</summary>
    public static void Shutdown() => LogManager.Shutdown();

    // Standard error, from a level on: one line per message, with the exception's message, never its stack.
    private sealed class StderrLogWriter(CliOutput output, LogLevel minimum) : LogWriter
    {
        protected override void Log(LogMessage message)
        {
            if (message.Level < minimum) return;
            var text = message.Text.ToString();
            var level = message.Level >= LogLevel.Error ? "error" : "warning";
            output.Error(message.Exception is { } exception ? $"altacli: {level}: {text} {exception.Message}" : $"altacli: {level}: {text}");
        }
    }
}
