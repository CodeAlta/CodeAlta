using System.Text;
using CodeAlta.Sandbox.Mxc;

namespace CodeAlta.Sandbox.Tests;

// Real containers: skipped where this computer has no sandbox, as the browser tests are without Edge.
[TestClass]
public sealed class MxcContainerTests
{
    [TestMethod]
    public async Task AProgram_WritesItsProject_AndCannotReadTheHomeFolder()
    {
        var backend = new MxcSandboxBackend();
        if (backend.GetAvailability() is { IsAvailable: false } availability) Assert.Inconclusive(availability.Reason ?? "No sandbox on this computer.");
        using var temp = new TempTree();
        var project = temp.Folder("project");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var secret = Path.Combine(home, ".codealta-sandbox-test-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(secret, "secret");
        try
        {
            var policy = SandboxPolicyBuilder.Build(SandboxProfiles.Resolve(SandboxProfiles.Strict, []), new SandboxSessionFolders { WorkingDirectory = project },
                SandboxHostDiscovery.Current(temp.Folder("data"), []), [], "test");
            var command = OperatingSystem.IsWindows()
                ? $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /d /c \"echo in> written.txt & type written.txt & type \"{secret}\"\""
                : $"/bin/sh -c \"echo in > written.txt; cat written.txt; cat '{secret}'\"";

            await using var process = await backend.StartAsync(policy, command, null, CancellationToken.None);
            var output = new StreamReader(process.StandardOutput, Encoding.UTF8).ReadToEndAsync();
            var errors = new StreamReader(process.StandardError, Encoding.UTF8).ReadToEndAsync();
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1));

            Assert.Contains("in", await output, await errors);
            Assert.DoesNotContain("secret", await output, "the home folder is outside the sandbox");
            Assert.IsTrue(File.Exists(Path.Combine(project, "written.txt")));
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [TestMethod]
    public async Task Git_RunsInARepositoryWithAConfigurationPerWorktree_ThatHasNone()
    {
        // With extensions.worktreeConfig, git reads .git/config.worktree: a missing one is no settings, a denied one is
        // fatal ("unable to access '.git/config.worktree': Permission denied").
        var backend = new MxcSandboxBackend();
        if (backend.GetAvailability() is { IsAvailable: false } availability) Assert.Inconclusive(availability.Reason ?? "No sandbox on this computer.");
        using var temp = new TempTree();
        var project = temp.Folder("project");
        var host = SandboxHostDiscovery.Current(temp.Folder("data"), []);
        var git = FindGit(host);
        if (git is null)
        {
            Assert.Inconclusive("git is not on the PATH.");
            return;
        }

        RunGit(git, project, "init", "--quiet");
        RunGit(git, project, "config", "extensions.worktreeConfig", "true");
        var worktreeConfig = Path.Combine(project, ".git", "config.worktree");
        Assert.IsFalse(File.Exists(worktreeConfig));
        var policy = SandboxPolicyBuilder.Build(SandboxProfiles.Resolve(SandboxProfiles.Strict, []), new SandboxSessionFolders { WorkingDirectory = project },
            host, [], "test");

        await using var process = await backend.StartAsync(policy, $"\"{git}\" config --worktree --list --show-origin", null, CancellationToken.None);
        var output = new StreamReader(process.StandardOutput, Encoding.UTF8).ReadToEndAsync();
        var errors = new StreamReader(process.StandardError, Encoding.UTF8).ReadToEndAsync();
        var exitCode = await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1));

        Assert.AreEqual(0, exitCode, await errors);
        Assert.AreEqual(string.Empty, (await output).Trim(), "an empty configuration of the worktree");
        Assert.AreEqual(0, new FileInfo(worktreeConfig).Length, "made empty before the start, read-only in the sandbox");
    }

    private static string? FindGit(SandboxHost host)
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        return host.ToolFolders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }

    private static void RunGit(string git, string folder, params string[] arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(git, arguments)
        {
            WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        var errors = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, errors);
    }
}
