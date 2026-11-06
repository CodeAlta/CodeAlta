using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.LiveTool.Scripts;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Tests;

[TestClass]
public sealed class UnattendedScriptRunnerTests
{
    private const string Probe = """
        export const meta = { name: "probe", params: { event: { type: "object" } } };
        const read = await tools.read_file({ path: "notes.txt" });
        const works = async (call) => { try { await call(); return true; } catch { return false; } };
        return {
          on: args.event?.on,
          read: read.content ?? read,
          canWrite: await works(() => tools.write_file({ path: "written.txt", content: "x" })),
          canShell: await works(() => tools.shell_command({ command: "echo hi > shell.txt" })),
          canListScripts: await works(() => alta.script.list({})),
          canCreateSession: await works(() => alta.session.create({ title: "x" })),
          canSendSession: await works(() => alta.session.send({ sessionId: "s", stdin: "x" })),
        };
        """;

    private const string Writer = """
        export const meta = { name: "writer" };
        await tools.write_file({ path: "out.txt", content: "written" });
        present("Wrote out.txt.");
        return "done";
        """;

    [TestMethod]
    public async Task AScriptWithoutAModel_ReadsAndCallsWhatChangesNothing_ButNothingThatStartsAnAgent()
    {
        using var root = TestTempDirectory.Create();
        var (runner, project) = Create(root.Path);
        File.WriteAllText(Path.Combine(project, "notes.txt"), "hello");

        var result = await runner.RunAsync(new UnattendedScriptRequest("run-1", "probe") { ProjectPath = project, EventJson = """{"on":"added","slug":"alpha"}""" }, default);

        Assert.IsTrue(result.Succeeded, result.Error);
        using var value = JsonDocument.Parse(result.Value!);
        var probe = value.RootElement;
        Assert.AreEqual("added", probe.GetProperty("on").GetString(), "the event reaches the script as an object");
        StringAssert.Contains(probe.GetProperty("read").GetString(), "hello");
        Assert.IsFalse(probe.GetProperty("canWrite").GetBoolean(), "reading only: no edit");
        Assert.IsFalse(probe.GetProperty("canShell").GetBoolean(), "reading only: no command");
        Assert.IsTrue(probe.GetProperty("canListScripts").GetBoolean(), "a command that changes nothing is there");
        Assert.IsFalse(probe.GetProperty("canCreateSession").GetBoolean(), "nothing starts an agent");
        Assert.IsFalse(probe.GetProperty("canSendSession").GetBoolean(), "nothing drives one");
        Assert.IsFalse(File.Exists(Path.Combine(project, "written.txt")) || File.Exists(Path.Combine(project, "shell.txt")), "nothing was written");
    }

    [TestMethod]
    public async Task AScriptAllowedToWrite_EditsInItsProject_AndWhatItPresentsIsKept()
    {
        using var root = TestTempDirectory.Create();
        var (runner, project) = Create(root.Path);

        var result = await runner.RunAsync(new UnattendedScriptRequest("run-2", "writer") { ProjectPath = project, Write = true }, default);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual("\"done\"", result.Value);
        CollectionAssert.AreEqual(new[] { "Wrote out.txt." }, result.Presented.ToArray());
        Assert.AreEqual("written", File.ReadAllText(Path.Combine(project, "out.txt")));

        // Without the right to write, the same script fails, and writes nothing.
        File.Delete(Path.Combine(project, "out.txt"));
        var refused = await runner.RunAsync(new UnattendedScriptRequest("run-3", "writer") { ProjectPath = project }, default);
        Assert.IsFalse(refused.Succeeded);
        Assert.IsFalse(File.Exists(Path.Combine(project, "out.txt")));
    }

    [TestMethod]
    public async Task AScriptThatCannotRun_SaysWhy()
    {
        using var root = TestTempDirectory.Create();
        var (runner, project) = Create(root.Path);
        StringAssert.Contains((await runner.RunAsync(new UnattendedScriptRequest("run-4", "missing") { ProjectPath = project }, default)).Error, "no script 'missing'");
        // A host without sandbox runs nothing a profile should hold.
        StringAssert.Contains((await runner.RunAsync(new UnattendedScriptRequest("run-5", "writer") { ProjectPath = project, Sandbox = "strict" }, default)).Error, "strict");
    }

    [TestMethod]
    public void TheCommandsOfARunWithoutAModel_AreThoseThatChangeNothing_AndThoseOfPlugins()
    {
        AltaCommandDescriptor Command(string path, bool? mutating) => new(path, null, [], [])
        {
            Policy = mutating is { } changes ? new AltaCommandPolicy { Path = path, IsMutating = changes } : null,
        };

        string[] plugins = ["mail", "todo add"];
        Assert.IsTrue(UnattendedScriptRunner.Allows(Command("session list", false), plugins));
        Assert.IsFalse(UnattendedScriptRunner.Allows(Command("session create", true), plugins));
        Assert.IsFalse(UnattendedScriptRunner.Allows(Command("job start", null), plugins), "a command that says nothing of itself is not given");
        Assert.IsTrue(UnattendedScriptRunner.Allows(Command("mail send", null), plugins), "a command of a plugin is how the script calls it");
        Assert.IsTrue(UnattendedScriptRunner.Allows(Command("todo add", null), plugins));
        Assert.IsFalse(UnattendedScriptRunner.Allows(Command("mailbox", null), plugins), "a prefix of a name is not the name");
    }

    private static (UnattendedScriptRunner Runner, string Project) Create(string root)
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        var catalog = new CatalogOptions { GlobalRoot = Path.Combine(root, "home") };
        var library = new CodeModeScriptLibrary(catalog);
        library.Save("probe", CodeModeScriptScope.Project, Probe, project, null);
        library.Save("writer", CodeModeScriptScope.Project, Writer, project, null);
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(catalog).Add(registry).Add(library));
        return (new UnattendedScriptRunner(dispatcher, library, null, () => []), project);
    }
}
