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
            await Assert.ThrowsAsync<OperationCanceledException>(() => runner.ForwardAsync(new SshTarget(null, "box", null), 2586, null, cancel.Token));

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

    [TestMethod]
    public async Task TrustingAHost_NeverAcceptsAnotherKeyThanTheOneShown()
    {
        var folder = Directory.CreateTempSubdirectory("codealta-ssh-trust-").FullName;
        try
        {
            var record = Path.Combine(folder, "arguments.txt");
            var knownHosts = Path.Combine(folder, "known_hosts");
            var ssh = RecordingSsh(folder, record);
            var keyA = "box ssh-ed25519 " + Convert.ToBase64String([.. Enumerable.Range(1, 51).Select(static i => (byte)i)]);
            var keyB = "box ssh-ed25519 " + Convert.ToBase64String([.. Enumerable.Range(2, 51).Select(static i => (byte)i)]);
            var offered = keyA;
            OpenSshRunner Runner() => new(ssh) { HostKeyReader = (_, _) => Task.FromResult<string?>(offered + "\n"), KnownHostsPath = knownHosts };
            var target = new SshTarget("me", "box", null);
            var runner = Runner();
            var shown = await runner.ReadHostKeyAsync(target, TestContext.CancellationToken);
            Assert.IsNotNull(shown);

            // Between the display and "Trust it", the computer offers another key: nothing runs with it.
            offered = keyB;
            var refused = await Runner().RunAsync(target, "exit 0", shown, null, TestContext.CancellationToken);
            Assert.AreEqual(SshResult.ConnectionFailed, refused.ExitCode);
            Assert.AreEqual("host_key_changed", SshNodeInstaller.ConnectionCode(refused.Error));
            Assert.IsFalse(File.Exists(record), "ssh never ran with a key the person did not trust");

            // The runner that showed the key requires that very key: ssh knows it alone and accepts no other.
            Assert.IsTrue((await runner.RunAsync(target, "exit 0", shown, null, TestContext.CancellationToken)).Succeeded);
            var arguments = await File.ReadAllTextAsync(record, TestContext.CancellationToken);
            Assert.IsFalse(arguments.Contains("accept-new", StringComparison.Ordinal), $"ssh is never told to accept a key it does not know: {arguments}");
            StringAssert.Contains(arguments, "StrictHostKeyChecking=yes");
            StringAssert.Contains(arguments, "UserKnownHostsFile=");
            var recorded = await File.ReadAllTextAsync(knownHosts, TestContext.CancellationToken);
            StringAssert.Contains(recorded, keyA, "the key trusted is recorded, as ssh records a key it accepts");
            Assert.IsFalse(recorded.Contains(keyB, StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    // An ssh that writes its arguments to a file and succeeds.
    private static string RecordingSsh(string folder, string record)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(folder, "ssh.cmd");
            File.WriteAllText(path, $"@echo %* >> \"{record}\"\r\n@exit /b 0\r\n");
            return path;
        }

        var script = Path.Combine(folder, "ssh");
        File.WriteAllText(script, $"#!/bin/sh\necho \"$*\" >> '{record}'\ncat > /dev/null\nexit 0\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
