using XenoAtom.Logging;
using XenoAtom.Logging.Writers;
using XenoAtom.Terminal;

namespace CodeAlta;

internal static class CodeAltaLogging
{
    internal const string LogFileName = "codealta.log";
    internal const long LogFileSizeLimitBytes = 10L * 1024L * 1024L;
    internal const int RetainedLogFileCountLimit = 10;

    public static bool Initialize(string homeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);

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

        config.RootLogger.MinimumLevel = LogLevel.Error;
        config.RootLogger.Writers.Add(new FileLogWriter(fileWriterOptions));
        return config;
    }

    internal static FileLogWriterOptions CreateFileWriterOptions(string homeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);

        return new FileLogWriterOptions(GetLogFilePath(homeRoot))
        {
            AutoFlush = true,
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
}
