using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The options of a session (<see cref="AgentSessionOptions"/>): recorded with it and kept when it is resumed, forked or
/// restarted; never wider for a session another session creates; the instructions of a session without sub-sessions do not
/// mention them.
/// </summary>
[TestClass]
public sealed class SessionOptionsRuntimeTests
{
    private static readonly AgentSessionOptions Off = new() { SubSessions = false };

    [TestMethod]
    public void Within_TurnsAnOptionOn_OnlyWhereTheCreatorHasIt()
    {
        Assert.IsTrue(AgentSessionOptions.Default.Within(null).SubSessions);
        Assert.IsTrue(AgentSessionOptions.Default.Within(AgentSessionOptions.Default).SubSessions);
        Assert.IsFalse(AgentSessionOptions.Default.Within(Off).SubSessions);
        Assert.IsFalse(Off.Within(AgentSessionOptions.Default).SubSessions);
        Assert.IsNull(AgentSessionOptions.Stored(AgentSessionOptions.Default));
        Assert.IsNull(AgentSessionOptions.Stored(new AgentSessionOptions()));
        Assert.AreSame(Off, AgentSessionOptions.Stored(Off));
        Assert.IsTrue(AgentSessionOptions.Of(null).SubSessions);
    }

    [TestMethod]
    public async Task ASessionWithoutSubSessions_IsToldNothingOfThem_AndKeepsItsOptionsAfterARestart()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        string sessionId;
        await using (var host = await CreateHostAsync(temp, provider))
        {
            var withSubSessions = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
            await SendAsync(host, withSubSessions, "Plan the work.");
            var full = provider.Instructions[^1]!;
            StringAssert.Contains(full, "## Sub-agents");
            StringAssert.Contains(full, "--no-sub-sessions");

            var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null, null, null, null, null, null, null, Off);
            sessionId = session.SessionId;
            Assert.IsFalse(AgentSessionOptions.Of(session.Options).SubSessions);
            await SendAsync(host, sessionId, "Do the work.");
            AssertWithoutSubAgents(provider.Instructions[^1]!);
            Assert.IsFalse((await host.RuntimeService.GetSessionOptionsAsync(sessionId)).SubSessions);
        }

        // A restart reads the options from the journal: the session is still told nothing of sub-agents.
        await using (var host = await CreateHostAsync(temp, provider))
        {
            var resumed = await host.RuntimeService.TryGetResumableSessionAsync(sessionId);
            Assert.IsNotNull(resumed);
            Assert.IsFalse(AgentSessionOptions.Of(resumed.Options).SubSessions);
            Assert.IsFalse((await host.RuntimeService.GetSessionOptionsAsync(sessionId)).SubSessions);
            await SendAsync(host, sessionId, "Go on.");
            AssertWithoutSubAgents(provider.Instructions[^1]!);
        }
    }

    [TestMethod]
    public async Task AChild_NeverHasMoreThanItsCreator_AndASessionWithoutSubSessionsCreatesNorForksNone()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var parent = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;

        // A creator with sub-sessions gives its children what it asks for them, on by default.
        var branch = await CreateChildAsync(host, provider, parent, null);
        Assert.IsTrue(AgentSessionOptions.Of(branch.Options).SubSessions);
        var leaf = await CreateChildAsync(host, provider, parent, Off);
        Assert.IsFalse(AgentSessionOptions.Of(leaf.Options).SubSessions);

        // A creator without sub-sessions creates none, whatever it asks.
        await Assert.ThrowsExactlyAsync<SubSessionsOffException>(() => CreateChildAsync(host, provider, leaf.SessionId, AgentSessionOptions.Default));
        await Assert.ThrowsExactlyAsync<SubSessionsOffException>(() => CreateChildAsync(host, provider, leaf.SessionId, null));

        // Nor forks one; the user forks it, and the fork keeps its options.
        await SendAsync(host, leaf.SessionId, "Work.");
        await Assert.ThrowsExactlyAsync<SubSessionsOffException>(() => host.RuntimeService.ForkSessionAsync(new SessionForkRequest
        {
            SessionId = branch.SessionId,
            CreatedBy = new AltaActorProvenance { Kind = "agent", SourceSessionId = leaf.SessionId },
        }));
        var fork = await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = leaf.SessionId });
        Assert.AreEqual("ok", fork.Status);
        Assert.IsFalse(AgentSessionOptions.Of(fork.Session!.Options).SubSessions);
        Assert.IsFalse((await host.RuntimeService.GetSessionOptionsAsync(fork.Session.SessionId)).SubSessions);
    }

    [TestMethod]
    public async Task ANewChoice_AppliesFromTheNextPrompt()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
        await SendAsync(host, sessionId, "First.");
        StringAssert.Contains(provider.Instructions[^1]!, "## Sub-agents");

        var changed = await host.Commands.SetSessionOptionsAsync(sessionId, Off);
        Assert.AreEqual("ok", changed.Status);
        await SendAsync(host, sessionId, "Second.");
        AssertWithoutSubAgents(provider.Instructions[^1]!);

        Assert.AreEqual("ok", (await host.Commands.SetSessionOptionsAsync(sessionId, null)).Status);
        await SendAsync(host, sessionId, "Third.");
        StringAssert.Contains(provider.Instructions[^1]!, "## Sub-agents");
    }

    [TestMethod]
    public async Task TheContextSizeOfASession_WinsOverTheOneOfItsProvider_AndIsKeptAfterARestart()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider(defaultContextSize: 400_000);
        string sessionId;
        await using (var host = await CreateHostAsync(temp, provider))
        {
            sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
            await SendAsync(host, sessionId, "First.");
            Assert.AreEqual(400_000L, provider.ContextSizes[^1], "the size of the provider");

            Assert.AreEqual("ok", (await host.Commands.SetSessionOptionsAsync(sessionId, new AgentSessionOptions { ContextSize = 100_000 })).Status);
            await SendAsync(host, sessionId, "Second.");
            Assert.AreEqual(100_000L, provider.ContextSizes[^1], "the size of the session, from its next prompt");
        }

        await using (var host = await CreateHostAsync(temp, provider))
        {
            Assert.AreEqual(100_000L, (await host.RuntimeService.GetSessionOptionsAsync(sessionId)).ContextSize);
            await SendAsync(host, sessionId, "Third.");
            Assert.AreEqual(100_000L, provider.ContextSizes[^1]);
        }
    }

    private static void AssertWithoutSubAgents(string instructions)
    {
        StringAssert.Contains(instructions, "## Working loop");
        // What a session is told of being a sub-agent itself stays: only the sessions it would start are left out.
        foreach (var text in new[] { "## Sub-agents", "your sub-agents", "alta session create", "--no-sub-sessions", "Model tiers:", "agent()" })
        {
            Assert.DoesNotContain(text, instructions, text);
        }
    }

    private static Task<SessionViewDescriptor> CreateChildAsync(CodeAltaHost host, RecordingProvider provider, string creator, AgentSessionOptions? options)
        => host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null,
            new AltaActorProvenance { Kind = "agent", SourceSessionId = creator }, null, null, null, null, creator, options);

    private static async Task SendAsync(CodeAltaHost host, string sessionId, string prompt)
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, prompt)).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(TestDeadlines.Hang);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
    }

    private static Task<CodeAltaHost> CreateHostAsync(TempDirectory temp, RecordingProvider provider)
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

    private sealed class RecordingProvider(long? defaultContextSize = null) : IAgentModelProviderRuntime, IModelProviderTurnExecutor, IModelProviderModelCatalog
    {
        private static readonly AgentModelInfo Model = new("fake-model", DisplayName: "Fake Model",
            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal) { ["inputTokenLimit"] = 100_000L }) { ContextSizes = [100_000, 400_000] };

        public List<string?> Instructions { get; } = [];

        /// <summary>The size of context of the model of each turn.</summary>
        public List<long?> ContextSizes { get; } = [];

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-options"), "Fake Options")
            { DefaultModelId = "fake-model", DefaultContextSize = defaultContextSize };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-options", DisplayName = "Fake Options", TransportKind = AgentTransportKind.OpenAIResponses,
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
                Models = [Model],
                SelectedModelId = "fake-model",
            });

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(ModelProviderRuntimeDescriptor provider, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([Model]);

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            lock (Instructions)
            {
                Instructions.Add(request.SystemMessage + "\n" + request.DeveloperInstructions + "\n" + request.SessionInstructions);
                ContextSizes.Add(request.ModelInfo?.GetContextSize());
            }

            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]),
            });
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

        public static TempDirectory Create() => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.Options.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A folder still held by a process that is ending is left to the cleanup of the temporary folder.
            }
        }
    }
}
