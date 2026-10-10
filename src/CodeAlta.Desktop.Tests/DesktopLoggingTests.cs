using CodeAlta.Desktop;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DesktopLoggingTests
{
    [TestMethod]
    public void Startup_InitializesLoggingBeforeHostWorkAndFlushesToDesktopRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-logging-" + Guid.NewGuid().ToString("N"));
        Assert.IsFalse(LogManager.IsInitialized);
        try
        {
            Assert.AreEqual(0, DesktopApplication.Run(new(root, null), _ =>
            {
                Assert.IsTrue(LogManager.IsInitialized);
                LogManager.GetLogger("CodeAlta.Desktop.Test").Info("desktop logging regression");
                return 0;
            }));
            Assert.IsFalse(LogManager.IsInitialized);
            var logs = Directory.GetFiles(Path.Combine(root, "logs"));
            Assert.IsTrue(logs.Any(path => File.ReadAllText(path).Contains("desktop logging regression", StringComparison.Ordinal)));
        }
        finally
        {
            LogManager.Shutdown();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Startup_WritesTheDebugLevelOfTheLoggersADeveloperNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-logging-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("CODEALTA_DEBUG_LOGGERS");
        Assert.IsFalse(LogManager.IsInitialized);
        try
        {
            Environment.SetEnvironmentVariable("CODEALTA_DEBUG_LOGGERS", "CodeAlta.ClaudeCode.Protocol");
            Assert.AreEqual(0, DesktopApplication.Run(new(root, null), _ =>
            {
                LogManager.GetLogger("CodeAlta.ClaudeCode.Protocol").Debug("protocol line of the regression");
                LogManager.GetLogger("CodeAlta.Desktop.Test").Debug("other debug line of the regression");
                return 0;
            }));
            var text = string.Concat(Directory.GetFiles(Path.Combine(root, "logs")).Select(File.ReadAllText));
            StringAssert.Contains(text, "protocol line of the regression");
            Assert.IsFalse(text.Contains("other debug line of the regression", StringComparison.Ordinal), "Only the loggers named are written at the Debug level.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_DEBUG_LOGGERS", previous);
            LogManager.Shutdown();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DebugLoggers_AreTheNamesOfTheList()
    {
        CollectionAssert.AreEqual(Array.Empty<string>(), DesktopLogging.DebugLoggers(null).ToArray());
        CollectionAssert.AreEqual(Array.Empty<string>(), DesktopLogging.DebugLoggers("  ").ToArray());
        CollectionAssert.AreEqual(new[] { "CodeAlta.ClaudeCode.Protocol", "CodeAlta.Desktop" },
            DesktopLogging.DebugLoggers(" CodeAlta.ClaudeCode.Protocol ; CodeAlta.Desktop,,CodeAlta.ClaudeCode.Protocol").ToArray());
    }

    [TestMethod]
    public void Startup_DoesNotReplaceOrShutDownAnExistingLogger()
    {
        Assert.IsFalse(LogManager.IsInitialized);
        LogManager.InitializeForAsync(new LogManagerConfig());
        try
        {
            Assert.AreEqual(0, DesktopApplication.Run(new("unused-existing-logger-root", null), _ => 0));
            Assert.IsTrue(LogManager.IsInitialized);
        }
        finally { LogManager.Shutdown(); }
    }

    [TestMethod]
    public void UnconfirmedShutdown_KeepsLoggerAvailableForRetainedWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-logging-" + Guid.NewGuid().ToString("N"));
        Assert.IsFalse(LogManager.IsInitialized);
        try
        {
            Assert.AreEqual(1, DesktopApplication.Run(new(root, null), _ => 1));
            Assert.IsTrue(LogManager.IsInitialized);
            LogManager.GetLogger("CodeAlta.Desktop.Test").Warn("retained work can still log");
        }
        finally
        {
            LogManager.Shutdown();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
