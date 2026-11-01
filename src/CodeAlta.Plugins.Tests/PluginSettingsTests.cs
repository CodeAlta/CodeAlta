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
        var kept = PluginSettingValidation.Filter(Builds,
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
        Assert.AreEqual(PluginSettingLimits.Settings, PluginSettingValidation.Filter(Builds, many, diagnostics).Count);
        Assert.AreEqual(3, diagnostics.Count);
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
    public void APlugin_ReadsItsOwnSettings_AndIsToldOnceOfEachChange()
    {
        using var root = new TemporaryRoot();
        var store = root.Store();
        var poll = Setting.Integer("poll_minutes", "Check every", null, 5);
        store.Declared = runtimeKey => runtimeKey == "source:builds" ? [poll] : [];
        var builds = store.ForPlugin("source:builds", "builds", null);
        var other = store.ForPlugin("source:other", "other", null);
        Assert.AreEqual(5L, builds.GetInt64("poll_minutes"));
        Assert.IsNull(builds.GetValue("unknown"), "a key the plugin does not declare");
        Assert.IsNull(other.GetValue("poll_minutes"), "another plugin does not read it");

        var told = new List<PluginSettingChangedEventArgs>();
        builds.Changed += (_, change) => told.Add(change);
        other.Changed += (_, _) => Assert.Fail("another plugin is not told");
        store.Write("builds", poll, Json("9"), null);
        store.NotifyChanged("source:builds", "poll_minutes", null, "agent");
        Assert.AreEqual(9L, builds.GetInt64("poll_minutes"));
        Assert.AreEqual(("poll_minutes", "agent"), (told.Single().Key, told.Single().Origin));
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
