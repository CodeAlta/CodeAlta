namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class CodeModeScopesTests
{
    [TestMethod]
    public void TheModel_WinsOverTheProvider_ThenEverySession_ThenOn()
    {
        Assert.AreEqual("only", CodeModeScopes.Resolve("only", "off", "on"));
        Assert.AreEqual("off", CodeModeScopes.Resolve(null, "off", "on"));
        Assert.AreEqual("only", CodeModeScopes.Resolve(null, null, "only"));
        Assert.AreEqual("off", CodeModeScopes.Resolve(null, null, "off"));
        Assert.AreEqual("on", CodeModeScopes.Resolve(null, null, null), "Code Mode is on by default");
        Assert.AreEqual("on", CodeModeScopes.Resolve(" ON ", "sometimes", "off"), "a mode is read as the file writes it");
        Assert.AreEqual("off", CodeModeScopes.Resolve("sometimes", "", "off"), "what is not a mode chooses nothing");
    }

    [TestMethod]
    public void TheModeOfAProviderOrModel_IsSaved_AndNothingElseOfTheProvider()
    {
        using var temp = TempFolder.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        File.WriteAllText(store.ConfigPath, """
            [providers.local]
            type = "openai-chat"
            api_key_env = "LOCAL_KEY"
            sandbox = "strict"

            [providers.local.model_overrides."qwen"]
            context_window = 32000
            """);

        Assert.IsTrue(store.SaveGlobalProviderCodeMode("local", null, "off"));
        Assert.IsTrue(store.SaveGlobalProviderCodeMode("local", "qwen", "only"));
        Assert.IsTrue(store.SaveGlobalProviderCodeMode("local", "llama", "ON"));
        var provider = store.LoadGlobal().Providers!["local"];
        Assert.AreEqual(("off", "only", 32000L, "on"), (provider.CodeMode, provider.ModelOverrides!["qwen"].CodeMode, provider.ModelOverrides["qwen"].ContextWindow, provider.ModelOverrides["llama"].CodeMode));
        Assert.AreEqual(("LOCAL_KEY", "strict"), (provider.ApiKeyEnv, provider.Sandbox));
        StringAssert.Contains(File.ReadAllText(store.ConfigPath), "code_mode = \"off\"");
        Assert.AreEqual(("off", "only", "on", "off", "on"), (store.ResolveCodeMode("local", null), store.ResolveCodeMode("local", "qwen"), store.ResolveCodeMode("LOCAL", "llama"),
            store.ResolveCodeMode("local", "other"), store.ResolveCodeMode("missing", "qwen")));

        Assert.IsTrue(store.SaveGlobalProviderCodeMode("local", "llama", null));
        Assert.IsTrue(store.SaveGlobalProviderCodeMode("local", "qwen", null));
        Assert.IsTrue(store.SaveGlobalProviderCodeMode("local", null, null));
        provider = store.LoadGlobal().Providers!["local"];
        Assert.IsFalse(provider.ModelOverrides!.ContainsKey("llama"), "an override with nothing left goes");
        Assert.AreEqual(32000L, provider.ModelOverrides["qwen"].ContextWindow);
        Assert.IsNull(provider.CodeMode);
        Assert.AreEqual("on", store.ResolveCodeMode("local", "qwen"));
        Assert.IsFalse(store.SaveGlobalProviderCodeMode("missing", null, "on"));
        Assert.ThrowsExactly<ArgumentException>(() => store.SaveGlobalProviderCodeMode("local", null, "sometimes"));
    }

    [TestMethod]
    public void EverySession_IsOnWithoutASetting_OffIsWritten_AndOnRemovesIt()
    {
        using var temp = TempFolder.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.AreEqual("on", store.LoadCodeModeMode());
        Assert.AreEqual("on", store.ResolveCodeMode("claude", "haiku"));

        store.SaveCodeModeMode("off");
        Assert.AreEqual("off", store.LoadCodeModeMode());
        StringAssert.Contains(File.ReadAllText(store.ConfigPath), "mode = \"off\"");
        Assert.AreEqual("off", store.ResolveCodeMode("codex", "gpt-6.1-sol"));

        store.SaveCodeModeMode("on");
        Assert.AreEqual("on", store.LoadCodeModeMode());
        Assert.IsFalse(File.ReadAllText(store.ConfigPath).Contains("[code_mode]", StringComparison.Ordinal), "on is the absence of the setting");
        Assert.ThrowsExactly<ArgumentException>(() => store.SaveCodeModeMode("sometimes"));
    }

    private sealed class TempFolder : IDisposable
    {
        private TempFolder(string path) => Path = path;

        public string Path { get; }

        public static TempFolder Create() => new(Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alta-code-mode-scopes-" + Guid.NewGuid().ToString("N"))).FullName);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
