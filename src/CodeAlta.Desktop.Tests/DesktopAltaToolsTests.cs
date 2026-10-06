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
