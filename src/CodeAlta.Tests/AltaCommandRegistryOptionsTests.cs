using System.Reflection;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Reviews;
using CodeAlta.Catalog.WorkItems;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Tests.Search;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

/// <summary>The built-in roots an <c>alta</c> registry offers: all of them, or those of an allow-list such as the one of the command-line application.</summary>
[TestClass]
public sealed class AltaCommandRegistryOptionsTests
{
    private static readonly string[] Excluded =
        ["ask", "review", "reminder", "window", "appearance", "canvas", "landing", "terminal", "automation", "diff", "editor", "documentation", "search"];

    private static readonly string[] Allowed =
        ["version", "project", "space", "session", "sandbox", "job", "skill", "skills", "skills_activate", "tool", "provider", "model", "prompt", "plugin", "task", "plan", "issue", "issues", "notes", "note"];

    [TestMethod]
    public void CliAllowList_NamesTheHeadlessRootsOnly()
    {
        var roots = AltaCommandRegistryOptions.CliBuiltInRoots;
        foreach (var root in Allowed.Append("bookmark")) Assert.IsTrue(roots.Contains(root), root);
        foreach (var root in Excluded) Assert.IsFalse(roots.Contains(root), root);
        Assert.IsNull(AltaCommandRegistryOptions.Default.AllowedBuiltInRoots);
        Assert.IsTrue(AltaCommandRegistryOptions.Default.AllowsBuiltInRoot("reminder"));
        Assert.IsFalse(AltaCommandRegistryOptions.Cli.AllowsBuiltInRoot("reminder"));
        Assert.AreSame(AltaCommandRegistryOptions.Default, new AltaCommandRegistry().Options);
    }

    [TestMethod]
    public async Task CliRegistry_LeavesOutTheExcludedRoots_FromTheTreeTheHelpTheGuidanceAndThePolicies()
    {
        await using var host = await Host.CreateAsync(AltaCommandRegistryOptions.Cli);

        var roots = host.RootNames();
        foreach (var root in Allowed.Append("dice")) Assert.IsTrue(roots.Contains(root), $"{root} is offered");
        foreach (var root in Excluded) Assert.IsFalse(roots.Contains(root), $"{root} is left out");

        var help = await host.Dispatcher.InvokeAsync(["--help"]);
        Assert.AreEqual(AltaExitCodes.Success, help.ExitCode, help.Transcript);
        foreach (var root in Excluded) Assert.IsFalse(help.Stdout.Contains($"\n  {root} ", StringComparison.Ordinal), $"{root} in the help");
        StringAssert.Contains(help.Stdout, "\n  session ");
        StringAssert.Contains(help.Stdout, "\n  dice ", "a root of a plugin is not filtered");
        // The guidance names the roots of the tree only.
        StringAssert.Contains(help.Stdout, "`alta session current`");
        StringAssert.Contains(help.Stdout, "`alta job start");
        StringAssert.Contains(help.Stdout, "`alta notes get`");
        StringAssert.Contains(help.Stdout, "Coordinate: delegate project-folder work");
        StringAssert.Contains(help.Stdout, "Discover: `alta <command> --help`");
        Assert.IsFalse(help.Stdout.Contains("alta ask", StringComparison.Ordinal));
        Assert.IsFalse(help.Stdout.Contains("alta reminder", StringComparison.Ordinal));

        var paths = await host.PolicyPathsAsync(["tool", "capability", "list"]);
        CollectionAssert.AreEquivalent(paths, await host.PolicyPathsAsync(["tool", "list"]));
        foreach (var root in Excluded) Assert.IsFalse(paths.Any(path => path == root || path.StartsWith(root + " ", StringComparison.Ordinal)), $"{root} in the policies");
        foreach (var path in new[] { "version", "session send", "job start", "task create", "plan list", "issue list", "notes set", "space list", "skills_activate", "dice roll" })
        {
            CollectionAssert.Contains(paths, path);
        }

        // An allowed command runs; an excluded one is unknown.
        Assert.AreEqual(AltaExitCodes.Success, (await host.Dispatcher.InvokeAsync(["version"])).ExitCode);
        Assert.AreEqual(AltaExitCodes.Success, (await host.Dispatcher.InvokeAsync(["dice", "roll"])).ExitCode);
        Assert.AreEqual(AltaExitCodes.Usage, (await host.Dispatcher.InvokeAsync(["reminder", "list"])).ExitCode);
        Assert.AreEqual(AltaExitCodes.Usage, (await host.Dispatcher.InvokeAsync(["ask", "--stdin"], stdin: "{}")).ExitCode);
    }

    [TestMethod]
    public async Task RegistryWithoutOptions_OffersEveryRootItsServicesSupport()
    {
        await using var host = await Host.CreateAsync(options: null);

        var roots = host.RootNames();
        foreach (var root in Allowed.Concat(Excluded).Append("dice")) Assert.IsTrue(roots.Contains(root), $"{root} is offered");

        var help = await host.Dispatcher.InvokeAsync(["--help"]);
        StringAssert.Contains(help.Stdout, "`alta ask --stdin`");
        StringAssert.Contains(help.Stdout, "`alta reminder create");
        var paths = await host.PolicyPathsAsync(["tool", "capability", "list"]);
        foreach (var path in new[] { "ask", "reminder create", "review request", "window list", "diff show", "editor open", "search" })
        {
            Assert.IsTrue(paths.Any(policy => policy == path || policy.StartsWith(path + " ", StringComparison.Ordinal)), path);
        }
    }

    [TestMethod]
    public async Task Guidance_WithoutOptions_IsCompleteEvenForARootTheHostLacks()
    {
        // Without an allow-list nothing changes: the space example is written even in a host that keeps no spaces.
        var bare = new AltaCommandRegistry();
        var help = await new AltaCommandDispatcher(bare, new AltaServiceCollection().Add(bare)).InvokeAsync(["--help"]);
        StringAssert.Contains(help.Stdout, "`alta space list`");

        // With one, an allowed root the host does not offer has no example.
        var cli = new AltaCommandRegistry(AltaCommandRegistryOptions.Cli);
        var cliHelp = await new AltaCommandDispatcher(cli, new AltaServiceCollection().Add(cli)).InvokeAsync(["--help"]);
        Assert.IsFalse(cliHelp.Stdout.Contains("alta space", StringComparison.Ordinal));
        StringAssert.Contains(cliHelp.Stdout, "`alta project current`");
    }

    /// <summary>A host with every service that brings a built-in root, and a plugin with a root of its own.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly TestTempDirectory _root;
        private readonly SearchFixture _search;
        private readonly PluginRuntimeManager _plugins;

        private Host(TestTempDirectory root, SearchFixture search, PluginRuntimeManager plugins, AltaCommandRegistry registry, AltaServiceCollection services)
        {
            _root = root;
            _search = search;
            _plugins = plugins;
            Registry = registry;
            Services = services;
            Dispatcher = new AltaCommandDispatcher(registry, services);
        }

        public AltaCommandRegistry Registry { get; }

        public AltaServiceCollection Services { get; }

        public AltaCommandDispatcher Dispatcher { get; }

        public static Task<Host> CreateAsync(AltaCommandRegistryOptions? options)
        {
            var root = TestTempDirectory.Create();
            var catalog = new CatalogOptions { GlobalRoot = root.Path };
            var projects = new ProjectCatalog(catalog);
            var search = new SearchFixture();
            var plugins = new PluginRuntimeManager();
            var registry = options is null ? new AltaCommandRegistry() : new AltaCommandRegistry(options);
            var services = new AltaServiceCollection()
                .Add(catalog)
                .Add(projects)
                .Add(new SpaceCatalog(projects))
                .Add(new WorkItemService(new CodeAltaConfigStore(catalog), Path.Combine(root.Path, "state")))
                .Add(new ReviewService(Path.Combine(root.Path, "state")))
                .Add(new CodeAlta.Catalog.Documentation.ShippedDocumentation(Path.Combine(root.Path, "guide")))
                .Add(search.Service)
                .Add(plugins)
                .Add(Present<IAltaChangesView>())
                .Add(Present<IAltaEditorView>())
                .Add(Present<IAltaCanvasView>())
                .Add(Present<IAltaTerminals>())
                .Add(Present<IAltaAutomations>())
                .Add(Present<IAltaAppearance>())
                .Add(Present<IAltaWindows>())
                .Add<IAltaPluginCatalog>(new DicePluginCatalog())
                .Add(registry);
            return Task.FromResult(new Host(root, search, plugins, registry, services));
        }

        public HashSet<string> RootNames()
        {
            var context = new AltaCommandContext
            {
                Caller = AltaCallerIdentity.Host,
                Services = Services,
                Stdin = TextReader.Null,
                Stdout = TextWriter.Null,
                Stderr = TextWriter.Null,
                CorrelationId = "roots",
            };
            return [.. Registry.CreateCommandApp(context).SubCommands.Keys];
        }

        public async Task<List<string>> PolicyPathsAsync(string[] args)
        {
            var result = await Dispatcher.InvokeAsync(args);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Transcript);
            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith('{')) continue;
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("paths", out var paths))
                {
                    return [.. paths.EnumerateArray().Select(static path => path.GetString()!)];
                }
            }

            Assert.Fail($"No paths in {result.Stdout}");
            return [];
        }

        public async ValueTask DisposeAsync()
        {
            await _plugins.DisposeAsync();
            await _search.DisposeAsync();
            _root.Dispose();
        }

        // A service whose presence is all the command tree reads.
        private static T Present<T>() where T : class => DispatchProxy.Create<T, PresenceProxy>();
    }

    public class PresenceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name);
    }

    [TestMethod]
    public void APlugin_TakesNoNameOfACommandASandboxedSessionMayRun()
    {
        // The gate of a sandboxed session admits commands by name: a plugin that took `editor` (absent where there is no
        // window) or `mcp` would be run for it.
        var plugin = new PluginDescriptor { RuntimeKey = "source:sly", TypeName = "Sly.Plugin", AssemblyName = "sly", DisplayName = "Sly" };
        AltaPluginCommandContribution Root(string name) => new()
        {
            Plugin = plugin,
            Services = NoopPluginServices.Create(),
            Scope = PluginScope.Global,
            Command = new PluginAltaCommandContribution
            {
                Path = name + " run",
                Description = "Runs.",
                CreateCommandNode = _ => new XenoAtom.CommandLine.Command(name, "Runs.") { new CommandUsage(), new HelpOption() },
            },
        };
        var services = new AltaServiceCollection().Add<IAltaPluginCatalog>(new ListedPluginCatalog([Root("editor"), Root("mcp"), Root("job"), Root("sly")]));
        var context = new AltaCommandContext
        {
            Caller = AltaCallerIdentity.Host, Services = services, Stdin = TextReader.Null, Stdout = TextWriter.Null, Stderr = TextWriter.Null, CorrelationId = "roots",
        };

        var roots = new PluginAltaCommandContributor().CreateCommandLineNodes(new AltaCommandContributionContext { Invocation = context })
            .OfType<XenoAtom.CommandLine.Command>().Select(static command => command.Name).ToArray();

        CollectionAssert.AreEqual(new[] { "sly" }, roots);
    }

    private sealed class ListedPluginCatalog(IReadOnlyList<AltaPluginCommandContribution> contributions) : IAltaPluginCatalog
    {
        public IReadOnlyList<AltaPluginSummary> ListPlugins() => [];

        public AltaPluginSummary? GetPlugin(string runtimeKey) => null;

        public IReadOnlyList<AltaCommandPolicy> ListCommandPolicies() => [];

        public IReadOnlyList<AltaPluginCommandContribution> ListCommandContributions() => contributions;
    }

    private sealed class DicePluginCatalog : IAltaPluginCatalog
    {
        private static readonly PluginDescriptor Descriptor = new() { RuntimeKey = "source:dice", TypeName = "Dice", AssemblyName = "dice", DisplayName = "Dice" };

        public IReadOnlyList<AltaPluginSummary> ListPlugins() => [];

        public AltaPluginSummary? GetPlugin(string runtimeKey) => null;

        public IReadOnlyList<AltaCommandPolicy> ListCommandPolicies() => [];

        public IReadOnlyList<AltaPluginCommandContribution> ListCommandContributions() =>
        [
            new AltaPluginCommandContribution
            {
                Plugin = Descriptor,
                Services = NoopPluginServices.Create(),
                Scope = PluginScope.Global,
                Command = new PluginAltaCommandContribution
                {
                    Path = "dice roll",
                    Description = "Rolls a die.",
                    CreateCommandNode = static context =>
                    {
                        var roll = new XenoAtom.CommandLine.Command("roll", "Rolls a die.") { new CommandUsage(), new HelpOption() };
                        roll.Add((_, _) =>
                        {
                            context.Stdout.WriteLine("{\"type\":\"alta.dice.roll\",\"value\":4}");
                            return ValueTask.FromResult(0);
                        });
                        return new XenoAtom.CommandLine.Command("dice", "Dice.") { new CommandUsage(), new HelpOption(), roll };
                    },
                },
            },
        ];
    }
}
