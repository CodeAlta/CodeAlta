using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaConfigStoreRawApiTests
{
    [TestMethod]
    public void LoadGlobalRawApiProviderDefinitions_NormalizesUnifiedProvidersAndOpenAIProjection()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [raw_api.compaction]
            trigger_threshold = 0.81
            reserved_overhead_tokens = 1024
            summary_input_tokens = 32000
            reasoning_mode = "summary_only"

            [providers.OpenRouter]
            display_name = " OpenRouter "
            provider = " OpenAI "
            api_key_env = " OPENROUTER_API_KEY "
            base_uri = " https://openrouter.ai/api/v1 "
            models_dev_provider_id = " OpenRouter "
            single_model_id = " gpt-5 "
            is_default = true

            [providers.OpenRouter.extra_body]
            custom_boolean = true
            custom_threshold = 0.75

            [providers.OpenRouter.profile]
            supports_developer_role = false
            supports_store = false
            max_tokens_field_name = " max_tokens "
            reasoning_field_names = [" reasoning_content ", "", "reasoning"]

            [providers.OpenRouter.compaction]
            reserved_output_tokens = 2048
            summary_output_tokens = 768
            target_context_ratio_max = 0.08

            [providers.OpenRouter.model_overrides." gpt-5 "]
            display_name = " GPT-5 "
            description = " flagship "
            context_window = 400000
            output_token_limit = 128000
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var rawProviders = store.LoadGlobalRawApiProviderDefinitions();
        Assert.AreEqual(1, rawProviders.Count);
        Assert.AreEqual("openrouter", rawProviders[0].ProviderKey);
        Assert.AreEqual("OpenRouter", rawProviders[0].DisplayName);
        Assert.AreEqual("openai", rawProviders[0].Provider);
        Assert.AreEqual("chat", rawProviders[0].WireApi);
        Assert.IsTrue(rawProviders[0].IsDefault);

        var providers = store.LoadGlobalOpenAIProviderDefinitions();
        Assert.AreEqual(1, providers.Count);
        Assert.AreEqual("openrouter", providers[0].ProviderKey);
        Assert.AreEqual("OpenRouter", providers[0].DisplayName);
        Assert.AreEqual("OPENROUTER_API_KEY", providers[0].ApiKeyEnv);
        Assert.AreEqual("https://openrouter.ai/api/v1", providers[0].BaseUri);
        Assert.AreEqual("openrouter", providers[0].ModelsDevProviderId);
        Assert.AreEqual("gpt-5", providers[0].SingleModelId);
        var extraBody = providers[0].ExtraBody;
        Assert.IsNotNull(extraBody);
        Assert.AreEqual(true, extraBody!["custom_boolean"]);
        Assert.AreEqual(0.75d, Convert.ToDouble(extraBody["custom_threshold"]));
        Assert.IsFalse(providers[0].EnableResponses);
        Assert.IsTrue(providers[0].EnableChat);
        Assert.IsTrue(providers[0].DefaultChat);
        Assert.IsFalse(providers[0].DefaultResponses);
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
        Assert.AreEqual(32000, compaction.SummaryInputTokens);
        Assert.AreEqual(768, compaction.SummaryOutputTokens);
        Assert.AreEqual(0.03d, compaction.TargetContextRatioIdeal!.Value, 0.0001d);
        Assert.AreEqual(0.08d, compaction.TargetContextRatioMax!.Value, 0.0001d);
        Assert.AreEqual("summary_only", compaction.ReasoningMode);
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

            [providers.openai]
            provider = "openai"
            api_key_env = "OPENAI_API_KEY"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalOpenAIProviderDefinitions(includeDisabled: true));
    }

    [TestMethod]
    public void LoadGlobalOpenAIProviderDefinitions_UsesUpdatedCompactionDefaults()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openai]
            provider = "openai"
            api_key_env = "OPENAI_API_KEY"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var providers = store.LoadGlobalOpenAIProviderDefinitions();
        Assert.AreEqual(1, providers.Count);
        var compaction = providers[0].Compaction;
        Assert.IsNotNull(compaction);
        Assert.AreEqual(0.85d, compaction!.TriggerThreshold!.Value, 0.0001d);
        Assert.AreEqual(0.50d, compaction.TargetThreshold!.Value, 0.0001d);
    }

    [TestMethod]
    public void LoadGlobalAnthropicAndGoogleProviderDefinitions_HonorDisabledFiltering()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.Anthropic]
            display_name = " Anthropic "
            provider = " anthropic "
            api_key_env = " ANTHROPIC_API_KEY "
            single_model_id = " MiniMax-M2.7 "
            is_default = true

            [providers.VertexWest]
            enabled = false
            provider = " google "
            use_vertex_ai = true
            project = " sample-project "
            location = " europe-west4 "
            single_model_id = " gemini-2.5-pro "
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var anthropicProviders = store.LoadGlobalAnthropicProviderDefinitions();
        Assert.AreEqual(1, anthropicProviders.Count);
        Assert.AreEqual("anthropic", anthropicProviders[0].ProviderKey);
        Assert.AreEqual("Anthropic", anthropicProviders[0].DisplayName);
        Assert.AreEqual("ANTHROPIC_API_KEY", anthropicProviders[0].ApiKeyEnv);
        Assert.AreEqual("MiniMax-M2.7", anthropicProviders[0].SingleModelId);
        Assert.IsTrue(anthropicProviders[0].IsDefault);

        var enabledGoogleProviders = store.LoadGlobalGoogleGenAIProviderDefinitions();
        Assert.AreEqual(0, enabledGoogleProviders.Count);

        var allGoogleProviders = store.LoadGlobalGoogleGenAIProviderDefinitions(includeDisabled: true);
        Assert.AreEqual(1, allGoogleProviders.Count);
        Assert.AreEqual("vertexwest", allGoogleProviders[0].ProviderKey);
        Assert.IsTrue(allGoogleProviders[0].UseVertexAI);
        Assert.AreEqual("sample-project", allGoogleProviders[0].Project);
        Assert.AreEqual("europe-west4", allGoogleProviders[0].Location);
        Assert.AreEqual("gemini-2.5-pro", allGoogleProviders[0].SingleModelId);
    }

    [TestMethod]
    public void LoadGlobalOpenAIProviderDefinitions_LegacyNestedProvidersRemainSupported()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [raw_api.openai.providers.myresponses]
            display_name = "OpenAI (Responses)"
            api_key_env = "CODEALTA_OPENAI_API_KEY"
            enable_responses = true
            enable_chat = false
            default_responses = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var providers = store.LoadGlobalOpenAIProviderDefinitions();
        Assert.AreEqual(1, providers.Count);
        Assert.AreEqual("myresponses", providers[0].ProviderKey);
        Assert.IsTrue(providers[0].EnableResponses);
        Assert.IsFalse(providers[0].EnableChat);
        Assert.IsTrue(providers[0].DefaultResponses);
    }

    [TestMethod]
    public void LoadGlobalRawApiProviderDefinitions_InvalidWireApi_Throws()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.badprovider]
            provider = "openai"
            wire_api = "messages"
            api_key_env = "OPENAI_API_KEY"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalRawApiProviderDefinitions(includeDisabled: true));
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
