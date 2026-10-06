using CodeAlta.Desktop.Terminals;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class TerminalIntegrationTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static async Task Until(Func<bool> condition, Func<string> what)
    {
        var limit = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > limit) Assert.Fail(what());
            await Task.Delay(20);
        }
    }

    [TestMethod]
    public void EachShell_IsStartedWithItsScript_AndAnotherProgramAsItIs()
    {
        var folder = Directory.CreateTempSubdirectory("codealta-terminal-scripts-").FullName;
        try
        {
            var environment = new Dictionary<string, string> { ["PROMPT"] = "$T $P$G", ["ZDOTDIR"] = "/home/me/.config/zsh" };
            TerminalProfile Profile(TerminalShellKind kind, params string[] arguments) => new("id", "name", "/bin/shell", arguments, kind);

            // PowerShell reads the script as a file of the system: its execution policy decides.
            var powershell = TerminalIntegration.Launch(Profile(TerminalShellKind.PowerShell, "-NoLogo"), folder, environment);
            var script = Path.Combine(folder, "integration.ps1");
            CollectionAssert.AreEqual(new[] { "-NoLogo", "-NoExit", "-Command", $"try {{ . '{script}' }} catch {{}}" }, powershell.Arguments.ToArray());
            Assert.AreEqual(0, powershell.Variables.Count);
            StringAssert.Contains(File.ReadAllText(script), "function Global:prompt");
            CollectionAssert.AreEqual(powershell.Arguments.ToArray(), TerminalIntegration.Launch(Profile(TerminalShellKind.WindowsPowerShell, "-NoLogo"), folder, environment).Arguments.ToArray());

            // The Command Prompt has no script: its prompt carries the marks around the prompt of the user.
            var command = TerminalIntegration.Launch(Profile(TerminalShellKind.CommandPrompt), folder, environment);
            Assert.AreEqual(0, command.Arguments.Count);
            Assert.AreEqual("$e]133;A$e\\$e]9;9;$P$e\\$T $P$G$e]133;B$e\\", command.Variables["PROMPT"]);
            Assert.AreEqual("$e]133;A$e\\$e]9;9;$P$e\\$P$G$e]133;B$e\\", TerminalIntegration.Launch(Profile(TerminalShellKind.CommandPrompt), null, new Dictionary<string, string>()).Variables["PROMPT"]);

            // bash reads the script instead of its own files; a login shell is told to read what a login shell reads.
            var bash = TerminalIntegration.Launch(Profile(TerminalShellKind.Bash, "--login", "-i"), folder, environment);
            CollectionAssert.AreEqual(new[] { "--init-file", Path.Combine(folder, "integration.bash"), "-i" }, bash.Arguments.ToArray());
            Assert.AreEqual("1", bash.Variables[TerminalIntegration.LoginVariable]);
            Assert.AreEqual(0, TerminalIntegration.Launch(Profile(TerminalShellKind.Bash), folder, environment).Variables.Count);

            // zsh reads its files from a folder of the application, which knows where those of the user are.
            var zsh = TerminalIntegration.Launch(Profile(TerminalShellKind.Zsh, "-l"), folder, environment);
            CollectionAssert.AreEqual(new[] { "-l" }, zsh.Arguments.ToArray());
            Assert.AreEqual((Path.Combine(folder, "zsh"), "/home/me/.config/zsh"), (zsh.Variables["ZDOTDIR"], zsh.Variables[TerminalIntegration.UserZdotdirVariable]));
            foreach (var name in new[] { ".zshenv", ".zprofile", ".zshrc", ".zlogin" }) Assert.IsTrue(File.Exists(Path.Combine(folder, "zsh", name)), name);
            Assert.AreEqual(string.Empty, TerminalIntegration.Launch(Profile(TerminalShellKind.Zsh), folder, new Dictionary<string, string>()).Variables[TerminalIntegration.UserZdotdirVariable]);

            var fish = TerminalIntegration.Launch(Profile(TerminalShellKind.Fish, "-l"), folder, environment);
            CollectionAssert.AreEqual(new[] { "-l", "--init-command", $"source \"{Path.Combine(folder, "integration.fish").Replace("\\", "\\\\", StringComparison.Ordinal)}\"" }, fish.Arguments.ToArray());

            // The shells of Unix read their scripts with the line ends of Unix.
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) Assert.IsFalse(File.ReadAllText(file).Contains('\r'), file);
            Assert.AreEqual(7, Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count());

            // Another program is started as it is, and so are the shells that need a file when none can be written.
            var other = TerminalIntegration.Launch(Profile(TerminalShellKind.Other, "-d", "Ubuntu"), folder, environment);
            CollectionAssert.AreEqual(new[] { "-d", "Ubuntu" }, other.Arguments.ToArray());
            Assert.AreEqual(0, other.Variables.Count);
            foreach (var kind in new[] { TerminalShellKind.PowerShell, TerminalShellKind.Bash, TerminalShellKind.Zsh, TerminalShellKind.Fish })
            {
                var plain = TerminalIntegration.Launch(Profile(kind, "-x"), null, environment);
                Assert.AreEqual(("-x", 0), (plain.Arguments.Single(), plain.Variables.Count), kind.ToString());
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task TheShellsOfThisSystem_ReportTheirPromptsTheirCommandsAndTheirFolder()
    {
        var kinds = new[] { TerminalShellKind.PowerShell, TerminalShellKind.Bash, TerminalShellKind.Zsh, TerminalShellKind.Fish };
        var profiles = new TerminalProfiles().List().Where(profile => kinds.Contains(profile.Kind)).GroupBy(static profile => profile.Kind).Select(static group => group.First()).ToArray();
        // Every system this is tested on has one of them at least.
        Assert.IsTrue(profiles.Length > 0);
        // Side by side: each shell takes a moment to start.
        await Task.WhenAll(profiles.Select(profile => Task.Run(() => ReportsAsync(profile))));
    }

    private static async Task ReportsAsync(TerminalProfile found)
    {
        var root = Directory.CreateTempSubdirectory("codealta-terminal-shell-").FullName;
        var work = Directory.CreateDirectory(Path.Combine(root, "work", "inner folder")).Parent!.FullName;
        // The files of the person who runs the test are not read: the shell starts as it does for a new user.
        var profile = found.Kind == TerminalShellKind.PowerShell ? found with { Arguments = [.. found.Arguments, "-NoProfile"] } : found;
        var environment = TerminalEnvironment.Create("test", "1.2.3");
        environment["HOME"] = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        environment.Remove("ZDOTDIR");
        environment.Remove("XDG_CONFIG_HOME");
        environment.Remove("PROMPT_COMMAND");
        var launch = TerminalIntegration.Launch(profile, Path.Combine(root, "scripts"), environment);
        foreach (var (name, value) in launch.Variables)
        {
            if (value is null) environment.Remove(name);
            else environment[name] = value;
        }
        var terminal = new DesktopTerminal(new TerminalDescription("test", null, null, work, profile, null, Keep: true, Agent: true, 120, 30),
            PseudoTerminal.Start(new PseudoTerminalStart(profile.FileName, launch.Arguments, work, environment, 120, 30)), static (_, _) => { }, TimeProvider.System);
        string Shown() => $"{profile.Name}: {string.Join(" | ", terminal.ReadLines(40))}";
        try
        {
            terminal.Start();
            await Until(() => terminal.Describe().Integrated, () => "No prompt was reported. " + Shown());
            Assert.AreEqual((true, false), (terminal.Describe().Running, terminal.Describe().Busy), Shown());

            // What a command printed and how it ended, however long the shell stays quiet: the wait ends with the command.
            var quiet = TimeSpan.FromMinutes(10);
            var echoed = await terminal.TypeAsync("echo codealta-marker\r", Patience, quiet, 200, default);
            Assert.IsTrue(echoed.Sent && echoed.Settled && echoed.Command is not null, Shown());
            Assert.AreEqual(("echo codealta-marker", 0, "codealta-marker", true), (echoed.Command!.CommandLine, echoed.Command.ExitCode, echoed.Command.Output.Trim(), echoed.Command.Finished), Shown());

            var failing = OperatingSystem.IsWindows() && profile.Kind == TerminalShellKind.PowerShell ? "cmd /c exit 7" : "sh -c 'exit 7'";
            var failed = await terminal.TypeAsync(failing + "\r", Patience, quiet, 200, default);
            Assert.AreEqual((failing, 7), (failed.Command?.CommandLine, failed.Command?.ExitCode), Shown());
            await Until(() => terminal.Describe() is { Busy: false, LastExitCode: 7 }, () => "The exit code was not kept. " + Shown());

            // A command of PowerShell that fails has no exit code: it does not take the one of the program before it.
            var commands = 4;
            if (profile.Kind == TerminalShellKind.PowerShell)
            {
                var missing = await terminal.TypeAsync("Get-Item codealta-nope\r", Patience, quiet, 200, default);
                Assert.AreEqual(1, missing.Command?.ExitCode, Shown());
                // A prompt that takes the place of the one of the script (a theme loaded later) is wrapped in its turn:
                // the command that brought it ends without a code, and the next ones are reported as before.
                var replaced = await terminal.TypeAsync("function global:prompt { 'codealta-new> ' }\r", Patience, quiet, 200, default);
                Assert.IsTrue(replaced.Settled && replaced.Command is { Finished: true, ExitCode: null }, Shown());
                var again = await terminal.TypeAsync((OperatingSystem.IsWindows() ? "cmd /c exit 5" : "sh -c 'exit 5'") + "; echo codealta-again\r", Patience, quiet, 200, default);
                Assert.AreEqual((0, "codealta-again"), (again.Command?.ExitCode, again.Command?.Output.Trim()), Shown());
                await Until(() => terminal.ReadLines(5).Any(static line => line.StartsWith("codealta-new>", StringComparison.Ordinal)) && !terminal.Describe().Busy, () => "The new prompt. " + Shown());
                commands += 3;
            }

            // A line with what would end or split a mark is reported whole.
            var line = profile.Kind == TerminalShellKind.PowerShell ? "echo 'a;b\\c'" : "printf '%s\\n' 'a;b\\c'";
            var odd = await terminal.TypeAsync(line + "\r", Patience, quiet, 200, default);
            Assert.AreEqual((line, "a;b\\c"), (odd.Command?.CommandLine, odd.Command?.Output.Trim()), Shown());

            // A line that runs nothing reports no command, and the shell says where it is when it moves.
            var empty = await terminal.TypeAsync("\r", Patience, quiet, 200, default);
            Assert.IsTrue(empty.Settled && empty.Command is null, Shown());
            var moved = await terminal.TypeAsync("cd 'inner folder'\r", Patience, quiet, 200, default);
            Assert.AreEqual(0, moved.Command?.ExitCode, Shown());
            await Until(() => string.Equals(Path.GetFileName(terminal.Describe().Folder), "inner folder", StringComparison.Ordinal), () => $"The folder is {terminal.Describe().Folder}. " + Shown());
            Assert.IsTrue(Path.IsPathFullyQualified(terminal.Describe().Folder), terminal.Describe().Folder);
            Assert.AreEqual(commands, terminal.ReadCommands(10, output: false).Count, Shown());

            terminal.Write("exit\r");
            await terminal.Ended.WaitAsync(Patience);
        }
        finally
        {
            terminal.Dispose();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* A shell of Windows can hold its folder for a moment after it ended. */ }
        }
    }

    [TestMethod]
    public async Task TheCommandPrompt_ReportsItsPromptAndItsFolder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("codealta-terminal-cmd-").FullName;
        var terminals = new DesktopTerminals("1.2.3", Path.Combine(root, "scripts"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "x64"));
            var (status, terminal) = terminals.Create(new TerminalRequest(null, null, root, "cmd", null, Agent: true));
            Assert.AreEqual("ok", status);
            string Shown() => string.Join(" | ", terminal!.ReadLines(40));
            await Until(() => string.Equals(terminal!.Describe().Folder, root, StringComparison.OrdinalIgnoreCase) && terminal.ReadScreen().Rows.Any(static row => row.EndsWith('>')), () => "No prompt. " + Shown());
            // It reports no command: nothing says it is busy, but it is at rest as soon as its prompt is back.
            Assert.IsFalse(terminal!.Describe().Integrated);
            var typed = await terminal.TypeAsync("echo codealta-marker\r", Patience, TimeSpan.FromMinutes(10), 200, default);
            Assert.IsTrue(typed.Sent && typed.Settled && typed.Command is null, Shown());
            CollectionAssert.Contains(typed.Text!.Split('\n'), "codealta-marker", Shown());
            // A folder whose name reads as an escaped character is the folder it is.
            await terminal.TypeAsync("cd x64\r", Patience, TimeSpan.FromMinutes(10), 200, default);
            Assert.AreEqual(Path.Combine(root, "x64"), terminal.Describe().Folder, ignoreCase: true, Shown());

            // Without the integration the prompt carries nothing.
            var (_, plain) = terminals.Create(new TerminalRequest(null, null, root, "cmd", null, Agent: true, Integration: false));
            await Until(() => plain!.ReadScreen().Rows.Any(static row => row.EndsWith('>')), () => "No prompt.");
            typed = await plain!.TypeAsync("echo plain-marker\r", Patience, TimeSpan.FromMilliseconds(600), 200, default);
            CollectionAssert.Contains(typed.Text!.Split('\n'), "plain-marker");
            Assert.AreEqual(root, plain.Describe().Folder, ignoreCase: true);
        }
        finally
        {
            await terminals.CloseAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* The shell can hold its folder for a moment after it ended. */ }
        }
    }
}
