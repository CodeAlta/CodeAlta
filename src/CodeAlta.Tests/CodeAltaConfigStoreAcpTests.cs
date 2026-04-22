using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaConfigStoreAcpTests
{
    [TestMethod]
    public void SaveGlobalAcpBackendDefinition_RoundTripsDisabledDefinition()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var definition = new AcpBackendDefinition
        {
            AgentId = "Sample-Agent",
            DisplayName = "Sample Agent",
            Enabled = false,
            RegistryId = "Sample-Agent",
            Command = "npx",
            Arguments = ["--yes", "@sample/agent"],
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SAMPLE_KEY"] = "value",
            },
            UseUnstable = true,
            EnableFilesystem = true,
            EnableTerminal = false,
            EnableElicitation = true,
        };

        store.SaveGlobalAcpBackendDefinition(definition);

        var loaded = store.LoadGlobalAcpBackendDefinition("sample-agent");
        Assert.IsNotNull(loaded);
        Assert.AreEqual("sample-agent", loaded.AgentId);
        Assert.AreEqual(false, loaded.Enabled);
        Assert.AreEqual("Sample Agent", loaded.DisplayName);
        Assert.AreEqual("sample-agent", loaded.RegistryId);
        CollectionAssert.AreEqual(new[] { "--yes", "@sample/agent" }, loaded.Arguments);

        var allDefinitions = store.LoadGlobalAcpBackendDefinitions(includeDisabled: true);
        Assert.AreEqual(1, allDefinitions.Count);

        var enabledOnly = store.LoadGlobalAcpBackendDefinitions();
        Assert.AreEqual(0, enabledOnly.Count);
    }

    [TestMethod]
    public void DeleteGlobalAcpBackendDefinition_RemovesPersistedOverride()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        store.SaveGlobalAcpBackendDefinition(new AcpBackendDefinition
        {
            AgentId = "sample-agent",
            DisplayName = "Sample Agent",
            Command = "npx",
        });

        Assert.IsTrue(store.HasGlobalAcpBackendDefinition("sample-agent"));
        Assert.IsTrue(store.DeleteGlobalAcpBackendDefinition("sample-agent"));
        Assert.IsFalse(store.HasGlobalAcpBackendDefinition("sample-agent"));
        Assert.IsNull(store.LoadGlobalAcpBackendDefinition("sample-agent"));
    }

    [TestMethod]
    public void SaveGlobalAcpBackendDefinition_OmitsDefaultFlags()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        store.SaveGlobalAcpBackendDefinition(new AcpBackendDefinition
        {
            AgentId = "sample-agent",
            DisplayName = "Sample Agent",
            Command = "npx",
            UseUnstable = true,
            EnableFilesystem = true,
            EnableTerminal = true,
            EnableElicitation = false,
            Enabled = true,
        });

        var content = File.ReadAllText(Path.Combine(temp.Path, "config.toml"));
        StringAssert.Contains(content, "[acp.agents.sample-agent]");
        Assert.IsFalse(content.Contains("enabled = true", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("use_unstable = true", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("enable_filesystem = true", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("enable_terminal = true", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("enable_elicitation = false", StringComparison.Ordinal));
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codealta-config-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
