using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Agent.OpenAI;
using Microsoft.Extensions.AI;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelsDevCatalogTests
{
    [TestMethod]
    public void ModelsDevDatabaseJson_DeserializesProvidersAndModels()
    {
        var database = CreateDatabase();

        Assert.AreEqual(3, database.Providers.Count);
        Assert.IsTrue(database.TryGetProvider("openai", out var provider));
        Assert.IsNotNull(provider);
        Assert.AreEqual("OpenAI", provider.Name);
        Assert.IsTrue(database.TryGetModel("openai", "gpt-5-test", out var model));
        Assert.IsNotNull(model);
        Assert.AreEqual(400000L, model.Limit?.Context);
        Assert.AreEqual(128000L, model.Limit?.Output);
    }

    [TestMethod]
    public void ModelsDevDatabase_TryGetModel_ResolvesCanonicalOpenAIVariants()
    {
        var database = ModelsDevDatabaseJson.Deserialize(
            """
            {
              "openai": {
                "id": "openai",
                "name": "OpenAI",
                "models": {
                  "gpt-5.4": {
                    "id": "gpt-5.4",
                    "name": "GPT-5.4",
                    "limit": { "context": 1050000, "output": 128000 }
                  }
                }
              }
            }
            """);

        Assert.IsTrue(database.TryGetModel("openai", "GPT-5.4", out var exactAlias));
        Assert.IsNotNull(exactAlias);
        Assert.AreEqual(1050000L, exactAlias.Limit?.Context);

        Assert.IsTrue(database.TryGetModel("openai", "gpt-5.4-2026-03-05", out var datedAlias));
        Assert.IsNotNull(datedAlias);
        Assert.AreEqual("gpt-5.4", datedAlias.Id);
    }

    [TestMethod]
    public async Task AgentModelMetadataEnricher_AppliesCatalogAndOverrideMetadata()
    {
        await using var catalog = CreateCatalog();
        var model = new AgentModelInfo(
            "gpt-5-test",
            DisplayName: "gpt-5-test",
            Provider: "openai");
        var overrides = new Dictionary<string, AgentModelOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5-test"] = new()
            {
                DisplayName = "GPT-5 Override",
                ContextWindowTokens = 123456,
                OutputTokenLimit = 64000,
            },
        };

        var enriched = AgentModelMetadataEnricher.EnrichModel(model, catalog, "openai", overrides);

        Assert.AreEqual("GPT-5 Override", enriched.DisplayName);
        Assert.IsNotNull(enriched.Capabilities);
        Assert.AreEqual(123456L, enriched.Capabilities["contextWindow"]);
        Assert.AreEqual(64000L, enriched.Capabilities["outputTokenLimit"]);
        Assert.AreEqual(true, enriched.Capabilities["supportsToolCall"]);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AgentModelMetadataEnricher_AppliesOverridesForCanonicalModelAliases()
    {
        await using var catalog = CreateCatalog();
        var model = new AgentModelInfo(
            "gpt-5-test-2026-04-01",
            DisplayName: "GPT 5 Test",
            Provider: "openai");
        var overrides = new Dictionary<string, AgentModelOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5-test"] = new()
            {
                ContextWindowTokens = 654321,
            },
        };

        var enriched = AgentModelMetadataEnricher.EnrichModel(model, catalog, "openai", overrides);

        Assert.IsNotNull(enriched.Capabilities);
        Assert.AreEqual(654321L, enriched.Capabilities["contextWindow"]);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OpenAIResponsesAgentBackend_ListModelsAsync_EnrichesWithModelsDevMetadata()
    {
        await using var catalog = CreateCatalog();
        using var temp = TestTempDirectory.Create();
        await using var backend = new OpenAIResponsesAgentBackend(new OpenAIResponsesAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new OpenAIProviderOptions
                {
                    ProviderKey = "openai",
                    IsDefault = true,
                    ModelsDevProviderId = "openai",
                    ModelCatalog = catalog,
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo("gpt-5-test", DisplayName: "gpt-5-test", Provider: "openai"),
                    ]),
                },
            },
        });

        var model = (await backend.ListModelsAsync().ConfigureAwait(false)).Single();

        Assert.AreEqual("GPT 5 Test", model.DisplayName);
        Assert.AreEqual(400000L, model.Capabilities?["contextWindow"]);
    }

    [TestMethod]
    public async Task AnthropicAgentBackend_ListModelsAsync_EnrichesWithModelsDevMetadata()
    {
        await using var catalog = CreateCatalog();
        using var temp = TestTempDirectory.Create();
        await using var backend = new AnthropicAgentBackend(new AnthropicAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new AnthropicProviderOptions
                {
                    ProviderKey = "anthropic",
                    IsDefault = true,
                    ModelsDevProviderId = "anthropic",
                    ModelCatalog = catalog,
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo("claude-sonnet-test", DisplayName: "claude-sonnet-test", Provider: "anthropic"),
                    ]),
                },
            },
        });

        var model = (await backend.ListModelsAsync().ConfigureAwait(false)).Single();

        Assert.AreEqual("Claude Sonnet Test", model.DisplayName);
        Assert.AreEqual(200000L, model.Capabilities?["contextWindow"]);
    }

    [TestMethod]
    public async Task AnthropicAgentBackend_ListModelsAsync_UsesConfiguredSingleModelId()
    {
        await using var catalog = CreateCatalog();
        using var temp = TestTempDirectory.Create();
        await using var backend = new AnthropicAgentBackend(new AnthropicAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new AnthropicProviderOptions
                {
                    ProviderKey = "anthropic",
                    IsDefault = true,
                    ModelsDevProviderId = "anthropic",
                    ModelCatalog = catalog,
                    SingleModelId = " claude-sonnet-test ",
                    ApiKey = "test-key",
                    BaseUri = new Uri("https://127.0.0.1.invalid/anthropic"),
                },
            },
        });

        var model = (await backend.ListModelsAsync().ConfigureAwait(false)).Single();

        Assert.AreEqual("claude-sonnet-test", model.Id);
        Assert.AreEqual("Claude Sonnet Test", model.DisplayName);
        Assert.AreEqual(200000L, model.Capabilities?["contextWindow"]);
    }

    [TestMethod]
    public async Task GoogleGenAIAgentBackend_ListModelsAsync_EnrichesWithModelsDevMetadata()
    {
        await using var catalog = CreateCatalog();
        using var temp = TestTempDirectory.Create();
        await using var backend = new GoogleGenAIAgentBackend(new GoogleGenAIAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new GoogleGenAIProviderOptions
                {
                    ProviderKey = "google",
                    IsDefault = true,
                    ModelsDevProviderId = "google",
                    ModelCatalog = catalog,
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo("gemini-test", DisplayName: "gemini-test", Provider: "google"),
                    ]),
                },
            },
        });

        var model = (await backend.ListModelsAsync().ConfigureAwait(false)).Single();

        Assert.AreEqual("Gemini Test", model.DisplayName);
        Assert.AreEqual(1000000L, model.Capabilities?["contextWindow"]);
    }

    [TestMethod]
    public async Task GoogleGenAIAgentBackend_ListModelsAsync_UsesConfiguredSingleModelId()
    {
        await using var catalog = CreateCatalog();
        using var temp = TestTempDirectory.Create();
        await using var backend = new GoogleGenAIAgentBackend(new GoogleGenAIAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new GoogleGenAIProviderOptions
                {
                    ProviderKey = "google",
                    IsDefault = true,
                    ModelsDevProviderId = "google",
                    ModelCatalog = catalog,
                    SingleModelId = " gemini-test ",
                    ApiKey = "test-key",
                    BaseUri = new Uri("https://127.0.0.1.invalid/v1beta"),
                },
            },
        });

        var model = (await backend.ListModelsAsync().ConfigureAwait(false)).Single();

        Assert.AreEqual("gemini-test", model.Id);
        Assert.AreEqual("Gemini Test", model.DisplayName);
        Assert.AreEqual(1000000L, model.Capabilities?["contextWindow"]);
    }

    [TestMethod]
    public async Task AnthropicAgentBackend_UsesModelsDevContextWindowForUsageSnapshots()
    {
        await using var catalog = CreateCatalog();
        using var temp = TestTempDirectory.Create();
        var client = new RecordingChatClient(
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Answer")])
            {
                MessageId = "anthropic-message",
                ResponseId = "anthropic-response",
                ConversationId = "anthropic-conversation",
                ModelId = "claude-sonnet-test",
            },
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails
            {
                InputTokenCount = 20,
                OutputTokenCount = 4,
                TotalTokenCount = 24,
            })])
            {
                MessageId = "anthropic-message",
                ResponseId = "anthropic-response",
                ConversationId = "anthropic-conversation",
                ModelId = "claude-sonnet-test",
            },
        ]);

        await using var backend = new AnthropicAgentBackend(new AnthropicAgentBackendOptions
        {
            StateRootPath = temp.Path,
            Providers =
            {
                new AnthropicProviderOptions
                {
                    ProviderKey = "anthropic",
                    IsDefault = true,
                    ModelsDevProviderId = "anthropic",
                    ModelCatalog = catalog,
                    ChatClientFactory = () => client,
                    ModelListAsync = static _ => Task.FromResult<IReadOnlyList<AgentModelInfo>>(
                    [
                        new AgentModelInfo("claude-sonnet-test", Provider: "anthropic"),
                    ]),
                },
            },
        });

        await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
        {
            Model = "claude-sonnet-test",
            WorkingDirectory = temp.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).ConfigureAwait(false);

        _ = await session.SendAsync(new AgentSendOptions
        {
            Input = new AgentInput([new AgentInputItem.Text("Hello")]),
        }).ConfigureAwait(false);

        var usageEvent = (await session.GetHistoryAsync().ConfigureAwait(false))
            .OfType<AgentSessionUpdateEvent>()
            .Single(static e => e.Kind == AgentSessionUpdateKind.UsageUpdated);

        Assert.AreEqual(24L, usageEvent.Usage?.CurrentTokens);
        Assert.AreEqual(200000L, usageEvent.Usage?.TokenLimit);
    }

    [TestMethod]
    public async Task ModelsDevCatalogService_StartBackgroundRefresh_UpdatesSnapshotAndCache()
    {
        var refreshedDatabase = ModelsDevDatabaseJson.Deserialize(
            """
            {
              "openai": {
                "id": "openai",
                "name": "OpenAI",
                "models": {
                  "gpt-5-test": {
                    "id": "gpt-5-test",
                    "name": "GPT 5 Test Refreshed",
                    "limit": { "context": 777777, "output": 64000 }
                  }
                }
              }
            }
            """);
        using var temp = TestTempDirectory.Create();
        using var httpClient = new HttpClient(new StaticHttpMessageHandler(ModelsDevDatabaseJson.Serialize(refreshedDatabase)));
        await using var catalog = new ModelsDevCatalogService(
            CreateDatabase(),
            new ModelsDevCatalogServiceOptions
            {
                CacheFilePath = Path.Combine(temp.Path, "models_dev_db.json"),
                HttpClient = httpClient,
                RefreshUri = new Uri("https://example.invalid/models.dev.json"),
                RefreshInterval = TimeSpan.FromHours(1),
            });

        catalog.StartBackgroundRefresh();

        var updated = await WaitUntilAsync(
            () => catalog.TryGetModel("openai", "gpt-5-test", out var model) &&
                  model.Limit?.Context == 777777,
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.IsTrue(updated);
        Assert.IsTrue(File.Exists(Path.Combine(temp.Path, "models_dev_db.json")));
        Assert.IsTrue(catalog.TryGetModel("openai", "gpt-5-test", out var refreshedModel));
        Assert.AreEqual("GPT 5 Test Refreshed", refreshedModel.Name);
    }

    private static ModelsDevDatabase CreateDatabase()
        => ModelsDevDatabaseJson.Deserialize(
            """
            {
              "openai": {
                "id": "openai",
                "name": "OpenAI",
                "models": {
                  "gpt-5-test": {
                    "id": "gpt-5-test",
                    "name": "GPT 5 Test",
                    "reasoning": true,
                    "tool_call": true,
                    "modalities": { "input": ["text"], "output": ["text"] },
                    "limit": { "context": 400000, "output": 128000 }
                  }
                }
              },
              "anthropic": {
                "id": "anthropic",
                "name": "Anthropic",
                "models": {
                  "claude-sonnet-test": {
                    "id": "claude-sonnet-test",
                    "name": "Claude Sonnet Test",
                    "tool_call": true,
                    "limit": { "context": 200000, "output": 64000 }
                  }
                }
              },
              "google": {
                "id": "google",
                "name": "Google",
                "models": {
                  "gemini-test": {
                    "id": "gemini-test",
                    "name": "Gemini Test",
                    "tool_call": true,
                    "limit": { "context": 1000000, "output": 65536 }
                  }
                }
              }
            }
            """);

    private static ModelsDevCatalogService CreateCatalog()
        => new(CreateDatabase(), new ModelsDevCatalogServiceOptions());

    private sealed class RecordingChatClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(updates.ToChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update.Clone();
                await Task.Yield();
            }
        }
    }

    private sealed class StaticHttpMessageHandler(string responseContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(responseContent),
                });
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return predicate();
    }

    private sealed class TestTempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TestTempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "models-dev-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestTempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
