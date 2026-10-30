using System.Reflection;
using CodeAlta.Agent;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Hosting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ConfiguredProviderRegistrationTests
{
    [TestMethod]
    [DataRow(null, null)]
    [DataRow("default", "default")]
    [DataRow("priority", "priority")]
    [DataRow(" FAST ", "priority")]
    public async Task CodexRegistration_MapsServiceTierWithoutChangingDefaults(string? tier, string? expected)
    {
        using var temp = TestTempDirectory.Create();
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument
            {
                ProviderKey = "codex",
                ProviderType = "codex",
                ServiceTier = tier,
            },
            temp.Path,
            null,
            out _,
            out var createRuntime));
        await using var runtime = createRuntime();
        var executor = Assert.IsInstanceOfType<IAgentModelProviderRuntime>(runtime).CreateTurnExecutor();
        // Inspect the captured options without starting/probing the provider or reading credentials.
        var providerField = executor.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(static field => field.FieldType == typeof(OpenAIProviderOptions));
        var options = Assert.IsInstanceOfType<OpenAIProviderOptions>(providerField.GetValue(executor));
        Assert.IsNotNull(options.CodexSubscription);
        Assert.AreEqual(expected, options.CodexSubscription.ServiceTier);
        Assert.IsNull(options.CodexSubscription.TextVerbosity);
        Assert.AreEqual("http", options.CodexSubscription.ResponseTransport);
    }

    [TestMethod]
    [DataRow(null, AgentTransportKind.OpenAIChatCompletions)]
    [DataRow("responses", AgentTransportKind.OpenAIResponses)]
    public async Task LlamaCppRegistration_NeedsNoKey_AndUsesTheApiOfTheFile(string? api, AgentTransportKind expected)
    {
        using var temp = TestTempDirectory.Create();
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument { ProviderKey = "llama", ProviderType = "llama-cpp", ApiUrl = "http://127.0.0.1:9931/v1", Api = api },
            temp.Path, null, out var descriptor, out var createRuntime));
        await using var runtime = createRuntime();
        var agentRuntime = Assert.IsInstanceOfType<IAgentModelProviderRuntime>(runtime);
        var executor = agentRuntime.CreateTurnExecutor();
        var providerField = executor.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(static field => field.FieldType == typeof(OpenAIProviderOptions));
        var options = Assert.IsInstanceOfType<OpenAIProviderOptions>(providerField.GetValue(executor));

        Assert.AreEqual(("llama", "llama-cpp"), (descriptor.ProviderId.Value, descriptor.ProviderType));
        Assert.AreEqual(expected, agentRuntime.RuntimeDescriptor.TransportKind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(options.ApiKey), "The OpenAI client is given a key the server ignores.");
        Assert.IsNotNull(options.ModelListAsync, "The models come from the list of the server.");
        Assert.IsNull(options.ModelCatalog, "A local model gets no price from models.dev.");
        // Chat Completions gives the server back the reasoning in the field it sends it in.
        Assert.AreEqual(api is null ? "reasoning_content" : null, options.Profile?.ReasoningInputFieldName);
    }

    [TestMethod]
    [DataRow("mistral", null, AgentTransportKind.MistralChat)]
    [DataRow("mistral", "responses", AgentTransportKind.OpenAIResponses)]
    [DataRow("mistral-vibe", null, AgentTransportKind.MistralChat)]
    [DataRow("mistral-vibe", "responses", AgentTransportKind.OpenAIResponses)]
    public async Task MistralRegistration_UsesTheApiOfTheFile(string type, string? api, AgentTransportKind expected)
    {
        using var temp = TestTempDirectory.Create();
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument { ProviderKey = "m", ProviderType = type, ApiKey = type == "mistral" ? "key" : null, Api = api },
            temp.Path, null, out var descriptor, out var createRuntime));
        await using var runtime = createRuntime();
        var agentRuntime = Assert.IsInstanceOfType<IAgentModelProviderRuntime>(runtime);

        Assert.AreEqual(("m", type), (descriptor.ProviderId.Value, descriptor.ProviderType));
        Assert.AreEqual(expected, agentRuntime.RuntimeDescriptor.TransportKind);
    }

    [TestMethod]
    public async Task MistralVibeRegistration_SignedOut_IsRegistered_AndSaysToSignIn()
    {
        using var temp = TestTempDirectory.Create();
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument { ProviderKey = "vibe", ProviderType = "mistral-vibe" },
            temp.Path, null, out _, out var createRuntime));
        await using var runtime = createRuntime();
        var agentRuntime = Assert.IsInstanceOfType<IAgentModelProviderRuntime>(runtime);

        // No key is stored: the list of the models asks for the sign-in before any request.
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => agentRuntime.ModelCatalog!.ListModelsAsync(agentRuntime.RuntimeDescriptor, CancellationToken.None));
        StringAssert.Contains(failure.Message, "not signed in");
    }

    [TestMethod]
    [DataRow(null, OpenAICodexWebSearchMode.Cached)]
    [DataRow("live", OpenAICodexWebSearchMode.Live)]
    [DataRow("disabled", OpenAICodexWebSearchMode.Disabled)]
    public async Task CodexRegistration_MapsWebSearch(string? webSearch, OpenAICodexWebSearchMode expected)
    {
        using var temp = TestTempDirectory.Create();
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument { ProviderKey = "codex", ProviderType = "codex", WebSearch = webSearch },
            temp.Path, null, out _, out var createRuntime));
        await using var runtime = createRuntime();
        var executor = Assert.IsInstanceOfType<IAgentModelProviderRuntime>(runtime).CreateTurnExecutor();
        var providerField = executor.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(static field => field.FieldType == typeof(OpenAIProviderOptions));
        var options = Assert.IsInstanceOfType<OpenAIProviderOptions>(providerField.GetValue(executor));

        Assert.AreEqual(expected, options.CodexSubscription?.WebSearch);
    }

    [TestMethod]
    public async Task CodexRegistration_GivesTheFolderOfItsCli()
    {
        using var temp = TestTempDirectory.Create();
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument { ProviderKey = "codex-work", ProviderType = "codex", CodexHome = " ~/.codex-work " },
            temp.Path, null, out _, out var createRuntime));
        await using var runtime = createRuntime();
        var executor = Assert.IsInstanceOfType<IAgentModelProviderRuntime>(runtime).CreateTurnExecutor();
        var providerField = executor.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(static field => field.FieldType == typeof(OpenAIProviderOptions));
        var options = Assert.IsInstanceOfType<OpenAIProviderOptions>(providerField.GetValue(executor));

        Assert.AreEqual("~/.codex-work", options.CodexSubscription?.CodexHome);
    }

    [TestMethod]
    public void ClaudeCodeRegistration_GivesTheConfigFolderOfTheAccount()
    {
        using var temp = TestTempDirectory.Create();
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument { ProviderKey = "claude-business", ProviderType = "claude-code", ConfigDir = " ~/.claude-business " },
            temp.Path, null, out var descriptor, out var createRuntime));
        var runtime = Assert.IsInstanceOfType<CodeAlta.Agent.Claude.ClaudeCodeModelProviderRuntime>(createRuntime());
        var optionsField = runtime.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(static field => field.FieldType == typeof(CodeAlta.Agent.Claude.ClaudeCodeModelProviderRuntimeOptions));
        var options = Assert.IsInstanceOfType<CodeAlta.Agent.Claude.ClaudeCodeModelProviderRuntimeOptions>(optionsField.GetValue(runtime));

        Assert.AreEqual("claude-business", descriptor.ProviderId.Value);
        Assert.AreEqual("~/.claude-business", options.ConfigDirectory);
    }

    [TestMethod]
    [DataRow("flex")]
    [DataRow("prioroty")]
    public void CodexOptions_RejectInvalidServiceTier(string tier)
    {
        var options = new OpenAICodexSubscriptionOptions();
        Assert.ThrowsExactly<ArgumentException>(() => options.ServiceTier = tier);
        Assert.IsNull(options.ServiceTier);
    }
}
