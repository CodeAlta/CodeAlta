using CodeAlta.CodexAppServer;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexAppServerProcessTests
{
    [TestMethod]
    public async Task Disposing_AChildThatRuns_EndsItAndItsExitTaskDoesNotFail()
    {
        // Ending the child disposes its process: the task that reads its exit code must have read it first, or it fails
        // where nothing observes it ("No process is associated with this object", in the log of the application).
        var startInfo = OperatingSystem.IsWindows()
            ? new CodexAppServerStartInfo { FileName = "cmd.exe", Arguments = ["/c", "ping -n 30 127.0.0.1 >nul"] }
            : new CodexAppServerStartInfo { FileName = "sleep", Arguments = ["30"] };
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var process = CodexAppServerProcess.Start(startInfo);
            await process.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            await process.Exited.ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsTrue(process.Exited.IsCompletedSuccessfully, $"attempt {attempt}: {process.Exited.Exception?.InnerException?.Message}");
        }
    }
}
