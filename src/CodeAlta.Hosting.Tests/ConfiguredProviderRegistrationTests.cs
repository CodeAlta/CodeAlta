using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting.Tests;

// These fixtures only assemble descriptors/options and retain concrete factories without invoking them.
// No config store, metadata service, provider SDK client, host, or discovery service is constructed.
[TestClass]
public sealed class ConfiguredProviderRegistrationTests
{
    [TestMethod]
    public void PermissionModes_AreThoseOfTheClaudeCodeCli_ForAProviderThatRunsIt()
    {
        CollectionAssert.AreEqual(
            new[] { "default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions" },
            ConfiguredModelProviderRegistryBuilder.GetPermissionModes(" Claude-Code ").ToArray());
        Assert.AreEqual(0, ConfiguredModelProviderRegistryBuilder.GetPermissionModes("anthropic").Count);
        Assert.AreEqual(0, ConfiguredModelProviderRegistryBuilder.GetPermissionModes(null).Count);
    }

    [TestMethod]
    [DataRow("openai-chat")]
    [DataRow("openai-responses")]
    [DataRow("azure-openai")]
    [DataRow("codex")]
    [DataRow("copilot")]
    [DataRow("xai")]
    [DataRow("anthropic")]
    [DataRow("google-genai")]
    [DataRow("vertex-ai")]
    [DataRow("mistral")]
    public void SupportedTypes_CreateDescriptorsAndUninvokedFactories(string providerType)
    {
        var stateRoot = NewStateRoot();
        var definition = CreateDefinition(providerType);
        definition.DisplayName = "  Fixture provider  ";
        definition.Model = " fallback-model ";
        definition.SingleModelId = " selected-model ";
        definition.ReasoningEffort = " XHIGH ";
        definition.SortModels = true;

        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            definition, stateRoot, null, out var descriptor, out var factory));

        Assert.AreEqual("fixture", descriptor.ProviderId.Value);
        Assert.AreEqual(providerType, descriptor.ProviderType);
        Assert.AreEqual("Fixture provider", descriptor.DisplayName);
        Assert.AreEqual(new Uri("https://provider.invalid/v1"), descriptor.BaseUri);
        Assert.AreEqual("selected-model", descriptor.DefaultModelId);
        Assert.AreEqual(AgentReasoningEffort.XHigh, descriptor.DefaultReasoningEffort);
        Assert.IsTrue(descriptor.IsEnabled);
        Assert.IsTrue(descriptor.IsDefault);
        Assert.IsTrue(descriptor.SortModels);
        Assert.IsNotNull(factory); // Deliberately never invoke a concrete provider factory.
        Assert.IsFalse(Directory.Exists(stateRoot));
    }

    [TestMethod]
    public void ClaudeCode_RegistersWithoutAnyCredentialOfCodeAlta()
    {
        // The CLI of the user signs in by itself: the provider is registered from its type alone, and whether the
        // CLI is installed and signed in is found when it is probed.
        var stateRoot = NewStateRoot();
        var definition = new CodeAltaProviderDocument
        {
            ProviderKey = "claude-code",
            ProviderType = "claude-code",
            DisplayName = " Claude Code ",
            Model = " sonnet ",
            ReasoningEffort = "high",
            Command = " /opt/claude/claude ",
            Arguments = ["--add-dir", "/data"],
            PermissionMode = "acceptEdits",
        };

        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            definition, stateRoot, null, out var descriptor, out var factory));

        Assert.AreEqual("claude-code", descriptor.ProviderId.Value);
        Assert.AreEqual("claude-code", descriptor.ProviderType);
        Assert.AreEqual("Claude Code", descriptor.DisplayName);
        Assert.IsNull(descriptor.BaseUri);
        Assert.AreEqual("sonnet", descriptor.DefaultModelId);
        Assert.AreEqual(AgentReasoningEffort.High, descriptor.DefaultReasoningEffort);
        Assert.IsTrue(descriptor.IsEnabled);
        Assert.IsNotNull(factory); // Never invoked: creating the runtime starts no process either.
        Assert.IsFalse(Directory.Exists(stateRoot));
    }

    [TestMethod]
    [DataRow("openai-chat")]
    [DataRow("openai-responses")]
    [DataRow("azure-openai")]
    [DataRow("anthropic")]
    [DataRow("google-genai")]
    [DataRow("mistral")]
    public void MissingLiteralAndEnvironmentName_SkipsRegistrationWithoutEnvironmentLookup(string providerType)
    {
        var definition = CreateDefinition(providerType);
        definition.ApiKey = " ";
        Assert.IsNull(definition.ApiKeyEnv);

        Assert.IsFalse(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            definition, NewStateRoot(), null, out var descriptor, out var factory));

        Assert.IsNull(descriptor);
        Assert.IsNull(factory);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("unknown")]
    [DataRow("OPENAI-CHAT")]
    public void UnsupportedType_ReturnsNoRegistration(string? providerType)
    {
        var definition = CreateDefinition("openai-chat");
        definition.ProviderType = providerType;

        Assert.IsFalse(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            definition, NewStateRoot(), null, out var descriptor, out var factory));

        Assert.IsNull(descriptor);
        Assert.IsNull(factory);
    }

    [TestMethod]
    [DataRow("github_token_env")]
    [DataRow("copilot_token_env")]
    public void CopilotEnvironmentAuthWithoutVariableName_ShortCircuitsBeforeLookup(string authSource)
    {
        var definition = CreateDefinition("copilot");
        definition.AuthSource = authSource;
        Assert.IsNull(definition.GitHubTokenEnv);
        Assert.IsNull(definition.CopilotTokenEnv);

        Assert.IsFalse(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            definition, NewStateRoot(), null, out _, out _));
    }

    [TestMethod]
    public void XaiUnsupportedAuthSource_ReturnsNoRegistration()
    {
        var definition = CreateDefinition("xai");
        definition.AuthSource = "unsupported";

        Assert.IsFalse(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            definition, NewStateRoot(), null, out _, out _));
    }

    [TestMethod]
    public void DescriptorDefaults_PreserveDisabledFlagAndModelFallback()
    {
        var definition = CreateDefinition("openai-chat");
        definition.Enabled = false;
        definition.DisplayName = " ";
        definition.Model = " fallback-model ";
        definition.SingleModelId = " ";
        definition.ReasoningEffort = "unsupported";

        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            definition, NewStateRoot(), null, out var descriptor, out _));

        Assert.AreEqual("fixture", descriptor.DisplayName);
        Assert.AreEqual("fallback-model", descriptor.DefaultModelId);
        Assert.IsFalse(descriptor.IsEnabled);
        Assert.IsFalse(descriptor.SortModels);
        Assert.IsNull(descriptor.DefaultReasoningEffort);
    }

    [TestMethod]
    public async Task DocumentBatch_SkipsUnsupportedAndPreservesCallerOrderingAndDisabledDefinitions()
    {
        await using var registry = new ModelProviderRegistry();
        var first = CreateDefinition("codex");
        first.ProviderKey = "z-first";
        var disabled = CreateDefinition("openai-chat");
        disabled.ProviderKey = "a-disabled";
        disabled.Enabled = false;
        var unsupported = CreateDefinition("unknown");

        var descriptors = ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(
            registry, [first, unsupported, disabled], NewStateRoot());

        CollectionAssert.AreEqual(new[] { "z-first", "a-disabled" }, descriptors.Select(item => item.ProviderId.Value).ToArray());
        Assert.AreEqual(2, registry.ListProviders(includeDisabled: true).Count);
        Assert.AreEqual(1, registry.ListProviders().Count);
        // Registry disposal sees no concrete runtimes: registration does not invoke its factories.
    }

    [TestMethod]
    public async Task Replacement_DisposesOnlyPreviouslyCreatedFakeAndKeepsRegistryBorrowed()
    {
        var registry = new ModelProviderRegistry();
        var fake = new OwnedFakeRuntime(new ModelProviderDescriptor(new ModelProviderId("fixture"), "Old"));
        registry.RegisterOrReplace(fake.Descriptor, () => fake);
        // This is the only factory invocation in this fixture, before any concrete registration exists.
        Assert.AreSame(fake, await registry.GetOrCreateRuntimeAsync(fake.Descriptor.ProviderId));
        try
        {
            var replacement = CreateDefinition("codex");
            replacement.DisplayName = "Replacement";
            ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(
                registry, [replacement], NewStateRoot(), null);

            Assert.AreEqual(1, fake.DisposeCount);
            Assert.AreEqual("Replacement", registry.ListProviders().Single().DisplayName);
            Assert.IsTrue(registry.Unregister(fake.Descriptor.ProviderId));
            Assert.AreEqual(1, fake.DisposeCount);
        }
        finally
        {
            await registry.DisposeAsync();
        }

        Assert.AreEqual(1, fake.DisposeCount);
    }

    [TestMethod]
    public async Task InvalidArguments_AreRejectedBeforeRegistrationOrConfigAccess()
    {
        await using var registry = new ModelProviderRegistry();
        var definition = CreateDefinition("codex");
        Assert.ThrowsExactly<ArgumentNullException>(() => ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            null!, NewStateRoot(), null, out _, out _));
        Assert.ThrowsExactly<ArgumentException>(() => ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            definition, " ", null, out _, out _));
        Assert.ThrowsExactly<ArgumentNullException>(() => ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(
            null!, [definition], NewStateRoot()));
        Assert.ThrowsExactly<ArgumentNullException>(() => ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(
            registry, null!, NewStateRoot()));
        Assert.ThrowsExactly<ArgumentException>(() => ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(
            registry, [definition], " "));
        Assert.ThrowsExactly<ArgumentNullException>(() => ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(
            registry, null!, NewStateRoot()));
        Assert.AreEqual(0, registry.ListProviders().Count);
    }

    [TestMethod]
    [DataRow("xiaomi", "https://unrelated.invalid", AgentTransportKind.OpenAIChatCompletions, false)]
    [DataRow("unrelated", "https://sub.xiaomimimo.com", AgentTransportKind.OpenAIChatCompletions, false)]
    [DataRow("unrelated", "https://notxiaomimimo.com", AgentTransportKind.OpenAIChatCompletions, true)]
    [DataRow("xiaomi", "https://xiaomimimo.com", AgentTransportKind.OpenAIResponses, true)]
    public void ShippedDefaults_MatchProviderOrHostWithTransportAndHostBoundary(
        string providerKey, string uri, AgentTransportKind transportKind, bool expectedDeveloperRole)
    {
        var original = new AgentProviderProfile { SupportsDeveloperRole = true };
        var profile = RawApiProviderDefaultsCatalog.ApplyProfileDefaults(transportKind, providerKey, new Uri(uri), original);

        Assert.AreEqual(expectedDeveloperRole, profile.SupportsDeveloperRole);
        Assert.IsTrue(original.SupportsDeveloperRole);
    }

    [TestMethod]
    public void ShippedExtraBodyDefaults_PreserveExplicitOverridesWithoutMutatingInput()
    {
        var configured = new Dictionary<string, object?> { ["enable_thinking"] = false, ["custom"] = "retained" };
        var result = RawApiProviderDefaultsCatalog.ApplyOpenAIExtraBodyDefaults(
            AgentTransportKind.OpenAIChatCompletions, "alibaba", null, configured);

        Assert.IsNotNull(result);
        Assert.AreEqual(false, result["enable_thinking"]);
        Assert.AreEqual(true, result["preserve_thinking"]);
        Assert.AreEqual("retained", result["custom"]);
        Assert.AreEqual(2, configured.Count);
        Assert.IsFalse(configured.ContainsKey("preserve_thinking"));
        Assert.IsNotNull(RawApiProviderDefaultsCatalog.CreateOpenAIExtraBodyDefaults(
            AgentTransportKind.OpenAIChatCompletions, "alibaba", null));
    }

    private static string NewStateRoot()
        => Path.Combine(Path.GetTempPath(), "CodeAlta.Hosting.Tests", Guid.NewGuid().ToString("N"));

    private static CodeAltaProviderDocument CreateDefinition(string providerType)
        => new()
        {
            ProviderKey = "fixture",
            ProviderType = providerType,
            ApiKey = "literal-test-key",
            ApiUrl = "https://provider.invalid/v1",
            // AuthSource and all credential-environment names remain null. Copilot/xAI choose
            // their normal OAuth descriptors, but no login manager or runtime is constructed.
        };

    private sealed class OwnedFakeRuntime(ModelProviderDescriptor descriptor) : IModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = descriptor;
        public int DisposeCount { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("No provider start is permitted.");
        public Task StopAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("No provider stop is permitted.");
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("No provider probe is permitted.");
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new AssertFailedException("No provider turns are permitted.");
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
