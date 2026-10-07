using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginFileStateStoreTests
{
    [TestMethod]
    public async Task APlugin_KeepsItsDataInAFolderOfItsOwn()
    {
        using var temp = new TestTempDirectory();
        var store = new PluginFileStateStore(Path.Combine(temp.Path, "plugin-data"), "plugin:notes", scopeProjectPath: null, NoopPluginServices.Create());

        Assert.AreEqual(Path.Combine(temp.Path, "plugin-data", "plugin_notes"), store.GetDirectory(PluginStateScope.User));
        Assert.IsNull(await store.ReadJsonAsync<Notes>(PluginStateScope.User, "notes"));
        Assert.IsFalse(Directory.Exists(store.GetDirectory(PluginStateScope.User)), "Reading creates nothing.");

        await store.WriteJsonAsync(PluginStateScope.User, "notes", new Notes(["one", "two"], 2));
        await store.WriteJsonAsync(PluginStateScope.User, "count.json", 3);

        // A name with and without its extension is the same item; a second store of the plugin reads what the first wrote.
        var file = Path.Combine(temp.Path, "plugin-data", "plugin_notes", "notes.json");
        Assert.IsTrue(File.Exists(file));
        var again = new PluginFileStateStore(Path.Combine(temp.Path, "plugin-data"), "plugin:notes", scopeProjectPath: null, NoopPluginServices.Create());
        var notes = await again.ReadJsonAsync<Notes>(PluginStateScope.User, "notes.json");
        CollectionAssert.AreEqual(new[] { "one", "two" }, notes!.Items.ToArray());
        Assert.AreEqual(2, notes.Count);
        Assert.AreEqual(3, await again.ReadJsonAsync<int>(PluginStateScope.User, "count"));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Length);

        // Another plugin has another folder, and a file that is not JSON is no data.
        var other = new PluginFileStateStore(Path.Combine(temp.Path, "plugin-data"), "builtin:git", scopeProjectPath: null, NoopPluginServices.Create());
        Assert.IsNull(await other.ReadJsonAsync<Notes>(PluginStateScope.User, "notes"));
        File.WriteAllText(file, "not json");
        Assert.IsNull(await again.ReadJsonAsync<Notes>(PluginStateScope.User, "notes"));

        await again.DeleteAsync(PluginStateScope.User, "notes");
        await again.DeleteAsync(PluginStateScope.User, "notes");
        Assert.IsFalse(File.Exists(file));
    }

    [TestMethod]
    public async Task TheDataOfAProject_IsInThatProject_AndANameIsNeverAPath()
    {
        using var temp = new TestTempDirectory();
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        var scoped = new PluginFileStateStore(Path.Combine(temp.Path, "plugin-data"), "plugin:notes", project, NoopPluginServices.Create());
        var global = new PluginFileStateStore(Path.Combine(temp.Path, "plugin-data"), "plugin:notes", scopeProjectPath: null, NoopPluginServices.Create());

        await scoped.WriteJsonAsync(PluginStateScope.Project, "notes", new Notes(["here"], 1));

        Assert.IsTrue(File.Exists(Path.Combine(project, ".alta", "plugin-data", "plugin_notes", "notes.json")));
        // A plugin of the user has the data of the project the host names; this host names none, and no session.
        Assert.ThrowsExactly<InvalidOperationException>(() => global.GetDirectory(PluginStateScope.Project));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await global.ReadJsonAsync<Notes>(PluginStateScope.Session, "notes"));

        foreach (var name in new[] { "../outside", "a/b", @"a\b", "..", " " })
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await scoped.WriteJsonAsync(PluginStateScope.User, name, 1), name);
        }

        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "plugin-data")));
    }

    [TestMethod]
    public async Task AnActivePlugin_HasTheStore_UnlessItsHostHasOne()
    {
        using var temp = new TestTempDirectory();
        var registry = new PluginContributionRegistry();
        var descriptor = PluginDescriptorFactory.FromType(typeof(StatefulPlugin));
        var host = new PluginHostInfo { ApplicationName = "CodeAlta", Version = "1.0.0", HostApiVersion = "1.0.0", UserDataDirectory = temp.Path };

        var activation = await new PluginRuntimeActivator(registry).ActivateAsync(
            new DiscoveredPluginType { Type = typeof(StatefulPlugin), Descriptor = descriptor }, sourcePackage: null, loadContext: null, new PluginActivationOptions { HostInfo = host });

        var plugin = (StatefulPlugin)activation.ActivePlugin!.Instance!;
        await plugin.SaveAsync(41);
        Assert.AreEqual(41, await plugin.ReadAsync());
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(temp.Path, "plugin-data"), "value.json", SearchOption.AllDirectories).Length);
        await activation.ActivePlugin.DeactivateAsync(TimeSpan.FromSeconds(10));
    }

    public sealed record Notes(IReadOnlyList<string> Items, int Count);

    [Plugin("stateful")]
    public sealed class StatefulPlugin : PluginBase
    {
        public ValueTask SaveAsync(int value) => Services.State.WriteJsonAsync(PluginStateScope.User, "value", value);

        public ValueTask<int> ReadAsync() => Services.State.ReadJsonAsync<int>(PluginStateScope.User, "value");
    }
}
