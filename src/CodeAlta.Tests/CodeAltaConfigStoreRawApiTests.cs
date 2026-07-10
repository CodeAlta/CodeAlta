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
            network_timeout_seconds = 100
            protocol_trace = true
            models_dev_provider_id = " OpenRouter "
            single_model_id = " gpt-5 "
            models_include_regex = " ^gpt-(5|4o) "

            [providers.OpenRouter.extra_body]
            custom_boolean = true
            custom_threshold = 0.75

            [providers.OpenRouter.request]
            remove_headers = [" X-Default ", "Authorization"]
            remove_extra_body = [" inherited_flag "]

            [providers.OpenRouter.request.headers]
            X-Provider-Feature = " enabled "

            [providers.OpenRouter.request.extra_body]
            request_boolean = true

            [providers.OpenRouter.profile]
            supports_developer_role = false
            supports_store = false
            supports_parallel_tool_calls = false
            max_tokens_field_name = " max_tokens "
            reasoning_field_names = [" reasoning_content ", "", "reasoning"]
            reasoning_input_field_name = " reasoning_content "

            [providers.OpenRouter.compaction]
            ratio = 0.95
            summary_output_ratio = 0.10
            post_compaction_target_ratio = 0.08
            summary_share_of_target = 0.35
            file_context_share_of_summary_target = 0.20
            keep_last_user_message = false
            allow_split_turn = false

            [providers.OpenRouter.model_overrides." gpt-5 "]
            display_name = " GPT-5 "
            description = " flagship "
            context_window = 400000
            output_token_limit = 128000

            [providers.OpenRouter.model_request." gpt-5 "]
            remove_headers = [" X-Provider-Feature "]
            remove_extra_body = [" request_boolean "]

            [providers.OpenRouter.model_request." gpt-5 ".headers]
            X-Model-Feature = "enabled"

            [providers.OpenRouter.model_request." gpt-5 ".extra_body]
            model_boolean = true

            [providers.responses]
            display_name = "OpenAI (Responses)"
            type = "responses"
            api_key_env = "CODEALTA_OPENAI_API_KEY"
            api_url = "https://api.openai.com/v1"

            [providers.azure]
            display_name = "Azure OpenAI"
            type = "aoai"
            model = " my-gpt-4o-mini-deployment "
            api_key_env = " AZURE_OPENAI_API_KEY "
            api_url = " https://example.openai.azure.com "
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual("openrouter", store.GetEffectiveDefaultProvider());

        var openRouter = providers["openrouter"];
        Assert.AreEqual("OpenRouter", openRouter.DisplayName);
        Assert.AreEqual("openai-chat", openRouter.ProviderType);
        Assert.AreEqual("gpt-5", openRouter.Model);
        Assert.AreEqual("high", openRouter.ReasoningEffort);
        Assert.AreEqual("OPENROUTER_API_KEY", openRouter.ApiKeyEnv);
        Assert.AreEqual("https://openrouter.ai/api/v1", openRouter.ApiUrl);
        Assert.AreEqual(100, openRouter.NetworkTimeoutSeconds);
        Assert.IsTrue(openRouter.ProtocolTrace);
        Assert.AreEqual("openrouter", openRouter.ModelsDevProviderId);
        Assert.AreEqual("gpt-5", openRouter.SingleModelId);
        Assert.AreEqual("^gpt-(5|4o)", openRouter.ModelsIncludeRegex);
        Assert.IsNotNull(openRouter.ExtraBody);
        Assert.AreEqual(true, openRouter.ExtraBody!["custom_boolean"]);
        Assert.AreEqual(0.75d, Convert.ToDouble(openRouter.ExtraBody["custom_threshold"]));
        Assert.IsNotNull(openRouter.Request);
        CollectionAssert.AreEqual(
            new[] { "X-Default", "Authorization" },
            openRouter.Request!.RemoveHeaders);
        CollectionAssert.AreEqual(
            new[] { "inherited_flag" },
            openRouter.Request.RemoveExtraBody);
        Assert.AreEqual(" enabled ", openRouter.Request.Headers!["X-Provider-Feature"]);
        Assert.AreEqual(true, openRouter.Request.ExtraBody!["request_boolean"]);
        Assert.IsNotNull(openRouter.Profile);
        Assert.IsFalse(openRouter.Profile!.SupportsDeveloperRole);
        Assert.IsFalse(openRouter.Profile.SupportsStore);
        Assert.IsFalse(openRouter.Profile.SupportsParallelToolCalls);
        Assert.AreEqual("max_tokens", openRouter.Profile.MaxTokensFieldName);
        CollectionAssert.AreEqual(
            new[] { "reasoning_content", "reasoning" },
            openRouter.Profile.ReasoningFieldNames);
        Assert.AreEqual("reasoning_content", openRouter.Profile.ReasoningInputFieldName);
        Assert.IsNotNull(openRouter.Compaction);
        Assert.AreEqual(0.95d, openRouter.Compaction!.Ratio!.Value, 0.0001d);
        Assert.AreEqual(0.10d, openRouter.Compaction.SummaryOutputRatio!.Value, 0.0001d);
        Assert.AreEqual(0.08d, openRouter.Compaction.PostCompactionTargetRatio!.Value, 0.0001d);
        Assert.AreEqual(0.35d, openRouter.Compaction.SummaryShareOfTarget!.Value, 0.0001d);
        Assert.AreEqual(0.20d, openRouter.Compaction.FileContextShareOfSummaryTarget!.Value, 0.0001d);
        Assert.IsFalse(openRouter.Compaction.KeepLastUserMessage!.Value);
        Assert.IsFalse(openRouter.Compaction.AllowSplitTurn!.Value);
        Assert.IsNotNull(openRouter.ModelOverrides);
        Assert.IsTrue(openRouter.ModelOverrides!.TryGetValue("gpt-5", out var modelOverride));
        Assert.IsNotNull(modelOverride);
        Assert.AreEqual("GPT-5", modelOverride.DisplayName);
        Assert.AreEqual("flagship", modelOverride.Description);
        Assert.AreEqual(400000L, modelOverride.ContextWindow);
        Assert.AreEqual(128000L, modelOverride.OutputTokenLimit);
        Assert.IsNotNull(openRouter.ModelRequest);
        Assert.IsTrue(openRouter.ModelRequest!.TryGetValue("gpt-5", out var modelRequest));
        Assert.IsNotNull(modelRequest);
        CollectionAssert.AreEqual(new[] { "X-Provider-Feature" }, modelRequest.RemoveHeaders);
        CollectionAssert.AreEqual(new[] { "request_boolean" }, modelRequest.RemoveExtraBody);
        Assert.AreEqual("enabled", modelRequest.Headers!["X-Model-Feature"]);
        Assert.AreEqual(true, modelRequest.ExtraBody!["model_boolean"]);

        var responses = providers["responses"];
        Assert.AreEqual("openai-responses", responses.ProviderType);
        Assert.AreEqual("OpenAI (Responses)", responses.DisplayName);

        var azure = providers["azure"];
        Assert.AreEqual("azure-openai", azure.ProviderType);
        Assert.AreEqual("Azure OpenAI", azure.DisplayName);
        Assert.AreEqual("my-gpt-4o-mini-deployment", azure.Model);
        Assert.AreEqual("AZURE_OPENAI_API_KEY", azure.ApiKeyEnv);
        Assert.AreEqual("https://example.openai.azure.com", azure.ApiUrl);
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_InvalidModelsIncludeRegex_Throws()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openai]
            type = "openai-chat"
            enabled = false
            api_key_env = "OPENAI_API_KEY"
            models_include_regex = "("
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });

        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "models_include_regex");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_InvalidNetworkTimeout_Throws()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"
            network_timeout_seconds = 0
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "network_timeout_seconds");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_RejectsNetworkTimeoutForUnsupportedProvider()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.anthropic]
            type = "anthropic"
            api_key_env = "ANTHROPIC_API_KEY"
            network_timeout_seconds = 100
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "network_timeout_seconds");
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
        Assert.AreEqual(0.10d, compaction.PostCompactionTargetRatio!.Value, 0.0001d);
        Assert.AreEqual(0.40d, compaction.SummaryShareOfTarget!.Value, 0.0001d);
        Assert.AreEqual(0.15d, compaction.FileContextShareOfSummaryTarget!.Value, 0.0001d);
        Assert.IsTrue(compaction.KeepLastUserMessage!.Value);
        Assert.IsTrue(compaction.AllowSplitTurn!.Value);
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_InvalidPostCompactionTargetRatio_Throws()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"

            [providers.openai.compaction]
            post_compaction_target_ratio = 0
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_InvalidCompactionShare_Throws()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"

            [providers.openai.compaction]
            summary_share_of_target = 1.5
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
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
    public void LoadGlobalProviderDefinitions_AcceptsMistralProvider()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.Mistral]
            display_name = " Mistral "
            type = " mistral-chat "
            api_key_env = " CODEALTA_MISTRAL_API_KEY "
            model = " mistral-small-latest "
            models_dev_provider_id = " mistral "
            single_model_id = " mistral-small-latest "
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        var provider = providers["mistral"];
        Assert.AreEqual("mistral", provider.ProviderType);
        Assert.AreEqual("Mistral", provider.DisplayName);
        Assert.AreEqual("CODEALTA_MISTRAL_API_KEY", provider.ApiKeyEnv);
        Assert.AreEqual("mistral-small-latest", provider.Model);
        Assert.AreEqual("mistral", provider.ModelsDevProviderId);
        Assert.AreEqual("mistral-small-latest", provider.SingleModelId);
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
        Assert.AreEqual("codex_endpoint_with_static_fallback", provider.ModelDiscovery);
        Assert.AreEqual("websocket_with_http_fallback", provider.ResponseTransport);
        Assert.IsFalse(provider.SendResponsesBetaHeader);
        Assert.IsFalse(provider.SendInstallationId);
        Assert.AreEqual("codealta_state", provider.InstallationIdSource);
        Assert.IsFalse(provider.Experimental);
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
    public void SaveGlobalProviderDefinitions_CodexSubscriptionPreservesExplicitLegacyBetaOptIn()
    {
        using var temp = TempDirectory.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        store.SaveGlobalProviderDefinitions(
        [
            new CodeAltaProviderDocument
            {
                ProviderKey = "codex",
                ProviderType = "codex",
                SendResponsesBetaHeader = true,
            },
        ]);

        var content = File.ReadAllText(Path.Combine(temp.Path, "config.toml"));
        StringAssert.Contains(content, "send_responses_beta_header = true");
        Assert.IsTrue(store.LoadGlobalProviderDefinitions(includeDisabled: true).Single().SendResponsesBetaHeader);
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

            [providers.codex.extra_body]
            suspicious = true
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "extra_body");
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
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var provider = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .Single(static provider => provider.ProviderKey == "codex");

        Assert.IsNull(provider.Model);
        Assert.AreEqual("codex_endpoint_with_static_fallback", provider.ModelDiscovery);
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
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var ex = Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProviderDefinitions(includeDisabled: true));
        StringAssert.Contains(ex.InnerException?.Message, "response_transport");
    }

    [TestMethod]
    public void LoadGlobalProviderDefinitions_MigratesDirectProviderAliases()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, "config.toml"),
            """
            [providers.codex_subscription]
            type = "openai-codex-subscription"

            [providers.github-copilot-direct]
            type = "github-copilot-direct"
            """);

        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

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
        StringAssert.Contains(content, "[providers.alibaba]");
        StringAssert.Contains(content, "api_key_env = \"CODEALTA_ALIBABA_API_KEY\"");
        StringAssert.Contains(content, "[providers.mistral]");
        StringAssert.Contains(content, "type = \"mistral\"");
        StringAssert.Contains(content, "api_key_env = \"CODEALTA_MISTRAL_API_KEY\"");
        StringAssert.Contains(content, "type = \"codex\"");
        StringAssert.Contains(content, "type = \"copilot\"");
        Assert.IsFalse(content.Contains("pc-ai", StringComparison.OrdinalIgnoreCase));

        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true);
        Assert.IsTrue(providers.Count > 4);
        Assert.IsTrue(providers.All(static provider => provider.Enabled == false));
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
                NetworkTimeoutSeconds = 100,
                ProtocolTrace = true,
                Compaction = new CodeAltaProviderCompactionDocument
                {
                    PostCompactionTargetRatio = 0.08,
                    SummaryShareOfTarget = 0.35,
                    FileContextShareOfSummaryTarget = 0.20,
                    KeepLastUserMessage = false,
                    AllowSplitTurn = false,
                },
            },
        ]);

        var providers = store.LoadGlobalProviderDefinitions(includeDisabled: true)
            .ToDictionary(static provider => provider.ProviderKey, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(providers["codex"].Enabled);
        Assert.IsTrue(providers["openrouter"].Enabled);
        Assert.AreEqual(100, providers["openrouter"].NetworkTimeoutSeconds);
        Assert.IsTrue(providers["openrouter"].ProtocolTrace);
        Assert.IsNotNull(providers["openrouter"].Compaction);
        var compaction = providers["openrouter"].Compaction!;
        Assert.AreEqual(0.08d, compaction.PostCompactionTargetRatio!.Value, 0.0001d);
        Assert.AreEqual(0.35d, compaction.SummaryShareOfTarget!.Value, 0.0001d);
        Assert.AreEqual(0.20d, compaction.FileContextShareOfSummaryTarget!.Value, 0.0001d);
        Assert.IsFalse(compaction.KeepLastUserMessage!.Value);
        Assert.IsFalse(compaction.AllowSplitTurn!.Value);
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
