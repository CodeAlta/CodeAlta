using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.EventLog;
using XenoAtom.Logging;
using XenoAtom.Logging.Writers;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeAlta.Server.Hosting;

/// <summary>
/// Where <c>altaserver run</c> keeps its log beside the console, which a service manager drops: its own file
/// (<see cref="ServerLogFile"/>), and the log of the system, the journal of systemd on Linux and the Application event
/// log on Windows.
/// </summary>
internal static class ServerLogging
{
    /// <summary>
    /// The variable systemd gives a process whose standard output or error it connected to the journal: the device and
    /// the inode of that stream (<c>8:12345</c>). Processes started by that process inherit it, with the stream or without.
    /// </summary>
    public const string JournalStreamVariable = "JOURNAL_STREAM";

    /// <summary>Adds the log of the system to the log of a server: the journal when standard error goes to it, the event log on Windows.</summary>
    /// <param name="logging">The log of the server, with its console.</param>
    /// <param name="environmentVariable">Gives the value of a variable of the environment.</param>
    public static void AddSystemLog(ILoggingBuilder logging, Func<string, string?> environmentVariable)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(environmentVariable);
        if (OperatingSystem.IsWindows()) AddEventLog(logging);
        else if (OperatingSystem.IsLinux() && IsJournalStream(environmentVariable(JournalStreamVariable), StandardErrorTarget())) AddJournal(logging);
    }

    /// <summary>
    /// Sends the console of a server to the journal of systemd: one line per record with its syslog priority
    /// (<c>&lt;6&gt;</c>), which the journal reads as its level, on standard error, the stream the unit of
    /// <c>altaserver install</c> sends there (it drops standard output).
    /// </summary>
    /// <param name="logging">The log of the server, with its console.</param>
    internal static void AddJournal(ILoggingBuilder logging)
    {
        logging.AddSystemdConsole();
        logging.Services.Configure<ConsoleLoggerOptions>(static options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    }

    /// <summary>
    /// Adds the Application event log of Windows, as ASP.NET Core does by default: warnings and errors, under the event
    /// source of .NET (a source of the server's own would need an administrator to register it).
    /// </summary>
    /// <param name="logging">The log of the server.</param>
    [SupportedOSPlatform("windows")]
    internal static void AddEventLog(ILoggingBuilder logging)
    {
        logging.AddEventLog();
        logging.AddFilter<EventLogLoggerProvider>(static level => level >= LogLevel.Warning);
    }

    /// <summary>
    /// Gets whether a stream of this process is the one systemd connected to the journal. The variable alone does not say:
    /// a version the launcher starts inherits it with the stream, a shell of a desktop session inherits it without. So the
    /// hosting extension of .NET for systemd is not used: it knows a service by its parent process, which the launcher hides.
    /// </summary>
    /// <param name="journalStream">The value of <see cref="JournalStreamVariable"/>: <c>device:inode</c>, or null.</param>
    /// <param name="streamTarget">What <c>/proc/self/fd/&lt;n&gt;</c> names for the stream (<c>socket:[inode]</c>), or null.</param>
    /// <returns>Whether the stream is that socket.</returns>
    internal static bool IsJournalStream(string? journalStream, string? streamTarget)
    {
        const string prefix = "socket:[";
        if (journalStream is null || streamTarget is null) return false;
        var separator = journalStream.IndexOf(':');
        if (separator <= 0 || !ulong.TryParse(journalStream.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var inode)) return false;
        return streamTarget.StartsWith(prefix, StringComparison.Ordinal) && streamTarget.EndsWith(']')
               && ulong.TryParse(streamTarget.AsSpan(prefix.Length, streamTarget.Length - prefix.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var actual)
               && actual == inode;
    }

    // What standard error is, as Linux names it: socket:[inode] for the stream of the journal.
    private static string? StandardErrorTarget()
    {
        try
        {
            return new FileInfo("/proc/self/fd/2").LinkTarget;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// The file of the log of <c>altaserver run</c>: <c>logs/altaserver.log</c> of the data root, rolled daily and bounded, as
/// the node keeps its own. It is the log of the process: one is open at a time, from <see cref="Open"/> to its disposal.
/// </summary>
internal sealed class ServerLogFile : IDisposable
{
    private readonly UnhandledExceptionEventHandler _unhandled;
    private readonly EventHandler<UnobservedTaskExceptionEventArgs> _unobserved;
    private int _disposed;

    private ServerLogFile()
    {
        _unhandled = static (_, args) => LogManager.GetLogger("CodeAlta.Server").Fatal(args.ExceptionObject as Exception, $"Unhandled exception (terminating: {args.IsTerminating}).");
        _unobserved = static (_, args) => LogManager.GetLogger("CodeAlta.Server").Error(args.Exception, "A task failed and nothing observed its exception.");
        AppDomain.CurrentDomain.UnhandledException += _unhandled;
        TaskScheduler.UnobservedTaskException += _unobserved;
    }

    /// <summary>Gets what writes the records of a server's log to the file.</summary>
    public ILoggerProvider Provider { get; } = new FileProvider();

    /// <summary>Opens the file of a data root.</summary>
    /// <param name="paths">The files of the server.</param>
    /// <param name="error">Where a file that cannot be kept is told: the server runs without it.</param>
    /// <returns>The file, to dispose once the server stopped; null when the process keeps a log already, or its folder cannot be made.</returns>
    public static ServerLogFile? Open(ServerPaths paths, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(error);
        if (LogManager.IsInitialized) return null;
        FileLogWriter writer;
        try
        {
            // It makes the logs folder and opens the file.
            writer = new FileLogWriter(new FileLogWriterOptions(paths.LogPath)
            {
                AutoFlush = true,
                FileSizeLimitBytes = 10L * 1024 * 1024,
                RollingInterval = FileRollingInterval.Daily,
                RetainedFileCountLimit = 10,
                FailureMode = FileLogWriterFailureMode.Ignore,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"altaserver: the log is not kept in {paths.LogPath}: {exception.Message}");
            return null;
        }

        var config = new LogManagerConfig
        {
            AsyncErrorHandler = static failure => Console.Error.WriteLine($"altaserver: logging: {failure.Message}"),
        };
        // The filters of the server's log decide what is kept (information and above, the warnings of ASP.NET Core).
        config.RootLogger.MinimumLevel = XenoAtom.Logging.LogLevel.All;
        config.RootLogger.Writers.Add(writer);
        LogManager.InitializeForAsync(config);
        return new ServerLogFile();
    }

    /// <summary>Writes the pending records and closes the file: a version this process starts next opens it itself.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException -= _unhandled;
        TaskScheduler.UnobservedTaskException -= _unobserved;
        LogManager.Shutdown();
    }

    // The file belongs to the process, not to the server's services: disposing the provider closes nothing.
    private sealed class FileProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FileLogger(LogManager.GetLogger(categoryName));

        public void Dispose()
        {
        }
    }

    private sealed class FileLogger(Logger logger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception);
            switch (logLevel)
            {
                case LogLevel.Trace: logger.Trace(exception, message); break;
                case LogLevel.Debug: logger.Debug(exception, message); break;
                case LogLevel.Information: logger.Info(exception, message); break;
                case LogLevel.Warning: logger.Warn(exception, message); break;
                case LogLevel.Error: logger.Error(exception, message); break;
                case LogLevel.Critical: logger.Fatal(exception, message); break;
            }
        }
    }
}
