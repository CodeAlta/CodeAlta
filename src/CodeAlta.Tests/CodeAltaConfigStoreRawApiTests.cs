using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaConfigStoreRawApiTests
{
    [TestMethod]
    public void LoadGlobalOpenAIProviderDefinitions_NormalizesKeysAndProfileOverrides()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [raw_api.compaction]
            trigger_threshold = 0.81
            reserved_overhead_tokens = 1024

            [raw_api.openai.providers.OpenRouter]
            display_name = " OpenRouter "
            api_key_env = " OPENROUTER_API_KEY "
            base_uri = " https://openrouter.ai/api/v1 "
            models_dev_provider_id = " OpenRouter "
            enable_responses = false
            default_chat = true

            [raw_api.openai.providers.OpenRouter.profile]
            supports_developer_role = false
            supports_store = false
            max_tokens_field_name = " max_tokens "
            reasoning_field_names = [" reasoning_content ", "", "reasoning"]

            [raw_api.openai.providers.OpenRouter.compaction]
            reserved_output_tokens = 2048

            [raw_api.openai.providers.OpenRouter.model_overrides." gpt-5 "]
            display_name = " GPT-5 "
            description = " flagship "
            context_window = 400000
            output_token_limit = 128000
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var providers = store.LoadGlobalOpenAIProviderDefinitions();
        Assert.AreEqual(1, providers.Count);
        Assert.AreEqual("openrouter", providers[0].ProviderKey);
        Assert.AreEqual("OpenRouter", providers[0].DisplayName);
        Assert.AreEqual("OPENROUTER_API_KEY", providers[0].ApiKeyEnv);
        Assert.AreEqual("https://openrouter.ai/api/v1", providers[0].BaseUri);
        Assert.AreEqual("openrouter", providers[0].ModelsDevProviderId);
        Assert.IsFalse(providers[0].EnableResponses);
        Assert.IsTrue(providers[0].EnableChat);
        Assert.IsTrue(providers[0].DefaultChat);
        var profile = providers[0].Profile;
        Assert.IsNotNull(profile);
        Assert.IsFalse(profile!.SupportsDeveloperRole);
        Assert.IsFalse(profile.SupportsStore);
        Assert.AreEqual("max_tokens", profile.MaxTokensFieldName);
        CollectionAssert.AreEqual(
            new[] { "reasoning_content", "reasoning" },
            profile.ReasoningFieldNames);
        var compaction = providers[0].Compaction;
        Assert.IsNotNull(compaction);
        Assert.IsTrue(compaction!.Enabled);
        Assert.AreEqual(0.81d, compaction.TriggerThreshold!.Value, 0.0001d);
        Assert.AreEqual(0.50d, compaction.TargetThreshold!.Value, 0.0001d);
        Assert.AreEqual(2048, compaction.ReservedOutputTokens);
        Assert.AreEqual(1024, compaction.ReservedOverheadTokens);
        Assert.IsTrue(compaction.KeepLastUserMessage);
        Assert.IsTrue(compaction.AllowSplitTurn);
        var modelOverrides = providers[0].ModelOverrides;
        Assert.IsNotNull(modelOverrides);
        Assert.IsTrue(modelOverrides!.TryGetValue("gpt-5", out var modelOverride));
        Assert.IsNotNull(modelOverride);
        Assert.AreEqual("GPT-5", modelOverride.DisplayName);
        Assert.AreEqual("flagship", modelOverride.Description);
        Assert.AreEqual(400000L, modelOverride.ContextWindow);
        Assert.AreEqual(128000L, modelOverride.OutputTokenLimit);
    }

    [TestMethod]
    public void LoadGlobalOpenAIProviderDefinitions_InvalidCompactionThreshold_Throws()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [raw_api.compaction]
            trigger_threshold = 0.5
            target_threshold = 0.5

            [raw_api.openai.providers.openai]
            api_key_env = "OPENAI_API_KEY"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalOpenAIProviderDefinitions(includeDisabled: true));
    }

    [TestMethod]
    public void LoadGlobalAnthropicAndGoogleProviderDefinitions_HonorDisabledFiltering()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [raw_api.anthropic.providers.Anthropic]
            display_name = " Anthropic "
            api_key_env = " ANTHROPIC_API_KEY "
            is_default = true

            [raw_api.google_genai.providers.VertexWest]
            enabled = false
            use_vertex_ai = true
            project = " sample-project "
            location = " europe-west4 "
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var anthropicProviders = store.LoadGlobalAnthropicProviderDefinitions();
        Assert.AreEqual(1, anthropicProviders.Count);
        Assert.AreEqual("anthropic", anthropicProviders[0].ProviderKey);
        Assert.AreEqual("Anthropic", anthropicProviders[0].DisplayName);
        Assert.AreEqual("ANTHROPIC_API_KEY", anthropicProviders[0].ApiKeyEnv);
        Assert.IsTrue(anthropicProviders[0].IsDefault);

        var enabledGoogleProviders = store.LoadGlobalGoogleGenAIProviderDefinitions();
        Assert.AreEqual(0, enabledGoogleProviders.Count);

        var allGoogleProviders = store.LoadGlobalGoogleGenAIProviderDefinitions(includeDisabled: true);
        Assert.AreEqual(1, allGoogleProviders.Count);
        Assert.AreEqual("vertexwest", allGoogleProviders[0].ProviderKey);
        Assert.IsTrue(allGoogleProviders[0].UseVertexAI);
        Assert.AreEqual("sample-project", allGoogleProviders[0].Project);
        Assert.AreEqual("europe-west4", allGoogleProviders[0].Location);
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "config-store-raw-api-tests",
                Guid.NewGuid().ToString("N"));
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
