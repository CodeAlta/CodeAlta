using XenoAtom.Logging.Writers;
using XenoAtom.Logging;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaLoggingTests
{
    [TestMethod]
    public void GetLogFilePath_UsesCodeAltaLogsDirectory()
    {
        var homeRoot = Path.Combine(Path.GetTempPath(), ".codealta-test-home");
        var path = CodeAltaLogging.GetLogFilePath(homeRoot);

        Assert.AreEqual(Path.Combine(homeRoot, "logs", CodeAltaLogging.LogFileName), path);
    }

    [TestMethod]
    public void CreateFileWriterOptions_UsesBoundedRollingSettings()
    {
        var homeRoot = Path.Combine(Path.GetTempPath(), ".codealta-test-home");

        var options = CodeAltaLogging.CreateFileWriterOptions(homeRoot);

        Assert.AreEqual(Path.Combine(homeRoot, "logs", CodeAltaLogging.LogFileName), options.FilePath);
        Assert.AreEqual(CodeAltaLogging.LogFileSizeLimitBytes, options.FileSizeLimitBytes);
        Assert.AreEqual(FileRollingInterval.Daily, options.RollingInterval);
        Assert.AreEqual(CodeAltaLogging.RetainedLogFileCountLimit, options.RetainedFileCountLimit);
        Assert.IsTrue(options.AutoFlush);
        Assert.AreEqual(FileLogWriterFailureMode.Ignore, options.FailureMode);
    }

    [TestMethod]
    public void CreateConfig_OnlyLogsErrorsByDefault()
    {
        var homeRoot = Path.Combine(Path.GetTempPath(), ".codealta-test-home");

        var config = CodeAltaLogging.CreateConfig(homeRoot);

        Assert.AreEqual(LogLevel.Error, config.RootLogger.MinimumLevel);
    }
}
