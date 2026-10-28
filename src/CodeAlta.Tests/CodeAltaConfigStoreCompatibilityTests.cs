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
    public void Validation_WarnsOnlyOfTheTypesNothingKnows_NotThoseOfARunningPlugin()
    {
        const string content = NewerVersionConfig + """

[providers.cursor]
type = "acp"
command = "cursor-agent"
""";

        // Without the plugins, both types are unknown to this version.
        var bare = CodeAltaConfigStore.ValidateGlobalConfigContent(content);
        Assert.IsTrue(bare.IsValid);
        StringAssert.Contains(bare.Warning, "providers.cursor (type \"acp\")");

        // A plugin adds "acp": only the type of a newer version is still warned about.
        var known = CodeAltaConfigStore.ValidateGlobalConfigContent(content, null, static type => type == "acp");
        Assert.IsTrue(known.IsValid);
        StringAssert.Contains(known.Warning, "providers.future (type \"future-provider\")");
        Assert.IsFalse(known.Warning!.Contains("cursor", StringComparison.Ordinal), known.Warning);

        // Every type known: no warning at all.
        Assert.IsNull(CodeAltaConfigStore.ValidateGlobalConfigContent(content, null, static _ => true).Warning);
    }

    [TestMethod]
    public void ExternalProviders_GiveTheValuesOfTheirSections()
        => WithGlobalConfig(NewerVersionConfig, static (store, _) =>
        {
            var provider = store.LoadGlobalExternalProviders().Single();

            Assert.AreEqual("future", provider.ProviderKey);
            Assert.AreEqual("future-provider", provider.ProviderType);
            Assert.AreEqual("future --serve", provider.Values["launch_command"]);
            Assert.AreEqual(true, provider.Values["enabled"]);
            var limits = (IReadOnlyDictionary<string, object?>)provider.Values["limits"]!;
            Assert.AreEqual(12L, limits["turns"]);
        });

    [TestMethod]
    public void ExternalProvider_IsWrittenAndRemovedWithoutTouchingTheOthers()
        => WithGlobalConfig(NewerVersionConfig, static (store, options) =>
        {
            store.SaveGlobalExternalProvider("Copilot-Acp", "ACP", new Dictionary<string, object?>
            {
                ["display_name"] = "Copilot",
                ["agent"] = "github-copilot-cli",
                ["arguments"] = new object?[] { "--acp" },
                ["env"] = new Dictionary<string, object?> { ["DEBUG"] = "1" },
                ["enabled"] = false,
                ["type"] = "ignored",
            });

            var content = File.ReadAllText(options.ConfigPath);
            AssertFutureProviderIsUntouched(content);
            StringAssert.Contains(content, "[providers.copilot-acp]");
            StringAssert.Contains(content, "type = \"acp\"");
            var written = store.LoadGlobalExternalProviders().Single(static provider => provider.ProviderKey == "copilot-acp");
            Assert.AreEqual("acp", written.ProviderType);
            CollectionAssert.AreEqual(new object?[] { "--acp" }, ((IEnumerable<object?>)written.Values["arguments"]!).ToArray());
            Assert.AreEqual("1", ((IReadOnlyDictionary<string, object?>)written.Values["env"]!)["DEBUG"]);
            Assert.AreEqual(false, written.Values["enabled"]);
            // The built-in providers do not see it.
            CollectionAssert.AreEqual(new[] { "codex" }, store.LoadGlobalProviderDefinitions(includeDisabled: true).Select(static provider => provider.ProviderKey).ToArray());

            // Written again in its place, with new values.
            store.SaveGlobalExternalProvider("copilot-acp", "acp", new Dictionary<string, object?> { ["agent"] = "gemini" });
            Assert.AreEqual("gemini", store.LoadGlobalExternalProviders().Single(static provider => provider.ProviderKey == "copilot-acp").Values["agent"]);

            Assert.IsTrue(store.RemoveGlobalExternalProvider("copilot-acp"));
            Assert.IsFalse(store.RemoveGlobalExternalProvider("copilot-acp"));
            Assert.IsFalse(store.RemoveGlobalExternalProvider("codex"));
            content = File.ReadAllText(options.ConfigPath);
            AssertFutureProviderIsUntouched(content);
            StringAssert.Contains(content, "[providers.codex]");
            Assert.IsFalse(content.Contains("copilot-acp", StringComparison.Ordinal), content);
        });

    [TestMethod]
    public void ExternalProvider_RefusesABuiltInType()
        => WithGlobalConfig(NewerVersionConfig, static (store, _) =>
        {
            Assert.ThrowsExactly<ArgumentException>(() => store.SaveGlobalExternalProvider("x", "claude-code", new Dictionary<string, object?>()));
            Assert.ThrowsExactly<InvalidOperationException>(() => store.SaveGlobalExternalProvider("codex", "acp", new Dictionary<string, object?>()));
        });

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
