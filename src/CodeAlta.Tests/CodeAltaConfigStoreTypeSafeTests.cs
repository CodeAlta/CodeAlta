using CodeAlta.Catalog;

namespace CodeAlta.Tests;

/// <summary>Tests of the decision providers (<c>type = "typesafe"</c>) in the configuration: read, checked, saved, never the default.</summary>
[TestClass]
public sealed class CodeAltaConfigStoreTypeSafeTests
{
    [TestMethod]
    [DataRow("typesafe")]
    [DataRow(" TypeSafe ")]
    [DataRow("typesafe-ai")]
    [DataRow("typesafe_ai")]
    [DataRow("jev")]
    [DataRow("JEV")]
    public void TheTypeAndItsAliases_AreReadAsTypeSafe(string type)
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(Path.Combine(temp.Path, "config.toml"), $$"""
            [providers.decide]
            type = "{{type}}"
            api_key_env = "CODEALTA_TYPESAFE_API_KEY"
            model = " jev-latest "
            api_url = "https://api.typesafe.ai"
            """);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var provider = store.LoadGlobalProviderDefinitions(includeDisabled: true).Single(static provider => provider.ProviderKey == "decide");

        Assert.AreEqual("typesafe", provider.ProviderType);
        Assert.AreEqual("jev-latest", provider.Model);
        Assert.AreEqual("CODEALTA_TYPESAFE_API_KEY", provider.ApiKeyEnv);
        Assert.IsTrue(CodeAltaConfigStore.IsDecisionProviderType(provider.ProviderType));
        Assert.AreEqual("typesafe", CodeAltaConfigStore.CreateProviderTypeDefaults("jev").ProviderType);
    }

    [TestMethod]
    public void IsDecisionProviderType_IsTrueForTheTypeAndItsAliases()
    {
        // A form sends the type as the user wrote it: its aliases are a decision provider too.
        foreach (var type in new[] { "typesafe", "TypeSafe", " jev ", "typesafe-ai", "typesafe_ai" })
            Assert.IsTrue(CodeAltaConfigStore.IsDecisionProviderType(type), type);
        foreach (var type in new[] { null, "", "openai-chat", "anthropic", "codex", "claude-code", "mistral" })
            Assert.IsFalse(CodeAltaConfigStore.IsDecisionProviderType(type), type ?? "(null)");
    }

    [TestMethod]
    [DataRow("reasoning_effort = \"high\"", "reasoning_effort")]
    [DataRow("organization_id = \"org\"", "organization_id")]
    [DataRow("project_id = \"proj\"", "project_id")]
    [DataRow("project = \"sample\"", "project")]
    [DataRow("location = \"europe-west4\"", "location")]
    [DataRow("single_model_id = \"jev-latest\"", "single_model_id")]
    [DataRow("models_dev_provider_id = \"typesafe\"", "models_dev_provider_id")]
    [DataRow("[providers.decide.extra_body]\nflag = true", "extra_body")]
    [DataRow("[providers.decide.request.headers]\nX-Feature = \"on\"", "request")]
    [DataRow("[providers.decide.model_request.jev-latest]\nremove_headers = [\"X-Feature\"]", "model_request")]
    [DataRow("[providers.decide.profile]\nsupports_store = false", "profile")]
    public void TheFieldsOfAChatRequest_AreRefused(string setting, string field)
    {
        // A decision model is never sent a chat request: nothing that shapes one applies to it.
        using var temp = TempDirectory.Create();
        File.WriteAllText(Path.Combine(temp.Path, "config.toml"), $"""
            [providers.decide]
            type = "typesafe"
            api_key_env = "CODEALTA_TYPESAFE_API_KEY"
            {setting}
            """);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var failure = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));

        StringAssert.Contains(failure.InnerException?.Message, $"'{field}'");
        StringAssert.Contains(failure.InnerException?.Message, "typesafe");
    }

    [TestMethod]
    [DataRow("http://api.typesafe.ai", false)]
    [DataRow("http://proxy.example/typesafe", false)]
    [DataRow("https://proxy.example/typesafe", true)]
    [DataRow("http://localhost:8080", true)]
    [DataRow("http://127.0.0.1:9999/typesafe", true)]
    public void TheEndpoint_UsesHttps_ExceptOnThisComputer(string url, bool accepted)
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(Path.Combine(temp.Path, "config.toml"), $$"""
            [providers.decide]
            type = "typesafe"
            api_key_env = "CODEALTA_TYPESAFE_API_KEY"
            api_url = "{{url}}"
            """);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        if (accepted)
        {
            Assert.AreEqual(url, store.LoadGlobalProviderDefinitions(includeDisabled: true).Single().ApiUrl);
        }
        else
        {
            var failure = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
            StringAssert.Contains(failure.InnerException?.Message, "HTTPS");
        }
    }

    [TestMethod]
    public void AnEnabledDecisionProvider_IsSavedOnlyWithAKeyOrItsVariable()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => store.SaveGlobalProviderDefinitions([TypeSafe("decide", enabled: true)]));
        StringAssert.Contains(failure.Message, "providers.decide requires api_key or api_key_env when enabled");

        store.SaveGlobalProviderDefinitions([TypeSafe("decide", enabled: false)]);
        Assert.AreEqual(false, store.LoadGlobalProviderDefinitions(includeDisabled: true).Single().Enabled);

        store.SaveGlobalProviderDefinitions([TypeSafe("decide", enabled: true, apiKeyEnv: "CODEALTA_TYPESAFE_API_KEY")]);
        store.SaveGlobalProviderDefinitions([TypeSafe("decide", enabled: true, apiKey: "secret")]);
        var saved = store.LoadGlobalProviderDefinitions().Single();
        Assert.AreEqual("typesafe", saved.ProviderType);
        Assert.AreEqual("secret", saved.ApiKey);
    }

    [TestMethod]
    public void TheTemplate_HasADisabledTypeSafeProvider()
    {
        var typesafe = CodeAltaConfigStore.LoadDefaultProviderDefinitions().Single(static definition => definition.ProviderKey == "typesafe");
        Assert.AreEqual("typesafe", typesafe.ProviderType);
        Assert.AreEqual(false, typesafe.Enabled);
        Assert.AreEqual("jev-latest", typesafe.Model);
        Assert.AreEqual("CODEALTA_TYPESAFE_API_KEY", typesafe.ApiKeyEnv);
        Assert.AreEqual("TypeSafe Jev", typesafe.DisplayName);

        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.IsTrue(store.EnsureGlobalConfigExists());
        var content = File.ReadAllText(Path.Combine(temp.Path, "config.toml"));
        StringAssert.Contains(content, "[providers.typesafe]");
        StringAssert.Contains(content, "api_key_env = \"CODEALTA_TYPESAFE_API_KEY\"");
        var written = store.LoadGlobalProviderDefinitions(includeDisabled: true).Single(static definition => definition.ProviderKey == "typesafe");
        Assert.AreEqual("typesafe", written.ProviderType);
        Assert.AreEqual(false, written.Enabled);
    }

    [TestMethod]
    public void AnInvalidDefault_IsReplacedByAChatProvider_NeverByADecisionProvider()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        store.SaveGlobalDefaultProvider("gone");

        // The decision provider comes first: a fallback that took the first enabled provider would choose it.
        store.SaveGlobalProviderDefinitions(
        [
            TypeSafe("decide", enabled: true, apiKey: "secret"),
            Chat("chat"),
        ]);

        Assert.AreEqual("chat", store.GetEffectiveDefaultProvider());
    }

    [TestMethod]
    public void ADefaultThatNamesADecisionProvider_IsReplaced()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        store.SaveGlobalDefaultProvider("decide");

        store.SaveGlobalProviderDefinitions([TypeSafe("decide", enabled: true, apiKey: "secret"), Chat("chat")]);

        Assert.AreEqual("chat", store.GetEffectiveDefaultProvider());
    }

    [TestMethod]
    public void WithOnlyADecisionProviderEnabled_ThereIsNoDefault()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        store.SaveGlobalDefaultProvider("gone");

        store.SaveGlobalProviderDefinitions([TypeSafe("decide", enabled: true, apiKey: "secret"), Chat("chat", enabled: false)]);

        Assert.IsNull(store.GetEffectiveDefaultProvider());
    }

    private static CodeAltaProviderDocument TypeSafe(string key, bool enabled, string? apiKey = null, string? apiKeyEnv = null)
        => new() { ProviderKey = key, ProviderType = "typesafe", Enabled = enabled, ApiKey = apiKey, ApiKeyEnv = apiKeyEnv, Model = "jev-latest" };

    private static CodeAltaProviderDocument Chat(string key, bool enabled = true)
        => new() { ProviderKey = key, ProviderType = "openai-chat", Enabled = enabled, ApiKeyEnv = "CHAT_KEY", ApiUrl = "http://127.0.0.1:9999/v1" };

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "config-store-typesafe-tests", Guid.NewGuid().ToString("N"));
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
