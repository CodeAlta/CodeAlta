using System.Text.Json;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationLogsTests
{
    [TestMethod]
    public void ConfirmedClear_UsesObservedBoundaryAndPreservesNewerAppends()
    {
        var capture = new DesktopLogCapture();
        var rpc = new ApplicationLogsService(capture);
        capture.Append("t", "Info", "test", "before");
        var observed = rpc.Read(new());
        capture.Append("t", "Info", "test", "after");
        var result = rpc.Clear(new(observed.CaptureId!, observed.Boundary, observed.Grant, "CLEAR CAPTURED LOGS"));
        Assert.AreEqual("cleared", result.Status);
        Assert.AreEqual(1, result.ClearedRows);
        Assert.AreEqual("0", result.CoveredOmitted);
        CollectionAssert.AreEqual(new[] { "after" }, rpc.Read(new()).Rows.Select(row => row.Text).ToArray());
        Assert.AreEqual("stale_snapshot", rpc.Clear(new(observed.CaptureId!, observed.Boundary, observed.Grant, "CLEAR CAPTURED LOGS")).Status);
    }

    [TestMethod]
    public void Clear_RejectsFabricatedBoundaryIdentityAndConfirmation_AccountsForOnlyActualEvictions()
    {
        var capture = new DesktopLogCapture();
        var rpc = new ApplicationLogsService(capture);
        for (var i = 0; i < DesktopLogCapture.MaximumRows + 72; i++) capture.Append("t", "Warn", "test", $"before {i}");
        var observed = rpc.Read(new());
        capture.Append("t", "Warn", "test", "newer");
        Assert.AreEqual("invalid_request", rpc.Clear(new(Guid.NewGuid().ToString("D"), observed.Boundary, observed.Grant, "CLEAR CAPTURED LOGS")).Status);
        Assert.AreEqual("invalid_request", rpc.Clear(new(observed.CaptureId!, "999999", observed.Grant, "CLEAR CAPTURED LOGS")).Status);
        Assert.AreEqual("invalid_request", rpc.Clear(new(observed.CaptureId!, "0" + observed.Boundary, observed.Grant, "CLEAR CAPTURED LOGS")).Status);
        Assert.AreEqual("invalid_request", rpc.Clear(new(observed.CaptureId!, observed.Boundary, observed.Grant, "confirm")).Status);
        Assert.AreEqual("invalid_request", rpc.Clear(new(observed.CaptureId!, observed.Boundary, "unissued", "CLEAR CAPTURED LOGS")).Status);
        var cleared = rpc.Clear(new(observed.CaptureId!, observed.Boundary, observed.Grant, "CLEAR CAPTURED LOGS"));
        Assert.AreEqual("cleared", cleared.Status);
        Assert.AreEqual(ApplicationLogsService.MaximumResponseRows, observed.Rows.Count);
        Assert.AreEqual(DesktopLogCapture.MaximumRows - ApplicationLogsService.MaximumResponseRows, observed.ReadOmitted);
        Assert.AreEqual(DesktopLogCapture.MaximumRows - 1, cleared.ClearedRows); // Read-omitted rows remain real captured rows, not capacity loss.
        Assert.AreEqual(long.Parse(observed.CaptureOmitted) + 1, long.Parse(cleared.CoveredOmitted)); // Evicted after read, still covered by boundary.
        var next = rpc.Read(new());
        Assert.AreEqual("0", next.CaptureOmitted);
        Assert.AreEqual("newer", next.Rows.Single().Text);
        Assert.AreEqual("unavailable", new ApplicationLogsService(null).Clear(new(observed.CaptureId!, observed.Boundary, observed.Grant, "CLEAR CAPTURED LOGS")).Status);
    }

    [TestMethod]
    public void Clear_RejectsExpiredAndZeroBoundaryGrantsWithoutTouchingRows()
    {
        var capture = new DesktopLogCapture();
        var rpc = new ApplicationLogsService(capture);
        var empty = rpc.Read(new());
        Assert.AreEqual("invalid_request", rpc.Clear(new(empty.CaptureId!, empty.Boundary, empty.Grant, "CLEAR CAPTURED LOGS")).Status);
        capture.Append("t", "Info", "test", "retained");
        var expired = rpc.Read(new());
        for (var i = 0; i < 32; i++) rpc.Read(new());
        Assert.AreEqual("invalid_request", rpc.Clear(new(expired.CaptureId!, expired.Boundary, expired.Grant, "CLEAR CAPTURED LOGS")).Status);
        Assert.AreEqual("retained", capture.Snapshot().Rows.Single().Text);
    }

    [TestMethod]
    public void Clear_NeverDeletesTestOwnedRollingWriterContent_AndBoundsTheResponse()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-clear-capture-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var capture = DesktopLogging.Initialize(root)!;
            LogManager.GetLogger("CodeAlta.Desktop.Test").Info("literal disposable root log entry");
            Assert.IsTrue(SpinWait.SpinUntil(() => capture.Snapshot().Rows.Length > 0, TimeSpan.FromSeconds(5)));
            var rpc = new ApplicationLogsService(capture);
            var observed = rpc.Read(new());
            var cleared = rpc.Clear(new(observed.CaptureId!, observed.Boundary, observed.Grant, "CLEAR CAPTURED LOGS"));
            Assert.AreEqual("cleared", cleared.Status);
            Assert.AreEqual(1, cleared.ClearedRows);
            Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(cleared, DesktopJsonContext.Default.ApplicationLogsClearResponse).Length < 1024);
            Assert.AreEqual(0, capture.Snapshot().Rows.Length);
            LogManager.Shutdown(); // Flush only this disposable test writer.
            Assert.IsTrue(Directory.GetFiles(Path.Combine(root, "logs")).Any(path =>
                File.ReadAllText(path).Contains("literal disposable root log entry", StringComparison.Ordinal)));
        }
        finally
        {
            LogManager.Shutdown();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    [TestMethod]
    public void Capture_BoundsUnicodeRowsBytesAndReportsEvictionsWithoutDiskReads()
    {
        var capture = new DesktopLogCapture();
        capture.Append("time", "Info", "test", new string('a', 2047) + "😀" + "tail");
        var first = capture.Snapshot().Rows.Single();
        Assert.AreEqual(2047, first.Text.Length);
        Assert.IsTrue(first.TextTruncated);
        for (var i = 0; i < 300; i++) capture.Append("time", "Warn", "test", new string('界', 2048));
        for (var i = 0; i < DesktopLogCapture.MaximumRows; i++) capture.Append("time", "Warn", "test", "short");
        var (rows, omitted) = capture.Snapshot();
        Assert.IsTrue(omitted > 0);
        Assert.IsTrue(rows.Length <= DesktopLogCapture.MaximumRows);
        Assert.IsTrue(rows.Sum(row => System.Text.Encoding.UTF8.GetByteCount(row.Text) + System.Text.Encoding.UTF8.GetByteCount(row.Logger)
            + System.Text.Encoding.UTF8.GetByteCount(row.Timestamp) + System.Text.Encoding.UTF8.GetByteCount(row.Level) + 64)
            <= DesktopLogCapture.MaximumStoredBytes);
        Assert.AreEqual("Warn", rows[^1].Level);
    }

    [TestMethod]
    public void Read_BoundsEscapedWireAndDisclosesCaptureAndResponseOmissions()
    {
        var capture = new DesktopLogCapture();
        for (var i = 0; i < DesktopLogCapture.MaximumRows + 50; i++) capture.Append("2026-01-01T00:00:00Z", "Warn", "CodeAlta.Test", new string('\0', 2048) + i);
        var result = new ApplicationLogsService(capture).Read(new());
        Assert.AreEqual("ok", result.Status);
        Assert.IsTrue(long.Parse(result.CaptureOmitted) > 0);
        Assert.IsTrue(result.ReadOmitted > 0);
        Assert.IsTrue(result.Rows.Count > 0);
        Assert.IsTrue(result.Rows.All(row => row.TextTruncated));
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.ApplicationLogsResponse).Length <= ApplicationLogsService.MaximumResponseBytes);
        Assert.AreEqual("unavailable", new ApplicationLogsService(null).Read(new()).Status);
    }

    [TestMethod]
    public void Writer_IsInstanceOwnedAndOnlyReceivesConfiguredLevels()
    {
        Assert.IsFalse(LogManager.IsInitialized);
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-capture-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var capture = DesktopLogging.Initialize(root)!;
            Assert.IsNotNull(capture);
            var logger = LogManager.GetLogger("CodeAlta.Desktop.Test");
            logger.Info("literal captured info");
            Assert.IsTrue(SpinWait.SpinUntil(() => capture.Snapshot().Rows.Any(row => row.Text.Contains("literal captured info", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)));
            Assert.IsNull(DesktopLogging.Initialize("unused-external-owner"));
            Assert.IsTrue(LogManager.IsInitialized);
        }
        finally
        {
            LogManager.Shutdown();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
