using CodeAlta.Agent;
using CodeAlta.Catalog;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaConfigStorePluginTests
{
    [TestMethod]
    public void SaveGlobalDefaultProviderPreservesUnknownPluginEntries()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "CodeAltaConfigStorePluginTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempPath);
            var options = new CatalogOptions { GlobalRoot = tempPath };
            File.WriteAllText(options.ConfigPath, """
[plugins.unknown_plugin]
enabled = true

[chat]
default_provider = "codex"
""");
            var store = new CodeAltaConfigStore(options);

            store.SaveGlobalDefaultProvider("copilot");
            var document = store.LoadGlobal();

            Assert.IsNotNull(document.Plugins);
            Assert.IsTrue(document.Plugins.TryGetValue("unknown_plugin", out var settings));
            Assert.AreEqual(true, settings.Enabled);
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SaveGlobalPluginEnabled_PersistsPluginOverride()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "CodeAltaConfigStorePluginTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempPath);
            var options = new CatalogOptions { GlobalRoot = tempPath };
            var store = new CodeAltaConfigStore(options);

            store.SaveGlobalPluginEnabled("sample-plugin", enabled: false);
            var document = store.LoadGlobal();

            Assert.IsNotNull(document.Plugins);
            Assert.IsTrue(document.Plugins.TryGetValue("sample-plugin", out var settings));
            Assert.AreEqual(false, settings.Enabled);
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SaveProjectPluginEnabled_PersistsProjectPluginOverride()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "CodeAltaConfigStorePluginTests", Guid.NewGuid().ToString("N"));
        try
        {
            var projectPath = Path.Combine(tempPath, "project");
            Directory.CreateDirectory(projectPath);
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.Combine(tempPath, "home") });

            store.SaveProjectPluginEnabled(projectPath, "project-plugin", enabled: true);
            var document = store.LoadProject(projectPath);

            Assert.IsNotNull(document.Plugins);
            Assert.IsTrue(document.Plugins.TryGetValue("project-plugin", out var settings));
            Assert.AreEqual(true, settings.Enabled);
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }

    // Settings the typed configuration document does not model: a plugin's own settings (the MCP policy),
    // a top-level key and a whole table from another version or tool.
    private const string ConfigWithUnmodeledSettings = """
future_setting = "kept"

[chat]
default_provider = "local"

[providers.local]
type = "openai-chat"
model = "model-a"
api_key_env = "LOCAL_KEY"
api_url = "http://127.0.0.1:9999/v1"

[providers.spare]
enabled = false
type = "anthropic"
api_key_env = "SPARE_KEY"

[plugins.mcp]
enabled = false
tool_timeout_ms = 1234

[plugins.mcp.servers.files]
enabled = false

[future_section]
answer = 42
""";

    [TestMethod]
    public void SaveGlobalProviderDefinitions_PreservesSettingsTheDocumentDoesNotModel()
    {
        using var config = TempConfig.Create(ConfigWithUnmodeledSettings);
        var definitions = config.Store.LoadGlobal().Providers!.Select(static entry =>
        {
            entry.Value.ProviderKey = entry.Key;
            return entry.Value;
        }).ToList();
        definitions.Single(static definition => definition.ProviderKey == "local").Model = "model-b";

        config.Store.SaveGlobalProviderDefinitions(definitions);

        Assert.AreEqual("model-b", config.Store.LoadGlobal().Providers!["local"].Model);
        AssertUnmodeledSettingsKept(config.Path);

        config.Store.SaveGlobalProviderDefinitions(definitions.Where(static definition => definition.ProviderKey != "spare"));

        Assert.IsFalse(config.Store.LoadGlobal().Providers!.ContainsKey("spare"));
        AssertUnmodeledSettingsKept(config.Path);
    }

    [TestMethod]
    public void SaveGlobalDefaultProviderAndPreference_PreserveSettingsTheDocumentDoesNotModel()
    {
        using var config = TempConfig.Create(ConfigWithUnmodeledSettings);

        config.Store.SaveGlobalDefaultProvider("spare");

        Assert.AreEqual("spare", config.Store.LoadGlobal().Chat?.DefaultProvider);
        AssertUnmodeledSettingsKept(config.Path);

        config.Store.SaveGlobalProviderPreference("local", "model-c", AgentReasoningEffort.High);

        Assert.AreEqual("model-c", config.Store.GetEffectiveProviderPreference("local").Model);
        AssertUnmodeledSettingsKept(config.Path);
    }

    [TestMethod]
    public void SaveGlobalPluginEnabled_PreservesTheOtherSettingsOfEveryPlugin()
    {
        using var config = TempConfig.Create(ConfigWithUnmodeledSettings);

        config.Store.SaveGlobalPluginEnabled("sample-plugin", enabled: false);

        Assert.AreEqual(false, config.Store.LoadGlobal().Plugins!["sample-plugin"].Enabled);
        Assert.AreEqual(false, config.Store.LoadGlobal().Plugins!["mcp"].Enabled);
        AssertUnmodeledSettingsKept(config.Path);

        // Toggling the plugin that owns the unmodeled settings must only change its enablement.
        config.Store.SaveGlobalPluginEnabled("mcp", enabled: true);

        Assert.AreEqual(true, config.Store.LoadGlobal().Plugins!["mcp"].Enabled);
        AssertUnmodeledSettingsKept(config.Path);
    }

    [TestMethod]
    public void SaveProjectPluginEnabled_PreservesTheOtherSettingsOfEveryPlugin()
    {
        using var config = TempConfig.Create(null);
        var projectPath = Path.Combine(config.Root, "project");
        var projectConfig = Path.Combine(projectPath, ".alta", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(projectConfig)!);
        File.WriteAllText(projectConfig, """
future_setting = "kept"

[plugins.mcp]
tool_timeout_ms = 1234

[plugins.mcp.servers.files]
enabled = false

[future_section]
answer = 42
""");

        config.Store.SaveProjectPluginEnabled(projectPath, "mcp", enabled: false);

        Assert.AreEqual(false, config.Store.LoadProject(projectPath).Plugins!["mcp"].Enabled);
        AssertUnmodeledSettingsKept(projectConfig);
        Assert.IsFalse(File.Exists(config.Path), "A project save must not create the global file.");
    }

    [TestMethod]
    public void SaveGlobalProviderPreference_KeepsLineEndingsAndEndOfLineComments()
    {
        using var config = TempConfig.Create(string.Join("\r\n",
            "[chat]",
            "default_provider = \"local\" # The chat default.",
            "",
            "[providers.local]",
            "type = \"openai-chat\" # The wire protocol.",
            "api_key_env = \"LOCAL_KEY\"",
            "",
            "[plugins.mcp]",
            "tool_timeout_ms = 1234 # The tool timeout.",
            ""));

        config.Store.SaveGlobalProviderPreference("local", "model-c", AgentReasoningEffort.High);

        var text = File.ReadAllText(config.Path);
        foreach (var comment in new[] { "# The chat default.", "# The wire protocol.", "# The tool timeout." })
        {
            StringAssert.Contains(text, comment, text);
        }

        Assert.IsFalse(text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'), "The file keeps its CRLF line endings.");
        Assert.AreEqual("model-c", config.Store.GetEffectiveProviderPreference("local").Model);
    }

    [TestMethod]
    public void SaveGlobalProviderDefinitions_RemovesADroppedProviderWithItsWholeTable()
    {
        using var config = TempConfig.Create("""
[providers.local]
type = "openai-chat"
api_key_env = "LOCAL_KEY"
future_provider_setting = "kept"

[providers.local.compaction]
enabled = true
future_compaction_setting = "kept"

[providers.Spare]
enabled = false
type = "anthropic"
future_provider_setting = "dropped"

[providers.Spare.future_table]
answer = 42
""");
        var local = config.Store.LoadGlobal().Providers!["local"];
        local.ProviderKey = "local";
        local.Model = "model-b";

        config.Store.SaveGlobalProviderDefinitions([local]);

        var text = File.ReadAllText(config.Path);
        var root = TomlSerializer.Deserialize<TomlTable>(text)!;
        Assert.AreEqual("model-b", Find(root, "providers", "local", "model"), "A key added beside an existing sub-table stays in its own table. " + text);
        Assert.AreEqual("kept", Find(root, "providers", "local", "future_provider_setting"), text);
        Assert.AreEqual("kept", Find(root, "providers", "local", "compaction", "future_compaction_setting"), text);
        Assert.IsNull(Find(root, "providers", "local", "compaction", "enabled"), "A modeled default is still pruned. " + text);
        Assert.IsNull(Find(root, "providers", "Spare"), text);
        Assert.IsFalse(text.Contains("dropped", StringComparison.Ordinal), text);
        Assert.AreEqual("local", config.Store.LoadGlobalProviderDefinitions(includeDisabled: true).Single().ProviderKey);
    }

    private static void AssertUnmodeledSettingsKept(string path)
    {
        var text = File.ReadAllText(path);
        var root = TomlSerializer.Deserialize<TomlTable>(text)!;
        Assert.AreEqual("kept", Find(root, "future_setting"), text);
        Assert.AreEqual(42L, Find(root, "future_section", "answer"), text);
        Assert.AreEqual(1234L, Find(root, "plugins", "mcp", "tool_timeout_ms"), text);
        Assert.AreEqual(false, Find(root, "plugins", "mcp", "servers", "files", "enabled"), text);
    }

    private static object? Find(TomlTable table, params string[] path)
    {
        object? current = table;
        foreach (var key in path)
        {
            if (current is not TomlTable currentTable || !currentTable.TryGetValue(key, out current))
            {
                return null;
            }
        }

        return current;
    }

    private sealed class TempConfig : IDisposable
    {
        private TempConfig(string root)
        {
            Root = root;
            var options = new CatalogOptions { GlobalRoot = System.IO.Path.Combine(root, "home") };
            Directory.CreateDirectory(options.GlobalRoot);
            Path = options.ConfigPath;
            Store = new CodeAltaConfigStore(options);
        }

        public string Root { get; }

        public string Path { get; }

        public CodeAltaConfigStore Store { get; }

        public static TempConfig Create(string? content)
        {
            var config = new TempConfig(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAltaConfigStorePluginTests", Guid.NewGuid().ToString("N")));
            if (content is not null)
            {
                File.WriteAllText(config.Path, content);
            }

            return config;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
