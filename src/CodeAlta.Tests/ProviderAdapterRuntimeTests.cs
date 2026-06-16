using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.Mistral;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.Xai;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ProviderAdapterRuntimeTests
{
    [TestMethod]
    public async Task OpenAIChatProviderRuntime_ProbeUsesModelCatalogAndExposesTurnExecutor()
    {
        await using var runtime = new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
        {
            ProviderIdOverride = new ModelProviderId("openai-test"),
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "openai-test",
                    ApiKey = "test-key",
                    ModelListAsync = _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo("listed-model", Provider: "openai-test"),
                    ]),
                },
            },
        });

        var probe = await runtime.ProbeAsync().ConfigureAwait(false);

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability);
        Assert.AreEqual("openai-test", probe.ProviderId.Value);
        Assert.AreEqual(1, probe.Models.Count);
        Assert.AreEqual("listed-model", probe.Models[0].Id);
        Assert.IsInstanceOfType<OpenAIChatTurnExecutor>(runtime.CreateTurnExecutor());
        Assert.AreEqual("openai-chat", runtime.RuntimeDescriptor.ProtocolFamily);
    }

    [TestMethod]
    public async Task ProviderInitialization_RecordsAdapterProbeFailureWithoutBlockingReadyProvider()
    {
        await using var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(
            new ModelProviderDescriptor(new ModelProviderId("broken"), "Broken", "openai-chat"),
            () => new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
            {
                ProviderIdOverride = new ModelProviderId("broken"),
                Providers =
                {
                    new OpenAIProviderOptions
                    {
                        ProviderKey = "broken",
                        ApiKey = "test-key",
                        ModelListAsync = _ => throw new InvalidOperationException("model list failed"),
                    },
                },
            }));
        registry.RegisterOrReplace(
            new ModelProviderDescriptor(new ModelProviderId("ready"), "Ready", "openai-chat"),
            () => new OpenAIChatModelProviderRuntime(new OpenAIChatModelProviderRuntimeOptions
            {
                ProviderIdOverride = new ModelProviderId("ready"),
                Providers =
                {
                    new OpenAIProviderOptions
                    {
                        ProviderKey = "ready",
                        ApiKey = "test-key",
                        ModelListAsync = _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                        [
                            new AgentModelInfo("ready-model", Provider: "ready"),
                        ]),
                    },
                },
            }));
        var initialization = new ModelProviderInitializationService(registry);

        await initialization.InitializeAllAsync().ConfigureAwait(false);

        var broken = initialization.CurrentStates.Single(state => state.ProviderId.Value == "broken");
        var ready = initialization.CurrentStates.Single(state => state.ProviderId.Value == "ready");
        Assert.AreEqual(ModelProviderAvailability.Failed, broken.Availability);
        Assert.AreEqual(ModelProviderAvailability.Ready, ready.Availability);
        Assert.AreEqual("ready-model", ready.Models.Single().Id);
    }

    [TestMethod]
    public async Task ProviderRuntimes_ExposeRuntimeDescriptorsAndTurnExecutors()
    {
        await using var anthropic = new AnthropicModelProviderRuntime(new AnthropicModelProviderRuntimeOptions
        {
            ProviderIdOverride = new ModelProviderId("anthropic-test"),
            Providers =
            {
                new AnthropicProviderOptions
                {
                    ProviderKey = "anthropic-test",
                    ApiKey = "test-key",
                    SingleModelId = "claude-test",
                },
            },
        });
        await using var copilot = new CopilotDirectModelProviderRuntime(new CopilotDirectModelProviderRuntimeOptions
        {
            ProviderIdOverride = new ModelProviderId("copilot-test"),
            Providers =
            {
                new CopilotDirectProviderOptions
                {
                    ProviderKey = "copilot-test",
                    SingleModelId = "gpt-test",
                },
            },
        });
        await using var google = new GoogleGenAIModelProviderRuntime(new GoogleGenAIModelProviderRuntimeOptions
        {
            ProviderIdOverride = new ModelProviderId("google-test"),
            Providers =
            {
                new GoogleGenAIProviderOptions
                {
                    ProviderKey = "google-test",
                    ApiKey = "test-key",
                    SingleModelId = "gemini-test",
                },
            },
        });
        await using var vertex = new GoogleGenAIModelProviderRuntime(new GoogleGenAIModelProviderRuntimeOptions
        {
            ProviderIdOverride = new ModelProviderId("vertex-test"),
            Providers =
            {
                new GoogleGenAIProviderOptions
                {
                    ProviderKey = "vertex-test",
                    UseVertexAI = true,
                    Project = "project",
                    Location = "us-central1",
                    SingleModelId = "gemini-vertex-test",
                },
            },
        });
        await using var xai = new XaiDirectModelProviderRuntime(new XaiModelProviderRuntimeOptions
        {
            ProviderIdOverride = new ModelProviderId("xai-test"),
            Providers =
            {
                new XaiProviderOptions
                {
                    ProviderKey = "xai-test",
                    SingleModelId = "grok-test",
                },
            },
        });
        await using var mistral = new MistralModelProviderRuntime(new MistralModelProviderRuntimeOptions
        {
            ProviderIdOverride = new ModelProviderId("mistral-test"),
            Providers =
            {
                new MistralProviderOptions
                {
                    ProviderKey = "mistral-test",
                    ApiKey = "test-key",
                    SingleModelId = "mistral-test-model",
                },
            },
        });

        AssertProviderRuntime(anthropic, "anthropic", "anthropic-messages", "claude-test");
        AssertProviderRuntime(copilot, "copilot", "copilot", "gpt-test");
        AssertProviderRuntime(google, "google-genai", "google-genai", "gemini-test");
        AssertProviderRuntime(vertex, "vertex-ai", "vertex-ai", "gemini-vertex-test");
        AssertProviderRuntime(xai, "xai", "xai", "grok-test");
        AssertProviderRuntime(mistral, "mistral", "mistral-chat", "mistral-test-model");
    }

    private static void AssertProviderRuntime(
        IAgentModelProviderRuntime runtime,
        string providerType,
        string protocolFamily,
        string expectedModelId)
    {
        Assert.AreEqual(providerType, runtime.Descriptor.ProviderType);
        Assert.AreEqual(protocolFamily, runtime.RuntimeDescriptor.ProtocolFamily);
        Assert.IsNotNull(runtime.CreateTurnExecutor());
        var registration = runtime.CreateProviderRegistration();
        Assert.AreSame(runtime.CreateTurnExecutor(), registration.TurnExecutor);
        Assert.AreEqual(expectedModelId, runtime.Descriptor.DefaultModelId);
    }
}
