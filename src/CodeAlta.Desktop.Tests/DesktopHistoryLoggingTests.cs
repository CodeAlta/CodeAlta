using System.Text.Json;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Tests;

/// <summary>Initializes the process logger, so it never runs beside other logging tests.</summary>
[TestClass]
[DoNotParallelize]
public sealed class DesktopHistoryLoggingTests
{
    [TestMethod]
    public async Task UnexpectedReadFailures_AreLoggedLocallyButAnsweredWithCodesOnly()
    {
        var capture = new DesktopLogCapture();
        var config = new LogManagerConfig();
        config.RootLogger.MinimumLevel = LogLevel.Info;
        config.RootLogger.Writers.Add(new DesktopCaptureLogWriter(capture));
        LogManager.Initialize(config);
        try
        {
            var history = await WorkspaceService.ReadHistoryAsync(new("history-session", null), (_, _, _) =>
                Task.FromException<AgentSessionHistoryPage>(new InvalidOperationException("private history detail")), CancellationToken.None);
            var timeline = await WorkspaceService.ReadTimelineAsync(new("timeline-session", null), (_, _, _) =>
                Task.FromException<AgentSessionHistoryPage>(new AgentSessionHistoryException("unrecognized_code")), CancellationToken.None);
            var source = await WorkspaceService.ReadSourceAsync(new(new("source-session", "10", "7"), "0", "10", "0"), (_, _, _, _, _) =>
                Task.FromException<AgentHistorySourceChunk>(new IOException("private source detail")), CancellationToken.None);
            var changed = await WorkspaceService.ReadHistoryAsync(new("changed-session", null), (_, _, _) =>
                Task.FromException<AgentSessionHistoryPage>(new AgentSessionHistoryException("history_changed")), CancellationToken.None);

            Assert.AreEqual("read_failed", history.Status);
            Assert.AreEqual("read_failed", timeline.Page.Status);
            Assert.AreEqual("read_failed", source.Status);
            Assert.AreEqual("history_changed", changed.Status);
            Assert.IsFalse(JsonSerializer.Serialize(history, DesktopJsonContext.Default.HistoryResponse).Contains("private", StringComparison.Ordinal));
            Assert.IsFalse(JsonSerializer.Serialize(source, DesktopJsonContext.Default.HistorySourceResponse).Contains("private", StringComparison.Ordinal));
        }
        finally { LogManager.Shutdown(); }

        var rows = capture.Snapshot().Rows;
        var logged = string.Join("\n", rows.Select(static row => row.Text));
        Assert.IsTrue(rows.All(static row => row.Logger == "CodeAlta.Desktop.History" && row.Level == "Warn"), logged);
        StringAssert.Contains(logged, "history for session history-session failed with read_failed");
        StringAssert.Contains(logged, "InvalidOperationException");
        StringAssert.Contains(logged, "private history detail");
        StringAssert.Contains(logged, "historyTimeline for session timeline-session failed with read_failed");
        StringAssert.Contains(logged, "failed with read_failed (history code unrecognized_code)");
        StringAssert.Contains(logged, "historySource for session source-session failed with read_failed");
        StringAssert.Contains(logged, "private source detail");
        // A journal that grows while it is read is routine during a live turn, not a failure to investigate.
        Assert.IsFalse(logged.Contains("changed-session", StringComparison.Ordinal), logged);
    }
}
