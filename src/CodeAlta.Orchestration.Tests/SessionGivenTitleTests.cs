using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The name a session was given stays, whichever frontend attaches the session again.
/// </summary>
/// <remarks>
/// A frontend that lists the sessions itself, as the terminal UI does, gets a named session under its name, and a
/// session that was never named under the first line of its summary, which is the title it is attached with.
/// </remarks>
[TestClass]
public sealed class SessionGivenTitleTests
{
    [TestMethod]
    public async Task NamedSession_KeepsItsName_WhenItIsAttachedFromAListOfSessions()
    {
        using var temp = TempDirectory.Create();
        string named, renamed, unnamed;
        {
            var provider = new AnsweringProvider();
            await using var host = await CreateHostAsync(temp, provider);
            named = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Nightly review")).SessionId;
            renamed = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
            unnamed = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
            foreach (var sessionId in new[] { named, renamed, unnamed })
            {
                var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "one")).Receipt;
                Assert.IsNotNull(receipt);
                var result = await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
            }

            Assert.IsTrue(await host.Commands.RenameSessionAsync(renamed, host.CurrentProject.Id, temp.ProjectRoot, "Renamed"));
        }

        // Another run of the host: the sessions are listed, and attached as they are listed.
        {
            var provider = new AnsweringProvider();
            await using var host = await CreateHostAsync(temp, provider);
            foreach (var (sessionId, name) in new[] { (named, "Nightly review"), (renamed, "Renamed"), (unnamed, (string?)null) })
            {
                var listed = await ListedAsync(host, sessionId);
                // The list names a session by the name it was given, and one that was never named by what it last said:
                // a parent session finds the sub-agents it created by the titles it gave them.
                Assert.AreEqual(name ?? AnsweringProvider.Answer, listed.Title);

                await host.RuntimeService.EnsureCoordinatorSessionAsync(listed, new SessionExecutionOptions
                {
                    ProviderId = provider.Descriptor.ProviderId,
                    ProviderKey = provider.Descriptor.ProviderId.Value,
                    WorkingDirectory = temp.ProjectRoot,
                    ProjectRoots = [temp.ProjectRoot],
                    Model = "fake-model",
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                });

                // The name stays; a session that was never named takes the line it is listed with.
                Assert.AreEqual(name ?? AnsweringProvider.Answer, await StoredTitleAsync(host, sessionId));
            }
        }
    }

    private static Task<CodeAltaHost> CreateHostAsync(TempDirectory temp, AnsweringProvider provider)
        => CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = temp.GlobalRoot,
                CurrentProjectPath = temp.ProjectRoot,
                IsHeadless = true,
                AutoApproveOwnedPermissions = true,
                OwnedCommandReceiptCapacity = 32,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            },
            CancellationToken.None);

    private static async Task<SessionViewDescriptor> ListedAsync(CodeAltaHost host, string sessionId)
    {
        await foreach (var session in host.RuntimeService.ListRecoverableSessionsAsync(cancellationToken: CancellationToken.None))
        {
            if (session.SessionId == sessionId) return session;
        }

        Assert.Fail($"Session '{sessionId}' is not listed.");
        return null!;
    }

    private static async Task<string?> StoredTitleAsync(CodeAltaHost host, string sessionId)
    {
        var metadata = await host.SessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(sessionId, CancellationToken.None);
        Assert.IsNotNull(metadata);
        return (metadata.Details as RawApiSessionMetadataDetails)?.Title;
    }

    private sealed class AnsweringProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public const string Answer = "The answer of the model.";

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-titles"), "Fake Titles") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-titles", DisplayName = "Fake Titles", TransportKind = AgentTransportKind.OpenAIResponses,
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

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(Answer)]),
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string root)
        {
            Root = root;
            GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            ProjectRoot = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        }

        public string Root { get; }

        public string GlobalRoot { get; }

        public string ProjectRoot { get; }

        public static TempDirectory Create() => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.Titles.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
