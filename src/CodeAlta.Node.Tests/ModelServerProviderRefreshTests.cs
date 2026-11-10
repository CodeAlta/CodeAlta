using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Fleet;
using CodeAlta.ModelServers;

namespace CodeAlta.Node.Tests;

[TestClass]
public sealed class ModelServerProviderRefreshTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ModelChangesDuringAProbe_AreReadAgain_AndActivityDoesNotProbe()
    {
        using var root = new NodeTestRoot();
        var token = TestContext.CancellationToken;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var id = new ModelProviderId("llama-coder");
        var descriptor = new ModelProviderDescriptor(id, "Coder", "llama-cpp");
        var runtime = new ListingRuntime(descriptor, async cancellation =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(cancellation);
                return [];
            }
            return [new AgentModelInfo("new-model")];
        });
        await using var node = await NodeHost.StartAsync(root.Options() with { ConfigureModelProviders = registry => registry.RegisterOrReplace(descriptor, () => runtime) }, token);
        new CodeAltaConfigStore(node.Host.CatalogOptions).SaveGlobalProviderDefinitions(
            [new CodeAltaProviderDocument { ProviderKey = id.Value, ProviderType = "llama-cpp", Server = "machine/coder" }]);
        await using var refresh = new ModelServerProviderRefresh(node.Host, null);
        var server = Server("ready", "old-model");
        var first = refresh.UpdateAsync("machine", [server]);
        try
        {
            await firstStarted.Task.WithinAsync(TestTime.Seconds(10), token);
            var changed = Server("ready", "new-model");
            var next = refresh.UpdateAsync("machine", [changed]);
            releaseFirst.SetResult();
            await Task.WhenAll(first, next).WithinAsync(TestTime.Seconds(10), token);
            Assert.AreEqual(2, calls, "the snapshot received during the first probe is not lost");
            Assert.AreEqual("new-model", (await node.Host.ModelProviderInitializationService.GetModelsAsync(id, token)).Single().Id);

            // The slot counters, queued requests, progress and speed change all the time, not the catalog.
            var activity = changed with
            {
                Status = changed.Status with
                {
                    Models = [changed.Status.Models.Single() with { BusySlots = 1, TotalSlots = 2, Waiting = 3, TokensPerSecond = 42, Progress = 0.5 }],
                },
            };
            await refresh.UpdateAsync("machine", [activity]).WithinAsync(TestTime.Seconds(10), token);
            Assert.AreEqual(2, calls);

            var context = activity with { Status = activity.Status with { Models = [activity.Status.Models.Single() with { Context = 8192, Vision = true }] } };
            await refresh.UpdateAsync("machine", [context]).WithinAsync(TestTime.Seconds(10), token);
            Assert.AreEqual(3, calls, "context and image support are model metadata");

            await refresh.UpdateAsync("machine", [Server("waiting", "new-model")]).WithinAsync(TestTime.Seconds(10), token);
            Assert.AreEqual(3, calls, "a waiting server is not probed");
            await refresh.UpdateAsync("machine", [context]).WithinAsync(TestTime.Seconds(10), token);
            Assert.AreEqual(4, calls, "becoming ready reads the catalog again");

            await refresh.UpdateAsync("machine", []).WithinAsync(TestTime.Seconds(10), token);
            await refresh.UpdateAsync("machine", [context]).WithinAsync(TestTime.Seconds(10), token);
            Assert.AreEqual(5, calls, "reconnection reads the server again even if its status is unchanged");
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
    }

    [TestMethod]
    public async Task OnlyEnabledLlamaProvidersOfTheChangedServer_AreRefreshed()
    {
        using var root = new NodeTestRoot();
        var token = TestContext.CancellationToken;
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var definitions = new[]
        {
            new CodeAltaProviderDocument { ProviderKey = "unregistered", ProviderType = "llama-cpp", Server = "machine/coder" },
            new CodeAltaProviderDocument { ProviderKey = "first", ProviderType = "llama-cpp", Server = "machine/coder" },
            new CodeAltaProviderDocument { ProviderKey = "alias", ProviderType = "llama-cpp", Server = "machine/coder" },
            new CodeAltaProviderDocument { ProviderKey = "disabled", ProviderType = "llama-cpp", Server = "machine/coder", Enabled = false },
            new CodeAltaProviderDocument { ProviderKey = "other-machine", ProviderType = "llama-cpp", Server = "elsewhere/coder" },
            new CodeAltaProviderDocument { ProviderKey = "other-server", ProviderType = "llama-cpp", Server = "machine/other" },
        };
        await using var node = await NodeHost.StartAsync(root.Options() with
        {
            ConfigureModelProviders = registry =>
            {
                foreach (var definition in definitions)
                {
                    if (definition.ProviderKey == "unregistered") continue;
                    var descriptor = new ModelProviderDescriptor(new(definition.ProviderKey), definition.ProviderKey, "llama-cpp");
                    registry.RegisterOrReplace(descriptor, () => new ListingRuntime(descriptor, _ =>
                    {
                        calls[definition.ProviderKey] = calls.GetValueOrDefault(definition.ProviderKey) + 1;
                        return Task.FromResult<IReadOnlyList<AgentModelInfo>>([]);
                    }));
                }
            },
        }, token);
        new CodeAltaConfigStore(node.Host.CatalogOptions).SaveGlobalProviderDefinitions(definitions);
        await using var refresh = new ModelServerProviderRefresh(node.Host, null);
        await refresh.UpdateAsync("machine", [Server("ready", "model")]).WithinAsync(TestTime.Seconds(10), token);
        CollectionAssert.AreEquivalent(new[] { "first", "alias" }, calls.Keys.ToArray());
        Assert.IsTrue(calls.Values.All(static count => count == 1));
        await refresh.DisposeAsync();
        await refresh.UpdateAsync("machine", [Server("ready", "another-model")]).WithinAsync(TestTime.Seconds(10), token);
        Assert.IsTrue(calls.Values.All(static count => count == 1), "a disposed observer starts no probe");
    }

    private static ServerView Server(string state, string model)
        => new(new ServerDefinition { Id = "coder", Name = "Coder", Backend = "cpu", Models = [new ServerModel { Model = model }] },
            ServerStatus.Stopped with { State = state, Models = [new ServerModelStatus(model, "loaded", null, 4096, null, 0, 1, null, false)] });

    private sealed class ListingRuntime(ModelProviderDescriptor descriptor, Func<CancellationToken, Task<IReadOnlyList<AgentModelInfo>>> list) : IAgentModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor => descriptor;
        public ModelProviderRuntimeDescriptor RuntimeDescriptor => throw new NotSupportedException();
        public IModelProviderModelCatalog? ModelCatalog => null;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => new() { ProviderId = descriptor.ProviderId, Availability = ModelProviderAvailability.Ready, Models = await list(cancellationToken) };
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();
        public AgentRuntimeProviderRegistration CreateProviderRegistration() => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
