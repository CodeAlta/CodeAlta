using XenoAtom.Logging;
using XenoAtom.Logging.Writers;

internal static class CodeAltaLogging
{
    internal const string LogFileName = "codealta.log";

    public static bool Initialize(string homeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);

        if (LogManager.IsInitialized)
        {
            return false;
        }

        var logFilePath = GetLogFilePath(homeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);

        var config = new LogManagerConfig
        {
            AsyncErrorHandler = static exception =>
            {
                try
                {
                    Console.Error.WriteLine($"[CodeAlta logging] {exception}");
                }
                catch
                {
                }
            }
        };

        config.RootLogger.MinimumLevel = LogLevel.Info;
        config.RootLogger.Writers.Add(new FileLogWriter(
            new FileLogWriterOptions(logFilePath)
            {
                RollingInterval = FileRollingInterval.Daily,
                RetainedFileCountLimit = 10,
                AutoFlush = true,
                FailureMode = FileLogWriterFailureMode.Ignore
            }));

        config.GetLoggerConfig("CodeAlta.Chat").MinimumLevel = LogLevel.Debug;
        config.GetLoggerConfig("CodeAlta.ChatAgentConnection").MinimumLevel = LogLevel.Debug;
        config.GetLoggerConfig("CodeAlta.Agent.Copilot.Session").MinimumLevel = LogLevel.Debug;
        config.GetLoggerConfig("CodeAlta.Agent.Copilot.Callbacks").MinimumLevel = LogLevel.Debug;
        config.GetLoggerConfig("CodeAlta.Program").MinimumLevel = LogLevel.Debug;

        LogManager.InitializeForAsync(config);
        return true;
    }

    public static string GetLogFilePath(string homeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRoot);
        return Path.Combine(homeRoot, "logs", LogFileName);
    }
}
