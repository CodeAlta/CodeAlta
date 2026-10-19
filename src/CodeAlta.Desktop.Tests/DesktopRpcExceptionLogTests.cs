using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopRpcExceptionLogTests
{
    [TestMethod]
    public void TryMap_LogsAndLeavesTheAnswerToTheFramework()
    {
        // The exception of a failed command is written down, and the page still gets the framework's internal_error.
        var log = new DesktopRpcExceptionLog();
        Assert.IsFalse(log.TryMap(new ObjectDisposedException("CodeAlta.LiveTool.AltaReminderService"), default, out _));
        Assert.IsFalse(log.TryMap(new InvalidOperationException("failed"), default, out _));
    }

    [TestMethod]
    public void TryMap_LeavesCancellationsAndExplicitErrorsAlone()
    {
        var log = new DesktopRpcExceptionLog();
        Assert.IsFalse(log.TryMap(new OperationCanceledException(), default, out _));
        Assert.IsFalse(log.TryMap(new NeoRpcException("not_found", "Not found."), default, out _));
    }
}
