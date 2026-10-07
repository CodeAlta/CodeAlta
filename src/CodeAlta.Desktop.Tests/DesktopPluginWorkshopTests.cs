using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using static CodeAlta.Desktop.Tests.DesktopUiToolsTests;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A plugin that is written while the application runs: a session builds it again and uses it in the same turn,
/// the code editor opens on its folder, and the Settings page creates, reloads and switches it.
/// </summary>
[TestClass]
public sealed class DesktopPluginWorkshopTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task ASession_ReloadsAPlugin_AndCallsTheNewVersionOfItsToolInTheSameTurn()
    {
        using var temp = new TempFolder();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var file = WritePlugin(Path.Combine(global, "plugins"), "notes", ToolSource("v1"));
        var provider = new ScriptedProvider();
        var pluginAlta = new PluginAltaServiceBridge();
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true, AutoApproveOwnedPermissions = true, OwnedCommandReceiptCapacity = 32,
            PluginServices = new DesktopPluginServices(pluginAlta, new DesktopPluginUi()),
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
        SkipWithoutFileBuilds(host.PluginRuntime);
        var editor = new DesktopEditorView();
        var changes = 0;
        host.PluginRuntime.Changed += (_, _) => Interlocked.Increment(ref changes);
        DesktopAltaTools.Attach(host, new AltaReminderService(new AltaServiceCollection()), pluginAlta, editor: editor, plugins: DesktopPlugins.Workshop(host.PluginRuntime, editor));
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Writes a plugin");

        // The tool of the plugin is one of the tools of the session.
        provider.Script(Call("note_version", "{}"), Say("the first one"));
        await SendAsync(host, session, "one");
        Assert.AreEqual("v1", provider.Requests[1].LastToolResult);

        // The source changes. The session builds the plugin again and, in the same turn, calls the new version.
        File.WriteAllText(file, ToolSource("v2"));
        provider.Requests.Clear();
        provider.Script(Call("alta", """{"args":["plugin","reload","notes"]}"""), Call("note_version", "{}"), Say("the second one"));
        await SendAsync(host, session, "two");
        Assert.AreEqual(3, provider.Requests.Count);
        var reload = provider.Requests[1].LastToolResult!;
        StringAssert.Contains(reload, "\"type\":\"alta.plugin.reload\"");
        StringAssert.Contains(reload, "\"change\":\"reloaded\"");
        StringAssert.Contains(reload, "\"available\":\"now\"");
        Assert.AreEqual(1, provider.Requests[1].Tools.Count(static tool => tool == "note_version"), "The new version takes the place of the previous one.");
        Assert.AreEqual("v2", provider.Requests[2].LastToolResult);
        Assert.AreEqual(1, changes);

        // A source that does not build leaves the session with the version that runs.
        File.WriteAllText(file, "this is not C#");
        provider.Requests.Clear();
        provider.Script(Call("alta", """{"args":["plugin","reload","notes"]}"""), Call("note_version", "{}"), Say("still the second one"));
        await SendAsync(host, session, "three");
        StringAssert.Contains(provider.Requests[1].LastToolResult, "\"change\":\"buildFailed\"");
        StringAssert.Contains(provider.Requests[1].LastToolResult, "\"file\":\"plugin.cs\"");
        Assert.AreEqual("v2", provider.Requests[2].LastToolResult);
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public async Task AHostThatHasItsCommandsReviewed_GivesItsSessionsNoCommandThatBuildsAPlugin()
    {
        using var temp = new TempFolder();
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName, CurrentProjectPath = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName,
            IsHeadless = true, OwnedCommandReceiptCapacity = 8,
        }, CancellationToken.None);

        // What the window composes when the user reviews the commands: the plugins are listed and nothing more.
        var reviewed = DesktopAltaTools.Compose(host, new AltaReminderService(new AltaServiceCollection()));
        var help = (await reviewed.InvokeAsync(["plugin", "--help"], caller: AltaCallerIdentity.Cli)).Transcript;
        StringAssert.Contains(help, "status");
        Assert.IsFalse(help.Contains("reload", StringComparison.Ordinal) || help.Contains("create", StringComparison.Ordinal), help);

        var editor = new DesktopEditorView();
        var open = DesktopAltaTools.Compose(host, new AltaReminderService(new AltaServiceCollection()), editor: editor, plugins: DesktopPlugins.Workshop(host.PluginRuntime, editor));
        help = (await open.InvokeAsync(["plugin", "--help"], caller: AltaCallerIdentity.Cli)).Transcript;
        foreach (var command in new[] { "create", "build", "reload", "refresh", "open" }) StringAssert.Contains(help, command);
    }

    [TestMethod]
    public async Task TheFolderOfAPlugin_IsNamedByAnId_ThatTheHostResolves()
    {
        using var temp = new TempFolder();
        var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName });
        var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName);
        WritePlugin(Path.Combine(projects.Options.GlobalRoot, "plugins"), "notes", "// global");
        WritePlugin(Path.Combine(project.ProjectPath, ".alta", "plugins"), "local.one", "// project");

        Assert.AreEqual("plugin:global:notes", new PluginFolder(null, "notes").Id);
        Assert.AreEqual($"plugin:project:{project.Id}:local.one", new PluginFolder(project.Id, "local.one").Id);
        Assert.IsTrue(PluginFolder.TryParse("plugin:global:notes", out var global));
        Assert.AreEqual(new PluginFolder(null, "notes"), global);
        Assert.IsTrue(PluginFolder.TryParse($"plugin:project:{project.Id}:local.one", out var local));
        Assert.AreEqual(new PluginFolder(project.Id, "local.one"), local);
        // The id of a project, a path, and a package name that would leave the plugin folder are not such ids.
        foreach (var other in new[] { null, "", project.Id, "plugin:", "plugin:global:", "plugin:global:../x", "plugin:global:a/b", @"plugin:global:a\b", "plugin:project:notes", "plugin:project::notes", "plugin:other:notes", "plugin:global:.hidden" })
        {
            Assert.IsFalse(PluginFolder.TryParse(other, out _), other);
        }

        Assert.AreEqual(("ok", Path.Combine(projects.Options.GlobalRoot, "plugins", "notes")), await global.ResolveAsync(projects, default));
        Assert.AreEqual(("ok", Path.Combine(project.ProjectPath, ".alta", "plugins", "local.one")), await local.ResolveAsync(projects, default));
        Assert.AreEqual(("project_unavailable", (string?)null), await new PluginFolder(null, "gone").ResolveAsync(projects, default));
        Assert.AreEqual(("unknown_project", (string?)null), await new PluginFolder("no-such-project", "notes").ResolveAsync(projects, default));
    }

    [TestMethod]
    public async Task TheCodeEditor_WorksInTheFolderOfAPlugin_AndIsAskedToOpenIt()
    {
        using var temp = new TempFolder();
        var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName });
        var file = WritePlugin(Path.Combine(projects.Options.GlobalRoot, "plugins"), "notes", "// first\n");
        File.WriteAllText(Path.Combine(projects.Options.GlobalRoot, "config.toml"), "# the configuration of the user\n");
        var editor = new DesktopEditorView();
        var files = new ProjectFilesService(projects, Epoch, view: editor);
        const string Folder = "plugin:global:notes";

        var listed = await files.ListAsync(new(Epoch, Folder, [new("", null)], false), default);
        Assert.AreEqual("ok", listed.Status);
        CollectionAssert.AreEqual(new[] { "plugin.cs" }, listed.Folders.Single().Entries.Select(static entry => entry.Name).ToArray());
        var read = await files.ReadAsync(new(Epoch, Folder, "plugin.cs"), default);
        Assert.AreEqual(("ok", "// first\n"), (read.Status, read.Content));
        Assert.AreEqual("ok", (await files.WriteAsync(new(Epoch, Folder, "plugin.cs", "// second\n", read.Revision, false), default)).Status);
        Assert.AreEqual("// second\n", File.ReadAllText(file));
        Assert.AreEqual("ok", (await files.CreateAsync(new(Epoch, Folder, "README.md", false), default)).Status);
        Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "README.md")));

        // The folder is the limit: nothing of the rest of the home of CodeAlta is read through it.
        Assert.AreEqual("outside_root", (await files.ReadAsync(new(Epoch, Folder, "../../config.toml"), default)).Status);
        Assert.AreEqual("project_unavailable", (await files.ReadAsync(new(Epoch, "plugin:global:gone", "plugin.cs"), default)).Status);
        Assert.AreEqual("unknown_project", (await files.ReadAsync(new(Epoch, "plugin:global:../plugins", "plugin.cs"), default)).Status);

        // `alta plugin open` asks the page for a tab on that folder: its id, its name and its path.
        var shown = new List<ProjectFileShowEvent>();
        var package = new SourcePluginDiscoveryService().Discover(new PluginRoot { RootPath = Path.Combine(projects.Options.GlobalRoot, "plugins"), Scope = PluginScope.Global }).Single();
        var workshop = DesktopPlugins.Workshop(new PluginRuntimeManager(), editor);
        Assert.IsFalse(workshop.OpenEditor!(package, "plugin.cs", 3, null), "No window watches yet.");
        using (editor.Watch(shown.Add))
        {
            Assert.IsTrue(workshop.OpenEditor!(package, "plugin.cs", 3, 5));
            Assert.IsTrue(workshop.OpenEditor!(package, null, 3, 5));
        }

        Assert.AreEqual(new ProjectFileShowEvent(Folder, "plugin.cs", 3, 5, "notes", package.PackageDirectory), shown[0]);
        Assert.AreEqual(new ProjectFileShowEvent(Folder, null, null, null, "notes", package.PackageDirectory), shown[1]);
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task TheSettingsPage_CreatesReloadsAndSwitchesAPluginOfTheRunningHost()
    {
        using var temp = new TempFolder();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
        var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName);
        var other = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp.Path, "other")).FullName);
        await using var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = global, ProjectContext = new PluginProjectContext { ProjectId = project.Id, ProjectPath = project.ProjectPath }, IsHeadless = true });
        var service = new PluginsService(projects, Epoch, runtime);

        // Create: the package, built and started, with the id the code editor opens its folder by.
        var created = await service.CreateAsync(new(Epoch, null, "Global", "notes", "Keeps notes."), default);
        Assert.AreEqual(("ok", "plugin:global:notes", "notes"), (created.Status, created.Folder, created.Name));
        Assert.AreEqual(Path.Combine(global, "plugins", "notes"), created.Path);
        var entry = (await service.ListAsync(new(Epoch, null), default)).Plugins.Single();
        SkipWithoutFileBuilds(runtime);
        Assert.AreEqual(("notes", "Notes", "Keeps notes.", "running", "plugin:global:notes"), (entry.Id, entry.Name, entry.Description, entry.Runtime, entry.Folder));
        Assert.IsTrue(entry is { Loadable: true, Changed: false, Enabled: true } && entry.Errors!.Count == 0);
        Assert.AreEqual("exists", (await service.CreateAsync(new(Epoch, null, "Global", "notes", null), default)).Status);
        Assert.AreEqual("invalid", (await service.CreateAsync(new(Epoch, null, "Global", "no/slash", null), default)).Status);
        Assert.AreEqual("invalid", (await service.CreateAsync(new(Epoch, null, "Project", "local", null), default)).Status, "A project plugin needs a project.");
        Assert.AreEqual("stale_epoch", (await service.CreateAsync(new("another", null, "Global", "more", null), default)).Status);

        // Reload: a source that does not build says where, and the plugin keeps running.
        var file = Path.Combine(created.Path!, "plugin.cs");
        var source = File.ReadAllText(file);
        File.WriteAllText(file, source.Replace("PluginCommandResult.Handled", "missing", StringComparison.Ordinal));
        Assert.IsTrue((await service.ListAsync(new(Epoch, null), default)).Plugins.Single().Changed);
        var failed = await service.ReloadAsync(new(Epoch, null, "Global", "notes"), default);
        Assert.AreEqual("build_failed", failed.Status);
        StringAssert.Contains(failed.Message, "error CS0103");
        entry = (await service.ListAsync(new(Epoch, null), default)).Plugins.Single();
        Assert.AreEqual("running", entry.Runtime);
        StringAssert.StartsWith(entry.Errors!.Single(), "plugin.cs(");
        File.WriteAllText(file, source.Replace("Hello from", "Good morning from", StringComparison.Ordinal));
        Assert.AreEqual(new PluginsMutationResponse("ok", null, true), await service.ReloadAsync(new(Epoch, null, "Global", "notes"), default));
        Assert.AreEqual(0, (await service.ListAsync(new(Epoch, null), default)).Plugins.Single().Errors!.Count);
        Assert.AreEqual("unknown", (await service.ReloadAsync(new(Epoch, null, "Global", "nothing"), default)).Status);

        // The switch is followed at once by a plugin the host loads.
        Assert.AreEqual(new PluginsMutationResponse("ok", null, true), await service.SetEnabledAsync(new(Epoch, null, "Global", "notes", false), default));
        Assert.AreEqual(0, runtime.ActivePlugins.Count);
        Assert.AreEqual("disabled", (await service.ReloadAsync(new(Epoch, null, "Global", "notes"), default)).Status);
        Assert.AreEqual(new PluginsMutationResponse("ok", null, true), await service.SetEnabledAsync(new(Epoch, null, "Global", "notes", true), default));
        Assert.AreEqual("plugin:notes", runtime.ActivePlugins.Single().Descriptor.RuntimeKey);
        // A plugin that has no package (a built-in one) is switched for the next start.
        Assert.AreEqual(new PluginsMutationResponse("ok", null, false), await service.SetEnabledAsync(new(Epoch, null, "Global", "git", false), default));

        // A plugin of another project than the one of the host is written and not loaded.
        var elsewhere = await service.CreateAsync(new(Epoch, other.Id, "Project", "local", null), default);
        Assert.AreEqual(("ok", $"plugin:project:{other.Id}:local"), (elsewhere.Status, elsewhere.Folder));
        var local = (await service.ListAsync(new(Epoch, other.Id), default)).Plugins.Single(static plugin => plugin.Id == "local");
        Assert.IsTrue(local is { Loadable: false, Runtime: "stopped", Scope: "Project" });
        Assert.AreEqual("not_loaded", (await service.ReloadAsync(new(Epoch, other.Id, "Project", "local"), default)).Status);
    }

    [TestMethod]
    public void ThePage_IsToldToReadAgainWhatPluginsShow()
    {
        using var ui = new DesktopPluginUi();
        var events = new List<PluginUiEvent>();

        // Nobody watches: nothing is kept for later, unlike a notification.
        ui.Refresh();
        using (ui.Watch(events.Add))
        {
            Assert.AreEqual(0, events.Count);
            ui.Refresh();
            Assert.AreEqual("refresh", events.Single().Kind);
        }
    }

    private static string WritePlugin(string root, string id, string source)
    {
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(root, id)).FullName, "plugin.cs");
        File.WriteAllText(file, source);
        return file;
    }

    private static string ToolSource(string version)
        => $$"""
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

[Plugin("notes")]
public sealed class NotesPlugin : PluginBase
{
    public override IEnumerable<PluginAgentToolContribution> GetAgentTools()
    {
        yield return AgentTool.Create(new AgentToolDefinition(
            new AgentToolSpec("note_version", "Says the version.", JsonDocument.Parse("{ \"type\": \"object\", \"properties\": {} }").RootElement.Clone()),
            static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("{{version}}")]))));
    }
}
""";

    private static void SkipWithoutFileBuilds(PluginRuntimeManager runtime)
    {
        if (runtime.GetPackages().Any(static package => package.Build is { Succeeded: false } build
                && (build.StandardOutput + build.StandardError).Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Inconclusive("The installed .NET SDK did not accept `dotnet build plugin.cs` file-based builds in this environment.");
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-plugin-workshop-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
            catch (UnauthorizedAccessException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
