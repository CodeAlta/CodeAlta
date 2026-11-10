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
        // A fast mode chosen off is a choice: it is recorded, unlike a session that chose nothing.
        Assert.IsNotNull(AgentSessionOptions.Stored(new AgentSessionOptions { FastMode = false }));
        Assert.IsNull(AgentSessionOptions.Default.FastMode);
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

            Assert.AreEqual("ok", (await host.Commands.SetSessionOptionsAsync(sessionId, new AgentSessionOptions { ContextSize = 123_456 })).Status);
            await SendAsync(host, sessionId, "Another model's size.");
            Assert.AreEqual(400_000L, provider.ContextSizes[^1], "a size the model does not have leaves the one of the provider");

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

    [TestMethod]
    public async Task TheFastModeOfASession_IsSentOnceChosen_AndIsKeptAfterARestart()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider(fastMode: false) { ReportedFastMode = true };
        string sessionId;
        await using (var host = await CreateHostAsync(temp, provider))
        {
            sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
            await SendAsync(host, sessionId, "First.");
            Assert.IsNull(provider.FastModes[^1], "a session that chose nothing sends nothing: the provider does as it does");
            Assert.IsTrue((await host.RuntimeService.GetCurrentStateAsync(sessionId)).Entry?.ReportedFastMode, "the speed the response reported");

            Assert.AreEqual("ok", (await host.Commands.SetSessionOptionsAsync(sessionId, new AgentSessionOptions { FastMode = false })).Status);
            Assert.IsNull((await host.RuntimeService.GetCurrentStateAsync(sessionId)).Entry?.ReportedFastMode,
                "a speed reported before another choice says nothing of the turns to come");
            await SendAsync(host, sessionId, "Second.");
            Assert.IsFalse(provider.FastModes[^1], "off is sent: the provider runs its standard speed");

            Assert.AreEqual("ok", (await host.Commands.SetSessionOptionsAsync(sessionId, new AgentSessionOptions { FastMode = true })).Status);
            await SendAsync(host, sessionId, "Third.");
            Assert.IsTrue(provider.FastModes[^1]);
        }

        await using (var host = await CreateHostAsync(temp, provider))
        {
            Assert.IsTrue((await host.RuntimeService.GetSessionOptionsAsync(sessionId)).FastMode);
            await SendAsync(host, sessionId, "Fourth.");
            Assert.IsTrue(provider.FastModes[^1]);
        }

        // A model without fast mode is sent nothing, whatever the session chose.
        var without = new RecordingProvider();
        using var other = TempDirectory.Create();
        await using (var host = await CreateHostAsync(other, without))
        {
            var session = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, without.Descriptor, null, null, null, null, null, null, null,
                new AgentSessionOptions { FastMode = true })).SessionId;
            await SendAsync(host, session, "First.");
            Assert.IsNull(without.FastModes[^1]);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FastMode_IsKeptWhenASessionIsForkedAndMoved(bool? fastMode)
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider(fastMode: false);
        string forkId;
        await using (var host = await CreateHostAsync(temp, provider))
        {
            var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
            Assert.AreEqual("ok", (await host.Commands.SetSessionOptionsAsync(sessionId, new AgentSessionOptions { FastMode = fastMode })).Status);
            await SendAsync(host, sessionId, "First.");

            var fork = await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = sessionId });
            Assert.AreEqual("ok", fork.Status);
            forkId = fork.Session!.SessionId;
            Assert.AreEqual(fastMode, AgentSessionOptions.Of(fork.Session.Options).FastMode);
            await SendAsync(host, forkId, "Fork.");
            Assert.AreEqual(fastMode, provider.FastModes[^1]);

            var target = await host.ProjectCatalog.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp.Root, "target")).FullName);
            var moved = await host.Commands.MoveSessionAsync(forkId, host.CurrentProject.Id, temp.ProjectRoot, target.Id, target.ProjectPath);
            Assert.AreEqual("ok", moved.Status, string.Join(" ", moved.Warnings));
            Assert.AreEqual(fastMode, (await host.RuntimeService.GetSessionOptionsAsync(forkId)).FastMode);
            await SendAsync(host, forkId, "Moved.");
            Assert.AreEqual(fastMode, provider.FastModes[^1]);
        }

        await using (var host = await CreateHostAsync(temp, provider))
        {
            Assert.AreEqual(fastMode, (await host.RuntimeService.GetSessionOptionsAsync(forkId)).FastMode);
            await SendAsync(host, forkId, "Resumed.");
            Assert.AreEqual(fastMode, provider.FastModes[^1]);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FastMode_JsonRoundTrip_PreservesAnExplicitChoice(bool fastMode)
    {
        var options = new AgentSessionOptions { FastMode = fastMode };
        var json = System.Text.Json.JsonSerializer.Serialize(options);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual(fastMode, document.RootElement.GetProperty("fast_mode").GetBoolean());
        Assert.AreEqual(options, System.Text.Json.JsonSerializer.Deserialize<AgentSessionOptions>(json));
        Assert.IsNull(System.Text.Json.JsonSerializer.Deserialize<AgentSessionOptions>("{}")!.FastMode,
            "Sessions recorded before fast mode must keep following their provider.");
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

    private sealed class RecordingProvider(long? defaultContextSize = null, bool? fastMode = null) : IAgentModelProviderRuntime, IModelProviderTurnExecutor, IModelProviderModelCatalog
    {
        private readonly AgentModelInfo _model = new("fake-model", DisplayName: "Fake Model",
            Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal) { ["inputTokenLimit"] = 100_000L }) { ContextSizes = [100_000, 400_000], FastMode = fastMode };

        public List<string?> Instructions { get; } = [];

        /// <summary>The size of context of the model of each turn.</summary>
        public List<long?> ContextSizes { get; } = [];

        /// <summary>The fast mode of each turn.</summary>
        public List<bool?> FastModes { get; } = [];

        /// <summary>The fast mode each response reports, or null for none.</summary>
        public bool? ReportedFastMode { get; init; }

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
                Models = [_model],
                SelectedModelId = "fake-model",
            });

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(ModelProviderRuntimeDescriptor provider, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([_model]);

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            lock (Instructions)
            {
                Instructions.Add(request.SystemMessage + "\n" + request.DeveloperInstructions + "\n" + request.SessionInstructions);
                ContextSizes.Add(request.ModelInfo?.GetContextSize());
                FastModes.Add(request.FastMode);
            }

            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]),
                Usage = ReportedFastMode is null ? null : new AgentSessionUsage(
                    LastOperation: new AgentOperationUsageSnapshot(Model: "fake-model", InputTokens: 10, OutputTokens: 2, FastMode: ReportedFastMode),
                    Scope: AgentUsageScope.LastOperation,
                    Source: AgentUsageSource.ProviderUsage,
                    UpdatedAt: DateTimeOffset.UtcNow),
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
