using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaConfigStoreRawApiTests
{
    [TestMethod]
    public void LoadGlobalProviderDefinitions_NormalizesProviderFirstProviders()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [chat]
            default_provider = " OpenRouter "

            [providers.OpenRouter]
            display_name = " OpenRouter "
            type = " openai "
            model = " gpt-5 "
            reasoning_effort = " high "
            api_key_env = " OPENROUTER_API_KEY "
            api_url = " https://openrouter.ai/api/v1 "
            models_dev_provider_id = " OpenRouter "
            single_model_id = " gpt-5 "

            [providers.OpenRouter.extra_body]
            custom_boolean = true
            custom_threshold = 0.75

            [providers.OpenRouter.profile]
            supports_developer_role = false
            supports_store = false
            max_tokens_field_name = " max_tokens "
            reasoning_field_names = [" reasoning_content ", "", "reasoning"]
            reasoning_input_field_name = " reasoning_content "

            [providers.OpenRouter.compaction]
            reserved_output_tokens = 2048
            summary_output_tokens = 768
            target_context_ratio_max = 0.08

            [providers.OpenRouter.model_overrides." gpt-5 "]
            display_name = " GPT-5 "
            description = " flagship "
            context_window = 400000
            output_token_limit = 128000

            [providers.responses]
            display_name = "OpenAI (Responses)"
            type = "responses"
            api_key_env = "CODEALTA_OPENAI_API_KEY"
            api_url = "https://api.openai.com/v1"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual("openrouter", store.GetEffectiveDefaultProvider());
        Assert.IsTrue(providers.ContainsKey("codex"));
        Assert.IsTrue(providers.ContainsKey("copilot"));

        var openRouter = providers["openrouter"];
        Assert.AreEqual("OpenRouter", openRouter.DisplayName);
        Assert.AreEqual("openai-chat", openRouter.ProviderType);
        Assert.AreEqual("gpt-5", openRouter.Model);
        Assert.AreEqual("high", openRouter.ReasoningEffort);
        Assert.AreEqual("OPENROUTER_API_KEY", openRouter.ApiKeyEnv);
        Assert.AreEqual("https://openrouter.ai/api/v1", openRouter.ApiUrl);
        Assert.AreEqual("openrouter", openRouter.ModelsDevProviderId);
        Assert.AreEqual("gpt-5", openRouter.SingleModelId);
        Assert.IsNotNull(openRouter.ExtraBody);
        Assert.AreEqual(true, openRouter.ExtraBody!["custom_boolean"]);
        Assert.AreEqual(0.75d, Convert.ToDouble(openRouter.ExtraBody["custom_threshold"]));
        Assert.IsNotNull(openRouter.Profile);
        Assert.IsFalse(openRouter.Profile!.SupportsDeveloperRole);
        Assert.IsFalse(openRouter.Profile.SupportsStore);
        Assert.AreEqual("max_tokens", openRouter.Profile.MaxTokensFieldName);
        CollectionAssert.AreEqual(
            new[] { "reasoning_content", "reasoning" },
            openRouter.Profile.ReasoningFieldNames);
        Assert.AreEqual("reasoning_content", openRouter.Profile.ReasoningInputFieldName);
        Assert.IsNotNull(openRouter.Compaction);
        Assert.AreEqual(2048, openRouter.Compaction!.ReservedOutputTokens);
        Assert.AreEqual(768, openRouter.Compaction.SummaryOutputTokens);
        Assert.AreEqual(0.08d, openRouter.Compaction.TargetContextRatioMax!.Value, 0.0001d);
        Assert.IsNotNull(openRouter.ModelOverrides);
        Assert.IsTrue(openRouter.ModelOverrides!.TryGetValue("gpt-5", out var modelOverride));
        Assert.IsNotNull(modelOverride);
        Assert.AreEqual("GPT-5", modelOverride.DisplayName);
        Assert.AreEqual("flagship", modelOverride.Description);
        Assert.AreEqual(400000L, modelOverride.ContextWindow);
        Assert.AreEqual(128000L, modelOverride.OutputTokenLimit);

        var responses = providers["responses"];
        Assert.AreEqual("openai-responses", responses.ProviderType);
        Assert.AreEqual("OpenAI (Responses)", responses.DisplayName);
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_InvalidCompactionThreshold_Throws()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"

            [providers.openai.compaction]
            trigger_threshold = 0.5
            target_threshold = 0.5
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_UsesUpdatedCompactionDefaults()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions();
        var openAi = providers.Single(static provider => string.Equals(provider.ProviderKey, "openai", StringComparison.OrdinalIgnoreCase));
        var compaction = openAi.Compaction;
        Assert.IsNotNull(compaction);
        Assert.AreEqual(0.85d, compaction!.TriggerThreshold!.Value, 0.0001d);
        Assert.AreEqual(0.50d, compaction.TargetThreshold!.Value, 0.0001d);
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_AnthropicAndVertexHonorDisabledFiltering()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.Anthropic]
            display_name = " Anthropic "
            type = " anthropic "
            api_key_env = " ANTHROPIC_API_KEY "
            single_model_id = " MiniMax-M2.7 "

            [providers.VertexWest]
            enabled = false
            type = " vertex-ai "
            project = " sample-project "
            location = " europe-west4 "
            single_model_id = " gemini-2.5-pro "
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var enabledProviders = store.LoadGlobalProviderDefinitions();
        Assert.IsTrue(enabledProviders.Any(static provider => provider.ProviderKey == "anthropic"));
        Assert.IsFalse(enabledProviders.Any(static provider => provider.ProviderKey == "vertexwest"));

        var allProviders = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual("Anthropic", allProviders["anthropic"].DisplayName);
        Assert.AreEqual("ANTHROPIC_API_KEY", allProviders["anthropic"].ApiKeyEnv);
        Assert.AreEqual("MiniMax-M2.7", allProviders["anthropic"].SingleModelId);
        Assert.AreEqual("vertex-ai", allProviders["vertexwest"].ProviderType);
        Assert.AreEqual("sample-project", allProviders["vertexwest"].Project);
        Assert.AreEqual("europe-west4", allProviders["vertexwest"].Location);
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexSubscriptionAppliesDefaults()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex_subscription]
            type = "openai-codex-subscription"
            model = " gpt-5.3-codex "
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        var provider = providers["codex_subscription"];
        Assert.AreEqual("openai-codex-subscription", provider.ProviderType);
        Assert.AreEqual("Codex (ChatGPT subscription)", provider.DisplayName);
        Assert.AreEqual("gpt-5.3-codex", provider.Model);
        Assert.AreEqual("https://chatgpt.com/backend-api/codex", provider.ApiUrl);
        Assert.AreEqual("codealta_oauth", provider.AuthSource);
        Assert.AreEqual(1, provider.MaxConcurrentRequests);
        Assert.AreEqual("medium", provider.TextVerbosity);
        Assert.IsTrue(provider.IncludeEncryptedReasoning);
        Assert.AreEqual("codex_endpoint_with_static_fallback", provider.ModelDiscovery);
        Assert.IsTrue(provider.SendResponsesBetaHeader);
        Assert.IsFalse(provider.SendInstallationId);
        Assert.AreEqual("codealta_state", provider.InstallationIdSource);
        Assert.IsTrue(provider.Experimental);
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexSubscriptionNormalizesExplicitFields()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex_subscription]
            enabled = true
            display_name = " Codex Sub "
            type = "openai-codex-subscription"
            model = " gpt-5.4 "
            api_url = " http://localhost:5111/backend-api/codex "
            auth_source = " CODEX_AUTH_FILE_READONLY "
            account_id = " acct_123 "
            max_concurrent_requests = 2
            text_verbosity = " HIGH "
            include_encrypted_reasoning = false
            model_discovery = " STATIC "
            send_responses_beta_header = false
            send_installation_id = true
            installation_id_source = " CODEX_HOME_READONLY "
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var provider = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .Single(static provider => provider.ProviderKey == "codex_subscription");

        Assert.AreEqual("Codex Sub", provider.DisplayName);
        Assert.AreEqual("http://localhost:5111/backend-api/codex", provider.ApiUrl);
        Assert.AreEqual("codex_auth_file_readonly", provider.AuthSource);
        Assert.AreEqual("acct_123", provider.AccountId);
        Assert.AreEqual(2, provider.MaxConcurrentRequests);
        Assert.AreEqual("high", provider.TextVerbosity);
        Assert.IsFalse(provider.IncludeEncryptedReasoning);
        Assert.AreEqual("static", provider.ModelDiscovery);
        Assert.IsFalse(provider.SendResponsesBetaHeader);
        Assert.IsTrue(provider.SendInstallationId);
        Assert.AreEqual("codex_home_readonly", provider.InstallationIdSource);
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexSubscriptionRejectsApiKeyFields()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex_subscription]
            type = "openai-codex-subscription"
            model = "gpt-5.3-codex"
            api_key_env = "OPENAI_API_KEY"
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "uses ChatGPT OAuth");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexSubscriptionRejectsExtraBody()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex_subscription]
            type = "openai-codex-subscription"
            model = "gpt-5.3-codex"
            experimental = true

            [providers.codex_subscription.extra_body]
            suspicious = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "extra_body");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexSubscriptionRejectsMissingExperimentalOptIn()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex_subscription]
            type = "openai-codex-subscription"
            model = "gpt-5.3-codex"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "experimental = true");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexSubscriptionRejectsMissingModel()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex_subscription]
            type = "openai-codex-subscription"
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "model is required");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexSubscriptionRejectsNonHttpsNonLocalEndpoint()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex_subscription]
            type = "openai-codex-subscription"
            model = "gpt-5.3-codex"
            api_url = "http://example.com/backend-api/codex"
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "HTTPS");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexSubscriptionRejectsInvalidEnums()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex_subscription]
            type = "openai-codex-subscription"
            model = "gpt-5.3-codex"
            text_verbosity = "verbose"
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "text_verbosity");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_ReservedProvidersReceiveDefaults()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex]
            model = "gpt-5.4"
            reasoning_effort = "high"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual("codex", providers["codex"].ProviderType);
        Assert.AreEqual("Codex", providers["codex"].DisplayName);
        Assert.IsFalse(providers["codex"].Enabled);
        Assert.AreEqual("copilot", providers["copilot"].ProviderType);
        Assert.AreEqual("GitHub Copilot", providers["copilot"].DisplayName);
        Assert.IsFalse(providers["copilot"].Enabled);
    }

    [TestMethod]
    public void SaveGlobalProviderDefinitions_PersistsEnabledProviders()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        store.SaveGlobalProviderDefinitions(
        [
            new CodeAltaProviderDocument
            {
                ProviderKey = "codex",
                Enabled = true,
                ProviderType = "codex",
            },
            new CodeAltaProviderDocument
            {
                ProviderKey = "openrouter",
                Enabled = true,
                ProviderType = "openai-chat",
                ApiKeyEnv = "OPENROUTER_API_KEY",
                ApiUrl = "https://openrouter.ai/api/v1",
            },
        ]);

        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(providers["codex"].Enabled);
        Assert.IsTrue(providers["openrouter"].Enabled);
    }

    [TestMethod]
    public void SaveGlobalProviderDefinitions_InvalidEnabledProvider_Throws()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        Assert.ThrowsExactly<InvalidOperationException>(
            () => store.SaveGlobalProviderDefinitions(
            [
                new CodeAltaProviderDocument
                {
                    ProviderKey = "sample",
                    Enabled = true,
                    ProviderType = "openai-chat",
                },
            ]));
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_InvalidReservedProviderType_Throws()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex]
            type = "openai-chat"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_LegacySchemaIsRejected()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [backends.codex]
            model = "gpt-5.4"

            [providers.openai]
            provider = "openai"
            wire_api = "chat"
            base_uri = "https://api.openai.com/v1"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "config-store-provider-tests",
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
