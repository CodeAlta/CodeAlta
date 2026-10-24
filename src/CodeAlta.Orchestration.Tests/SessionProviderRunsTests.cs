using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The runs of the sessions of providers that are about to be replaced or taken away (the plugin that gives their type
/// stops) are stopped as the user stops them, and the runs of the other providers go on.
/// </summary>
[TestClass]
public sealed class SessionProviderRunsTests
{
    [TestMethod]
    public async Task AbortRunsOfProviders_StopsTheRunsOfThoseProvidersOnly()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"CodeAlta.ProviderRuns.{Guid.NewGuid():N}")).FullName;
        try
        {
            var stopping = new HeldProvider("agent-of-a-plugin");
            var staying = new HeldProvider("other-provider");
            await using (var host = await CodeAltaHost.CreateAsync(
                             new CodeAltaHostOptions
                             {
                                 GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName,
                                 CurrentProjectPath = Directory.CreateDirectory(Path.Combine(root, "project")).FullName,
                                 IsHeadless = true,
                                 AutoApproveOwnedPermissions = true,
                                 OwnedCommandReceiptCapacity = 32,
                                 ConfigureModelProviders = registry =>
                                 {
                                     registry.RegisterOrReplace(stopping.Descriptor, () => stopping);
                                     registry.RegisterOrReplace(staying.Descriptor, () => staying);
                                 },
                             },
                             CancellationToken.None))
            {
                var first = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, stopping.Descriptor, "With the agent");
                var second = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, staying.Descriptor, "With another provider");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                Assert.IsNotNull(host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), first.SessionId, "one")).Receipt);
                Assert.IsNotNull(host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), second.SessionId, "two")).Receipt);
                await stopping.Started.Task.WaitAsync(timeout.Token);
                await staying.Started.Task.WaitAsync(timeout.Token);
                SessionRuntimeOverview Overview(string sessionId) => host.RuntimeService.ListOverview().Single(overview => overview.SessionId == sessionId);
                while (!Overview(first.SessionId).Running || !Overview(second.SessionId).Running) await Task.Delay(20, timeout.Token);
                Assert.AreEqual("agent-of-a-plugin", Overview(first.SessionId).ProviderKey);

                var aborted = await host.RuntimeService.AbortRunsOfProvidersAsync(["AGENT-of-a-plugin"], timeout.Token);

                CollectionAssert.AreEqual(new[] { first.SessionId }, aborted.ToArray());
                await stopping.Cancelled.Task.WaitAsync(timeout.Token);
                while (Overview(first.SessionId).Running) await Task.Delay(20, timeout.Token);
                Assert.IsTrue(Overview(second.SessionId).Running, "The run of another provider goes on.");
                Assert.IsFalse(staying.Cancelled.Task.IsCompleted);
                Assert.AreEqual(0, (await host.RuntimeService.AbortRunsOfProvidersAsync(["agent-of-a-plugin"], timeout.Token)).Count, "Nothing runs with it any more.");

                staying.Release.SetResult();
                while (Overview(second.SessionId).Running) await Task.Delay(20, timeout.Token);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A provider whose turn runs until it is released or stopped.
    private sealed class HeldProvider(string key) : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId(key), key) { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = key, DisplayName = key, TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };

        public IModelProviderTurnExecutor CreateTurnExecutor() => this;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo("fake-model", DisplayName: "Fake Model")],
                SelectedModelId = "fake-model",
            });

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("done")]),
            };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
