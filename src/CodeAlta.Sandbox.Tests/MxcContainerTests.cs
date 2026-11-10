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
            await process.WaitForExitAsync(CancellationToken.None).WithinAsync(TestTime.Minutes(1));

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
        var exitCode = await process.WaitForExitAsync(CancellationToken.None).WithinAsync(TestTime.Minutes(1));

        Assert.AreEqual(0, exitCode, await errors);
        Assert.AreEqual(string.Empty, (await output).Trim(), "an empty configuration of the worktree");
        Assert.AreEqual(0, new FileInfo(worktreeConfig).Length, "made empty before the start, read-only in the sandbox");
    }

    [TestMethod]
    public async Task AProtectedFile_IsNeitherReplacedNorDeleted_NorMovedAwayWithAFolderAboveIt()
    {
        // A read-only grant stops a write to the file, not a rename in its writable folder that replaces it, nor the move of
        // a folder above it: what the sandbox puts there (core.fsmonitor) would run later outside the sandbox. Windows
        // holds the entries open, and Linux mounts them and the folders above them. Seatbelt refuses the replacement by
        // path, but not yet the move of a folder above the entry, which mxc cannot express there: only the replacement is
        // tried on macOS.
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
        var gitConfig = Path.Combine(project, ".git", "config");
        var altaConfig = Path.Combine(temp.Folder("project/.alta"), "config.toml");
        File.WriteAllText(altaConfig, "original");
        var originalGitConfig = File.ReadAllText(gitConfig);
        var policy = SandboxPolicyBuilder.Build(SandboxProfiles.Resolve(SandboxProfiles.Strict, []), new SandboxSessionFolders { WorkingDirectory = project },
            host, [], "test");
        string commandLine;
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllLines(Path.Combine(project, "attempts.cmd"),
            [
                "@echo off",
                $"\"{git}\" config core.fsmonitor evil",
                "echo evil> config.new",
                "move /y config.new .git\\config",
                "del /f .alta\\config.toml",
                "attrib -h .git",
                "move .git moved",
                "echo [core]> moved\\config",
                "echo fsmonitor = evil>> moved\\config",
                "move moved .git",
                "move .alta moved-alta",
                "echo evil> moved-alta\\mcp.json",
                "move moved-alta .alta",
            ]);
            commandLine = $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /d /c attempts.cmd";
        }
        else
        {
            List<string> lines =
            [
                $"'{git}' config core.fsmonitor evil",
                "echo evil > config.new",
                "mv -f config.new .git/config",
                "rm -f .alta/config.toml",
            ];
            if (MovesAreRefused)
            {
                lines.AddRange(
                [
                    "mv .git moved",
                    "printf '[core]\\n\\tfsmonitor = evil\\n' > moved/config",
                    "mv moved .git",
                    // A new folder in the place of the one moved away, from a copy: its configuration is an ordinary file.
                    "mkdir .git && cp -a moved/. .git/",
                    "printf '[core]\\n\\tfsmonitor = evil\\n' >> .git/config",
                    "mv .alta moved-alta",
                    "mkdir .alta && echo evil > .alta/mcp.json",
                    "echo evil > moved-alta/mcp.json",
                    "mv moved-alta .alta",
                ]);
            }

            File.WriteAllLines(Path.Combine(project, "attempts.sh"), lines);
            commandLine = "/bin/sh attempts.sh";
        }

        var outputs = new List<string>();
        await using (var process = await backend.StartAsync(policy, commandLine, null, CancellationToken.None))
        {
            var output = new StreamReader(process.StandardOutput, Encoding.UTF8).ReadToEndAsync();
            var errors = new StreamReader(process.StandardError, Encoding.UTF8).ReadToEndAsync();
            await process.WaitForExitAsync(CancellationToken.None).WithinAsync(TestTime.Minutes(1));
            outputs.Add(await output);
            outputs.Add(await errors);
        }

        var report = string.Join("\n", outputs);
        Assert.AreEqual(originalGitConfig, File.ReadAllText(gitConfig), report);
        Assert.AreEqual("original", File.ReadAllText(altaConfig), report);
        if (MovesAreRefused)
        {
            Assert.IsFalse(File.Exists(Path.Combine(project, ".alta", "mcp.json")), "a denied entry is not made by moving its folder away and back\n" + report);
            Assert.IsFalse(Directory.Exists(Path.Combine(project, "moved")) || Directory.Exists(Path.Combine(project, "moved-alta")), report);
        }

        // Released with the container: the user's own programs replace the file again.
        File.WriteAllText(Path.Combine(project, ".alta", "config.new"), "changed");
        File.Move(Path.Combine(project, ".alta", "config.new"), altaConfig, overwrite: true);
        Assert.AreEqual("changed", File.ReadAllText(altaConfig));
    }

    // The systems where the folders above a protected entry cannot move: held open on Windows, mounted on Linux.
    private static bool MovesAreRefused => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    private static string? FindGit(SandboxHost host)
    {
        // A folder of the developer tools, or one of the system that the sandbox reads (/usr/bin/git of macOS and Linux).
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return host.ToolFolders.Concat(path).Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
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
