using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopAltaToolsTests
{
    [TestMethod]
    public async Task SessionTool_RunsAltaCommandsAsItsSession()
    {
        var notes = new Notes();
        var tools = DesktopAltaTools.CreateSessionTools(Dispatcher(notes));

        // A session that exists is named by the request; a session being created is learned from its first call.
        var known = tools(new("known-session", "project", "C:/work", "provider")).Single();
        var created = tools(new(null, null, "C:/work", "provider")).Single();
        var first = await known.Handler(Invocation("ignored", ["notes", "set", "--stdin"], "# Known"), default);
        var second = await created.Handler(Invocation("created-session", ["notes", "set", "--stdin"], "# Created"), default);

        Assert.IsTrue(first.Success, first.Error);
        Assert.IsTrue(second.Success, second.Error);
        CollectionAssert.AreEqual(new[] { ("known-session", "# Known"), ("created-session", "# Created") }, notes.Writes);
    }

    [TestMethod]
    public async Task SessionTool_OfASessionWhoseWorktreeIsGone_RunsItsCommandsFromTheFolderOfTheProject()
    {
        var root = Directory.CreateTempSubdirectory("codealta-alta-tool-").FullName;
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var worktree = Directory.CreateDirectory(Path.Combine(root, "trees", "quiet-heron")).FullName;
            var tool = DesktopAltaTools.CreateSessionTools(Dispatcher(new Notes()))(new("s", "p", worktree, "provider") { ProjectDirectory = project }).Single();
            async Task<string?> WorksInAsync()
            {
                var result = await tool.Handler(Invocation("s", ["tool", "status"], ""), default);
                Assert.IsTrue(result.Success, result.Error);
                var status = ((AgentToolResultItem.Text)result.Items.Single()).Value.Split('\n').Single(static line => line.Contains("\"alta.tool.status\"", StringComparison.Ordinal));
                using var document = JsonDocument.Parse(status);
                return document.RootElement.GetProperty("cwd").GetString();
            }

            Assert.AreEqual(worktree, await WorksInAsync());

            // The worktree is removed while the session keeps its tool: the commands no longer start from a folder that is gone.
            Directory.Delete(worktree, recursive: true);
            Assert.AreEqual(project, await WorksInAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SessionTool_HasTheSameSignatureForEverySession()
    {
        var tools = DesktopAltaTools.CreateSessionTools(Dispatcher(new Notes()));
        var left = tools(new("a", null, "C:/one", "p")).Single().Spec;
        var right = tools(new(null, "project", "C:/two", "q")).Single().Spec;

        // The runtime replaces a session's attachment when its tools differ by name, description or schema.
        Assert.AreEqual("alta", left.Name);
        Assert.AreEqual(left.Name, right.Name);
        Assert.AreEqual(left.Description, right.Description);
        Assert.AreEqual(left.InputSchema.GetRawText(), right.InputSchema.GetRawText());
    }

    [TestMethod]
    public async Task SessionTool_ReportsACommandThatHasNoService()
    {
        // Without an ask service the command fails as a tool result; it never throws into the run.
        var tool = DesktopAltaTools.CreateSessionTools(Dispatcher(new Notes()))(new("s", null, "C:/work", "p")).Single();

        var result = await tool.Handler(Invocation("s", ["ask", "--stdin"], """{"questions":[{"title":"T","question":"Q","freeform":{}}]}"""), default);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task DiffShow_ExistsOnlyWhereAWindowCanShowChanges()
    {
        // The terminal host registers no view: the command is not part of its tool, in its help or its policies.
        var plain = Dispatcher(new Notes());
        Assert.AreEqual(AltaExitCodes.Usage, (await plain.InvokeAsync(["diff", "show"])).ExitCode);
        Assert.IsFalse((await plain.InvokeAsync(["--help"])).Stdout.Contains("diff", StringComparison.Ordinal));
        Assert.IsFalse((await plain.InvokeAsync(["tool", "list"])).Stdout.Contains("diff show", StringComparison.Ordinal));

        var root = Directory.CreateTempSubdirectory("codealta-alta-diff-").FullName;
        try
        {
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "app")).FullName);
            var other = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "other")).FullName);
            var view = new DesktopChangesView();
            var services = new AltaServiceCollection().Add(projects).Add<IAltaChangesView>(view);
            var registry = new AltaCommandRegistry();
            var desktop = new AltaCommandDispatcher(registry, services);
            services.Add(registry).Add(desktop);
            var session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "s", SourceProjectId = project.Id };
            StringAssert.Contains((await desktop.InvokeAsync(["--help"])).Stdout, "diff");
            StringAssert.Contains((await desktop.InvokeAsync(["tool", "list"])).Stdout, "diff show");

            // No window listens: the command says so instead of pretending.
            var unseen = await desktop.InvokeAsync(["diff", "show"], caller: session);
            Assert.AreEqual(AltaExitCodes.ServiceUnavailable, unseen.ExitCode);
            StringAssert.Contains(unseen.Stdout + unseen.Stderr, "view.unavailable");

            var shown = new List<ProjectGitShowEvent>();
            using var watching = view.Watch(shown.Add);
            // The project of the calling session by default; another one by id, slug or path; from a terminal, the cwd.
            var own = await desktop.InvokeAsync(["diff", "show", "--file", "src\\app.ts"], caller: session);
            Assert.AreEqual(AltaExitCodes.Success, own.ExitCode, own.Stdout + own.Stderr);
            StringAssert.Contains(own.Stdout, "alta.diff.shown");
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["diff", "show", "--project", other.Slug], caller: session)).ExitCode);
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["diff", "show", "--project", other.ProjectPath], caller: session)).ExitCode);
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["diff", "show"], caller: AltaCallerIdentity.Cli, cwd: project.ProjectPath)).ExitCode);
            CollectionAssert.AreEqual(new ProjectGitShowEvent[] { new(project.Id, "src/app.ts"), new(other.Id, null), new(other.Id, null), new(project.Id, null) }, shown);

            // Nothing is shown for a project that is not one, nor for a file outside the repository.
            Assert.AreEqual(AltaExitCodes.NotFound, (await desktop.InvokeAsync(["diff", "show", "--project", "missing"], caller: session)).ExitCode);
            Assert.AreEqual(AltaExitCodes.NotFound, (await desktop.InvokeAsync(["diff", "show"], caller: AltaCallerIdentity.Cli, cwd: root)).ExitCode);
            foreach (var file in new[] { "../outside.txt", "/etc/passwd", "a//b", "a/./b" })
                Assert.AreEqual(AltaExitCodes.Usage, (await desktop.InvokeAsync(["diff", "show", "--file", file], caller: session)).ExitCode, file);
            other.Archived = true;
            await projects.SaveAsync(other);
            Assert.AreEqual(AltaExitCodes.NotFound, (await desktop.InvokeAsync(["diff", "show", "--project", other.Id], caller: session)).ExitCode);
            Assert.HasCount(4, shown);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task EditorOpen_ExistsOnlyWhereAWindowHasACodeEditor()
    {
        // The terminal host registers no view: the command is not part of its tool, in its help or its policies.
        var plain = Dispatcher(new Notes());
        Assert.AreEqual(AltaExitCodes.Usage, (await plain.InvokeAsync(["editor", "open"])).ExitCode);
        Assert.IsFalse((await plain.InvokeAsync(["--help"])).Stdout.Contains("editor", StringComparison.Ordinal));
        Assert.IsFalse((await plain.InvokeAsync(["tool", "list"])).Stdout.Contains("editor open", StringComparison.Ordinal));

        var root = Directory.CreateTempSubdirectory("codealta-alta-editor-").FullName;
        try
        {
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "app")).FullName);
            var other = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "other")).FullName);
            Directory.CreateDirectory(Path.Combine(project.ProjectPath, "src"));
            File.WriteAllText(Path.Combine(project.ProjectPath, "src", "app.ts"), "x");
            File.WriteAllText(Path.Combine(root, "outside.txt"), "x");
            var view = new DesktopEditorView();
            var services = new AltaServiceCollection().Add(projects).Add<IAltaEditorView>(view);
            var registry = new AltaCommandRegistry();
            var desktop = new AltaCommandDispatcher(registry, services);
            services.Add(registry).Add(desktop);
            var session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "s", SourceProjectId = project.Id };
            StringAssert.Contains((await desktop.InvokeAsync(["--help"])).Stdout, "editor");
            StringAssert.Contains((await desktop.InvokeAsync(["tool", "list"])).Stdout, "editor open");
            // The changes view is another service: its command is not there without it.
            Assert.IsFalse((await desktop.InvokeAsync(["tool", "list"])).Stdout.Contains("diff show", StringComparison.Ordinal));

            // No window listens: the command says so instead of pretending.
            var unseen = await desktop.InvokeAsync(["editor", "open"], caller: session);
            Assert.AreEqual(AltaExitCodes.ServiceUnavailable, unseen.ExitCode);
            StringAssert.Contains(unseen.Stdout + unseen.Stderr, "view.unavailable");

            var opened = new List<ProjectFileShowEvent>();
            using var watching = view.Watch(opened.Add);
            // The project of the calling session by default; a file by its path in the project or by a full path inside it.
            var own = await desktop.InvokeAsync(["editor", "open", "--file", "src\\app.ts", "--line", "12", "--column", "3"], caller: session);
            Assert.AreEqual(AltaExitCodes.Success, own.ExitCode, own.Stdout + own.Stderr);
            StringAssert.Contains(own.Stdout, "alta.editor.opened");
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["editor", "open", "--file", Path.Combine(project.ProjectPath, "src", "app.ts")], caller: session)).ExitCode);
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["editor", "open", "--project", other.Slug], caller: session)).ExitCode);
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["editor", "open"], caller: AltaCallerIdentity.Cli, cwd: project.ProjectPath)).ExitCode);
            CollectionAssert.AreEqual(new ProjectFileShowEvent[] { new(project.Id, "src/app.ts", 12, 3), new(project.Id, "src/app.ts", null, null),
                new(other.Id, null, null, null), new(project.Id, null, null, null) }, opened);

            // Nothing is opened for a project that is not one, for a file that is not in the folder, or for a position that is not one.
            Assert.AreEqual(AltaExitCodes.NotFound, (await desktop.InvokeAsync(["editor", "open", "--project", "missing"], caller: session)).ExitCode);
            Assert.AreEqual(AltaExitCodes.NotFound, (await desktop.InvokeAsync(["editor", "open", "--file", "src/none.ts"], caller: session)).ExitCode);
            Assert.AreEqual(AltaExitCodes.NotFound, (await desktop.InvokeAsync(["editor", "open", "--file", "src"], caller: session)).ExitCode);
            foreach (var file in new[] { "../outside.txt", Path.Combine(root, "outside.txt"), "a//b", "a/./b", project.ProjectPath })
                Assert.AreEqual(AltaExitCodes.Usage, (await desktop.InvokeAsync(["editor", "open", "--file", file], caller: session)).ExitCode, file);
            Assert.AreEqual(AltaExitCodes.Usage, (await desktop.InvokeAsync(["editor", "open", "--file", "src/app.ts", "--line", "0"], caller: session)).ExitCode);
            Assert.AreEqual(AltaExitCodes.Usage, (await desktop.InvokeAsync(["editor", "open", "--file", "src/app.ts", "--line", "x"], caller: session)).ExitCode);
            Assert.AreEqual(AltaExitCodes.Usage, (await desktop.InvokeAsync(["editor", "open", "--file", "src/app.ts", "--column", "2"], caller: session)).ExitCode);
            Assert.AreEqual(AltaExitCodes.Usage, (await desktop.InvokeAsync(["editor", "open", "--line", "4"], caller: session)).ExitCode);
            other.Archived = true;
            await projects.SaveAsync(other);
            Assert.AreEqual(AltaExitCodes.NotFound, (await desktop.InvokeAsync(["editor", "open", "--project", other.Id], caller: session)).ExitCode);
            Assert.HasCount(4, opened);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task EditorOpenAndDiffShow_RefuseAProjectTheShownSpaceDoesNotHave()
    {
        var root = Directory.CreateTempSubdirectory("codealta-alta-space-view-").FullName;
        try
        {
            var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName };
            var projects = new ProjectCatalog(options);
            var work = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "work")).FullName);
            var home = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "home")).FullName);
            var loose = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "loose")).FullName);
            File.WriteAllText(Path.Combine(work.ProjectPath, "readme.md"), "x");
            var editor = new DesktopEditorView();
            var changes = new DesktopChangesView();
            var window = new DesktopSpaceView();
            var services = new AltaServiceCollection().Add(options).Add(projects).Add(new SpaceCatalog(projects))
                .Add<IAltaEditorView>(editor).Add<IAltaChangesView>(changes).Add<IAltaSpaceView>(window);
            var registry = new AltaCommandRegistry();
            var desktop = new AltaCommandDispatcher(registry, services);
            services.Add(registry).Add(desktop);
            var session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "s", SourceProjectId = work.Id };
            var opened = new List<ProjectFileShowEvent>();
            var shown = new List<ProjectGitShowEvent>();
            using var files = editor.Watch(opened.Add);
            using var diffs = changes.Watch(shown.Add);
            foreach (var (name, project) in new[] { ("Work", work), ("Personal", home) })
                Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["space", "create", "--name", name, "--project", project.Id])).ExitCode);

            // Until a window says what it shows, and in the default space, every project is shown.
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["editor", "open"], caller: session)).ExitCode);
            window.SetShown("default");
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["diff", "show"], caller: session)).ExitCode);

            // A session of Work asks while the window shows Personal: nothing is opened there, and the command says so.
            window.SetShown("personal");
            var file = await desktop.InvokeAsync(["editor", "open", "--file", "readme.md"], caller: session);
            Assert.AreEqual(AltaExitCodes.Unsupported, file.ExitCode, file.Stdout + file.Stderr);
            Assert.IsFalse(file.Stdout.Contains("alta.editor.opened", StringComparison.Ordinal));
            StringAssert.Contains(file.Stdout + file.Stderr, "project.notInShownSpace");
            StringAssert.Contains(file.Stdout + file.Stderr, "The editor was not opened");
            StringAssert.Contains(file.Stdout + file.Stderr, "the space 'Personal'");
            StringAssert.Contains(file.Stdout + file.Stderr, "alta space switch work");
            var diff = await desktop.InvokeAsync(["diff", "show"], caller: session);
            Assert.AreEqual(AltaExitCodes.Unsupported, diff.ExitCode, diff.Stdout + diff.Stderr);
            Assert.IsFalse(diff.Stdout.Contains("alta.diff.shown", StringComparison.Ordinal));
            StringAssert.Contains(diff.Stdout + diff.Stderr, "project.notInShownSpace");
            StringAssert.Contains(diff.Stdout + diff.Stderr, "The changes were not shown");
            StringAssert.Contains(diff.Stdout + diff.Stderr, "alta space switch work");
            // A project of no space is shown in the default one, which has every project.
            var other = await desktop.InvokeAsync(["editor", "open", "--project", loose.Id], caller: session);
            Assert.AreEqual(AltaExitCodes.Unsupported, other.ExitCode);
            StringAssert.Contains(other.Stdout + other.Stderr, "alta space switch default");
            // The window was asked each time: it is the one that offers the space to the user.
            CollectionAssert.AreEqual(new ProjectFileShowEvent[] { new(work.Id, null, null, null), new(work.Id, "readme.md", null, null), new(loose.Id, null, null, null) }, opened);
            Assert.HasCount(2, shown);

            // What the shown space has is shown, and a project is named for the first of its spaces.
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["editor", "open", "--project", home.Id], caller: session)).ExitCode);
            window.SetShown("work");
            var own = await desktop.InvokeAsync(["editor", "open", "--file", "readme.md"], caller: session);
            Assert.AreEqual(AltaExitCodes.Success, own.ExitCode, own.Stdout + own.Stderr);
            StringAssert.Contains(own.Stdout, "alta.editor.opened");
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["diff", "show"], caller: session)).ExitCode);
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["space", "add", "personal", work.Id])).ExitCode);
            window.SetShown("personal");
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["diff", "show"], caller: session)).ExitCode);

            // A space that is gone gives its place to the default one. Without a window nothing is shown at all.
            window.SetShown("gone");
            Assert.AreEqual(AltaExitCodes.Success, (await desktop.InvokeAsync(["editor", "open", "--project", loose.Id], caller: session)).ExitCode);
            window.SetShown("work");
            files.Dispose();
            var unseen = await desktop.InvokeAsync(["editor", "open", "--project", home.Id], caller: session);
            Assert.AreEqual(AltaExitCodes.ServiceUnavailable, unseen.ExitCode);
            StringAssert.Contains(unseen.Stdout + unseen.Stderr, "view.unavailable");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AltaCommandDispatcher Dispatcher(Notes notes)
    {
        var services = new AltaServiceCollection().Add<IAltaNotesService>(notes);
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, services);
        services.Add(registry).Add(dispatcher);
        return dispatcher;
    }

    private static AgentToolInvocation Invocation(string session, string[] args, string stdin)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object> { ["args"] = args, ["stdin"] = stdin }));
        return new(new("provider"), session, "call", "alta", document.RootElement.Clone());
    }

    private sealed class Notes : IAltaNotesService
    {
        public List<(string Session, string Markdown)> Writes { get; } = [];

        public event EventHandler<AltaNotesChangedEventArgs>? Changed { add { } remove { } }

        public AltaCallerIdentity CaptureCaller(AltaCallerIdentity caller) => caller;

        public ValueTask<string> GetMarkdownAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Writes.LastOrDefault(write => write.Session == caller.SourceSessionId).Markdown ?? string.Empty);

        public ValueTask SetMarkdownAsync(string markdown, AltaCallerIdentity caller, CancellationToken cancellationToken = default)
        {
            Writes.Add((caller.SourceSessionId ?? string.Empty, markdown));
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
