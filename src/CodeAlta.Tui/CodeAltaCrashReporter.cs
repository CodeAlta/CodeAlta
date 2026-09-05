using System.Text;
using XenoAtom.Logging;
using XenoAtom.Terminal;

namespace CodeAlta.Tui;

internal static class CodeAltaCrashReporter
{
    private static readonly object Gate = new();
    private static string? _homeRoot;
    private static int _registered;
    private static Action<string, Exception> _terminateProcess = DefaultTerminateProcess;

    public static void Register(string homeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);

        lock (Gate)
        {
            _homeRoot = homeRoot;
        }

        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    internal static void ReportFatalException(string source, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(exception);

        TryLog(source, exception);
        TryWriteTerminal(source, exception);
    }

    internal static void ReportFatalTaskException(string source, Exception exception)
    {
        ReportFatalException(source, exception);
        TerminateProcess(source, exception);
    }

    internal static void SetProcessTerminatorForTesting(Action<string, Exception>? terminateProcess)
    {
        lock (Gate)
        {
            _terminateProcess = terminateProcess ?? DefaultTerminateProcess;
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException($"Unhandled non-exception object: {e.ExceptionObject}");
        ReportFatalException("Unhandled exception", exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportFatalTaskException("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    private static void TerminateProcess(string source, Exception exception)
    {
        Action<string, Exception> terminateProcess;
        lock (Gate)
        {
            terminateProcess = _terminateProcess;
        }

        terminateProcess(source, exception);
    }

    private static void DefaultTerminateProcess(string source, Exception exception)
        => Environment.FailFast($"CodeAlta fatal task failure: {source}", exception);

    private static void TryLog(string source, Exception exception)
    {
        try
        {
            var logger = LogManager.GetLogger("CodeAlta.Crash");
            logger.Error(exception, source);
        }
        catch
        {
        }

        try
        {
            var homeRoot = GetHomeRootSnapshot();
            if (string.IsNullOrWhiteSpace(homeRoot))
            {
                return;
            }

            var logPath = CodeAltaLogging.GetLogFilePath(homeRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, FormatCrashLog(source, exception), Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string? GetHomeRootSnapshot()
    {
        lock (Gate)
        {
            return _homeRoot;
        }
    }

    private static void TryWriteTerminal(string source, Exception exception)
    {
        try
        {
            Terminal.Error.WriteLine($"[CodeAlta.Fatal] {source}: {exception}");
        }
        catch
        {
        }
    }

    internal static string FormatCrashLog(string source, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(exception);

        return $"{DateTimeOffset.Now:O} FTL CodeAlta.Crash {source}{Environment.NewLine}{exception}{Environment.NewLine}";
    }
}
