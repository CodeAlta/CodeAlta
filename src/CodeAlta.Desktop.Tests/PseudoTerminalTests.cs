using System.Text;
using CodeAlta.Desktop.Terminals;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class PseudoTerminalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // A program that runs a line of the system's command interpreter and ends.
    private static PseudoTerminalStart Command(string line, string? directory = null, (string Name, string Value)? variable = null, int columns = 80, int rows = 24)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(static entry => (string)entry.Key, static entry => (string?)entry.Value ?? string.Empty, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (variable is { } added) environment[added.Name] = added.Value;
        return OperatingSystem.IsWindows()
            ? new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", line], directory ?? Environment.CurrentDirectory, environment, columns, rows)
            : new("/bin/sh", ["-c", line], directory ?? Environment.CurrentDirectory, environment, columns, rows);
    }

    // The interpreter itself, waiting for lines.
    private static PseudoTerminalStart Interpreter()
    {
        var start = Command("unused");
        return start with { Arguments = OperatingSystem.IsWindows() ? ["/d"] : [] };
    }

    // Everything a program shows until it ends, read as its terminal would read it.
    private static Task<string> Shown(PseudoTerminal terminal, StringBuilder? live = null) => Task.Run(() =>
    {
        var text = live ?? new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var characters = new char[4096];
        while (terminal.Read(bytes) is var read and > 0)
        {
            var count = decoder.GetChars(bytes, 0, read, characters, 0);
            lock (text) text.Append(characters, 0, count);
        }
        lock (text) return text.ToString();
    });

    private static async Task Until(StringBuilder live, string expected)
    {
        var limit = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < limit)
        {
            lock (live)
            {
                if (live.ToString().Contains(expected, StringComparison.Ordinal)) return;
            }
            await Task.Delay(20);
        }
        lock (live) Assert.Fail($"'{expected}' was not shown. Shown: {live}");
    }

    private static void Type(PseudoTerminal terminal, string text) => terminal.Write(Encoding.UTF8.GetBytes(text));

    [TestMethod]
    public async Task AProgram_ShowsItsOutput_AndReportsItsExitCode()
    {
        using var terminal = PseudoTerminal.Start(Command("echo shown-by-the-program"));
        Assert.IsTrue(terminal.ProcessId > 0);
        var shown = await Shown(terminal).WaitAsync(Patience);
        StringAssert.Contains(shown, "shown-by-the-program");
        Assert.AreEqual(0, await terminal.Exited.WaitAsync(Patience));

        using var failing = PseudoTerminal.Start(Command("exit 7"));
        _ = await Shown(failing).WaitAsync(Patience);
        Assert.AreEqual(7, await failing.Exited.WaitAsync(Patience));
    }

    [TestMethod]
    public async Task AProgram_StartsInItsFolder_WithItsEnvironment_AndShowsUnicode()
    {
        var folder = Directory.CreateTempSubdirectory("codealta-terminal-");
        try
        {
            var line = OperatingSystem.IsWindows() ? "cd & echo value=%CODEALTA_TERMINAL_TEST%" : "pwd; echo value=$CODEALTA_TERMINAL_TEST";
            using var terminal = PseudoTerminal.Start(Command(line, folder.FullName, ("CODEALTA_TERMINAL_TEST", "héllo-日本")));
            var shown = await Shown(terminal).WaitAsync(Patience);
            StringAssert.Contains(shown, folder.Name);
            // The terminal speaks UTF-8 in both directions, whatever the code page of the system.
            StringAssert.Contains(shown, "value=héllo-日本");
        }
        finally { folder.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task WhatIsWritten_IsTheKeyboardOfTheProgram()
    {
        using var terminal = PseudoTerminal.Start(Interpreter());
        var live = new StringBuilder();
        var shown = Shown(terminal, live);
        // The echo of what is typed, then what it prints: the variable has its value only in the second.
        Type(terminal, OperatingSystem.IsWindows() ? "set MARK=typed\recho was-%MARK%\r" : "MARK=typed; echo was-$MARK\r");
        await Until(live, "was-typed");
        Type(terminal, "exit 3\r");
        Assert.AreEqual(3, await terminal.Exited.WaitAsync(Patience));
        _ = await shown.WaitAsync(Patience);
        // Nothing is sent to a program that has ended, and nothing fails.
        Type(terminal, "echo late\r");
    }

    [TestMethod]
    public async Task TheScreen_HasTheSizeItWasGiven_AndTheOneItIsResizedTo()
    {
        // The size is asked twice, with a line read from the keyboard in between.
        var start = OperatingSystem.IsWindows()
            ? Command("unused", columns: 91, rows: 27) with
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = ["-NoLogo", "-NoProfile", "-Command", "'size=' + [Console]::WindowWidth + 'x' + [Console]::WindowHeight; $null = [Console]::ReadLine(); 'size=' + [Console]::WindowWidth + 'x' + [Console]::WindowHeight"],
            }
            : Command("echo size=$(stty size); read line; echo size=$(stty size)", columns: 91, rows: 27);
        using var terminal = PseudoTerminal.Start(start);
        var live = new StringBuilder();
        var shown = Shown(terminal, live);
        await Until(live, OperatingSystem.IsWindows() ? "size=91x27" : "size=27 91");
        terminal.Resize(132, 41);
        Type(terminal, "\r");
        await Until(live, OperatingSystem.IsWindows() ? "size=132x41" : "size=41 132");
        Assert.AreEqual(0, await terminal.Exited.WaitAsync(Patience));
        _ = await shown.WaitAsync(Patience);
    }

    [TestMethod]
    public async Task Kill_EndsAProgramThatWaits_AndTheReadEnds()
    {
        using var terminal = PseudoTerminal.Start(Interpreter());
        var live = new StringBuilder();
        var shown = Shown(terminal, live);
        Type(terminal, "echo ready-to-end\r");
        await Until(live, "ready-to-end");
        terminal.Kill();
        _ = await terminal.Exited.WaitAsync(Patience);
        _ = await shown.WaitAsync(Patience);
        // Ending it twice, or resizing what has ended, does nothing.
        terminal.Kill();
        terminal.Resize(100, 30);
    }

    [TestMethod]
    public async Task Dispose_EndsAProgramNobodyReads()
    {
        var terminal = PseudoTerminal.Start(Interpreter());
        var exited = terminal.Exited;
        terminal.Dispose();
        terminal.Dispose();
        _ = await exited.WaitAsync(Patience);
        Assert.AreEqual(0, terminal.Read(new byte[16]));
    }

    [TestMethod]
    public void AProgramThatDoesNotExist_IsReported()
    {
        var missing = Command("unused") with { FileName = Path.Combine(Path.GetTempPath(), "codealta-no-such-program-" + Guid.NewGuid().ToString("N")) };
        if (OperatingSystem.IsWindows())
        {
            var exception = Assert.ThrowsExactly<PseudoTerminalException>(() => PseudoTerminal.Start(missing));
            StringAssert.Contains(exception.Message, missing.FileName);
            return;
        }
        // Elsewhere the program is looked for by the process that becomes it: it ends with the code of a shell that found nothing.
        using var terminal = PseudoTerminal.Start(missing);
        Assert.AreEqual(127, terminal.Exited.WaitAsync(Patience).GetAwaiter().GetResult());
    }

    [TestMethod]
    public async Task OnUnix_TheProgramLeadsASession_WithThePtyAsItsTerminal()
    {
        if (OperatingSystem.IsWindows()) return;
        // /dev/tty opens only for a process that has a controlling terminal; the leader of a session has its own number as its session.
        using var terminal = PseudoTerminal.Start(Command("test -t 0 && test -t 1 && test -t 2 && echo is-a-terminal; (exec 3</dev/tty) && echo has-a-terminal; echo session=$(ps -o sid= -p $$ | tr -d ' ') process=$$; trap '' PIPE 2>/dev/null; kill -l >/dev/null && echo signals-ok"));
        var shown = await Shown(terminal).WaitAsync(Patience);
        StringAssert.Contains(shown, "is-a-terminal");
        StringAssert.Contains(shown, "has-a-terminal");
        StringAssert.Contains(shown, $"session={terminal.ProcessId} process={terminal.ProcessId}");
        Assert.AreEqual(0, await terminal.Exited.WaitAsync(Patience));
    }

    [TestMethod]
    public async Task OnUnix_Interrupt_ReachesWhatRunsInTheForeground()
    {
        if (OperatingSystem.IsWindows()) return;
        using var terminal = PseudoTerminal.Start(Interpreter());
        var live = new StringBuilder();
        var shown = Shown(terminal, live);
        Type(terminal, "sleep 600\r");
        await Task.Delay(500);
        // Ctrl+C: the terminal sends the interrupt signal to the foreground job alone, which ends with 128 + 2.
        // The shell goes on: without job control it would have ended with what it runs.
        Type(terminal, "\u0003");
        Type(terminal, "echo ended-with=$?\r");
        await Until(live, "ended-with=130");
        Type(terminal, "exit\r");
        _ = await terminal.Exited.WaitAsync(Patience);
        _ = await shown.WaitAsync(Patience);
    }

    [TestMethod]
    public void OnWindows_ACommandLine_IsParsedBackIntoItsArguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.AreEqual(@"C:\Windows\cmd.exe /d", WindowsPseudoTerminal.CommandLine(@"C:\Windows\cmd.exe", ["/d"]));
        Assert.AreEqual("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo", WindowsPseudoTerminal.CommandLine(@"C:\Program Files\PowerShell\7\pwsh.exe", ["-NoLogo"]));
        // An empty argument, a quote, and backslashes before a quote and at the end of a quoted argument.
        Assert.AreEqual("x \"\" \"a \\\"b\\\"\" \"c d\\\\\" e\\f\\", WindowsPseudoTerminal.CommandLine("x", ["", "a \"b\"", "c d\\", "e\\f\\"]));
        Assert.AreEqual("x \"\\\\\\\"\"", WindowsPseudoTerminal.CommandLine("x", ["\\\""]));
    }

    [TestMethod]
    public void OnWindows_AnEnvironment_IsSortedAndEndedByAnEmptyString()
    {
        if (!OperatingSystem.IsWindows()) return;
        var block = new string(WindowsPseudoTerminal.EnvironmentBlock(new Dictionary<string, string> { ["b"] = "2", ["A"] = "1", [""] = "dropped", ["c"] = "x\0y" }));
        Assert.AreEqual("A=1\0b=2\0\0", block);
        Assert.AreEqual("\0\0", new string(WindowsPseudoTerminal.EnvironmentBlock(new Dictionary<string, string>())));
    }
}
