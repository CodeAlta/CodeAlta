using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginRuntimeBuiltInTests
{
    [TestMethod]
    public async Task ABuiltInPlugin_IsStartedWhenItIsTurnedOn_AndStoppedWhenItIsTurnedOff()
    {
        using var temp = new TestTempDirectory();
        var home = Path.Combine(temp.Path, "home");
        var instances = new List<CountingPlugin>();
        await using var runtime = new PluginRuntimeManager();
        var changes = new List<string>();
        runtime.Changed += (_, args) => changes.AddRange(args.PackageIds);
        await runtime.StartAsync(new PluginRuntimeManagerOptions
        {
            GlobalRoot = home, IsHeadless = true,
            BuiltIns = [new BuiltInPluginDefinition { Id = "phone", DisplayName = "Phone", EnabledByDefault = false, PluginType = typeof(CountingPlugin), Factory = () => Add(instances) }],
        });
        Assert.AreEqual(0, instances.Count, "Off by default: not started.");
        Assert.AreEqual(PluginPackageChange.Disabled, await runtime.ApplyBuiltInAsync("phone"));

        WriteConfig(home, "[plugins.phone]\nenabled = true\n");
        Assert.AreEqual(PluginPackageChange.Started, await runtime.ApplyBuiltInAsync("Phone"), "The id is read as the configuration reads it, without case.");
        Assert.AreEqual(1, instances.Count);
        Assert.IsTrue(instances[0].Activated);
        CollectionAssert.AreEqual(new[] { "first" }, Commands(runtime));
        Assert.AreEqual(PluginPackageChange.Unchanged, await runtime.ApplyBuiltInAsync("phone"), "It runs already.");

        WriteConfig(home, "[plugins.phone]\nenabled = false\n");
        Assert.AreEqual(PluginPackageChange.Stopped, await runtime.ApplyBuiltInAsync("phone"));
        Assert.IsTrue(instances[0].Deactivated);
        Assert.AreEqual(0, Commands(runtime).Length);
        Assert.IsFalse(runtime.ActivePlugins.Any(static plugin => plugin.Descriptor.RuntimeKey == "builtin:phone"));
        CollectionAssert.AreEqual(new[] { "phone", "phone" }, changes, "The window reads the contributions again after each start and stop.");

        // Turned on again: a new instance, as at a start of the application.
        WriteConfig(home, "[plugins.phone]\nenabled = true\n");
        Assert.AreEqual(PluginPackageChange.Started, await runtime.ApplyBuiltInAsync("phone"));
        Assert.AreEqual(2, instances.Count);
        Assert.AreEqual(PluginPackageChange.Unchanged, await runtime.ApplyBuiltInAsync("unknown"), "A host without that built-in plugin changes nothing.");
    }

    [TestMethod]
    public async Task ABuiltInPluginOfAnotherApplication_IsNotStarted()
    {
        using var temp = new TestTempDirectory();
        var home = Path.Combine(temp.Path, "home");
        var instances = new List<CountingPlugin>();
        await using var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions
        {
            GlobalRoot = home, IsHeadless = true, Frontend = PluginFrontends.Terminal,
            BuiltIns = [new BuiltInPluginDefinition { Id = "phone", DisplayName = "Phone", EnabledByDefault = false, Frontends = PluginFrontends.Desktop, PluginType = typeof(CountingPlugin), Factory = () => Add(instances) }],
        });
        WriteConfig(home, "[plugins.phone]\nenabled = true\n");

        Assert.AreEqual(PluginPackageChange.Disabled, await runtime.ApplyBuiltInAsync("phone"));
        Assert.AreEqual(0, instances.Count);
    }

    [TestMethod]
    public async Task APlugin_ReadsTheSpacesItIsAssignedTo_AsTheConfigurationSaysThemNow()
    {
        using var temp = new TestTempDirectory();
        var home = Path.Combine(temp.Path, "home");
        var instances = new List<CountingPlugin>();
        var assignments = SpaceAssignments.Empty;
        await using var runtime = new PluginRuntimeManager();
        runtime.Adapter.SpaceAssignments = () => assignments;
        await runtime.StartAsync(new PluginRuntimeManagerOptions
        {
            GlobalRoot = home, IsHeadless = true,
            BuiltIns = [new BuiltInPluginDefinition { Id = "phone", DisplayName = "Phone", PluginType = typeof(CountingPlugin), Factory = () => Add(instances) }],
        });
        var spaces = instances.Single().Spaces;

        Assert.AreEqual(0, spaces.GetAssignedSpaces().Count, "In every space.");
        assignments = SpaceAssignments.Parse("[plugins.phone]\nspaces = [\"personal\", \"work\"]\n[plugins.other]\nspaces = [\"work\"]\n");
        CollectionAssert.AreEqual(new[] { "personal", "work" }, spaces.GetAssignedSpaces().ToArray(), "Read when asked: a change of the settings applies at once.");
    }

    [TestMethod]
    public void AHostWithoutSpaces_PutsEveryPluginInEverySpace()
    {
        IPluginServices services = new NoopPluginServices(XenoAtom.Logging.LogManager.GetLogger("test"));
        Assert.AreEqual(0, services.Spaces.GetAssignedSpaces().Count);
        Assert.IsFalse(services.AppFiles.HasFiles);
    }

    private static CountingPlugin Add(List<CountingPlugin> instances)
    {
        var plugin = new CountingPlugin();
        instances.Add(plugin);
        return plugin;
    }

    private static void WriteConfig(string home, string content)
    {
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.toml"), content);
    }

    private static string[] Commands(PluginRuntimeManager runtime)
        => [.. runtime.Registry.GetSnapshot().Select(static registration => registration.Contribution).OfType<PluginCommandContribution>().Select(static command => command.Name)];

    public sealed class CountingPlugin : PluginBase
    {
        public bool Activated { get; private set; }

        public bool Deactivated { get; private set; }

        public IPluginSpaceService Spaces => Services.Spaces;

        public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
        {
            Activated = true;
            return ValueTask.CompletedTask;
        }

        public override ValueTask OnDeactivatingAsync(CancellationToken cancellationToken = default)
        {
            Deactivated = true;
            return ValueTask.CompletedTask;
        }

        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            yield return Command.Shell("first", "A command.", static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled));
        }
    }
}
