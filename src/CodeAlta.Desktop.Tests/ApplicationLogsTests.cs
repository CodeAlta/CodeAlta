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
    public void Capture_BoundsUnicodeRowsBytesAndReportsEvictionsWithoutDiskReads()
    {
        var capture = new DesktopLogCapture();
        capture.Append("time", "Info", "test", new string('a', 2047) + "😀" + "tail");
        var first = capture.Snapshot().Rows.Single();
        Assert.AreEqual(2047, first.Text.Length);
        Assert.IsTrue(first.TextTruncated);
        for (var i = 0; i < 300; i++) capture.Append("time", "Warn", "test", new string('界', 2048));
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
        for (var i = 0; i < 250; i++) capture.Append("2026-01-01T00:00:00Z", "Warn", "CodeAlta.Test", new string('\0', 2048) + i);
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
