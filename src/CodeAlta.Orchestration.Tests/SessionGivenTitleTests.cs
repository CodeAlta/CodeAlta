using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The name a session was given stays, whichever frontend attaches the session again, and a session nobody named
/// is titled as the setting says.
/// </summary>
/// <remarks>
/// A title is saved with who chose it. The one somebody chose is never replaced; the one CodeAlta chose is written
/// by the session that runs, as its mode says, and listed as it is saved. A session written before that was
/// recorded is listed as it always was, and decided when it runs again.
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
            // The session nobody named has the title its first prompt gave it.
            foreach (var (sessionId, name) in new[] { (named, "Nightly review"), (renamed, "Renamed"), (unnamed, "one") })
            {
                var listed = await ListedAsync(host, sessionId);
                Assert.AreEqual(name, listed.Title);
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

                // Attaching a session changes neither a title somebody chose nor the one CodeAlta chose.
                Assert.AreEqual(name, saved);
                Assert.AreEqual(saved, await StoredTitleAsync(host, sessionId));
            }
        }
    }

    [TestMethod]
    public async Task UnnamedSession_IsTitledAsTheSettingSays()
    {
        foreach (var (mode, reply, afterFirst, afterSecond) in new[]
                 {
                     (AgentSessionTitleMode.FirstPrompt, "An answer.", "Fix the login error", "Fix the login error"),
                     (AgentSessionTitleMode.LastMessage, "An answer.", "An answer.", "A second answer."),
                     (AgentSessionTitleMode.Model, "<session_title>Login fix</session_title>\nAn answer.", "Login fix", "Login fix"),
                 })
        {
            using var temp = TempDirectory.Create();
            var provider = new AnsweringProvider { Reply = reply };
            string sessionId;
            {
                await using var host = await CreateHostAsync(temp, provider);
                host.RuntimeService.SaveSessionTitleMode(mode);
                sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
                Assert.AreEqual(host.CurrentProject.DisplayName, (await ListedAsync(host, sessionId)).Title, "Until its first prompt, a session has the title it was created with.");

                await SendAsync(host, sessionId, "Fix the login error. Then add a test.");
                Assert.AreEqual(afterFirst, (await ListedAsync(host, sessionId)).Title, mode.ToString());
                provider.Reply = "A second answer.";
                await SendAsync(host, sessionId, "Go on.");
                Assert.AreEqual(afterSecond, (await ListedAsync(host, sessionId)).Title, mode.ToString());
                Assert.AreEqual(AgentSessionTitleSource.Auto, await StoredSourceAsync(host, sessionId));
                // Only a session titled by its model is asked for a title.
                Assert.AreEqual(mode is AgentSessionTitleMode.Model, provider.Instructions[^1]!.Contains("Session title:", StringComparison.Ordinal), mode.ToString());

                // What a session shows has no title tag.
                var history = await host.RuntimeService.TryReadStoredHistoryAsync(await ListedAsync(host, sessionId));
                Assert.IsNotNull(history);
                Assert.IsFalse(history.OfType<AgentContentCompletedEvent>().Any(static content => content.Content.Contains("session_title", StringComparison.Ordinal)));
            }

            // Another run of the host lists the session under the same title, the one its deletion is confirmed with.
            await using var reopened = await CreateHostAsync(temp, provider);
            var listed = await ListedAsync(reopened, sessionId);
            Assert.AreEqual(afterSecond, listed.Title, mode.ToString());
            var metadata = await reopened.SessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(sessionId);
            Assert.IsNotNull(metadata);
            Assert.AreEqual(afterSecond, SessionRuntimeService.ListedTitle(metadata, listed.Kind, reopened.CurrentProject));
        }
    }

    [TestMethod]
    public async Task TitleSomebodyChose_IsNeverReplaced_AlsoWhenItIsTheOneASessionIsCreatedWith()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { Reply = "<session_title>From the model</session_title>\nAn answer." };
        await using var host = await CreateHostAsync(temp, provider);
        foreach (var mode in Enum.GetValues<AgentSessionTitleMode>())
        {
            host.RuntimeService.SaveSessionTitleMode(mode);
            foreach (var project in new[] { host.CurrentProject, (ProjectDescriptor?)null })
            {
                var session = await host.Commands.CreateDraftSessionAsync(project, provider.Descriptor, "Review authentication");
                var renamed = project?.DisplayName ?? "Global Session";
                Assert.IsTrue(await host.Commands.RenameSessionAsync(session.SessionId, project?.Id, project?.ProjectPath ?? temp.GlobalRoot, renamed));
                await SendAsync(host, session.SessionId, "Fix the login error.");
                Assert.AreEqual(renamed, (await ListedAsync(host, session.SessionId)).Title, mode.ToString());
                Assert.AreEqual(AgentSessionTitleSource.User, await StoredSourceAsync(host, session.SessionId));
                var metadata = await host.SessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(session.SessionId);
                Assert.IsNotNull(metadata);
                Assert.AreEqual(renamed, SessionRuntimeService.ListedTitle(metadata, session.Kind, project));
            }
        }
    }

    [TestMethod]
    public async Task SessionWrittenBeforeSourcesWereRecorded_IsListedAsBeforeAndDecidedWhenItRunsAgain()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider();
        string unnamed, named;
        {
            await using var host = await CreateHostAsync(temp, provider);
            unnamed = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
            named = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Nightly review")).SessionId;
            foreach (var sessionId in new[] { unnamed, named }) await SendAsync(host, sessionId);
        }

        {
            await using var host = await CreateHostAsync(temp, provider);
            // As an older build left them: no source, and for the session nobody named the title it was created with.
            var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
            foreach (var (sessionId, title) in new[] { (unnamed, host.CurrentProject.DisplayName), (named, "Nightly review") })
            {
                var summary = await store.GetSessionSummaryAsync(sessionId);
                Assert.IsNotNull(summary);
                await store.UpsertSessionAsync(summary with { Title = title, TitleSource = null });
            }

            // Listed as before: the one nobody named by what it last said, the other by its name.
            Assert.AreEqual(AnsweringProvider.Answer, (await ListedAsync(host, unnamed)).Title);
            Assert.AreEqual("Nightly review", (await ListedAsync(host, named)).Title);
            Assert.IsNull(await StoredSourceAsync(host, unnamed));
            Assert.AreEqual("unset", AgentSessionTitles.NameOf((await host.RuntimeService.GetSessionTitleAsync(unnamed))!.Source));

            // They run again, and are decided: the title that was the one a session is created with was never chosen.
            // The session keeps the title it was listed under; its first prompt is long past.
            host.RuntimeService.SaveSessionTitleMode(AgentSessionTitleMode.FirstPrompt);
            provider.Reply = "A second answer.";
            foreach (var sessionId in new[] { unnamed, named }) await SendAsync(host, sessionId, "Go on with the work.");
            Assert.AreEqual(AgentSessionTitleSource.Auto, await StoredSourceAsync(host, unnamed));
            Assert.AreEqual(AnsweringProvider.Answer, await StoredTitleAsync(host, unnamed));
            Assert.AreEqual(AnsweringProvider.Answer, (await ListedAsync(host, unnamed)).Title);
            Assert.AreEqual(AgentSessionTitleSource.User, await StoredSourceAsync(host, named));
            Assert.AreEqual("Nightly review", (await ListedAsync(host, named)).Title);
        }
    }

    [TestMethod]
    public async Task SessionMode_IsItsOwn_AndReachesTheSessionThatRuns()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { Reply = "The first answer." };
        string sessionId;
        {
            await using var host = await CreateHostAsync(temp, provider);
            host.RuntimeService.SaveSessionTitleMode(AgentSessionTitleMode.FirstPrompt);
            sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Review authentication")).SessionId;
            await SendAsync(host, sessionId, "Fix the login error.");
            var state = await host.RuntimeService.GetSessionTitleAsync(sessionId);
            Assert.AreEqual(new SessionTitleState(sessionId, "Review authentication", AgentSessionTitleSource.User, AgentSessionTitleMode.FirstPrompt, OwnMode: false), state);

            // Its own mode, while it is attached: the title is handed back, and is what the mode makes of the session.
            state = await host.RuntimeService.SetSessionTitleAsync(sessionId, title: null, changeMode: true, AgentSessionTitleMode.LastMessage);
            Assert.AreEqual(new SessionTitleState(sessionId, "The first answer.", AgentSessionTitleSource.Auto, AgentSessionTitleMode.LastMessage, OwnMode: true), state);
            Assert.AreEqual(AgentSessionTitleMode.FirstPrompt, host.RuntimeService.GetSessionTitleMode(), "The setting is not the session's to change.");
            provider.Reply = "The second answer.";
            await SendAsync(host, sessionId, "Go on.");
            Assert.AreEqual("The second answer.", (await ListedAsync(host, sessionId)).Title);

            // A name given with a mode is the one somebody chose: only the mode is recorded.
            state = await host.RuntimeService.SetSessionTitleAsync(sessionId, "Authentication", changeMode: true, AgentSessionTitleMode.Model);
            Assert.AreEqual(new SessionTitleState(sessionId, "Authentication", AgentSessionTitleSource.User, AgentSessionTitleMode.Model, OwnMode: true), state);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => host.RuntimeService.SetSessionTitleAsync(sessionId, " padded ", changeMode: false, null));
            Assert.IsNull(await host.RuntimeService.GetSessionTitleAsync("no-such-session"));
        }

        // Another run of the host: the mode is saved with the session, and the session follows the setting again on demand.
        await using var reopened = await CreateHostAsync(temp, provider);
        Assert.AreEqual("model", (await ListedAsync(reopened, sessionId)).TitleMode);
        var followed = await reopened.RuntimeService.SetSessionTitleAsync(sessionId, title: null, changeMode: true, mode: null);
        Assert.AreEqual(new SessionTitleState(sessionId, "Authentication", AgentSessionTitleSource.Auto, AgentSessionTitleMode.FirstPrompt, OwnMode: false), followed);
        Assert.IsNull((await ListedAsync(reopened, sessionId)).TitleMode);
    }

    [TestMethod]
    public async Task Fork_IsTitledAsItsSourceIs_UnlessItIsGivenATitle()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { Reply = "The first answer." };
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
        await SendAsync(host, sessionId, "Fix the login error.");
        Assert.IsNotNull(await host.RuntimeService.SetSessionTitleAsync(sessionId, title: null, changeMode: true, AgentSessionTitleMode.LastMessage));

        // Without a title: the title CodeAlta chose, which goes on following the mode the source has of its own.
        var fork = (await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = sessionId })).Session;
        Assert.IsNotNull(fork);
        Assert.AreEqual("The first answer. (fork)", fork.Title);
        Assert.AreEqual(AgentSessionTitleSource.Auto, await StoredSourceAsync(host, fork.SessionId));
        Assert.AreEqual("last_message", (await ListedAsync(host, fork.SessionId)).TitleMode);
        provider.Reply = "The answer of the fork.";
        await SendAsync(host, fork.SessionId, "Go on.");
        Assert.AreEqual("The answer of the fork.", (await ListedAsync(host, fork.SessionId)).Title);

        // With a title: the one somebody chose.
        var named = (await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = sessionId, Title = "Other approach" })).Session;
        Assert.IsNotNull(named);
        Assert.AreEqual(AgentSessionTitleSource.User, await StoredSourceAsync(host, named.SessionId));
        await SendAsync(host, named.SessionId, "Go on.");
        Assert.AreEqual("Other approach", (await ListedAsync(host, named.SessionId)).Title);
    }

    [TestMethod]
    public async Task SettingChange_ReachesAnAttachedSessionWithItsNextPrompt()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { Reply = "The first answer." };
        await using var host = await CreateHostAsync(temp, provider);
        host.RuntimeService.SaveSessionTitleMode(AgentSessionTitleMode.FirstPrompt);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
        await SendAsync(host, sessionId, "Fix the login error.");
        Assert.AreEqual("Fix the login error", (await ListedAsync(host, sessionId)).Title);
        Assert.IsFalse(provider.Instructions[^1]!.Contains("Session title:", StringComparison.Ordinal));

        host.RuntimeService.SaveSessionTitleMode(AgentSessionTitleMode.Model);
        provider.Reply = "<session_title>Login fix</session_title>\nThe second answer.";
        await SendAsync(host, sessionId, "Go on.");
        StringAssert.Contains(provider.Instructions[^1], "Session title:");
        Assert.AreEqual("Login fix", (await ListedAsync(host, sessionId)).Title);

        host.RuntimeService.SaveSessionTitleMode(AgentSessionTitleMode.LastMessage);
        provider.Reply = "The third answer.";
        await SendAsync(host, sessionId, "And again.");
        Assert.IsFalse(provider.Instructions[^1]!.Contains("Session title:", StringComparison.Ordinal));
        Assert.AreEqual("The third answer.", (await ListedAsync(host, sessionId)).Title);
    }

    private static async Task SendAsync(CodeAltaHost host, string sessionId, string prompt = "one")
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, prompt)).Receipt;
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

    private static async Task<AgentSessionTitleSource?> StoredSourceAsync(CodeAltaHost host, string sessionId)
    {
        var metadata = await host.SessionViewCatalog.JournalStore.CreateSessionStore().GetSessionAsync(sessionId, CancellationToken.None);
        Assert.IsNotNull(metadata);
        return (metadata.Details as RawApiSessionMetadataDetails)?.TitleSource;
    }

    private sealed class AnsweringProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public const string Answer = "The answer of the model.";

        /// <summary>The developer instructions of each turn, in order.</summary>
        public List<string?> Instructions { get; } = [];

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
            lock (Instructions) Instructions.Add(request.DeveloperInstructions);
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
