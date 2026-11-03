using XenoAtom.Logging;
using XenoAtom.Logging.Writers;

namespace CodeAlta.Node;

/// <summary>
/// The logging of <c>altanode</c>: a file, <c>logs/altanode.log</c> of the state root (information and above of CodeAlta,
/// warnings of the libraries), rolled daily and bounded, and standard output too when it runs in the foreground.
/// </summary>
internal static class NodeLogging
{
    /// <summary>The name of the log file, in the <c>logs</c> folder of the state root.</summary>
    public const string FileName = "altanode.log";

    /// <summary>Gets the path of the log of a state root.</summary>
    public static string GetLogFilePath(string stateRoot) => Path.Combine(stateRoot, "logs", FileName);

    /// <summary>Sets up the logging of the process, once; false when it was set up already.</summary>
    /// <param name="stateRoot">The state root of the node.</param>
    /// <param name="console">Whether the messages are written to standard output too.</param>
    public static bool Initialize(string stateRoot, bool console) => Initialize(stateRoot, console, null);

    /// <summary>Sets up the logging of the process, once, with a writer more (the telemetry's); false when it was set up already.</summary>
    /// <param name="stateRoot">The state root of the node.</param>
    /// <param name="console">Whether the messages are written to standard output too.</param>
    /// <param name="telemetry">A writer that sends the records to the telemetry of the node, or null.</param>
    public static bool Initialize(string stateRoot, bool console, LogWriter? telemetry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        if (LogManager.IsInitialized) return false;
        var path = GetLogFilePath(stateRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var config = new LogManagerConfig
        {
            AsyncErrorHandler = static failure => Console.Error.WriteLine($"altanode: logging: {failure.Message}"),
        };
        config.RootLogger.MinimumLevel = LogLevel.Warn;
        config.RootLogger.Writers.Add(new FileLogWriter(new FileLogWriterOptions(path)
        {
            AutoFlush = true,
            FileSizeLimitBytes = 10L * 1024 * 1024,
            RollingInterval = FileRollingInterval.Daily,
            RetainedFileCountLimit = 10,
            FailureMode = FileLogWriterFailureMode.Ignore,
        }));
        if (console) config.RootLogger.Writers.Add(new StreamLogWriter(Console.OpenStandardOutput(), ownsStream: false) { AutoFlush = true });
        // The records the log keeps go to the collector too, when the telemetry of the node sends logs.
        if (telemetry is not null) config.RootLogger.Writers.Add(telemetry);
        config.Loggers.Add("CodeAlta", LogLevel.Info);
        LogManager.InitializeForAsync(config);
        AppDomain.CurrentDomain.UnhandledException += static (_, args) =>
            LogManager.GetLogger("CodeAlta.Node").Error(args.ExceptionObject as Exception, $"Unhandled exception (terminating: {args.IsTerminating}).");
        TaskScheduler.UnobservedTaskException += static (_, args) =>
            LogManager.GetLogger("CodeAlta.Node").Error(args.Exception, "A task failed and nothing observed its exception.");
        return true;
    }

    /// <summary>Writes the pending messages and shuts the logging down.</summary>
    public static void Shutdown() => LogManager.Shutdown();
}
