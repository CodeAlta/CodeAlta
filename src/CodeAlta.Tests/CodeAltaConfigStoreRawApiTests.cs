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
            protocol_trace = true
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
            ratio = 0.95
            summary_output_ratio = 0.10

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
        Assert.IsTrue(providers.ContainsKey("codex_cli"));
        Assert.IsTrue(providers.ContainsKey("copilot_cli"));

        var openRouter = providers["openrouter"];
        Assert.AreEqual("OpenRouter", openRouter.DisplayName);
        Assert.AreEqual("openai-chat", openRouter.ProviderType);
        Assert.AreEqual("gpt-5", openRouter.Model);
        Assert.AreEqual("high", openRouter.ReasoningEffort);
        Assert.AreEqual("OPENROUTER_API_KEY", openRouter.ApiKeyEnv);
        Assert.AreEqual("https://openrouter.ai/api/v1", openRouter.ApiUrl);
        Assert.IsTrue(openRouter.ProtocolTrace);
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
        Assert.AreEqual(0.95d, openRouter.Compaction!.Ratio!.Value, 0.0001d);
        Assert.AreEqual(0.10d, openRouter.Compaction.SummaryOutputRatio!.Value, 0.0001d);
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
    public void LoadGlobalProviderDefinitions_InvalidCompactionRatio_Throws()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"

            [providers.openai.compaction]
            ratio = 1.5
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_InvalidCompactionSummaryOutputRatio_Throws()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"

            [providers.openai.compaction]
            summary_output_ratio = 0.60
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
        Assert.AreEqual(0.95d, compaction!.Ratio!.Value, 0.0001d);
        Assert.AreEqual(0.10d, compaction.SummaryOutputRatio!.Value, 0.0001d);
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
            [providers.codex]
            type = "codex"
            model = " gpt-5.3-codex "
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        var provider = providers["codex"];
        Assert.AreEqual("codex", provider.ProviderType);
        Assert.AreEqual("Codex", provider.DisplayName);
        Assert.AreEqual("gpt-5.3-codex", provider.Model);
        Assert.AreEqual("https://chatgpt.com/backend-api/codex", provider.ApiUrl);
        Assert.AreEqual("codealta_oauth", provider.AuthSource);
        Assert.AreEqual(16, provider.MaxConcurrentRequests);
        Assert.AreEqual("medium", provider.TextVerbosity);
        Assert.IsTrue(provider.IncludeEncryptedReasoning);
        Assert.AreEqual("static", provider.ModelDiscovery);
        Assert.AreEqual("websocket_with_http_fallback", provider.ResponseTransport);
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
            [providers.codex]
            enabled = true
            display_name = " Codex Sub "
            type = "codex"
            model = " gpt-5.4 "
            api_url = " http://localhost:5111/backend-api/codex "
            auth_source = " CODEX_AUTH_FILE_READONLY "
            account_id = " acct_123 "
            max_concurrent_requests = 2
            text_verbosity = " HIGH "
            include_encrypted_reasoning = false
            model_discovery = " STATIC "
            response_transport = " HTTP "
            send_responses_beta_header = false
            send_installation_id = true
            installation_id_source = " CODEX_HOME_READONLY "
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var provider = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .Single(static provider => provider.ProviderKey == "codex");

        Assert.AreEqual("Codex Sub", provider.DisplayName);
        Assert.AreEqual("http://localhost:5111/backend-api/codex", provider.ApiUrl);
        Assert.AreEqual("codex_auth_file_readonly", provider.AuthSource);
        Assert.AreEqual("acct_123", provider.AccountId);
        Assert.AreEqual(2, provider.MaxConcurrentRequests);
        Assert.AreEqual("high", provider.TextVerbosity);
        Assert.IsFalse(provider.IncludeEncryptedReasoning);
        Assert.AreEqual("static", provider.ModelDiscovery);
        Assert.AreEqual("http", provider.ResponseTransport);
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
            [providers.codex]
            type = "codex"
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
            [providers.codex]
            type = "codex"
            model = "gpt-5.3-codex"
            experimental = true

            [providers.codex.extra_body]
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
            [providers.codex_alt]
            type = "codex"
            model = "gpt-5.3-codex"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "experimental = true");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexSubscriptionAllowsMissingModel()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex]
            type = "codex"
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var provider = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .Single(static provider => provider.ProviderKey == "codex");

        Assert.IsNull(provider.Model);
        Assert.AreEqual("static", provider.ModelDiscovery);
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexSubscriptionRejectsNonHttpsNonLocalEndpoint()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex]
            type = "codex"
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
            [providers.codex]
            type = "codex"
            model = "gpt-5.3-codex"
            text_verbosity = "verbose"
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "text_verbosity");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CodexSubscriptionRejectsInvalidResponseTransport()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex]
            type = "codex"
            model = "gpt-5.3-codex"
            response_transport = "socket"
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "response_transport");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_ReservedProvidersReceiveDefaults()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex_cli]
            model = "gpt-5.4"
            reasoning_effort = "high"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual("codex_cli", providers["codex_cli"].ProviderType);
        Assert.AreEqual("Codex CLI", providers["codex_cli"].DisplayName);
        Assert.IsFalse(providers["codex_cli"].Enabled);
        Assert.AreEqual("copilot_cli", providers["copilot_cli"].ProviderType);
        Assert.AreEqual("Copilot CLI", providers["copilot_cli"].DisplayName);
        Assert.IsFalse(providers["copilot_cli"].Enabled);
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_MigratesLegacyProviderTypes()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex]
            type = "codex"

            [providers.copilot]
            type = "copilot"

            [providers.codex_subscription]
            type = "openai-codex-subscription"
            experimental = true

            [providers.github-copilot-direct]
            type = "github-copilot-direct"
            experimental = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual("codex_cli", providers["codex_cli"].ProviderType);
        Assert.AreEqual("copilot_cli", providers["copilot_cli"].ProviderType);
        Assert.AreEqual("codex", providers["codex"].ProviderType);
        Assert.AreEqual("copilot", providers["copilot"].ProviderType);
    }

    [TestMethod]
    public void EnsureGlobalConfigExists_WritesDisabledBundledProviderTemplate()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        Assert.IsTrue(store.EnsureGlobalConfigExists());
        Assert.IsFalse(store.EnsureGlobalConfigExists());

        var content = File.ReadAllText(Path.Combine(temp.Path, "config.toml"));
        StringAssert.Contains(content, "type = \"codex_cli\"");
        StringAssert.Contains(content, "type = \"codex\"");
        StringAssert.Contains(content, "type = \"copilot_cli\"");
        StringAssert.Contains(content, "type = \"copilot\"");
        Assert.IsFalse(content.Contains("pc-ai", StringComparison.OrdinalIgnoreCase));

        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true);
        Assert.IsTrue(providers.Count > 4);
        Assert.IsTrue(providers.All(static provider => provider.Enabled == false));
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_CopilotExplicitlyEnabledIsTemporarilyDisabled()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.copilot]
            enabled = true
            model = "claude-opus-4.6"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        Assert.IsFalse(providers["copilot_cli"].Enabled);
        Assert.AreEqual("claude-opus-4.6", providers["copilot_cli"].Model);
    }

    [TestMethod]
    public void SaveGlobalProviderDefinitions_CopilotEnabledIsPrunedWhileTemporarilyDisabled()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        store.SaveGlobalProviderDefinitions(
        [
            new CodeAltaProviderDocument
            {
                ProviderKey = "copilot_cli",
                Enabled = true,
                ProviderType = "copilot_cli",
                Model = "claude-opus-4.6",
            },
        ]);

        var content = File.ReadAllText(Path.Combine(temp.Path, "config.toml"));
        Assert.IsFalse(content.Contains("enabled = true", StringComparison.Ordinal));

        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);
        Assert.IsFalse(providers["copilot_cli"].Enabled);
        Assert.AreEqual("claude-opus-4.6", providers["copilot_cli"].Model);
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
                ProviderKey = "codex_cli",
                Enabled = true,
                ProviderType = "codex_cli",
            },
            new CodeAltaProviderDocument
            {
                ProviderKey = "openrouter",
                Enabled = true,
                ProviderType = "openai-chat",
                ApiKeyEnv = "OPENROUTER_API_KEY",
                ApiUrl = "https://openrouter.ai/api/v1",
                ProtocolTrace = true,
            },
        ]);

        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(providers["codex_cli"].Enabled);
        Assert.IsTrue(providers["openrouter"].Enabled);
        Assert.IsTrue(providers["openrouter"].ProtocolTrace);
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
            [providers.codex_cli]
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
