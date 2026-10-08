using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaConfigStoreCompatibilityTests
{
    // A configuration a newer version wrote: its provider type and two of its settings are unknown here.
    private const string NewerVersionConfig = """
[chat]
default_provider = "future"

[providers.codex]
type = "codex"
model = "gpt-5.5"

[providers.future]
type = "future-provider"
enabled = true
model = "future-large" # the newer version's model
launch_command = "future --serve"

[providers.future.limits]
turns = 12
""";

    private static void WithGlobalConfig(string content, Action<CodeAltaConfigStore, CatalogOptions> test)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), nameof(CodeAltaConfigStoreCompatibilityTests), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempPath);
            var options = new CatalogOptions { GlobalRoot = tempPath };
            File.WriteAllText(options.ConfigPath, content);
            test(new CodeAltaConfigStore(options), options);
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }

    private static void AssertFutureProviderIsUntouched(string content)
    {
        StringAssert.Contains(content, "[providers.future]");
        StringAssert.Contains(content, "type = \"future-provider\"");
        StringAssert.Contains(content, "enabled = true");
        StringAssert.Contains(content, "model = \"future-large\" # the newer version's model");
        StringAssert.Contains(content, "launch_command = \"future --serve\"");
        StringAssert.Contains(content, "[providers.future.limits]");
        StringAssert.Contains(content, "turns = 12");
    }

    [TestMethod]
    public void ProviderOfATypeThisVersionDoesNotKnow_IsSetAside_AndTheFileLoads()
        => WithGlobalConfig(NewerVersionConfig, static (store, options) =>
        {
            // Version 1.2.0 refused this file because of the type 1.3.0 added, and showed nothing but its editor.
            Assert.IsTrue(CodeAltaConfigStore.ValidateGlobalConfigContent(File.ReadAllText(options.ConfigPath), options.ConfigPath).IsValid);

            var document = store.LoadGlobal();
            CollectionAssert.AreEqual(new[] { new CodeAltaUnsupportedProvider("future", "future-provider") }, document.UnsupportedProviders.ToArray());
            CollectionAssert.AreEqual(new[] { "codex" }, document.Providers!.Keys.ToArray());
            CollectionAssert.AreEqual(new[] { "codex" }, store.LoadGlobalProviderDefinitions(includeDisabled: true).Select(static provider => provider.ProviderKey).ToArray());
        });

    [TestMethod]
    public void ProviderOfATypeThisVersionDoesNotKnow_KeepsItsSectionWhateverIsSaved()
        => WithGlobalConfig(NewerVersionConfig, static (store, options) =>
        {
            // Settings > Providers saves the providers it lists: the one it cannot list is not a removed one.
            var codex = store.LoadGlobalProviderDefinitions(includeDisabled: true).Single();
            codex.Model = "gpt-5.5-mini";
            store.SaveGlobalProviderDefinitions([codex]);
            var content = File.ReadAllText(options.ConfigPath);
            AssertFutureProviderIsUntouched(content);
            StringAssert.Contains(content, "model = \"gpt-5.5-mini\"");
            // The default the newer version chose is its own to change.
            StringAssert.Contains(content, "default_provider = \"future\"");

            // So do the other saves, and removing every provider this version knows.
            store.SaveGlobalSkillEnabled("release-notes", enabled: false);
            store.SaveGlobalProviderPreference("codex", "gpt-5.5", reasoningEffort: null);
            AssertFutureProviderIsUntouched(File.ReadAllText(options.ConfigPath));
            store.SaveGlobalProviderDefinitions([]);
            content = File.ReadAllText(options.ConfigPath);
            AssertFutureProviderIsUntouched(content);
            Assert.IsFalse(content.Contains("[providers.codex]", StringComparison.Ordinal), content);
            Assert.AreEqual("future-provider", store.LoadGlobal().UnsupportedProviders.Single().ProviderType);

            // A provider of this version cannot take its key: it would be written into that section.
            var before = File.ReadAllText(options.ConfigPath);
            var taken = new CodeAltaProviderDocument { ProviderKey = "Future", ProviderType = "anthropic", Enabled = false };
            var refused = Assert.ThrowsExactly<InvalidOperationException>(() => store.SaveGlobalProviderDefinitions([taken]));
            StringAssert.Contains(refused.Message, "providers.future");
            Assert.AreEqual(before, File.ReadAllText(options.ConfigPath));
        });

    [TestMethod]
    public void ProviderWithoutAType_OrWrongForItsType_IsStillRefused()
    {
        // Not a provider of another version: a file that is wrong.
        var missing = CodeAltaConfigStore.ValidateGlobalConfigContent("[providers.custom]\nmodel = \"m\"\n", "config.toml");
        Assert.IsFalse(missing.IsValid);
        StringAssert.Contains(missing.Message, "providers.custom type must be one of");

        var wrong = CodeAltaConfigStore.ValidateGlobalConfigContent("[providers.codex]\ntype = \"codex\"\nservice_tier = \"turbo\"\n", "config.toml");
        Assert.IsFalse(wrong.IsValid);
        StringAssert.Contains(wrong.Message, "providers.codex service_tier must be one of");
    }

    [TestMethod]
    public void SaveGlobalDefaultProvider_PreservesIgnoredAcpConfigBlocks()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), nameof(CodeAltaConfigStoreCompatibilityTests), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempPath);
            var options = new CatalogOptions { GlobalRoot = tempPath };
            File.WriteAllText(options.ConfigPath, """
[acp]
legacy = true

[acp.agents.sample]
command = "sample-agent"
args = ["--stdio"]

[chat]
default_provider = "codex"
""");
            var store = new CodeAltaConfigStore(options);

            store.SaveGlobalDefaultProvider("copilot");
            var content = File.ReadAllText(options.ConfigPath);

            StringAssert.Contains(content, "[acp]");
            StringAssert.Contains(content, "legacy = true");
            StringAssert.Contains(content, "[acp.agents.sample]");
            StringAssert.Contains(content, "command = \"sample-agent\"");
            Assert.AreEqual("copilot", store.LoadGlobal().Chat?.DefaultProvider);
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }
}
