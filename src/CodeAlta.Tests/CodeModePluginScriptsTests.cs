using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.LiveTool.Scripts;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeModePluginScriptsTests
{
    private const string Notify = """
        export const meta = { name: "notify", description: "Tells a plugin what happened.", params: { event: { type: "object" } } };
        return "notified " + args.event?.on;
        """;

    [TestMethod]
    public void TheScriptsOfPlugins_AreInTheLibraryUnderTheirOwnNames_AndAreNeverSaved()
    {
        using var root = TestTempDirectory.Create();
        var (library, project) = Create(root.Path);
        // A script of the project with the short name of the plugin's script takes nothing of it.
        library.Save("notify", CodeModeScriptScope.Project, Notify, project, null);
        var scripts = new List<CodeModePluginScript> { new("acme.deploys/notify", Notify, "acme"), new("not a name", Notify, "acme") };
        library.PluginScripts = () => scripts;

        var listed = library.List(project);
        CollectionAssert.AreEqual(new[] { "acme.deploys/notify", "notify" }, listed.Select(static script => script.Name).ToArray(), "a script of a plugin with a wrong name is left out");
        var plugin = library.Find("acme.deploys/notify", project)!;
        Assert.AreEqual((CodeModeScriptScope.Plugin, "Tells a plugin what happened."), (plugin.Scope, plugin.Description));
        Assert.AreEqual(Notify, plugin.Source.Original);
        Assert.AreEqual(plugin.Hash, library.FindHash("acme.deploys/notify", project));
        Assert.AreEqual(CodeModeScriptScope.Project, library.Find("notify", project)!.Scope);

        // A new version of the plugin is a new hash: what an automation was allowed with changes.
        scripts[0] = scripts[0] with { Content = Notify + "\n// 2" };
        Assert.AreNotEqual(plugin.Hash, library.FindHash("acme.deploys/notify", project));

        var (saved, refusal) = library.Save("acme.deploys/notify", CodeModeScriptScope.Global, Notify, project, null);
        Assert.IsNull(saved);
        StringAssert.Contains(refusal, "script of a plugin");

        // A space that does not have the plugin does not have its scripts.
        var none = new SpaceFilter(SpaceAssignments.Parse("[plugins.acme]\nspaces = [\"work\"]\n"), "home");
        Assert.IsNull(library.Find("acme.deploys/notify", project, none));
        Assert.IsNotNull(library.Find("acme.deploys/notify", project, new SpaceFilter(none.Assignments, "work")));

        // Plugins that cannot be listed now take nothing else with them.
        library.PluginScripts = () => throw new InvalidOperationException("restarting");
        Assert.AreEqual(1, library.List(project).Count);
    }

    [TestMethod]
    public void TheNameOfAScriptOfAPlugin_IsAPrefixAndAName()
    {
        foreach (var name in new[] { "acme/notify", "acme.deploys/notify-all", "9x/a" })
            Assert.IsTrue(CodeModeScriptLibrary.IsPluginName(name) && CodeModeScriptLibrary.IsReference(name), name);
        foreach (var name in new[] { "notify", "acme/", "/notify", "acme/b/c", "Acme/notify", "acme/Notify", "acme/notify_all", "acme/1x" })
            Assert.IsFalse(CodeModeScriptLibrary.IsPluginName(name), name);
        Assert.IsTrue(CodeModeScriptLibrary.IsReference("notify"));
        Assert.IsTrue(CodeModeScriptRunPrompt.IsRunOfScript("return await workflow(\"acme.deploys/notify\", {});"), "a launcher runs a script of a plugin too");
    }

    [TestMethod]
    public async Task AScriptOfAPlugin_RunsWithoutAModel_WithItsEvent()
    {
        using var root = TestTempDirectory.Create();
        var (library, project) = Create(root.Path);
        library.PluginScripts = () => [new("acme.deploys/notify", Notify, "acme")];
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "home") }).Add(registry).Add(library));
        var runner = new UnattendedScriptRunner(dispatcher, library, null, () => []);

        var result = await runner.RunAsync(new UnattendedScriptRequest("run-1", "acme.deploys/notify") { ProjectPath = project, EventJson = """{"on":"succeeded"}""" }, default);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual("\"notified succeeded\"", result.Value);
    }

    private static (CodeModeScriptLibrary Library, string Project) Create(string root)
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        return (new CodeModeScriptLibrary(new CatalogOptions { GlobalRoot = Path.Combine(root, "home") }), project);
    }
}
