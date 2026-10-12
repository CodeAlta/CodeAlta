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
/// Every saved title is authoritative, including one that matches a creation placeholder. Attaching a session
/// leaves its saved title as it is rather than replacing it with the latest response summary.
/// </remarks>
[TestClass]
public sealed class SessionGivenTitleTests
{
    [TestMethod]
    public async Task PromptTitle_StaysUntilRename_EvenWhenARunningProviderReturnsAnotherTitle()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { ResponseTitle = "An unsolicited provider title" };
        string sessionId;
        {
            await using var host = await CreateHostAsync(temp, provider);
            sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Fix the login error")).SessionId;
            await SendAsync(host, sessionId);
            Assert.AreEqual("Fix the login error", await StoredTitleAsync(host, sessionId));
            Assert.AreEqual("Fix the login error", (await ListedAsync(host, sessionId)).Title);

            provider.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var send = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "Continue")).Receipt;
            Assert.IsNotNull(send);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                Assert.IsTrue(await host.Commands.RenameSessionAsync(sessionId, host.CurrentProject.Id, temp.ProjectRoot, "Repair authentication"));
            }
            finally { provider.Release.TrySetResult(); }
            Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await send.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
            Assert.AreEqual("Repair authentication", await StoredTitleAsync(host, sessionId));
            Assert.AreEqual("Repair authentication", (await ListedAsync(host, sessionId)).Title);
        }
        provider.Started = null;
        provider.Release = null;
        await using var reopened = await CreateHostAsync(temp, provider);
        Assert.AreEqual("Repair authentication", (await ListedAsync(reopened, sessionId)).Title);
        await SendAsync(reopened, sessionId);
        Assert.AreEqual("Repair authentication", await StoredTitleAsync(reopened, sessionId));
    }

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
            foreach (var sessionId in new[] { named, renamed, unnamed }) await SendAsync(host, sessionId);

            Assert.IsTrue(await host.Commands.RenameSessionAsync(renamed, host.CurrentProject.Id, temp.ProjectRoot, "Renamed"));
        }

        // Another run of the host: the sessions are listed, and attached as they are listed.
        {
            var provider = new AnsweringProvider();
            await using var host = await CreateHostAsync(temp, provider);
            foreach (var (sessionId, name) in new[] { (named, "Nightly review"), (renamed, "Renamed"), (unnamed, (string?)null) })
            {
                var listed = await ListedAsync(host, sessionId);
                Assert.AreEqual(name ?? host.CurrentProject.DisplayName, listed.Title);
                var saved = await StoredTitleAsync(host, sessionId);

                await host.RuntimeService.EnsureCoordinatorSessionAsync(listed, new SessionExecutionOptions
                {
                    ProviderId = provider.Descriptor.ProviderId,
                    ProviderKey = provider.Descriptor.ProviderId.Value,
                    WorkingDirectory = temp.ProjectRoot,
                    ProjectRoots = [temp.ProjectRoot],
                    Model = "fake-model",
                    OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                });

                // Both chosen titles and creation placeholders stay unchanged.
                Assert.AreEqual(name ?? host.CurrentProject.DisplayName, saved);
                Assert.AreEqual(saved, await StoredTitleAsync(host, sessionId));
            }
        }
    }

    [TestMethod]
    public async Task CreationTitle_StaysListedUnchanged_AfterASendAttachesItAgain()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider();
        string unnamed;
        {
            await using var host = await CreateHostAsync(temp, provider);
            unnamed = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
            await SendAsync(host, unnamed);
        }

        foreach (var reply in new[] { "A second answer.", "A third answer." })
        {
            await using var host = await CreateHostAsync(temp, provider);
            Assert.AreEqual(host.CurrentProject.DisplayName, (await ListedAsync(host, unnamed)).Title);
            provider.Reply = reply;
            await SendAsync(host, unnamed);
            Assert.AreEqual(host.CurrentProject.DisplayName, await StoredTitleAsync(host, unnamed));
        }

        {
            await using var host = await CreateHostAsync(temp, provider);
            Assert.AreEqual(host.CurrentProject.DisplayName, (await ListedAsync(host, unnamed)).Title);
        }
    }

    [TestMethod]
    public async Task RenameToCreationTitle_DoesNotResumeAutomaticSummaryNaming()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider();
        await using var host = await CreateHostAsync(temp, provider);
        foreach (var project in new[] { host.CurrentProject, (ProjectDescriptor?)null })
        {
            var session = await host.Commands.CreateDraftSessionAsync(project, provider.Descriptor, "Review authentication");
            var renamed = project?.DisplayName ?? "Global Session";
            Assert.IsTrue(await host.Commands.RenameSessionAsync(session.SessionId, project?.Id, project?.ProjectPath ?? temp.GlobalRoot, renamed));
            await SendAsync(host, session.SessionId);
            Assert.AreEqual(renamed, (await ListedAsync(host, session.SessionId)).Title);
            var metadata = await host.SessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(session.SessionId);
            Assert.IsNotNull(metadata);
            Assert.AreEqual(renamed, SessionRuntimeService.ListedTitle(metadata, session.Kind, project));
        }
    }

    private static async Task SendAsync(CodeAltaHost host, string sessionId)
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "one")).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
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

        public string Reply { get; set; } = Answer;

        public string? ResponseTitle { get; set; }

        public TaskCompletionSource? Started { get; set; }

        public TaskCompletionSource? Release { get; set; }

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

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            Started?.TrySetResult();
            if (Release is { } release) await release.Task.WaitAsync(cancellationToken);
            return new AgentTurnResponse
            {
                Title = ResponseTitle,
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(Reply)]),
            };
        }

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
