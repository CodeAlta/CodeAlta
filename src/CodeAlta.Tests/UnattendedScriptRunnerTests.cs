using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.LiveTool.Scripts;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Scripts;
using CodeAlta.Plugins.Abstractions;

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

    private const string Notifier = """
        export const meta = { name: "notifier" };
        const shown = await notify({ title: "Deploy", body: "It passed.", tone: "success", session: "s1" });
        const short = await notify("Done");
        const refused = await notify({ title: "Deploy", tone: "loud" }).then(() => "notified", (e) => e.name);
        return { shown: shown.shown, short: short.shown, refused };
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
    public async Task AScriptWithoutAModel_Notifies_WhereTheHostShowsNotifications()
    {
        using var root = TestTempDirectory.Create();
        var (runner, project) = Create(root.Path);

        // A host that has nowhere to show them: the script goes on, and reads that nothing was shown.
        var nowhere = await runner.RunAsync(new UnattendedScriptRequest("run-6", "notifier") { ProjectPath = project }, default);
        Assert.IsTrue(nowhere.Succeeded, nowhere.Error);
        Assert.AreEqual("""{"shown":false,"short":false,"refused":"TypeError"}""", nowhere.Value);

        var notifications = new RecordedNotifications();
        runner.Notifications = notifications;
        var told = await runner.RunAsync(new UnattendedScriptRequest("run-7", "notifier") { ProjectPath = project }, default);
        Assert.IsTrue(told.Succeeded, told.Error);
        Assert.AreEqual("""{"shown":true,"short":true,"refused":"TypeError"}""", told.Value);
        Assert.AreEqual(2, notifications.Shown.Count);
        Assert.AreEqual(("Deploy", "It passed.", PluginNotificationTone.Success, "s1"),
            (notifications.Shown[0].Title, notifications.Shown[0].Body, notifications.Shown[0].Tone, notifications.Shown[0].SessionId));
        // No session runs it: a click on a notification that names none brings CodeAlta to the front.
        Assert.AreEqual(("Done", (string?)null, PluginNotificationTone.Info, (string?)null),
            (notifications.Shown[1].Title, notifications.Shown[1].Body, notifications.Shown[1].Tone, notifications.Shown[1].SessionId));
    }

    [TestMethod]
    public async Task AScriptOfASession_Notifies_AndAClickOpensThatSession()
    {
        var notifications = new RecordedNotifications();
        var binding = new CodeModeNotifyBinding(notifications, "session-1");
        var result = await binding.InvokeAsync(new CodeAlta.CodeMode.CodeModeCall(0, CodeModeNotifyBinding.Path, """{"title":"Review","body":"3 findings."}""", null, null), default);
        Assert.AreEqual("""{"shown":true}""", result.ValueJson);
        Assert.AreEqual(("Review", "session-1"), (notifications.Shown.Single().Title, notifications.Shown.Single().SessionId));
        var refused = await binding.InvokeAsync(new CodeAlta.CodeMode.CodeModeCall(1, CodeModeNotifyBinding.Path, """{"title":" "}""", null, null), default);
        Assert.AreEqual("TypeError", refused.Error?.Name);
    }

    private sealed class RecordedNotifications : IUserNotifications
    {
        internal List<PluginNotification> Shown { get; } = [];

        public ValueTask<bool> NotifyAsync(PluginNotification notification, CancellationToken cancellationToken)
        {
            Shown.Add(notification);
            return ValueTask.FromResult(true);
        }
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
        Assert.IsFalse(UnattendedScriptRunner.Allows(Command("mcp tool call", null), [.. plugins, "mcp"]), "the tools of MCP servers run under no sandbox nor guard here");
        Assert.IsFalse(UnattendedScriptRunner.Allows(Command("mcp", null), [.. plugins, "mcp"]));
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
        library.Save("notifier", CodeModeScriptScope.Project, Notifier, project, null);
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(catalog).Add(registry).Add(library));
        return (new UnattendedScriptRunner(dispatcher, library, null, () => []), project);
    }
}
