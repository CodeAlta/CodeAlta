using XenoAtom.Logging;
using XenoAtom.Logging.Writers;
using XenoAtom.Terminal;

namespace CodeAlta;

internal static class CodeAltaLogging
{
    internal const string LogFileName = "codealta.log";
    internal const long LogFileSizeLimitBytes = 10L * 1024L * 1024L;
    internal const int RetainedLogFileCountLimit = 10;
    internal const int UiLogCapacity = 20000;

    private static readonly CodeAltaUiLogBuffer UiLogBufferStorage = new(UiLogCapacity);
    private static readonly CodeAltaUiLogWriter UiLogWriter = CreateUiLogWriter();

    public static bool Initialize(string homeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);
        CodeAltaCrashReporter.Register(homeRoot);

        if (LogManager.IsInitialized)
        {
            return false;
        }

        var config = CreateConfig(homeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(CreateFileWriterOptions(homeRoot).FilePath)!);
        LogManager.InitializeForAsync(config);
        return true;
    }

    internal static LogManagerConfig CreateConfig(string homeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);

        UiLogBufferStorage.Clear();

        var fileWriterOptions = CreateFileWriterOptions(homeRoot);
        var config = new LogManagerConfig
        {
            AsyncErrorHandler = static exception =>
            {
                try
                {
                    Terminal.Error.WriteLine($"[CodeAlta logging] {exception}");
                }
                catch
                {
                }
            }
        };

        config.RootLogger.MinimumLevel = LogLevel.Warn;
        config.RootLogger.Writers.Add(new FileLogWriter(fileWriterOptions));
        config.RootLogger.Writers.Add(UiLogWriter);
        config.Loggers.Add("CodeAlta", LogLevel.Info);
        return config;
    }

    internal static CodeAltaUiLogBuffer GetUiLogBuffer()
        => UiLogBufferStorage;

    internal static FileLogWriterOptions CreateFileWriterOptions(string homeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);

        return new FileLogWriterOptions(GetLogFilePath(homeRoot))
        {
            AutoFlush = true,
            Formatter = CodeAltaLocalTimeLogFormatter.Instance,
            FileSizeLimitBytes = LogFileSizeLimitBytes,
            RollingInterval = FileRollingInterval.Daily,
            RetainedFileCountLimit = RetainedLogFileCountLimit,
            FailureMode = FileLogWriterFailureMode.Ignore,
        };
    }

    public static string GetLogFilePath(string homeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);
        return Path.Combine(homeRoot, "logs", LogFileName);
    }

    private static CodeAltaUiLogWriter CreateUiLogWriter()
    {
        var writer = new CodeAltaUiLogWriter(UiLogBufferStorage)
        {
            Formatter = CodeAltaLocalTimeLogFormatter.Instance,
            EnableRichFormatting = true,
            EnableMarkupMessages = true,
        };

        writer.Styles.ResetToDefaults();
        writer.Styles.SetStyle(LogMessageFormatSegmentKind.Timestamp, "dim");
        writer.Styles.SetStyle(LogMessageFormatSegmentKind.LoggerName, "bold cyan");
        writer.Styles.SetLevelStyle(LogLevel.Info, "bold green");
        writer.Styles.SetLevelStyle(LogLevel.Warn, "bold yellow");
        writer.Styles.SetLevelStyle(LogLevel.Error, "bold white on red");
        return writer;
    }
}
