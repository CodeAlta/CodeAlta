using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

/// <summary>
/// The <c>alta plugin</c> commands: what a host did with its plugins, the plugin API, and, in a host that lets
/// its sessions do it, the creation, the build and the reload of a source plugin.
/// </summary>
[TestClass]
public sealed class AltaPluginCommandTests
{
    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task ASourcePlugin_IsCreatedBuiltReloadedAndInspected()
    {
        using var temp = new TempFolder();
        await using var runtime = await StartAsync(temp);
        var dispatcher = Dispatcher(new AltaServiceCollection().Add(new AltaPluginWorkshop(runtime)).AddPluginRuntimeHooks(runtime));

        // Create: the files, the first build and the start, in one command.
        var created = await dispatcher.InvokeAsync(["plugin", "create", "notes", "--description", "Keeps notes."], caller: AltaCallerIdentity.Cli);
        var record = Record(created, "alta.plugin.created");
        SkipWithoutFileBuilds(record);
        Assert.AreEqual(AltaExitCodes.Success, created.ExitCode, created.Stdout + created.Stderr);
        Assert.AreEqual(("notes", "global", "started", "running"), (Text(record, "id"), Text(record, "scope"), Text(record, "change"), Text(record, "state")));
        var file = Text(record, "file")!;
        Assert.AreEqual(Path.Combine(temp.Path, "home", "plugins", "notes", "plugin.cs"), file);
        Assert.IsTrue(File.Exists(Text(record, "readme")));
        Assert.AreEqual("plugin:notes", record.GetProperty("plugins")[0].GetProperty("runtimeKey").GetString());
        Assert.AreEqual("notes", record.GetProperty("plugins")[0].GetProperty("contributions")[0].GetProperty("name").GetString());

        var listed = Record(await dispatcher.InvokeAsync(["plugin", "list"], caller: AltaCallerIdentity.Cli), "alta.plugin.refs").GetProperty("plugins").EnumerateArray().Single();
        Assert.AreEqual(("notes", "global", "running", "plugin:notes"), (Text(listed, "id"), Text(listed, "scope"), Text(listed, "state"), Text(listed, "runtimeKey")));

        // A source that does not compile: the build says where, and what ran keeps running.
        File.WriteAllText(file, Source("notes", tool: "v1").Replace("Task.FromResult", "Task.FromResults", StringComparison.Ordinal));
        var broken = await dispatcher.InvokeAsync(["plugin", "build", "notes"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Failure, broken.ExitCode);
        record = Record(broken, "alta.plugin.build");
        Assert.AreEqual(("running", true), (Text(record, "state"), record.GetProperty("sourceChanged").GetBoolean()));
        var build = record.GetProperty("build");
        Assert.IsFalse(build.GetProperty("succeeded").GetBoolean());
        Assert.IsTrue(build.GetProperty("errors").GetInt32() >= 1);
        var error = build.GetProperty("diagnostics").EnumerateArray().First(static item => Text(item, "code") == "CS0117");
        Assert.AreEqual(("error", "plugin.cs"), (Text(error, "severity"), Text(error, "file")));
        Assert.IsTrue(error.GetProperty("line").GetInt32() > 0);
        StringAssert.Contains(Text(record, "next"), "The previous version is still running");
        var refused = await dispatcher.InvokeAsync(["plugin", "reload", "notes"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Failure, refused.ExitCode);
        Assert.AreEqual("buildFailed", Text(Record(refused, "alta.plugin.reload"), "change"));

        // Fixed, with a tool: a session that reloads has the tool for the rest of its turn.
        File.WriteAllText(file, Source("notes", tool: "v1"));
        var tools = new AgentRunTools();
        var session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1", RunTools = tools };
        var reloaded = await dispatcher.InvokeAsync(["plugin", "reload", "notes"], caller: session);
        Assert.AreEqual(AltaExitCodes.Success, reloaded.ExitCode, reloaded.Stdout + reloaded.Stderr);
        record = Record(reloaded, "alta.plugin.reload");
        Assert.AreEqual(("reloaded", "running", "now"), (Text(record, "change"), Text(record, "state"), Text(record.GetProperty("agentTools"), "available")));
        Assert.AreEqual("note_version", record.GetProperty("agentTools").GetProperty("names")[0].GetString());
        Assert.IsTrue(tools.Contains("note_version"));
        Assert.AreEqual("v1", await CallAsync(tools.Take()!.Single()));

        // The next version of the tool takes the place of the previous one in that turn.
        File.WriteAllText(file, Source("notes", tool: "v2"));
        reloaded = await dispatcher.InvokeAsync(["plugin", "reload", "notes"], caller: session);
        Assert.AreEqual(AltaExitCodes.Success, reloaded.ExitCode, reloaded.Stdout + reloaded.Stderr);
        Assert.AreEqual("v2", await CallAsync(tools.Take()!.Single()));
        // A caller that is not a turn has it from its next prompt.
        var later = Record(await dispatcher.InvokeAsync(["plugin", "reload", "notes"], caller: AltaCallerIdentity.Cli), "alta.plugin.reload");
        Assert.AreEqual("next_prompt", Text(later.GetProperty("agentTools"), "available"));

        var status = Record(await dispatcher.InvokeAsync(["plugin", "status", "plugin:notes"], caller: AltaCallerIdentity.Cli), "alta.plugin.status");
        CollectionAssert.Contains(status.GetProperty("plugins")[0].GetProperty("contributions").EnumerateArray().Select(static item => $"{Text(item, "point")}:{Text(item, "name")}").ToArray(), "AgentTool:note_version");
        Assert.AreEqual("Running.", Text(status, "next"));

        // What is refused, and why.
        var again = await dispatcher.InvokeAsync(["plugin", "create", "notes"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Usage, again.ExitCode);
        StringAssert.Contains(again.Stdout + again.Stderr, "plugin.exists");
        var invalid = await dispatcher.InvokeAsync(["plugin", "create", "no/slash"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Usage, invalid.ExitCode);
        StringAssert.Contains(invalid.Stdout + invalid.Stderr, "usage.invalidPlugin");
        var missing = await dispatcher.InvokeAsync(["plugin", "reload", "nothing"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.NotFound, missing.ExitCode);
        StringAssert.Contains(missing.Stdout + missing.Stderr, "plugin.notFound");

        // A plugin of the project, written without a start; then what changed on disk is applied at once.
        var local = Record(await dispatcher.InvokeAsync(["plugin", "create", "local", "--project", "--no-start"], caller: AltaCallerIdentity.Cli), "alta.plugin.created");
        Assert.AreEqual(("project", "stopped"), (Text(local, "scope"), Text(local, "state")));
        Assert.AreEqual(Path.Combine(temp.Path, "project", ".alta", "plugins", "local"), Text(local, "directory"));
        StringAssert.Contains(Text(local, "next"), "`alta plugin reload local`");
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        var refreshed = Record(await dispatcher.InvokeAsync(["plugin", "refresh"], caller: AltaCallerIdentity.Cli), "alta.plugin.refresh");
        CollectionAssert.AreEquivalent(new[] { "notes:stopped", "local:started" }, refreshed.GetProperty("changes").EnumerateArray().Select(static item => $"{Text(item, "id")}:{Text(item, "change")}").ToArray());
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task AHostWhoseSessionsDoNotBuildPlugins_ListsThemAndSaysWhyOneFailed()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(temp.Path, "home", "plugins", "broken")).FullName, "plugin.cs");
        File.WriteAllText(file, "this is not C#");
        await using var runtime = await StartAsync(temp);
        var dispatcher = Dispatcher(new AltaServiceCollection().Add(runtime));

        var help = (await dispatcher.InvokeAsync(["plugin", "--help"], caller: AltaCallerIdentity.Cli)).Stdout;
        StringAssert.Contains(help, "status");
        StringAssert.Contains(help, "api");
        Assert.IsFalse(help.Contains("reload", StringComparison.Ordinal) || help.Contains("create", StringComparison.Ordinal), help);
        Assert.AreNotEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["plugin", "reload", "broken"], caller: AltaCallerIdentity.Cli)).ExitCode);

        var status = Record(await dispatcher.InvokeAsync(["plugin", "status", "broken"], caller: AltaCallerIdentity.Cli), "alta.plugin.status");
        SkipWithoutFileBuilds(status);
        Assert.AreEqual("failed", Text(status, "state"));
        Assert.IsTrue(status.GetProperty("build").GetProperty("errors").GetInt32() > 0);
        Assert.AreEqual("plugin.cs", Text(status.GetProperty("build").GetProperty("diagnostics")[0], "file"));
        StringAssert.Contains(Text(status, "next"), "a restart of CodeAlta");
        var listed = Record(await dispatcher.InvokeAsync(["plugin", "list", "--detailed"], caller: AltaCallerIdentity.Cli), "alta.plugin.item");
        Assert.AreEqual(("broken", "failed"), (Text(listed, "id"), Text(listed, "state")));
    }

    [TestMethod]
    public async Task TheApi_IsLookedUpByTypePartOrMember()
    {
        var dispatcher = Dispatcher(new AltaServiceCollection());

        var one = Record(await dispatcher.InvokeAsync(["plugin", "api", "PluginDialogRequest"], caller: AltaCallerIdentity.Cli), "alta.plugin.api");
        var type = one.GetProperty("types").EnumerateArray().Single();
        Assert.AreEqual(("PluginDialogRequest", "CodeAlta.Plugins.Abstractions", "public record PluginDialogRequest"), (Text(type, "name"), Text(type, "namespace"), Text(type, "declaration")));
        Assert.IsTrue(type.GetProperty("members").EnumerateArray().Any(static member => member.GetString()!.StartsWith("string? Html { get; init; }", StringComparison.Ordinal)));

        // Many types have the word: their names, to choose one.
        var many = Record(await dispatcher.InvokeAsync(["plugin", "api", "Plugin"], caller: AltaCallerIdentity.Cli), "alta.plugin.api");
        Assert.IsTrue(many.GetProperty("count").GetInt32() > BuiltInAltaCommandContributor.MaximumApiTypes);
        Assert.AreEqual(JsonValueKind.String, many.GetProperty("types")[0].ValueKind);
        StringAssert.Contains(Text(many, "next"), "Name one of these types");

        var member = Record(await dispatcher.InvokeAsync(["plugin", "api", "SelectedProjectPath"], caller: AltaCallerIdentity.Cli), "alta.plugin.api");
        CollectionAssert.Contains(member.GetProperty("types").EnumerateArray().Select(static item => Text(item, "name")).ToArray(), "IPluginWorkspaceService");

        var index = Record(await dispatcher.InvokeAsync(["plugin", "api"], caller: AltaCallerIdentity.Cli), "alta.plugin.api.index");
        Assert.IsTrue(index.GetProperty("count").GetInt32() > 100);
        CollectionAssert.Contains(index.GetProperty("types").EnumerateArray().Select(static item => item.GetString()).ToArray(), "PluginBase (class)");

        var none = await dispatcher.InvokeAsync(["plugin", "api", "NoSuchThingAnywhere"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.NotFound, none.ExitCode);
        StringAssert.Contains(none.Stdout + none.Stderr, "plugin.apiNotFound");
        Assert.IsFalse((none.Stdout + none.Stderr).Contains("Close names", StringComparison.Ordinal));

        // A name that was guessed is answered with the names that are close to it.
        var guessed = await dispatcher.InvokeAsync(["plugin", "api", "PluginUiContext"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.NotFound, guessed.ExitCode);
        StringAssert.Contains(guessed.Stdout + guessed.Stderr, "Close names: ");
        StringAssert.Contains(guessed.Stdout + guessed.Stderr, "PluginStatusContext");
    }

    [TestMethod]
    public async Task APlugin_IsOpenedInTheEditorOfTheHost_AndAProjectPluginBelongsToTheProjectOfTheHost()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "home", "plugins", "notes"));
        File.WriteAllText(Path.Combine(temp.Path, "home", "plugins", "notes", "plugin.cs"), Source("notes", tool: "v1"));
        // Plugins are turned off for this run: nothing is built, and the packages are still there to open.
        await using var runtime = await StartAsync(temp, safeMode: true);
        var catalogOptions = new CatalogOptions { GlobalRoot = Path.Combine(temp.Path, "home") };
        var projects = new ProjectCatalog(catalogOptions);
        var other = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp.Path, "other")).FullName);
        var opened = new List<(string Id, string? File, int? Line, int? Column)>();
        var shown = true;
        var workshop = new AltaPluginWorkshop(runtime)
        {
            OpenEditor = (package, file, line, column) => { opened.Add((package.PackageId, file, line, column)); return shown; },
        };
        var dispatcher = Dispatcher(new AltaServiceCollection().Add(catalogOptions).Add(projects).Add(workshop));

        var result = await dispatcher.InvokeAsync(["plugin", "open", "notes", "--line", "7", "--column", "3"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
        Assert.AreEqual(("notes", (string?)"plugin.cs", (int?)7, (int?)3), opened.Single());
        Assert.AreEqual("plugin.cs", Text(Record(result, "alta.plugin.opened"), "file"));

        foreach (var (arguments, code) in new (string[], string)[]
        {
            (["plugin", "open", "notes", "--file", "../config.toml"], "usage.invalidFile"),
            (["plugin", "open", "notes", "--file", "missing.cs"], "file.notFound"),
            (["plugin", "open", "notes", "--column", "3"], "usage.invalidColumn"),
            (["plugin", "open", "nothing"], "plugin.notFound"),
        })
        {
            var refused = await dispatcher.InvokeAsync(arguments, caller: AltaCallerIdentity.Cli);
            Assert.AreNotEqual(AltaExitCodes.Success, refused.ExitCode, code);
            StringAssert.Contains(refused.Stdout + refused.Stderr, code);
        }

        shown = false;
        var closed = await dispatcher.InvokeAsync(["plugin", "open", "notes"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.ServiceUnavailable, closed.ExitCode);
        Assert.AreEqual(2, opened.Count);

        // A session of another project cannot have a project plugin: the host loads those of its own project.
        var elsewhere = await dispatcher.InvokeAsync(["plugin", "create", "local", "--project"], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "s", SourceProjectId = other.Id });
        Assert.AreEqual(AltaExitCodes.Unsupported, elsewhere.ExitCode);
        StringAssert.Contains(elsewhere.Stdout + elsewhere.Stderr, "plugin.otherProject");
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "project", ".alta", "plugins", "local")));
        Assert.IsFalse(Directory.Exists(Path.Combine(other.ProjectPath, ".alta")));
    }

    private static async Task<PluginRuntimeManager> StartAsync(TempFolder temp, bool safeMode = false)
    {
        var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions
        {
            GlobalRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "home")).FullName,
            ProjectContext = new PluginProjectContext { ProjectId = "project-1", ProjectPath = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName },
            IsHeadless = true,
            SafeMode = safeMode,
        });
        return runtime;
    }

    private static AltaCommandDispatcher Dispatcher(AltaServiceCollection services)
    {
        var registry = new AltaCommandRegistry();
        return new AltaCommandDispatcher(registry, services.Add(registry));
    }

    private static string Source(string key, string tool)
        => $$"""
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

[Plugin("{{key}}")]
public sealed class NotesPlugin : PluginBase
{
    public override IEnumerable<PluginAgentToolContribution> GetAgentTools()
    {
        yield return AgentTool.Create(new AgentToolDefinition(
            new AgentToolSpec("note_version", "Says the version.", JsonDocument.Parse("{ \"type\": \"object\", \"properties\": {} }").RootElement.Clone()),
            static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("{{tool}}")]))));
    }
}
""";

    private static async Task<string> CallAsync(AgentToolDefinition tool)
    {
        using var arguments = JsonDocument.Parse("{}");
        var result = await tool.Handler(new AgentToolInvocation(new ModelProviderId("provider"), "session-1", "call-1", tool.Spec.Name, arguments.RootElement.Clone()), CancellationToken.None);
        return string.Concat(result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));
    }

    private static JsonElement Record(AltaCommandResult result, string type)
    {
        foreach (var line in (result.Stdout + "\n" + result.Stderr).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("type", out var kind) && kind.GetString() == type) return document.RootElement.Clone();
        }

        Assert.Fail($"No '{type}' record in: {result.Stdout}{result.Stderr}");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }

    private static string? Text(JsonElement record, string name)
        => record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void SkipWithoutFileBuilds(JsonElement record)
    {
        if (record.TryGetProperty("build", out var build) && build.TryGetProperty("output", out var output)
            && (output.GetString() ?? string.Empty).Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("The installed .NET SDK did not accept `dotnet build plugin.cs` file-based builds in this environment.");
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-plugin-commands-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
            catch (UnauthorizedAccessException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
