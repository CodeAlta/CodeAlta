using CodeAlta.Desktop;
using NeoAstra;
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
        var previousError = Console.Error;
        using var error = new StringWriter();
        Assert.IsFalse(LogManager.IsInitialized);
        try
        {
            Console.SetError(error);
            Assert.AreEqual(0, DesktopApplication.Run(new(root, null), _ =>
            {
                Assert.IsTrue(LogManager.IsInitialized);
                LogManager.GetLogger("CodeAlta.Desktop.Test").Info("desktop logging regression");
                return 0;
            }));
            Assert.IsFalse(LogManager.IsInitialized);
            var logs = Directory.GetFiles(Path.Combine(root, "logs"));
            Assert.IsTrue(logs.Any(path => File.ReadAllText(path).Contains("desktop logging regression", StringComparison.Ordinal)));
            Assert.AreEqual(string.Empty, error.ToString());
        }
        finally
        {
            LogManager.Shutdown();
            Console.SetError(previousError);
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
    [DataRow(0)]
    [DataRow(1)]
    public void Startup_DoesNotReplaceOrShutDownAnExistingLogger(int result)
    {
        Assert.IsFalse(LogManager.IsInitialized);
        LogManager.InitializeForAsync(new LogManagerConfig());
        try
        {
            Assert.AreEqual(result, DesktopApplication.Run(new("unused-existing-logger-root", null), _ => result));
            Assert.IsTrue(LogManager.IsInitialized);
            var failure = new InvalidOperationException("borrowed logging failure");
            Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() =>
                DesktopApplication.Run(new("unused-existing-logger-root", null), _ => throw failure)));
            Assert.IsTrue(LogManager.IsInitialized);
        }
        finally { LogManager.Shutdown(); }
    }

    [TestMethod]
    public void Startup_ThrownNativeFailureIsPersistedBeforeRethrowWithoutClosingLogging()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-logging-" + Guid.NewGuid().ToString("N"));
        var failure = new NeoAstraNativeLibraryException("Could not load libneoastra_native.so",
            new DllNotFoundException("libwebkitgtk-6.0.so.4 and libjavascriptcoregtk-6.0.so.1: cannot open shared object file"));
        var previousError = Console.Error;
        using var error = new StringWriter();
        Assert.IsFalse(LogManager.IsInitialized);
        try
        {
            Console.SetError(error);
            var caught = Assert.ThrowsExactly<NeoAstraNativeLibraryException>(() => DesktopApplication.Run(new(root, null), _ => throw failure));
            Assert.AreSame(failure, caught, "Reporting must not swallow or replace the startup exception.");
            Assert.IsTrue(LogManager.IsInitialized, "A thrown run does not prove retained callbacks have stopped.");
            // Read before any fixture shutdown: an async log merely queued at return is not sufficient.
            var text = ReadLogs(root);
            StringAssert.Contains(text, "Could not load libneoastra_native.so");
            StringAssert.Contains(text, "System.DllNotFoundException");
            StringAssert.Contains(text, "libwebkitgtk-6.0.so.4");
            StringAssert.Contains(text, "libjavascriptcoregtk-6.0.so.1");
            StringAssert.Contains(error.ToString(), "libwebkitgtk-6.0.so.4");
            StringAssert.Contains(error.ToString(), "libjavascriptcoregtk-6.0.so.1");
        }
        finally
        {
            LogManager.Shutdown();
            Console.SetError(previousError);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Startup_FailedResultPersistsQueuedFailureAndExitCodeBeforeReturning()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-logging-" + Guid.NewGuid().ToString("N"));
        var previousError = Console.Error;
        using var error = new StringWriter();
        Assert.IsFalse(LogManager.IsInitialized);
        try
        {
            Console.SetError(error);
            Assert.AreEqual(17, DesktopApplication.Run(new(root, null), _ =>
            {
                // Models an inner startup boundary which already reported its exception and returned failure.
                LogManager.GetLogger("CodeAlta.Desktop.Test").Info("preceding startup diagnostic");
                DesktopLogging.ReportFailure(new DllNotFoundException("missing native dependency"), "native startup failed");
                return 17;
            }));
            Assert.IsTrue(LogManager.IsInitialized);
            var text = ReadLogs(root);
            StringAssert.Contains(text, "preceding startup diagnostic");
            StringAssert.Contains(text, "native startup failed");
            StringAssert.Contains(text, "missing native dependency");
            StringAssert.Contains(text, "exit code 17");
            StringAssert.Contains(error.ToString(), "exit code 17");
            StringAssert.Contains(error.ToString(), "System.DllNotFoundException: missing native dependency");
        }
        finally
        {
            LogManager.Shutdown();
            Console.SetError(previousError);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnconfirmedShutdown_KeepsLoggerAvailableForRetainedWork(bool throws)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-logging-" + Guid.NewGuid().ToString("N"));
        Assert.IsFalse(LogManager.IsInitialized);
        try
        {
            Action? retainedCallback = null;
            var failure = new InvalidOperationException("unconfirmed startup failure");
            int Run(DesktopLaunchOptions _)
            {
                var logger = LogManager.GetLogger("CodeAlta.Desktop.Test");
                retainedCallback = () => logger.Warn("retained work can still log");
                if (throws) throw failure;
                return 1;
            }
            if (throws)
                Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => DesktopApplication.Run(new(root, null), Run)));
            else Assert.AreEqual(1, DesktopApplication.Run(new(root, null), Run));
            Assert.IsTrue(LogManager.IsInitialized);
            StringAssert.Contains(ReadLogs(root), throws ? "unconfirmed startup failure" : "exit code 1");
            retainedCallback!();
            LogManager.Shutdown(); // Only the fixture retires the retained callback and its logger.
            StringAssert.Contains(ReadLogs(root), "retained work can still log");
        }
        finally
        {
            LogManager.Shutdown();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CommandLine_StartupFailureStillReturnsOneWithPersistedInnerException()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-logging-" + Guid.NewGuid().ToString("N"));
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.IsFalse(LogManager.IsInitialized);
        try
        {
            Console.SetError(error);
            var failure = new NeoAstraNativeLibraryException("native startup unavailable", new DllNotFoundException("missing WebKitGTK dependency"));
            var result = DesktopCommandLine.Run(["--data-root", root], output, error,
                options => DesktopApplication.Run(options, _ => throw failure));
            Assert.AreEqual(1, result);
            Assert.AreEqual(string.Empty, output.ToString());
            StringAssert.Contains(error.ToString(), "missing WebKitGTK dependency");
            StringAssert.Contains(ReadLogs(root), "missing WebKitGTK dependency");
            Assert.IsTrue(LogManager.IsInitialized);
        }
        finally
        {
            LogManager.Shutdown();
            Console.SetError(previousError);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void FlushPending_PreservesCaptureAndWriterForLaterDiagnosticsWithoutExposingMarkers()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-logging-" + Guid.NewGuid().ToString("N"));
        Assert.IsFalse(LogManager.IsInitialized);
        try
        {
            var capture = DesktopLogging.Initialize(root, out var flushPending)!;
            var logger = LogManager.GetLogger("CodeAlta.Desktop.Test");
            logger.Warn("before failure boundary");
            flushPending!();
            Assert.IsTrue(LogManager.IsInitialized);
            StringAssert.Contains(ReadLogs(root), "before failure boundary");
            Assert.HasCount(1, capture.Snapshot().Rows);
            logger.Warn("after failure boundary");
            flushPending();
            StringAssert.Contains(ReadLogs(root), "after failure boundary");
            Assert.HasCount(2, capture.Snapshot().Rows);
            Assert.IsFalse(ReadLogs(root).Contains("CodeAlta.Desktop.LogFlush", StringComparison.Ordinal));
        }
        finally
        {
            LogManager.Shutdown();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string ReadLogs(string root)
        => string.Concat(Directory.GetFiles(Path.Combine(root, "logs")).Select(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
}
