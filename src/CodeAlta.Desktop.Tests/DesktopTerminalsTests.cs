using CodeAlta.Desktop.Terminals;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopTerminalsTests
{
    private const string Esc = "\u001b";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("codealta-terminals-");

        public Fixture()
        {
            var shell = OperatingSystem.IsWindows() ? @"C:\shell\sh" : "/shell/sh";
            var profiles = new TerminalProfiles(name => name == "SHELL" ? shell : null, path => path == shell, static () => [], static _ => [], windows: false, mac: false);
            Terminals = new DesktopTerminals("1.2.3", profiles, start =>
            {
                Starts.Add(start);
                var program = new FakeTerminalProgram();
                Programs.Add(program);
                return program;
            }, TimeProvider.System);
        }

        public DesktopTerminals Terminals { get; }
        public List<FakeTerminalProgram> Programs { get; } = [];
        public List<PseudoTerminalStart> Starts { get; } = [];
        public string Folder => _folder.FullName;

        public (DesktopTerminal Terminal, FakeTerminalProgram Program) Create(bool agent = false, string? title = null)
        {
            var (status, terminal) = Terminals.Create(new TerminalRequest("project", null, Folder, null, title, agent));
            Assert.AreEqual("ok", status);
            return (terminal!, Programs[^1]);
        }

        public void Dispose()
        {
            Terminals.CloseAsync().GetAwaiter().GetResult();
            _folder.Delete(recursive: true);
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var limit = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > limit) Assert.Fail(what + " did not happen.");
            await Task.Delay(10);
        }
    }

    // The news of a feed, read one at a time with a limit on the wait.
    private sealed class Reader(TerminalFeed feed) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private IAsyncEnumerator<TerminalNews>? _news;
        private Task<bool>? _next;

        public async Task<TerminalNews> Next()
        {
            _news ??= feed.ReadAsync(_stop.Token).GetAsyncEnumerator();
            _next ??= _news.MoveNextAsync().AsTask();
            Assert.IsTrue(await _next.WaitAsync(Patience));
            _next = null;
            return _news.Current;
        }

        // Whether nothing comes for a moment.
        public async Task<bool> Quiet()
        {
            _news ??= feed.ReadAsync(_stop.Token).GetAsyncEnumerator();
            _next ??= _news.MoveNextAsync().AsTask();
            return await Task.WhenAny(_next, Task.Delay(300)) != _next;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                if (_next is not null) await _next;
            }
            catch (OperationCanceledException)
            {
                // The reading was stopped.
            }
            feed.Dispose();
        }
    }

    [TestMethod]
    public void TheHistory_IsReadInPieces_EachOfOnePartAndOneSize()
    {
        var history = new TerminalHistory(80, 24, capacity: 100);
        Assert.IsNull(history.Read(0, 10));
        history.Append("0123456789");
        Assert.AreEqual(new TerminalPiece(0, "0123", 80, 24, string.Empty), history.Read(0, 4));
        // The modes are given where they were noted only: at the start of a part.
        Assert.AreEqual(new TerminalPiece(4, "456789", 80, 24, null), history.Read(4, 100));
        Assert.IsNull(history.Read(10, 100));
        history.Cut(100, 30, "modes");
        history.Append("abcdef");
        Assert.AreEqual(new TerminalPiece(8, "89", 80, 24, null), history.Read(8, 100));
        Assert.AreEqual(new TerminalPiece(10, "abcdef", 100, 30, "modes"), history.Read(10, 100));
        Assert.AreEqual((0L, 16L), (history.Start, history.End));

        // Past its capacity the oldest parts go, never the one being written.
        history.Cut(100, 30, "later");
        history.Append(new string('x', 120));
        Assert.AreEqual((16L, 136L), (history.Start, history.End));
        // What was at an offset that is gone is read from the oldest that is left.
        Assert.AreEqual(16, history.Read(3, 5)!.Value.Offset);
        Assert.AreEqual("later", history.Read(3, 5)!.Value.Modes);

        // A part is full at its size, and two halves of one character stay together.
        var large = new TerminalHistory(80, 24);
        large.Append(new string('y', TerminalHistory.PartSize - 1));
        Assert.IsFalse(large.Full);
        large.Append("y😀");
        Assert.IsTrue(large.Full);
        Assert.AreEqual(TerminalHistory.PartSize, large.Read(0, TerminalHistory.PartSize + 1)!.Value.Text.Length);
        Assert.AreEqual("😀", large.Read(TerminalHistory.PartSize, 10)!.Value.Text);
    }

    [TestMethod]
    public void TheInterpretersOfWindows_AreFoundWhereTheyAreInstalled_ThePreferredOneFirst()
    {
        var variables = new Dictionary<string, string?>
        {
            ["SystemRoot"] = @"C:\Windows", ["USERPROFILE"] = @"C:\Users\me", ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local", ["ProgramFiles"] = @"C:\Program Files",
        };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"C:\Windows", "System32", "WindowsPowerShell", "v1.0", "powershell.exe"), Path.Combine(@"C:\Windows", "System32", "cmd.exe"),
            Path.Combine(@"C:\Windows", "System32", "wsl.exe"), Path.Combine(@"C:\Program Files", "Git", "bin", "bash.exe"),
        };
        var profiles = new TerminalProfiles(name => variables.GetValueOrDefault(name), files.Contains, static () => ["Ubuntu", "docker-desktop-data"], static _ => [], windows: true, mac: false);
        CollectionAssert.AreEqual(new[] { "powershell", "cmd", "git-bash", "wsl:Ubuntu" }, profiles.List().Select(static profile => profile.Id).ToArray());
        Assert.AreEqual("powershell", profiles.Choose(null)!.Id);
        Assert.AreEqual("cmd", profiles.Choose("CMD")!.Id);
        Assert.AreEqual("powershell", profiles.Choose("missing")!.Id);
        CollectionAssert.AreEqual(new[] { "--login", "-i" }, profiles.Choose("git-bash")!.Arguments.ToArray());
        CollectionAssert.AreEqual(new[] { "-d", "Ubuntu" }, profiles.Choose("wsl:Ubuntu")!.Arguments.ToArray());

        // PowerShell 7 comes first wherever it is installed: as a .NET tool here.
        files.Add(Path.Combine(@"C:\Users\me", ".dotnet", "tools", "pwsh.exe"));
        var first = profiles.List()[0];
        Assert.AreEqual(("pwsh", "PowerShell", TerminalShellKind.PowerShell), (first.Id, first.Name, first.Kind));
        files.Add(Path.Combine(@"C:\Program Files", "PowerShell", "7", "pwsh.exe"));
        Assert.AreEqual(Path.Combine(@"C:\Program Files", "PowerShell", "7", "pwsh.exe"), profiles.List()[0].FileName);
        Assert.IsNull(new TerminalProfiles(static _ => null, static _ => false, static () => [], static _ => [], windows: true, mac: false).Choose(null));
    }

    [TestMethod]
    public void TheShellsOfUnix_AreTheOneOfTheUserThenThoseTheSystemLists()
    {
        var files = new HashSet<string> { "/usr/bin/fish", "/bin/bash", "/bin/zsh", "/bin/sh" };
        IReadOnlyList<string> Shells(string path) => path == "/etc/shells" ? ["# shells", "/bin/sh", "/bin/bash", "/usr/bin/bash", "/usr/sbin/nologin", "/usr/bin/fish"] : [];
        var linux = new TerminalProfiles(name => name == "SHELL" ? "/usr/bin/fish" : null, files.Contains, static () => [], Shells, windows: false, mac: false);
        CollectionAssert.AreEqual(new[] { "fish", "sh", "bash", "zsh" }, linux.List().Select(static profile => profile.Id).ToArray());
        Assert.AreEqual(0, linux.List()[0].Arguments.Count);
        Assert.AreEqual(TerminalShellKind.Fish, linux.List()[0].Kind);
        // On macOS a shell is started as a login shell; one that is not a known shell is started as it is.
        var mac = new TerminalProfiles(static _ => null, files.Contains, static () => [], Shells, windows: false, mac: true);
        CollectionAssert.AreEqual(new[] { "sh:", "bash:-l", "fish:-l", "zsh:-l" }, mac.List().Select(static profile => profile.Id + ":" + string.Concat(profile.Arguments)).ToArray());
    }

    [TestMethod]
    public void TheEnvironmentOfAProgram_SaysWhichTerminalItRunsIn_AndHasNothingOfTheApplication()
    {
        var variables = new Dictionary<string, string>
        {
            ["PATH"] = "/bin", ["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = "--remote-debugging-port=9222", ["CODEALTA_START_TOKEN"] = "t", ["TMUX"] = "x", ["LINES"] = "24",
        };
        var unix = TerminalEnvironment.Prepare(variables, "id-1", "1.2.3", windows: false, "fr-FR");
        CollectionAssert.AreEquivalent(new[] { "PATH", "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "COLORTERM", "CODEALTA_TERMINAL", "TERM", "LANG" }, unix.Keys.ToArray());
        Assert.AreEqual(("CodeAlta", "1.2.3", "truecolor", "id-1", "xterm-256color", "fr_FR.UTF-8"),
            (unix["TERM_PROGRAM"], unix["TERM_PROGRAM_VERSION"], unix["COLORTERM"], unix["CODEALTA_TERMINAL"], unix["TERM"], unix["LANG"]));
        // A locale that is already UTF-8 stays; Windows has no TERM and no locale of this kind.
        var kept = TerminalEnvironment.Prepare(new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8" }, "id", "1", windows: false, "fr-FR");
        Assert.AreEqual("de_DE.UTF-8", kept["LANG"]);
        var windows = TerminalEnvironment.Prepare(new Dictionary<string, string>(), "id", "1", windows: true, "fr-FR");
        Assert.IsFalse(windows.ContainsKey("TERM") || windows.ContainsKey("LANG"));
        Assert.AreEqual("en_US.UTF-8", TerminalEnvironment.Locale(string.Empty));
        Assert.AreEqual("ja_JP.UTF-8", TerminalEnvironment.Locale("ja"));
        Assert.AreEqual("zh_CN.UTF-8", TerminalEnvironment.Locale("zh-Hans-CN"));
        Assert.AreEqual("de_DE.UTF-8", TerminalEnvironment.Locale("de"));

        // What the registry holds now replaces what the application started with; the two paths are joined.
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Path"] = "old", ["OWN"] = "kept", ["TOOL"] = "old" };
        TerminalEnvironment.Refresh(current, new Dictionary<string, string> { ["Path"] = @"C:\Windows;", ["TOOL"] = "machine" }, new Dictionary<string, string> { ["Path"] = @"C:\Users\me\bin", ["TOOL"] = "user" });
        Assert.AreEqual((@"C:\Windows;C:\Users\me\bin", "kept", "user"), (current["PATH"], current["OWN"], current["TOOL"]));
        // Who the user is comes from the sign-in, never from the registry: the one of a machine names its own account.
        current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["USERNAME"] = "me", ["USERPROFILE"] = @"C:\Users\me", ["APPDATA"] = @"C:\Users\me\AppData\Roaming" };
        TerminalEnvironment.Refresh(current, new Dictionary<string, string> { ["USERNAME"] = "SYSTEM", ["ComSpec"] = @"C:\Windows\system32\cmd.exe" },
            new Dictionary<string, string> { ["UserProfile"] = @"C:\elsewhere", ["TEMP"] = @"C:\Users\me\AppData\Local\Temp" });
        Assert.AreEqual(("me", @"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Windows\system32\cmd.exe", @"C:\Users\me\AppData\Local\Temp"),
            (current["USERNAME"], current["USERPROFILE"], current["APPDATA"], current["ComSpec"], current["TEMP"]));
    }

    [TestMethod]
    public async Task ATerminal_StartsTheInterpreterInItsFolder_AndIsListedWithWhatItShows()
    {
        using var fixture = new Fixture();
        Assert.AreEqual("no_folder", fixture.Terminals.Create(new TerminalRequest(null, null, Path.Combine(fixture.Folder, "missing"), null, null, false)).Status);
        Assert.AreEqual("no_folder", fixture.Terminals.Create(new TerminalRequest(null, null, "relative", null, null, false)).Status);
        var (terminal, program) = fixture.Create();
        var start = fixture.Starts.Single();
        Assert.AreEqual((fixture.Folder, DesktopTerminals.DefaultColumns, DesktopTerminals.DefaultRows), (start.WorkingDirectory, start.Columns, start.Rows));
        Assert.AreEqual((terminal.Id, "CodeAlta", "1.2.3"), (start.Environment["CODEALTA_TERMINAL"], start.Environment["TERM_PROGRAM"], start.Environment["TERM_PROGRAM_VERSION"]));
        Assert.IsTrue(Guid.TryParseExact(terminal.Id, "D", out _));

        var info = fixture.Terminals.List().Single();
        // Its title is the folder it is in until it is given one.
        Assert.AreEqual((terminal.Id, "project", fixture.Folder, false, fixture.Folder, "sh", true, 4242, false, false),
            (info.Id, info.ProjectId, info.Title, info.Titled, info.Folder, info.Profile, info.Running, info.ProcessId, info.Open, info.Agent));
        Assert.AreSame(terminal, fixture.Terminals.Find(terminal.Id.ToUpperInvariant()));
        Assert.IsNull(fixture.Terminals.Find("other"));

        program.Show($"{Esc}]0;editor{Esc}\\{Esc}]7;file://host/srv/app\u0007one\r\ntwo");
        await Until(() => terminal.ReadLines(10).Count == 2, "The text");
        CollectionAssert.AreEqual(new[] { "one", "two" }, terminal.ReadLines(10).ToArray());
        var screen = terminal.ReadScreen();
        Assert.AreEqual(("one", "two", 1, 3, false, DesktopTerminals.DefaultRows), (screen.Rows[0], screen.Rows[1], screen.CursorRow, screen.CursorColumn, screen.Alternate, screen.Rows.Count));
        info = terminal.Describe();
        Assert.AreEqual(("/srv/app", "/srv/app", "editor"), (info.Title, info.Folder, info.ProgramTitle));

        terminal.Rename("  build\u0007 ");
        Assert.AreEqual(("build", true), (terminal.Describe().Title, terminal.Describe().Titled));
        terminal.Rename(" ");
        Assert.AreEqual(("/srv/app", false), (terminal.Describe().Title, terminal.Describe().Titled));

        Assert.IsTrue(terminal.Write("ls\r"));
        await Until(() => program.Typed == "ls\r", "The typing");
        terminal.Resize(100, 40);
        terminal.Resize(100, 40);
        CollectionAssert.AreEqual(new[] { (100, 40) }, program.Sizes.ToArray());
        Assert.AreEqual((100, 40, 40), (terminal.Describe().Columns, terminal.Describe().Rows, terminal.ReadScreen().Rows.Count));
    }

    [TestMethod]
    public async Task ATerminalWhoseProgramEnds_GoesAway_UnlessItIsKeptToBeRead()
    {
        using var fixture = new Fixture();
        var (shell, program) = fixture.Create();
        await Task.Delay(TimeSpan.FromSeconds(3.2));
        program.Show("bye");
        program.End(1);
        await Until(() => fixture.Terminals.List().Count == 0, "The removal");
        Assert.IsFalse(shell.Write("late"));

        // One that a session created stays, with its exit code and its text.
        var (kept, task) = fixture.Create(agent: true, title: "tests");
        task.Show("3 passed\r\n");
        task.End(0);
        await kept.Ended.WaitAsync(Patience);
        var info = fixture.Terminals.List().Single();
        Assert.AreEqual(("tests", false, 0, true), (info.Title, info.Running, info.ExitCode, info.Agent));
        CollectionAssert.AreEqual(new[] { "3 passed", string.Empty, "[process exited with code 0]", string.Empty }, kept.ReadLines(10).ToArray());
        kept.Close();
        await Until(() => fixture.Terminals.List().Count == 0, "The removal of the closed terminal");

        // A shell that fails as soon as it starts stays too: what it wrote says why.
        var (failed, broken) = fixture.Create();
        broken.Show("bad option\r\n");
        broken.End(2);
        await failed.Ended.WaitAsync(Patience);
        Assert.AreEqual((false, 2), (fixture.Terminals.List().Single().Running, fixture.Terminals.List().Single().ExitCode));
        Assert.IsTrue(failed.FailedToStart);

        // Closing a terminal ends its program, and it is gone when the program is.
        var (closed, running) = fixture.Create();
        closed.Close();
        await Until(() => fixture.Terminals.List().Count == 1, "The removal of the terminal that ran");
        Assert.IsTrue(running.Killed);
    }

    [TestMethod]
    public async Task AWindow_IsToldWhichTerminalsThereAre_AndWhatTheOnesItShowsWrote()
    {
        using var fixture = new Fixture();
        var (terminal, program) = fixture.Create();
        program.Show("before\r\n");
        await Until(() => terminal.End == 8, "The first text");
        await using var reader = new Reader(fixture.Terminals.Open());
        var list = await reader.Next();
        Assert.AreEqual((TerminalNewsKind.List, terminal.Id), (list.Kind, list.Terminals!.Single().Id));
        // Nothing is sent of a terminal the window does not show.
        program.Show("unseen\r\n");
        Assert.IsTrue(await reader.Quiet());

        var feed = fixture.Terminals.Open();
        await using var second = new Reader(feed);
        Assert.AreEqual(TerminalNewsKind.List, (await second.Next()).Kind);
        Assert.IsFalse(feed.Attach("missing"));
        Assert.IsTrue(feed.Attach(terminal.Id));
        // The terminal is now shown: both windows are told, and the one that shows it is given what was written.
        Assert.IsTrue((await second.Next()).Terminals!.Single().Open);
        var start = await second.Next();
        Assert.AreEqual((TerminalNewsKind.Start, terminal.Id, string.Empty, DesktopTerminals.DefaultColumns, DesktopTerminals.DefaultRows, false),
            (start.Kind, start.Id, start.Data, start.Columns, start.Rows, start.Replayed));
        var replay = await second.Next();
        Assert.AreEqual((TerminalNewsKind.Data, "before\r\nunseen\r\n", 0, true), (replay.Kind, replay.Data, replay.Columns, replay.Replayed));
        program.Show("live");
        var live = await second.Next();
        Assert.AreEqual(("live", false), (live.Data, live.Replayed));

        // What is written after another size is told with its size, and the list says the terminal changed.
        terminal.Resize(90, 20);
        Assert.AreEqual((90, 20), ((await second.Next()).Terminals!.Single().Columns, terminal.Describe().Rows));
        program.Show("resized");
        var resized = await second.Next();
        Assert.AreEqual(("resized", 90, 20), (resized.Data, resized.Columns, resized.Rows));
        program.Show("same");
        Assert.AreEqual(0, (await second.Next()).Columns);

        // The tab that is shown takes the attention the bell asked for.
        feed.Show(terminal.Id, true);
        Assert.IsTrue((await second.Next()).Terminals!.Single().Visible);
        feed.Show(terminal.Id, false);
        Assert.IsFalse((await second.Next()).Terminals!.Single().Visible);
        program.Show("\u0007");
        Assert.IsTrue((await second.Next()).Terminals!.Single().Attention);
        Assert.AreEqual("\u0007", (await second.Next()).Data);
        feed.Show(terminal.Id, true);
        Assert.IsFalse((await second.Next()).Terminals!.Single().Attention);

        // A window that stops showing it, or that goes away, is no longer counted.
        feed.Detach(terminal.Id);
        var detached = (await second.Next()).Terminals!.Single();
        Assert.AreEqual((false, false), (detached.Open, detached.Visible));
        program.Show("after");
        Assert.IsTrue(await second.Quiet());
    }

    [TestMethod]
    public async Task ATerminalAnswersItsProgram_OnlyWhileNoWindowShowsIt()
    {
        using var fixture = new Fixture();
        var (terminal, program) = fixture.Create();
        program.Show($"ab{Esc}[6n");
        await Until(() => program.Typed == $"{Esc}[1;3R", "The answer");
        var feed = fixture.Terminals.Open();
        await using var reader = new Reader(feed);
        feed.Attach(terminal.Id);
        program.Show($"{Esc}[6n{Esc}[c");
        await Until(() => terminal.End == 13, "The questions");
        // The terminal of the window answers these.
        Assert.AreEqual($"{Esc}[1;3R", program.Typed);
        feed.Detach(terminal.Id);
        program.Show($"{Esc}[c");
        await Until(() => program.Typed == $"{Esc}[1;3R{Esc}[?1;2c", "The second answer");
    }

    [TestMethod]
    public async Task AWindowThatDoesNotKeepUp_Waits_AndStartsAgainWhenItIsTooFarBehind()
    {
        using var fixture = new Fixture();
        var (terminal, program) = fixture.Create();
        var feed = fixture.Terminals.Open();
        await using var reader = new Reader(feed);
        Assert.AreEqual(TerminalNewsKind.List, (await reader.Next()).Kind);
        feed.Attach(terminal.Id);
        Assert.AreEqual(TerminalNewsKind.List, (await reader.Next()).Kind);
        Assert.AreEqual((TerminalNewsKind.Start, true), ((await reader.Next()).Kind, true));

        // More than the window can be sent without saying it took it in.
        var line = new string('z', 1022) + "\r\n";
        var total = TerminalFeed.Window + 4 * TerminalFeed.MaximumPiece;
        for (var written = 0; written < total; written += line.Length) program.Show(line);
        await Until(() => terminal.End >= total, "The flood");
        long received = 0;
        while (received < TerminalFeed.Window) received += (await reader.Next()).Data!.Length;
        Assert.IsTrue(await reader.Quiet(), "The terminal waits for the window.");
        feed.Acknowledge(terminal.Id, received);
        while (received < total) received += (await reader.Next()).Data!.Length;
        Assert.AreEqual(terminal.End, received);
        feed.Acknowledge(terminal.Id, received);

        // So far behind that what it was to read next is gone: it starts again from what is left.
        var flood = TerminalHistory.DefaultCapacity + 2 * TerminalHistory.PartSize;
        for (var written = 0; written < flood; written += line.Length) program.Show(line);
        await Until(() => terminal.End >= total + flood, "The second flood");
        var restart = await reader.Next();
        Assert.AreEqual((TerminalNewsKind.Start, false), (restart.Kind, restart.Replayed));
        long again = 0;
        TerminalNews piece;
        do
        {
            piece = await reader.Next();
            Assert.AreEqual(TerminalNewsKind.Data, piece.Kind);
            again += piece.Data!.Length;
            feed.Acknowledge(terminal.Id, piece.Data.Length);
        }
        while (!piece.Replayed);
        // Less than was written, and all of what the terminal still has.
        Assert.IsTrue(again < flood && again >= TerminalHistory.DefaultCapacity - TerminalHistory.PartSize, again.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task WhatIsTyped_IsWaitedFor_AndWhatItShowedIsRead()
    {
        using var fixture = new Fixture();
        var (terminal, program) = fixture.Create(agent: true);
        var quiet = TimeSpan.FromMilliseconds(800);
        // A shell that reports nothing is at rest when it has been quiet for a while: what it showed since is read.
        program.Show("$ ");
        await Until(() => terminal.End == 2, "The prompt");
        var typing = terminal.TypeAsync("ls\r", Patience, quiet, 100, default);
        await Until(() => program.Typed == "ls\r", "The line");
        program.Show("ls\r\nfile.txt\r\n$ ");
        var typed = await typing.WaitAsync(Patience);
        Assert.AreEqual((true, true, null, "ls\nfile.txt\n$", false), (typed.Sent, typed.Settled, typed.Command, typed.Text, typed.Truncated));
        // Without a wait nothing is read, and only the last lines are when there are too many.
        Assert.AreEqual(new TerminalTyped(true, false, null, null, false), await terminal.TypeAsync("x", null, quiet, 100, default));
        typing = terminal.TypeAsync("seq\r", Patience, quiet, 2, default);
        await Until(() => program.Typed.EndsWith("seq\r", StringComparison.Ordinal), "The second line");
        program.Show("seq\r\n1\r\n2\r\n3\r\n$ ");
        typed = await typing.WaitAsync(Patience);
        Assert.AreEqual(("3\n$", true), (typed.Text, typed.Truncated));

        // One that reports its commands is at rest when the command has ended, however long it is quiet.
        program.Show($"\r\n{Esc}]633;A\u0007$ {Esc}]633;B\u0007");
        await Until(() => terminal.Describe().Integrated, "The integration");
        typing = terminal.TypeAsync("make\r", Patience, TimeSpan.FromMilliseconds(50), 100, default);
        await Until(() => program.Typed.EndsWith("make\r", StringComparison.Ordinal), "The command line");
        program.Show($"make\r\n{Esc}]633;E;make\u0007{Esc}]633;C\u0007compiling\r\n");
        await Until(() => terminal.Describe().Busy, "The command");
        Assert.AreEqual("make", terminal.Describe().Command);
        // What an exit of the application would interrupt.
        Assert.AreEqual(1, fixture.Terminals.Busy);
        await Task.Delay(300);
        Assert.IsFalse(typing.IsCompleted);
        program.Show($"done\r\n{Esc}]633;D;2\u0007{Esc}]633;A\u0007$ {Esc}]633;B\u0007");
        typed = await typing.WaitAsync(Patience);
        var command = typed.Command!;
        Assert.AreEqual((true, null), (typed.Settled, typed.Text));
        Assert.AreEqual((1L, "make", true, 2, "compiling\ndone", false), (command.Number, command.CommandLine, command.Finished, command.ExitCode, command.Output, command.Truncated));
        var info = terminal.Describe();
        Assert.AreEqual((false, null, 2), (info.Busy, info.Command, info.LastExitCode));
        Assert.AreEqual((0, 1), (fixture.Terminals.Busy, fixture.Terminals.Running));
        Assert.AreEqual("make", terminal.ReadCommands(5, output: false).Single().CommandLine);
        Assert.AreEqual(string.Empty, terminal.ReadCommands(5, output: false).Single().Output);

        // A line that runs nothing only brings the prompt back.
        typing = terminal.TypeAsync("\r", Patience, quiet, 100, default);
        await Until(() => program.Typed.EndsWith("make\r\r", StringComparison.Ordinal), "The empty line");
        program.Show($"\r\n{Esc}]633;D\u0007{Esc}]633;A\u0007$ {Esc}]633;B\u0007");
        typed = await typing.WaitAsync(Patience);
        Assert.AreEqual((true, null, "\n$"), (typed.Settled, typed.Command, typed.Text));

        // A command that takes longer than the caller waits is left running: what it showed so far is read.
        typing = terminal.TypeAsync("serve\r", TimeSpan.FromMilliseconds(400), quiet, 100, default);
        await Until(() => program.Typed.EndsWith("serve\r", StringComparison.Ordinal), "The long command");
        program.Show($"serve\r\n{Esc}]633;E;serve\u0007{Esc}]633;C\u0007listening\r\n");
        typed = await typing.WaitAsync(Patience);
        Assert.AreEqual((true, false, null, "serve\nlistening", false), (typed.Sent, typed.Settled, typed.Command, typed.Text, typed.Truncated));
        // What is typed while it runs goes to the command, not to the shell: the terminal is at rest when it is quiet.
        typing = terminal.TypeAsync("q\r", Patience, quiet, 100, default);
        await Until(() => program.Typed.EndsWith("q\r", StringComparison.Ordinal), "The key");
        program.Show("bye\r\n");
        typed = await typing.WaitAsync(Patience);
        Assert.AreEqual((true, null, "bye"), (typed.Settled, typed.Command, typed.Text));

        // A program that draws the whole screen shows its screen.
        program.Show($"{Esc}[?1049h{Esc}[2J{Esc}[Hfile.txt\r\n~\r\n~");
        await Until(() => terminal.ReadScreen().Alternate, "The editor");
        typing = terminal.TypeAsync("i", Patience, quiet, 100, default);
        typed = await typing.WaitAsync(Patience);
        Assert.AreEqual((true, "file.txt\n~\n~"), (typed.Settled, typed.Text));
        program.Show($"{Esc}[?1049l");
        await Until(() => !terminal.ReadScreen().Alternate, "The end of the editor");

        // The wait ends with the program too, and with its caller; a program that has ended is typed nothing.
        var ended = terminal.TypeAsync("exit\r", Patience, TimeSpan.FromSeconds(60), 100, default);
        await Until(() => program.Typed.EndsWith("exit\r", StringComparison.Ordinal), "The last line");
        program.End(0);
        typed = await ended.WaitAsync(Patience);
        Assert.IsTrue(typed.Sent && typed.Settled && typed.Text!.EndsWith("[process exited with code 0]", StringComparison.Ordinal), typed.Text);
        Assert.AreEqual(new TerminalTyped(false, false, null, null, false), await terminal.TypeAsync("x", Patience, quiet, 100, default));
        using var stop = new CancellationTokenSource();
        var (other, _) = fixture.Create(agent: true);
        var stopped = other.TypeAsync("x", Patience, TimeSpan.FromSeconds(60), 100, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => stopped);
    }

    [TestMethod]
    public async Task TheKeyboard_SendsWhatAKeyboardWould_ForTextAndForNamedKeys()
    {
        using var fixture = new Fixture();
        var (terminal, program) = fixture.Create();
        // An end of line is the Enter key; the keys follow the text, and Enter comes last.
        Assert.AreEqual("a\rb\r", terminal.Keyboard("a\r\nb\n", [], enter: false, out var unknown));
        Assert.IsNull(unknown);
        Assert.AreEqual($"git status\t{Esc}[A\u0003\r", terminal.Keyboard("git status", ["Tab", "up", "ctrl+c"], enter: true, out _));
        Assert.AreEqual(string.Empty, terminal.Keyboard(null, [], enter: false, out _));
        Assert.IsNull(terminal.Keyboard("x", ["up", "hyper+x"], enter: true, out unknown));
        Assert.AreEqual("hyper+x", unknown);

        // A program that asked to be told pastes from typing is given several lines as one paste, and the arrows it asked for.
        program.Show($"{Esc}[?2004h{Esc}[?1h");
        await Until(() => terminal.End > 0 && terminal.Keyboard(null, ["up"], false, out _) == $"{Esc}OA", "The modes");
        Assert.AreEqual($"{Esc}[200~if x:\r  y{Esc}[201~\r", terminal.Keyboard("if x:\n  y\n", [], enter: false, out _));
        Assert.AreEqual($"{Esc}[200~a\rb{Esc}[201~", terminal.Keyboard($"a\n{Esc}[201~b", [], enter: false, out _));
        // One line is typed, as a person types it.
        Assert.AreEqual("one\r", terminal.Keyboard("one\n", [], enter: false, out _));

        // What each key sends.
        (string Name, string Sent)[] keys =
        [
            ("enter", "\r"), ("Return", "\r"), ("tab", "\t"), ("shift+tab", $"{Esc}[Z"), ("escape", Esc), ("esc", Esc), ("space", " "), ("ctrl+space", "\0"),
            ("backspace", "\u007f"), ("ctrl+backspace", "\b"), ("alt+backspace", $"{Esc}\u007f"), ("delete", $"{Esc}[3~"), ("insert", $"{Esc}[2~"),
            ("up", $"{Esc}[A"), ("down", $"{Esc}[B"), ("right", $"{Esc}[C"), ("left", $"{Esc}[D"), ("home", $"{Esc}[H"), ("end", $"{Esc}[F"),
            ("pageup", $"{Esc}[5~"), ("pgdn", $"{Esc}[6~"), ("ctrl+left", $"{Esc}[1;5D"), ("shift+alt+up", $"{Esc}[1;4A"), ("ctrl+delete", $"{Esc}[3;5~"),
            ("f1", $"{Esc}OP"), ("f4", $"{Esc}OS"), ("f5", $"{Esc}[15~"), ("f12", $"{Esc}[24~"), ("shift+f2", $"{Esc}[1;2Q"), ("ctrl+f5", $"{Esc}[15;5~"),
            ("ctrl+c", "\u0003"), ("Ctrl+D", "\u0004"), ("ctrl+z", "\u001a"), ("ctrl+[", Esc), ("ctrl+\\", "\u001c"), ("ctrl+?", "\u007f"),
            ("alt+b", $"{Esc}b"), ("ctrl+alt+c", $"{Esc}\u0003"), ("q", "q"), ("shift+q", "Q"), ("+", "+"), ("alt++", $"{Esc}+"),
        ];
        foreach (var (name, sent) in keys) Assert.AreEqual(sent, TerminalKeys.Encode(name, application: false), name);
        Assert.AreEqual($"{Esc}OH", TerminalKeys.Encode("home", application: true));
        Assert.AreEqual($"{Esc}[1;5A", TerminalKeys.Encode("ctrl+up", application: true));
        foreach (var name in new[] { null, "", " ", "f0", "f13", "ctrl+", "ctrl+ctrl+c", "super+c", "ctrl+1", "ab", "\u0007", "enter+ctrl" }) Assert.IsNull(TerminalKeys.Encode(name, application: false), name);
    }

    [TestMethod]
    public async Task WhenTheApplicationExits_EveryTerminalEnds_AndNoneIsCreated()
    {
        var fixture = new Fixture();
        var (_, first) = fixture.Create();
        var (_, second) = fixture.Create(agent: true);
        for (var count = 2; count < DesktopTerminals.MaximumTerminals; count++) fixture.Create();
        Assert.AreEqual("limit", fixture.Terminals.Create(new TerminalRequest(null, null, fixture.Folder, null, null, false)).Status);
        Assert.AreEqual(DesktopTerminals.MaximumTerminals, fixture.Terminals.Running);
        var feed = fixture.Terminals.Open();
        await fixture.Terminals.CloseAsync();
        Assert.IsTrue(first.Killed && second.Killed);
        Assert.AreEqual(0, fixture.Terminals.List().Count);
        Assert.AreEqual("closed", fixture.Terminals.Create(new TerminalRequest(null, null, fixture.Folder, null, null, false)).Status);
        // The windows that listened are told nothing more.
        await foreach (var _ in feed.ReadAsync(default)) Assert.Fail("A closed feed tells nothing.");
        fixture.Dispose();
    }

    [TestMethod]
    public async Task ARealShell_RunsWhatIsTyped_AndItsTextIsRead()
    {
        var folder = Directory.CreateTempSubdirectory("codealta-terminal-shell-");
        var terminals = new DesktopTerminals("1.2.3");
        try
        {
            var (status, terminal) = terminals.Create(new TerminalRequest(null, null, folder.FullName, OperatingSystem.IsWindows() ? "cmd" : "sh", null, Agent: true));
            Assert.AreEqual("ok", status);
            Assert.IsTrue(terminal!.Describe().ProcessId > 0);
            terminal.Write(OperatingSystem.IsWindows() ? "echo terminal=%CODEALTA_TERMINAL% in %TERM_PROGRAM%\r" : "echo terminal=$CODEALTA_TERMINAL in $TERM_PROGRAM\r");
            await Until(() => terminal.ReadLines(50).Any(line => line == $"terminal={terminal.Id} in CodeAlta"), "The line of the shell");
            terminal.Write("exit 4\r");
            await terminal.Ended.WaitAsync(Patience);
            Assert.AreEqual(4, terminal.Describe().ExitCode);
            Assert.IsTrue(terminal.ReadLines(50).Contains("[process exited with code 4]"));
        }
        finally
        {
            await terminals.CloseAsync();
            folder.Delete(recursive: true);
        }
    }
}
