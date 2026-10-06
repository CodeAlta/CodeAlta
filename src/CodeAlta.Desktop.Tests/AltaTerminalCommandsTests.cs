using System.Text;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Terminals;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class AltaTerminalCommandsTests
{
    private const string Esc = "\u001b";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // The alta commands over the terminals of an application whose programs are fakes, and a real project catalog.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project, ProjectDescriptor other, bool acceptsInput)
        {
            (_root, Project, Other) = (root, project, other);
            var shell = OperatingSystem.IsWindows() ? @"C:\shell\sh" : "/shell/sh";
            var profiles = new TerminalProfiles(name => name == "SHELL" ? shell : null, path => path == shell, static () => [], static _ => [], windows: false, mac: false);
            Terminals = new DesktopTerminals("1.2.3", profiles, start =>
            {
                Starts.Add(start);
                var program = new FakeTerminalProgram();
                Programs.Add(program);
                return program;
            }, TimeProvider.System);
            var services = new AltaServiceCollection().Add(projects).Add<IAltaTerminals>(new DesktopAltaTerminals(Terminals, acceptsInput));
            var registry = new AltaCommandRegistry();
            Alta = new AltaCommandDispatcher(registry, services);
            services.Add(registry).Add(Alta);
            Session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1", SourceProjectId = project.Id };
        }

        public ProjectDescriptor Project { get; }
        public ProjectDescriptor Other { get; }
        public DesktopTerminals Terminals { get; }
        public AltaCommandDispatcher Alta { get; }
        public AltaCallerIdentity Session { get; }
        public List<FakeTerminalProgram> Programs { get; } = [];
        public List<PseudoTerminalStart> Starts { get; } = [];

        public static async Task<Fixture> CreateAsync(bool acceptsInput = true)
        {
            var root = Directory.CreateTempSubdirectory("codealta-alta-terminal-").FullName;
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "app")).FullName);
            var other = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "other")).FullName);
            Directory.CreateDirectory(Path.Combine(project.ProjectPath, "src"));
            return new Fixture(root, projects, project, other, acceptsInput);
        }

        // Runs a command as the session, in the folder of its project.
        public async Task<(int Code, List<JsonElement> Records, string Text)> Run(string[] arguments, string? stdin = null, int? maximumBytes = null, bool session = true)
        {
            var result = await Alta.InvokeAsync(arguments, stdin, session ? Session : AltaCallerIdentity.Cli, Project.ProjectPath, maxOutputBytes: maximumBytes).AsTask().WaitAsync(Patience);
            var records = new List<JsonElement>();
            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                records.Add(document.RootElement.Clone());
            }
            return (result.ExitCode, records, result.Stdout + result.Stderr);
        }

        // Runs a command that succeeds, and gives its one record of a type.
        public async Task<JsonElement> One(string type, params string[] arguments)
        {
            var (code, records, text) = await Run(arguments);
            Assert.AreEqual(AltaExitCodes.Success, code, text);
            return records.Single(record => record.GetProperty("type").GetString() == type);
        }

        public async Task<(string Id, FakeTerminalProgram Program)> Create(params string[] options)
        {
            var created = await One("alta.terminal.created", ["terminal", "create", .. options]);
            return (created.GetProperty("id").GetString()!, Programs[^1]);
        }

        public async ValueTask DisposeAsync()
        {
            await Terminals.CloseAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string? Text(JsonElement record, string name) => record.TryGetProperty(name, out var value) ? value.ToString() : null;

    private static async Task Until(Func<bool> condition, string what)
    {
        var limit = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > limit) Assert.Fail(what + " did not happen.");
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task TheCommands_ExistOnlyWhereAHostHasTerminals()
    {
        // The terminal host registers no terminals: the commands are not part of its tool, in its help or its policies.
        var services = new AltaServiceCollection();
        var registry = new AltaCommandRegistry();
        var plain = new AltaCommandDispatcher(registry, services);
        services.Add(registry).Add(plain);
        Assert.AreEqual(AltaExitCodes.Usage, (await plain.InvokeAsync(["terminal", "list"])).ExitCode);
        Assert.IsFalse((await plain.InvokeAsync(["--help"])).Stdout.Contains("Use the terminals", StringComparison.Ordinal));
        Assert.IsFalse((await plain.InvokeAsync(["tool", "list"])).Stdout.Contains("terminal send", StringComparison.Ordinal));

        await using var fixture = await Fixture.CreateAsync();
        StringAssert.Contains((await fixture.Alta.InvokeAsync(["--help"])).Stdout, "Use the terminals");
        var tools = (await fixture.Alta.InvokeAsync(["tool", "list"])).Stdout;
        foreach (var command in new[] { "list", "shells", "create", "read", "commands", "send", "rename", "show", "close" }) StringAssert.Contains(tools, "terminal " + command);
        // Each command says what it takes.
        foreach (var command in new[] { "list", "shells", "create", "read", "commands", "send", "rename", "show", "close" })
        {
            var help = await fixture.Alta.InvokeAsync(["terminal", command, "--help"]);
            Assert.AreEqual(AltaExitCodes.Success, help.ExitCode, command);
            Assert.IsTrue(help.IsHelp && help.Stdout.Length > 0, command);
        }
        var shell = await fixture.One("alta.terminal.shell", "terminal", "shells");
        Assert.AreEqual(("sh", "True"), (Text(shell, "id"), Text(shell, "default")));
    }

    [TestMethod]
    public async Task ASession_CreatesATerminal_InItsFolderInAFolderItNamesOrInAnotherProject()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.One("alta.terminal.created", "terminal", "create", "--title", "dev server");
        Assert.AreEqual(("dev server", "running", fixture.Project.Id, "session-1", "session", "closed", "sh"),
            (Text(created, "title"), Text(created, "state"), Text(created, "projectId"), Text(created, "sessionId"), Text(created, "createdBy"), Text(created, "tab"), Text(created, "shell")));
        Assert.AreEqual(Path.GetFullPath(fixture.Project.ProjectPath), fixture.Starts[0].WorkingDirectory);
        Assert.IsTrue(Guid.TryParse(Text(created, "id"), out _));

        // A folder relative to where the session works, another project, and from outside a session the project of the folder.
        await fixture.Create("--cwd", "src");
        Assert.AreEqual(Path.Combine(Path.GetFullPath(fixture.Project.ProjectPath), "src"), fixture.Starts[1].WorkingDirectory);
        var other = await fixture.One("alta.terminal.created", "terminal", "create", "--project", fixture.Other.Slug);
        Assert.AreEqual((fixture.Other.Id, Path.GetFullPath(fixture.Other.ProjectPath)), (Text(other, "projectId"), fixture.Starts[2].WorkingDirectory));
        var (code, records, text) = await fixture.Run(["terminal", "create"], session: false);
        Assert.AreEqual(AltaExitCodes.Success, code, text);
        Assert.AreEqual((fixture.Project.Id, null), (Text(records[^1], "projectId"), Text(records[^1], "sessionId")));

        // Listed in the order they were created, all or those of one project.
        (code, records, _) = await fixture.Run(["terminal", "list"]);
        Assert.AreEqual((AltaExitCodes.Success, 4, "4"), (code, records.Count(record => Text(record, "type") == "alta.terminal"), Text(records[^1], "count")));
        Assert.AreEqual("dev server", Text(records.First(record => Text(record, "type") == "alta.terminal"), "title"));
        (_, records, _) = await fixture.Run(["terminal", "list", "--project", fixture.Other.Id]);
        Assert.AreEqual(Text(other, "id"), Text(records.Single(record => Text(record, "type") == "alta.terminal"), "id"));

        // No terminal for a shell, a folder or a project that is not there.
        foreach (var (arguments, error) in new (string[], string)[] { (["--shell", "nope"], "shell.notFound"), (["--cwd", "missing"], "folder.notFound"), (["--project", "missing"], "project.notFound") })
        {
            (code, _, text) = await fixture.Run(["terminal", "create", .. arguments]);
            Assert.AreEqual(AltaExitCodes.NotFound, code, text);
            StringAssert.Contains(text, error);
        }
        (code, _, text) = await fixture.Run(["terminal", "create", "--title", new string('t', 257)]);
        Assert.AreEqual(AltaExitCodes.Usage, code, text);
        Assert.AreEqual(4, fixture.Starts.Count);

        // A command line is typed in the new terminal, and its tab is shown when a window is there.
        using var window = fixture.Terminals.Open();
        var served = await fixture.One("alta.terminal.created", "terminal", "create", "--command", "npm run dev", "--show");
        Assert.AreEqual(("True", "True"), (Text(served, "commandSent"), Text(served, "shown")));
        await Until(() => fixture.Programs[^1].Typed == "npm run dev\r", "The command line");
    }

    [TestMethod]
    public async Task ASession_ReadsWhatATerminalShows_AndTypesInIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (id, program) = await fixture.Create();
        program.Show("hello\r\nworld\r\n$ ");
        await Until(() => fixture.Terminals.Find(id)!.End == 16, "The text");

        var screen = await fixture.One("alta.terminal.text", "terminal", "read", id);
        Assert.AreEqual(("hello\nworld\n$", "screen", "3", "False", "running"), (Text(screen, "text"), Text(screen, "source"), Text(screen, "lineCount"), Text(screen, "truncated"), Text(screen, "state")));
        Assert.AreEqual((2, 2), (screen.GetProperty("cursor").GetProperty("row").GetInt32(), screen.GetProperty("cursor").GetProperty("column").GetInt32()));
        var lines = await fixture.One("alta.terminal.text", "terminal", "read", id, "--lines", "2");
        Assert.AreEqual(("world\n$", "lines", null), (Text(lines, "text"), Text(lines, "source"), Text(lines, "cursor")));
        foreach (var arguments in new string[][] { ["read", id, "--lines", "0"], ["read", id, "--lines", "x"], ["read"], ["send", id], ["send", id, "--text", "x", "--stdin"], ["send", id, "--key", "hyper+x"],
            ["send", id, "--text", "x", "--wait", "0"], ["send", id, "--text", "x", "--wait", "soon"], ["send", id, "--text", "x", "--wait", "3601"], ["commands", id, "--last", "0"], ["close"], ["show"], ["rename"] })
        {
            var (code, _, text) = await fixture.Run(["terminal", .. arguments]);
            Assert.AreEqual(AltaExitCodes.Usage, code, string.Join(' ', arguments) + ": " + text);
        }
        foreach (var arguments in new string[][] { ["read", "none"], ["send", "none", "--enter"], ["commands", "none"], ["rename", "none", "x"], ["show", "none"], ["close", "none"] })
        {
            var (code, _, text) = await fixture.Run(["terminal", .. arguments]);
            Assert.AreEqual(AltaExitCodes.NotFound, code, string.Join(' ', arguments) + ": " + text);
            StringAssert.Contains(text, "terminal.notFound");
        }
        Assert.AreEqual(string.Empty, program.Typed);

        // Text, keys by name, and text from stdin: what the keyboard of the terminal would send.
        var sent = await fixture.One("alta.terminal.sent", "terminal", "send", id, "--text", "ls", "--enter");
        Assert.AreEqual(("False", null, null), (Text(sent, "waited"), Text(sent, "settled"), Text(sent, "output")));
        await fixture.One("alta.terminal.sent", "terminal", "send", id, "--key", "ctrl+c", "--key", "up");
        var (status, _, output) = await fixture.Run(["terminal", "send", id, "--stdin"], stdin: "a\nb\n");
        Assert.AreEqual(AltaExitCodes.Success, status, output);
        await Until(() => program.Typed == $"ls\r\u0003{Esc}[Aa\rb\r", "What was typed");

        // A shell that reports nothing is waited for until it is quiet: what it showed since is the output.
        var waiting = fixture.One("alta.terminal.sent", "terminal", "send", id, "--text", "pwd", "--enter", "--wait", "30");
        await Until(() => program.Typed.EndsWith("pwd\r", StringComparison.Ordinal), "The line");
        program.Show("pwd\r\n/work\r\n$ ");
        sent = await waiting;
        Assert.AreEqual(("True", "True", "pwd\n/work\n$", "False", "running"), (Text(sent, "waited"), Text(sent, "settled"), Text(sent, "output"), Text(sent, "outputTruncated"), Text(sent, "state")));
        var (none, records, warned) = await fixture.Run(["terminal", "commands", id]);
        Assert.AreEqual((AltaExitCodes.Success, "0"), (none, Text(records.Single(record => Text(record, "type") == "alta.terminal.command.summary"), "count")));
        StringAssert.Contains(warned, "terminal.noCommandReports");

        // One that reports its commands is waited for until the command has ended: its output and its exit code.
        program.Show($"\r\n{Esc}]633;A\u0007$ {Esc}]633;B\u0007");
        await Until(() => fixture.Terminals.Find(id)!.Describe().Integrated, "The integration");
        Assert.AreEqual("idle", Text(await fixture.One("alta.terminal.text", "terminal", "read", id), "state"));
        waiting = fixture.One("alta.terminal.sent", "terminal", "send", id, "--text", "make", "--enter", "--wait", "30");
        await Until(() => program.Typed.EndsWith("make\r", StringComparison.Ordinal), "The command line");
        program.Show($"make\r\n{Esc}]633;E;make\u0007{Esc}]633;C\u0007compiling\r\n");
        await Until(() => fixture.Terminals.Find(id)!.Describe().Busy, "The command");
        var busy = (await fixture.Run(["terminal", "list"])).Records.First(record => Text(record, "id") == id);
        Assert.AreEqual(("busy", "make"), (Text(busy, "state"), Text(busy, "command")));
        program.Show($"done\r\n{Esc}]633;D;2\u0007{Esc}]633;A\u0007$ {Esc}]633;B\u0007");
        sent = await waiting;
        Assert.AreEqual(("True", "make", "2", "compiling\ndone", "idle", "2"), (Text(sent, "settled"), Text(sent, "commandLine"), Text(sent, "commandExitCode"), Text(sent, "output"), Text(sent, "state"), Text(sent, "lastExitCode")));
        var command = await fixture.One("alta.terminal.command", "terminal", "commands", id, "--output");
        Assert.AreEqual((id, "1", "finished", "make", "2", "compiling\ndone", "False"),
            (Text(command, "terminalId"), Text(command, "number"), Text(command, "state"), Text(command, "commandLine"), Text(command, "exitCode"), Text(command, "output"), Text(command, "outputTruncated")));
        Assert.IsNull(Text(await fixture.One("alta.terminal.command", "terminal", "commands", id), "output"));

        // A command that outlasts the wait is left running, and the caller is told how to read it later.
        waiting = fixture.One("alta.terminal.sent", "terminal", "send", id, "--text", "serve", "--enter", "--wait", "2");
        await Until(() => program.Typed.EndsWith("serve\r", StringComparison.Ordinal), "The long command");
        program.Show($"serve\r\n{Esc}]633;E;serve\u0007{Esc}]633;C\u0007listening\r\n");
        sent = await waiting;
        Assert.AreEqual(("False", "busy"), (Text(sent, "settled"), Text(sent, "state")));
        StringAssert.Contains(Text(sent, "nextStep"), "alta terminal read " + id);
    }

    [TestMethod]
    public async Task ASession_NamesShowsAndClosesATerminal_AndReadsOneThatHasEnded()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (id, program) = await fixture.Create();
        var renamed = await fixture.One("alta.terminal.renamed", "terminal", "rename", id, "  build  ");
        Assert.AreEqual("build", Text(renamed, "title"));
        Assert.AreEqual(Path.GetFullPath(fixture.Project.ProjectPath), Text(await fixture.One("alta.terminal.renamed", "terminal", "rename", id), "title"));

        // Nothing shows a terminal without a window; with one, the window is asked for its tab.
        var (code, _, text) = await fixture.Run(["terminal", "show", id]);
        Assert.AreEqual(AltaExitCodes.ServiceUnavailable, code, text);
        StringAssert.Contains(text, "view.unavailable");
        using (var window = fixture.Terminals.Open())
        {
            await fixture.One("alta.terminal.shown", "terminal", "show", id);
            using var stop = new CancellationTokenSource(Patience);
            await foreach (var news in window.ReadAsync(stop.Token))
            {
                if (news.Kind != TerminalNewsKind.Show) continue;
                Assert.AreEqual(id, news.Id);
                break;
            }
            Assert.IsTrue(window.Attach(id));
            window.Show(id, true);
            var listed = (await fixture.Run(["terminal", "list"])).Records.First(record => Text(record, "id") == id);
            Assert.AreEqual("visible", Text(listed, "tab"));
        }
        Assert.AreEqual("closed", Text((await fixture.Run(["terminal", "list"])).Records.First(record => Text(record, "id") == id), "tab"));

        // A terminal a session created stays once its program has ended: what it wrote is still read, nothing is typed.
        program.Show("bye\r\n");
        program.End(3);
        await fixture.Terminals.Find(id)!.Ended.WaitAsync(Patience);
        var ended = await fixture.One("alta.terminal.text", "terminal", "read", id);
        Assert.AreEqual(("exited", "3", "bye\n\n[process exited with code 3]"), (Text(ended, "state"), Text(ended, "exitCode"), Text(ended, "text")));
        (code, _, text) = await fixture.Run(["terminal", "send", id, "--enter"]);
        Assert.AreEqual(AltaExitCodes.Failure, code, text);
        StringAssert.Contains(text, "terminal.ended");
        var closed = await fixture.One("alta.terminal.closed", "terminal", "close", id);
        Assert.AreEqual("False", Text(closed, "wasRunning"));
        Assert.AreEqual(0, fixture.Terminals.List().Count);

        // Closing a terminal ends its program.
        var (running, shell) = await fixture.Create();
        Assert.AreEqual("True", Text(await fixture.One("alta.terminal.closed", "terminal", "close", running), "wasRunning"));
        await Until(() => fixture.Terminals.List().Count == 0, "The end of the terminal");
        Assert.IsTrue(shell.Killed);
    }

    [TestMethod]
    public async Task AHostThatReviewsCommands_LetsNoSessionType()
    {
        await using var fixture = await Fixture.CreateAsync(acceptsInput: false);
        var (code, _, text) = await fixture.Run(["terminal", "create", "--command", "rm -rf ."]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, code, text);
        StringAssert.Contains(text, "terminal.inputDenied");
        Assert.AreEqual(0, fixture.Starts.Count);

        // A terminal can still be created for the user to type in, read and closed.
        var (id, program) = await fixture.Create();
        (code, _, text) = await fixture.Run(["terminal", "send", id, "--text", "x", "--enter"]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, code, text);
        (code, _, text) = await fixture.Run(["terminal", "send", id, "--key", "ctrl+c"]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, code, text);
        Assert.AreEqual(string.Empty, program.Typed);
        Assert.AreEqual(AltaExitCodes.Success, (await fixture.Run(["terminal", "read", id])).Code);
        Assert.AreEqual(AltaExitCodes.Success, (await fixture.Run(["terminal", "close", id])).Code);
    }

    [TestMethod]
    public async Task WhatATerminalShowed_IsCutToWhatOneCallReturns_FromItsEnd()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (id, program) = await fixture.Create();
        var shown = new StringBuilder();
        for (var line = 1; line <= 3000; line++) shown.Append(System.Globalization.CultureInfo.InvariantCulture, $"line {line:D4} {new string('é', 60)}\r\n");
        program.Show(shown.ToString());
        await Until(() => fixture.Terminals.Find(id)!.End == shown.Length, "The flood");

        // More than a call returns: the last lines are kept, and the record is there to say so.
        var (code, records, text) = await fixture.Run(["terminal", "read", id, "--lines", "3000"]);
        Assert.AreEqual(AltaExitCodes.Success, code, text);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(text) <= 64 * 1024, "The transcript fits what a call returns.");
        var read = records.Single(record => Text(record, "type") == "alta.terminal.text");
        var kept = read.GetProperty("text").GetString()!.Split('\n');
        Assert.AreEqual(("True", kept.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)), (Text(read, "truncated"), Text(read, "lineCount")));
        Assert.IsTrue(kept.Length is > 300 and < 3000, kept.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        StringAssert.StartsWith(kept[^2], "line 3000 ");
        Assert.AreEqual("False", Text((await fixture.Run(["terminal", "read", id])).Records[^1], "truncated"));

        // A caller that asks for less is given less.
        (code, records, text) = await fixture.Run(["terminal", "read", id, "--lines", "3000"], maximumBytes: 8 * 1024);
        Assert.AreEqual(AltaExitCodes.Success, code, text);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(text) <= 8 * 1024);
        var fewer = records.Single(record => Text(record, "type") == "alta.terminal.text").GetProperty("text").GetString()!.Split('\n');
        Assert.IsTrue(fewer.Length is > 20 and < 100, fewer.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        StringAssert.StartsWith(fewer[^2], "line 3000 ");
    }
}
