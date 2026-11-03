using System.Diagnostics;
using CodeAlta.Desktop.Fleet.MachineSetup;

namespace CodeAlta.Desktop.Tests;

/// <summary>The OpenSSH runner (<see cref="OpenSshRunner"/>) over a stand-in for ssh that never opens its forward.</summary>
[TestClass]
public sealed class OpenSshRunnerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CancellingTheStartOfAForward_EndsItsSshProcess()
    {
        var folder = Directory.CreateTempSubdirectory("codealta-ssh-forward-").FullName;
        Process? observed = null;
        try
        {
            // An ssh that runs on and never listens: the forward waits for its port until it is cancelled.
            string ssh;
            if (OperatingSystem.IsWindows())
            {
                ssh = Path.Combine(folder, "ssh.cmd");
                await File.WriteAllTextAsync(ssh, "@ping -n 120 127.0.0.1 >nul\r\n", TestContext.CancellationToken);
            }
            else
            {
                ssh = Path.Combine(folder, "ssh");
                await File.WriteAllTextAsync(ssh, "#!/bin/sh\nexec sleep 120\n", TestContext.CancellationToken);
                File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var runner = new OpenSshRunner(ssh) { ForwardStarted = process => observed = Process.GetProcessById(process.Id) };
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            cancel.CancelAfter(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<OperationCanceledException>(() => runner.ForwardAsync(new SshTarget(null, "box", null), 2586, false, cancel.Token));

            Assert.IsNotNull(observed, "ssh started");
            Assert.IsTrue(observed.WaitForExit(TimeSpan.FromSeconds(10)), "the ssh of a forward that was cancelled before it opened is ended, not left running");
        }
        finally
        {
            if (observed is { HasExited: false }) observed.Kill(entireProcessTree: true);
            observed?.Dispose();
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
