using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

/// <summary>Tests of the settings plugins declare: what is kept, where the values go, and who is told of a change.</summary>
[TestClass]
public sealed class PluginSettingsTests
{
    private static readonly PluginDescriptor Builds = new() { RuntimeKey = "source:builds", DisplayName = "Builds", TypeName = "Builds.Plugin", AssemblyName = "Builds" };

    [TestMethod]
    public void Validation_KeepsTheValidSettings_AndSaysWhyTheOthersAreLeftOut()
    {
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        var kept = PluginSettingValidation.Filter(Builds, PluginScope.Global,
        [
            Setting.Integer("poll_minutes", "Check every", "Minutes between two looks.", 5, 1, 120),
            Setting.Boolean("Bad-Key", "Bad", null, true),
            Setting.Boolean("enabled", "Enabled", null, true),
            Setting.Integer("poll_minutes", "Again", null, 5),
            Setting.Integer("too_big", "Too big", null, 500, 1, 120),
            new PluginSettingContribution { Key = "empty_choice", Label = "Nothing to choose", Kind = PluginSettingKind.Choice },
            Setting.Choice("notify", "Tell me when a build", null, ["failed", "always"], "failed") with { Agents = PluginSettingAccess.ReadWrite },
        ], diagnostics);
        CollectionAssert.AreEqual(new[] { "poll_minutes", "notify" }, kept.Select(static setting => setting.Key).ToArray());
        Assert.AreEqual(5, diagnostics.Count);
        Assert.IsTrue(diagnostics.All(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Warning && diagnostic.RuntimeKey == "source:builds"));
        StringAssert.Contains(diagnostics.Single(static diagnostic => diagnostic.Message.Contains("'enabled'", StringComparison.Ordinal)).Message, "keeps for itself");

        var many = Enumerable.Range(0, PluginSettingLimits.Settings + 3).Select(static index => Setting.Boolean($"flag_{index}", $"Flag {index}", null, false));
        diagnostics.Clear();
        Assert.AreEqual(PluginSettingLimits.Settings, PluginSettingValidation.Filter(Builds, PluginScope.Global, many, diagnostics).Count);
        Assert.AreEqual(3, diagnostics.Count);
    }

    [TestMethod]
    public void APluginOfAProject_KeepsEveryOneOfItsSettingsInThatProject()
    {
        var kept = PluginSettingValidation.Filter(Builds, PluginScope.Project, [Setting.Boolean("notify", "Notify", null, true)], []);
        Assert.AreEqual(PluginSettingScope.Project, kept.Single().Scope, "a plugin a repository brings never writes the configuration of the user");

        using var root = new TemporaryRoot();
        var project = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "repo")).FullName;
        var store = new PluginSettingsStore(root.ConfigPath, id => id == "p1" ? project : null);
        var view = (PluginSettingsStore.PluginScopedSettings)store.ForPlugin("builds", "p1");
        IPluginSettingsService reads = view;
        view.Declare(kept);
        store.Write("builds", kept.Single(), Json("false"), "p1");
        Assert.AreEqual(false, reads.GetBoolean("notify"));
        Assert.AreEqual(false, reads.GetBoolean("notify", "p2"), "it reads its own project, whatever it asks");
        Assert.IsFalse(File.Exists(root.ConfigPath));

        // Even a setting of the user that reached the view: the plugin of a project reads it from its project.
        view.Declare([Setting.Boolean("notify", "Notify", null, true)]);
        Assert.AreEqual(false, reads.GetBoolean("notify"));
    }

    [TestMethod]
    public void AWholeNumber_IsWrittenExactly()
    {
        using var root = new TemporaryRoot();
        var store = root.Store();
        var big = Setting.Integer("big", "Big", null, 0);
        store.Write("builds", big, Json("9007199254740993"), null);
        StringAssert.Contains(File.ReadAllText(root.ConfigPath), "big = 9007199254740993");
        Assert.AreEqual(9007199254740993L, store.Read("builds", big, null).Value!.Value.GetInt64());
    }

    [TestMethod]
    public void AValue_IsTheDefaultUntilWritten_ThenKeptUnderThePlugin_WithTheRestOfTheFile()
    {
        using var root = new TemporaryRoot();
        File.WriteAllText(root.ConfigPath, "# My configuration\n[plugins.builds]\nenabled = true # on\n\n[sessions]\ntitle = \"model\"\n");
        var store = root.Store();
        var poll = Setting.Integer("poll_minutes", "Check every", null, 5, 1, 120);
        Assert.AreEqual((5L, false), Read(store, poll));

        store.Write("builds", poll, Json("15"), null);
        Assert.AreEqual((15L, true), Read(store, poll));
        var text = File.ReadAllText(root.ConfigPath);
        StringAssert.Contains(text, "# My configuration");
        StringAssert.Contains(text, "enabled = true # on");
        StringAssert.Contains(text, "poll_minutes = 15");
        StringAssert.Contains(text, "title = \"model\"");

        // A value that does not fit the setting is neither written nor read.
        Assert.ThrowsExactly<ArgumentException>(() => store.Write("builds", poll, Json("500"), null));
        File.WriteAllText(root.ConfigPath, File.ReadAllText(root.ConfigPath).Replace("poll_minutes = 15", "poll_minutes = \"often\"", StringComparison.Ordinal));
        Assert.AreEqual((5L, false), Read(store, poll), "a hand edit that does not fit gives the default");

        store.Write("builds", poll, Json("20"), null);
        store.Write("builds", poll, null, null);
        Assert.AreEqual((5L, false), Read(store, poll), "a reset removes the value");
    }

    [TestMethod]
    public void TurningThePluginOffAndOn_KeepsItsSettings()
    {
        using var root = new TemporaryRoot();
        var store = root.Store();
        var notify = Setting.Choice("notify", "Tell me when a build", null, ["failed", "always"], "failed");
        var hosts = Setting.List("hosts", "Hosts", null);
        store.Write("builds", notify, Json("\"always\""), null);
        store.Write("builds", hosts, Json("[\"ci.example\", \"build.example\"]"), null);
        var config = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path });
        config.SaveGlobalPluginEnabled("builds", false);
        config.SaveGlobalPluginEnabled("builds", true);
        Assert.AreEqual("always", store.Read("builds", notify, null).Value!.Value.GetString());
        CollectionAssert.AreEqual(new[] { "ci.example", "build.example" }, store.Read("builds", hosts, null).Value!.Value.EnumerateArray().Select(static item => item.GetString()).ToArray());
    }

    [TestMethod]
    public async Task APlugin_ReadsItsSettingsWhenItStarts_IsToldOfEachChangeWhileItRuns_AndNotOnceStopped()
    {
        using var root = new TemporaryRoot();
        var home = System.IO.Path.Combine(root.Path, "home");
        Directory.CreateDirectory(home);
        var config = System.IO.Path.Combine(home, "config.toml");
        File.WriteAllText(config, "[plugins.builds]\nenabled = true\npoll_minutes = 9\n");
        var store = new PluginSettingsStore(config, static _ => null);
        var instances = new List<SettingsPlugin>();
        await using var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions
        {
            GlobalRoot = home, IsHeadless = true, Services = new WithSettings(store),
            BuiltIns = [new BuiltInPluginDefinition { Id = "builds", DisplayName = "Builds", PluginType = typeof(SettingsPlugin), Factory = () => { var plugin = new SettingsPlugin(); instances.Add(plugin); return plugin; } }],
        });
        var plugin = instances.Single();
        Assert.AreEqual(9L, plugin.AtStart, "the settings are read from OnActivatedAsync on");
        var active = runtime.ActivePlugins.Single(static active => active.Descriptor.RuntimeKey == "builtin:builds");

        await store.NotifyChangedAsync(active, "poll_minutes", null, "agent");
        Assert.AreEqual(("poll_minutes", "agent"), (plugin.Told.Single().Key, plugin.Told.Single().Origin));

        File.WriteAllText(config, "[plugins.builds]\nenabled = false\n");
        Assert.AreEqual(PluginPackageChange.Stopped, await runtime.ApplyBuiltInAsync("builds"));
        await store.NotifyChangedAsync(active, "poll_minutes", null, "agent");
        Assert.AreEqual(1, plugin.Told.Count, "a plugin that stopped is not called");
    }

    [TestMethod]
    public void ASettingOfEachProject_IsWrittenInTheFileOfThatProject()
    {
        using var root = new TemporaryRoot();
        var project = Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "repo")).FullName;
        var store = new PluginSettingsStore(root.ConfigPath, id => id == "p1" ? project : null);
        var branch = Setting.Text("branch", "Release branch", null, "main") with { Scope = PluginSettingScope.Project };
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Write("builds", branch, Json("\"release\""), null), "a project is named");
        store.Write("builds", branch, Json("\"release\""), "p1");
        StringAssert.Contains(File.ReadAllText(System.IO.Path.Combine(project, ".alta", "config.toml")), "branch = \"release\"");
        Assert.IsFalse(File.Exists(root.ConfigPath), "the file of the user is left alone");
        Assert.AreEqual("release", store.Read("builds", branch, "p1").Value!.Value.GetString());
        Assert.AreEqual("main", store.Read("builds", branch, "p2").Value!.Value.GetString());
    }

    public sealed class SettingsPlugin : PluginBase
    {
        public long? AtStart { get; private set; }

        public List<PluginSettingChangedEventArgs> Told { get; } = [];

        public override IEnumerable<PluginSettingContribution> GetSettings()
        {
            yield return Setting.Integer("poll_minutes", "Check every", null, 5);
        }

        public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
        {
            AtStart = Services.Settings.GetInt64("poll_minutes");
            Services.Settings.Changed += (_, change) => { lock (Told) Told.Add(change); };
            return ValueTask.CompletedTask;
        }
    }

    // The services of a host that keeps the settings of its plugins, and nothing else.
    private sealed class WithSettings(PluginSettingsStore settings) : IPluginServices
    {
        private readonly NoopPluginServices _inner = new(XenoAtom.Logging.LogManager.GetLogger("test"));

        public XenoAtom.Logging.Logger Logger => _inner.Logger;

        public IPluginUiService Ui => _inner.Ui;

        public IPluginStateStore State => _inner.State;

        public IPluginWorkspaceService Workspace => _inner.Workspace;

        public IPluginSessionService Sessions => _inner.Sessions;

        public IPluginPromptService Prompts => _inner.Prompts;

        public IPluginAgentService Agents => _inner.Agents;

        public IPluginTaskService Tasks => _inner.Tasks;

        public IPluginAltaService Alta => _inner.Alta;

        public IPluginSettingsService Settings => settings;
    }

    private static (long Value, bool Written) Read(PluginSettingsStore store, PluginSettingContribution setting)
    {
        var (value, written) = store.Read("builds", setting, null);
        return (value!.Value.GetInt64(), written);
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.PluginSettingsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string ConfigPath => System.IO.Path.Combine(Path, "config.toml");

        public PluginSettingsStore Store() => new(ConfigPath, static _ => null);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The temporary folder is removed later.
            }
        }
    }
}
