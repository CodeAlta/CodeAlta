using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The models of the providers of CodeAlta Desktop are described by models.dev: their limits, which compaction needs,
/// and their price, which the Models page shows (#206).
/// </summary>
[TestClass]
public sealed class DesktopModelsDevTests
{
    private const string Epoch = "epoch";

    private const string Snapshot = """
        {
          "openai": {
            "id": "openai", "name": "OpenAI",
            "models": {
              "gpt-6.1-sol": { "id": "gpt-6.1-sol", "name": "GPT-6.1 Sol",
                "limit": { "context": 1050000, "input": 922000, "output": 128000 }, "cost": { "input": 2.5, "output": 15 } }
            }
          },
          "anthropic": {
            "id": "anthropic", "name": "Anthropic",
            "models": {
              "claude-opus-4-8": { "id": "claude-opus-4-8", "name": "Claude Opus 4.8", "knowledge": "2026-01", "release_date": "2026-05-28",
                "modalities": { "input": ["text", "image", "pdf"], "output": ["text"] }, "open_weights": false,
                "limit": { "context": 1000000, "output": 128000 }, "cost": { "input": 5, "output": 25, "cache_read": 0.5, "cache_write": 6.25 } }
            }
          }
        }
        """;

    [TestMethod]
    public async Task AConfiguredOpenAIResponsesProvider_AppliedAgain_ListsItsModelWithTheLimitsOfModelsDev()
    {
        using var root = new TempRoot();
        await using var catalog = root.CreateCatalog();
        File.WriteAllText(new CatalogOptions { GlobalRoot = root.Path }.ConfigPath, """
            [providers.azure-openai]
            type = "openai-responses"
            api_key = "test-key"
            api_url = "https://example-resource.openai.azure.com/openai/v1"
            single_model_id = "gpt-6.1-sol"
            """);
        await using var registry = new ModelProviderRegistry();

        Assert.AreEqual(1, GlobalConfigService.ApplyProviders(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root.Path }), registry, root.Path, null, catalog));
        var models = await new ModelProviderInitializationService(registry).GetModelsAsync(new ModelProviderId("azure-openai"));

        var model = models.Single();
        Assert.AreEqual(1_050_000L, model.Capabilities?["contextWindow"]);
        Assert.AreEqual(922_000L, model.Capabilities?["inputTokenLimit"]);
        Assert.AreEqual(128_000L, model.Capabilities?["outputTokenLimit"]);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta-models-dev-" + Guid.NewGuid().ToString("N"));

        public TempRoot() => Directory.CreateDirectory(Path);

        // The snapshot of the build replaced by a small one; no cache and no refresh.
        public ModelsDevCatalogService CreateCatalog()
        {
            var snapshot = System.IO.Path.Combine(Path, "models_dev_db.json");
            File.WriteAllText(snapshot, Snapshot);
            return new ModelsDevCatalogService(new ModelsDevCatalogServiceOptions { SnapshotFilePath = snapshot });
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    private sealed class LiteralRuntime(ModelProviderDescriptor descriptor, IReadOnlyList<AgentModelInfo> models) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => descriptor;
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken token = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = descriptor.ProviderId, Models = models });
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new AssertFailedException("No turns.");
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken token = default) => throw new AssertFailedException("No sessions.");
        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken token = default) => throw new AssertFailedException("No sessions.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
