using System.Collections.Concurrent;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>The turns of a session, its fork and its rewind, as the page asks for them.</summary>
[TestClass]
public sealed class WorkspaceBranchSessionTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task Turns_AreListedByPages_WithThePromptAndTheStateOfEach()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sessionId = await fixture.CreateSessionAsync("Review");
        foreach (var prompt in new[] { "one", "two", "three" }) await fixture.SendAsync(sessionId, prompt);
        var rpc = new WorkspaceService(fixture.Host, Epoch);

        var all = await rpc.SessionTurnsAsync(new(Epoch, sessionId), CancellationToken.None);

        Assert.AreEqual("ok", all.Status);
        Assert.AreEqual(3, all.Total);
        Assert.IsNull(all.ActiveRunId);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, all.Turns.Select(static turn => turn.Number).ToArray());
        CollectionAssert.AreEqual(new[] { "one", "two", "three" }, all.Turns.Select(static turn => turn.Prompt).ToArray());
        Assert.IsTrue(all.Turns.All(static turn => turn is { Status: "completed", PromptTruncated: false, SourceSessionId: null, AnswersQuestion: false, AssistantMessages: 1 }));
        // The offset of a prompt is the key of its row in the timeline of the page.
        var timeline = await rpc.HistoryTimelineAsync(new(sessionId, null), CancellationToken.None);
        var rows = timeline.Page.Entries.Where(static entry => entry is { EventType: "contentCompleted", Kind: "User" }).Select(static entry => entry.Offset).ToArray();
        CollectionAssert.AreEqual(rows, all.Turns.Select(static turn => turn.PromptOffset).ToArray());
        Assert.AreEqual(sessionId, all.Revision!.SessionId);

        // The page before the turn the caller has.
        var older = await rpc.SessionTurnsAsync(new(Epoch, sessionId, BeforeTurn: 3), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { 1, 2 }, older.Turns.Select(static turn => turn.Number).ToArray());
        Assert.AreEqual(3, older.Total);
        Assert.AreEqual(0, (await rpc.SessionTurnsAsync(new(Epoch, sessionId, BeforeTurn: 1), CancellationToken.None)).Turns.Length);

        Assert.AreEqual("session_missing", (await rpc.SessionTurnsAsync(new(Epoch, Guid.NewGuid().ToString()), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await rpc.SessionTurnsAsync(new(Guid.NewGuid().ToString("D"), sessionId), CancellationToken.None)).Status);
        Assert.AreEqual("invalid_scope", (await rpc.SessionTurnsAsync(new(Epoch, sessionId, BeforeTurn: 0), CancellationToken.None)).Status);
        Assert.AreEqual("invalid_scope", (await rpc.SessionTurnsAsync(new(Epoch, "line\nbreak"), CancellationToken.None)).Status);
        Assert.AreEqual(all.Turns[0], JsonSerializer.Deserialize(JsonSerializer.Serialize(all, DesktopJsonContext.Default.WorkspaceSessionTurnsResponse),
            DesktopJsonContext.Default.WorkspaceSessionTurnsResponse)!.Turns[0]);
        await rpc.CloseSessionsAsync();
        await rpc.CloseImportsAsync();
    }

    [TestMethod]
    public async Task Fork_CreatesASessionBesideItsSource_AndHandsBackThePromptToContinueFrom()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sessionId = await fixture.CreateSessionAsync("Review");
        var longPrompt = "third " + new string('x', 5000);
        foreach (var prompt in new[] { "one", "two", longPrompt }) await fixture.SendAsync(sessionId, prompt);
        var rpc = new WorkspaceService(fixture.Host, Epoch);
        var turns = (await rpc.SessionTurnsAsync(new(Epoch, sessionId), CancellationToken.None)).Turns;
        Assert.IsTrue(turns[2].PromptTruncated, "The list holds the start of a long prompt.");
        var project = fixture.Host.CurrentProject;
        WorkspaceForkSessionRequest Request(string? before, string? title = null) => new(Epoch, "project", project.Id, project.ProjectPath, sessionId, before, title);

        var forked = await rpc.ForkSessionAsync(Request(turns[2].RunId), CancellationToken.None);

        Assert.AreEqual("ok", forked.Status);
        Assert.AreEqual(2, forked.TurnCount);
        Assert.AreEqual("Review (fork)", forked.Title);
        Assert.AreEqual(longPrompt, forked.Prompt, "The whole prompt of the turn the fork stops before goes to the composer.");
        Assert.IsFalse(forked.PromptTruncated);
        Assert.AreNotEqual(sessionId, forked.ForkSessionId);
        var snapshot = await rpc.SnapshotAsync(new(), CancellationToken.None);
        var row = snapshot.Sessions.Single(session => session.Id == forked.ForkSessionId);
        Assert.AreEqual(sessionId, row.ForkedFromSessionId);
        Assert.AreEqual("Review", row.ForkedFromTitle);
        Assert.AreEqual("fork", row.ForkKind);
        Assert.AreEqual(2, row.ForkTurnCount);
        Assert.IsNull(row.ParentSessionId, "A fork is not listed as a sub-agent of its source.");
        Assert.AreEqual(project.Id, row.ProjectId);
        Assert.AreEqual("project", row.ScopeKind);
        var source = snapshot.Sessions.Single(session => session.Id == sessionId);
        Assert.IsNull(source.ForkedFromSessionId);
        Assert.AreEqual(0, source.RewindCount);

        // The fork answers as a session of the page: its next request holds the turns it started with.
        await fixture.SendAsync(forked.ForkSessionId!, "another third");
        CollectionAssert.AreEqual(new[] { "one", "two", "another third" }, fixture.UserTexts(forked.ForkSessionId!));
        Assert.AreEqual(sessionId, fixture.Provider.Requests.Last(request => request.SessionId == forked.ForkSessionId).PromptCacheKey);

        // The whole session, with a title of the caller: nothing to continue from.
        var whole = await rpc.ForkSessionAsync(Request(null, "All of it"), CancellationToken.None);
        Assert.AreEqual("ok", whole.Status);
        Assert.AreEqual(3, whole.TurnCount);
        Assert.AreEqual("All of it", whole.Title);
        Assert.IsNull(whole.Prompt);

        Assert.AreEqual("turn_not_found", (await rpc.ForkSessionAsync(Request("no-such-run"), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await rpc.ForkSessionAsync(Request(null) with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
        Assert.AreEqual("scope_missing", (await rpc.ForkSessionAsync(Request(null) with { ProjectPath = Path.Combine(fixture.Root, "elsewhere") }, CancellationToken.None)).Status);
        Assert.AreEqual("session_missing", (await rpc.ForkSessionAsync(new(Epoch, "global", null, fixture.Host.CatalogOptions.GlobalRoot, sessionId, null, null), CancellationToken.None)).Status);
        foreach (var title in new[] { "", " padded", "line\nbreak", new string('x', 257) })
            Assert.AreEqual("invalid_scope", (await rpc.ForkSessionAsync(Request(null, title), CancellationToken.None)).Status);
        Assert.AreEqual("invalid_scope", (await rpc.ForkSessionAsync(Request(null) with { Scope = "elsewhere" }, CancellationToken.None)).Status);
        var request = Request(turns[2].RunId);
        Assert.AreEqual(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request, DesktopJsonContext.Default.WorkspaceForkSessionRequest),
            DesktopJsonContext.Default.WorkspaceForkSessionRequest));
        Assert.AreEqual(forked, JsonSerializer.Deserialize(JsonSerializer.Serialize(forked, DesktopJsonContext.Default.WorkspaceForkSessionResponse),
            DesktopJsonContext.Default.WorkspaceForkSessionResponse));
        await rpc.CloseSessionsAsync();
        await rpc.CloseImportsAsync();
    }

    [TestMethod]
    public async Task Fork_HandsBackThePromptWithoutTheLinesOfItsFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sessionId = await fixture.CreateSessionAsync("Files");
        var project = fixture.Host.CurrentProject;
        var receipt = fixture.Host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "Read the notes")
        {
            Selection = new(fixture.Provider.Descriptor.ProviderId.Value, "default", "fake-model", null),
            References = new(project.Id, project.ProjectPath),
            Images = [new("notes.txt", "text/plain", Convert.ToBase64String("hello"u8.ToArray()))],
        }).Receipt;
        Assert.IsNotNull(receipt);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        await fixture.SendAsync(sessionId, "two");
        var rpc = new WorkspaceService(fixture.Host, Epoch);
        var turns = (await rpc.SessionTurnsAsync(new(Epoch, sessionId), CancellationToken.None)).Turns;
        StringAssert.Contains(turns[0].Prompt, "Attached file (notes.txt)", "The turn list shows the record as it is.");

        // A fork before the first turn hands back its prompt, as a rewind does (ReadPromptAsync).
        var forked = await rpc.ForkSessionAsync(new(Epoch, "project", project.Id, project.ProjectPath, sessionId, turns[0].RunId, null), CancellationToken.None);
        Assert.AreEqual("ok", forked.Status);
        Assert.AreEqual("Read the notes", forked.Prompt, "The composer gets what was typed, not the line of the file.");
        await rpc.CloseSessionsAsync();
        await rpc.CloseImportsAsync();
    }

    [TestMethod]
    public async Task Rewind_CutsTheSession_KeepsWhatItRemoves_AndCountsItself()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sessionId = await fixture.CreateSessionAsync("Refactoring");
        foreach (var prompt in new[] { "one", "two", "three" }) await fixture.SendAsync(sessionId, prompt);
        var rpc = new WorkspaceService(fixture.Host, Epoch);
        var turns = (await rpc.SessionTurnsAsync(new(Epoch, sessionId), CancellationToken.None)).Turns;
        var project = fixture.Host.CurrentProject;
        WorkspaceRewindSessionRequest Request(string before, bool keep = true) => new(Epoch, "project", project.Id, project.ProjectPath, sessionId, before, keep);

        var rewound = await rpc.RewindSessionAsync(Request(turns[1].RunId), CancellationToken.None);

        Assert.AreEqual("ok", rewound.Status);
        Assert.AreEqual(1, rewound.TurnCount);
        Assert.AreEqual(2, rewound.RemovedTurnCount);
        Assert.AreEqual("two", rewound.Prompt);
        Assert.AreEqual("Refactoring (before rewind)", rewound.RemovedTurnsTitle);
        Assert.AreEqual(1, rewound.RewindCount);
        var snapshot = await rpc.SnapshotAsync(new(), CancellationToken.None);
        var row = snapshot.Sessions.Single(session => session.Id == sessionId);
        Assert.AreEqual(1, row.RewindCount, "A pane that shows the session reads its conversation again when this changes.");
        Assert.AreEqual("Refactoring", row.Title);
        var kept = snapshot.Sessions.Single(session => session.Id == rewound.RemovedTurnsSessionId);
        Assert.AreEqual("rewind", kept.ForkKind);
        Assert.AreEqual(sessionId, kept.ForkedFromSessionId);
        Assert.AreEqual(3, (await rpc.SessionTurnsAsync(new(Epoch, kept.Id), CancellationToken.None)).Total);
        var after = await rpc.SessionTurnsAsync(new(Epoch, sessionId), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "one" }, after.Turns.Select(static turn => turn.Prompt).ToArray());
        // The timeline of the page reads the shortened journal from its start.
        var timeline = await rpc.HistoryTimelineAsync(new(sessionId, null), CancellationToken.None);
        Assert.AreEqual("ok", timeline.Page.Status);
        CollectionAssert.AreEqual(new[] { "one" },
            timeline.Page.Entries.Where(static entry => entry is { EventType: "contentCompleted", Kind: "User" }).Select(static entry => entry.Text).ToArray());

        await fixture.SendAsync(sessionId, "another two");
        CollectionAssert.AreEqual(new[] { "one", "another two" }, fixture.UserTexts(sessionId));

        // Without keeping them, and down to nothing.
        turns = (await rpc.SessionTurnsAsync(new(Epoch, sessionId), CancellationToken.None)).Turns;
        var emptied = await rpc.RewindSessionAsync(Request(turns[0].RunId, keep: false), CancellationToken.None);
        Assert.AreEqual("ok", emptied.Status);
        Assert.AreEqual(0, emptied.TurnCount);
        Assert.AreEqual(2, emptied.RemovedTurnCount);
        Assert.IsNull(emptied.RemovedTurnsSessionId);
        Assert.AreEqual(2, emptied.RewindCount);
        Assert.AreEqual("one", emptied.Prompt);

        Assert.AreEqual("turn_not_found", (await rpc.RewindSessionAsync(Request("no-such-run"), CancellationToken.None)).Status);
        Assert.AreEqual("invalid_scope", (await rpc.RewindSessionAsync(Request(""), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await rpc.RewindSessionAsync(Request(turns[0].RunId) with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, CancellationToken.None)).Status);
        var request = Request(turns[0].RunId);
        Assert.AreEqual(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request, DesktopJsonContext.Default.WorkspaceRewindSessionRequest),
            DesktopJsonContext.Default.WorkspaceRewindSessionRequest));
        Assert.AreEqual(rewound, JsonSerializer.Deserialize(JsonSerializer.Serialize(rewound, DesktopJsonContext.Default.WorkspaceRewindSessionResponse),
            DesktopJsonContext.Default.WorkspaceRewindSessionResponse));
        await rpc.CloseSessionsAsync();
        await rpc.CloseImportsAsync();
    }

    [TestMethod]
    public async Task SessionThatRuns_IsForkedWithoutItsTurnInProgress_AndIsNotRewound()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sessionId = await fixture.CreateSessionAsync("Long work");
        await fixture.SendAsync(sessionId, "one");
        var rpc = new WorkspaceService(fixture.Host, Epoch);
        var project = fixture.Host.CurrentProject;
        fixture.Provider.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = fixture.Host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "two")).Receipt!;
        try
        {
            await fixture.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var turns = await rpc.SessionTurnsAsync(new(Epoch, sessionId), CancellationToken.None);
            Assert.AreEqual(2, turns.Total);
            Assert.AreEqual("running", turns.Turns[1].Status);
            Assert.AreEqual(turns.Turns[1].RunId, turns.ActiveRunId);
            var forked = await rpc.ForkSessionAsync(new(Epoch, "project", project.Id, project.ProjectPath, sessionId, null, null), CancellationToken.None);
            var rewound = await rpc.RewindSessionAsync(new(Epoch, "project", project.Id, project.ProjectPath, sessionId, turns.Turns[1].RunId, true), CancellationToken.None);

            Assert.AreEqual("ok", forked.Status);
            Assert.AreEqual(1, forked.TurnCount);
            Assert.AreEqual("two", forked.Prompt, "The prompt of the turn in progress is the one the fork stops before.");
            Assert.AreEqual("session_in_use", rewound.Status);
        }
        finally
        {
            fixture.Provider.Hold.TrySetResult();
            await running.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await rpc.CloseSessionsAsync();
        await rpc.CloseImportsAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, CodeAltaHost host, RecordingProvider provider)
        {
            Root = root;
            Host = host;
            Provider = provider;
        }

        public string Root { get; }

        public CodeAltaHost Host { get; }

        public RecordingProvider Provider { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "codealta-session-branch-" + Guid.NewGuid().ToString("N"));
            var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            var projectPath = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var provider = new RecordingProvider();
            var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, IsHeadless = true, AutoApproveOwnedPermissions = true,
                OwnedCommandReceiptCapacity = 64, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            });
            return new(root, host, provider);
        }

        public async Task<string> CreateSessionAsync(string title)
            => (await Host.Commands.CreateDraftSessionAsync(Host.CurrentProject, Provider.Descriptor, title)).SessionId;

        // As the composer of the page sends a prompt.
        public async Task SendAsync(string sessionId, string prompt)
        {
            var receipt = Host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, prompt)).Receipt;
            Assert.IsNotNull(receipt, "The send was not admitted.");
            Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        }

        public string[] UserTexts(string sessionId)
            => Provider.Requests.Last(request => request.SessionId == sessionId).Conversation
                .Where(static message => message.Role == AgentConversationRole.User)
                .Select(static message => string.Concat(message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value))).ToArray();

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    // Answers every prompt and keeps the requests it was sent.
    private sealed class RecordingProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public ConcurrentQueue<AgentTurnRequest> Requests { get; } = new();

        public TaskCompletionSource? Hold { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("branch-answers"), "Branch Answers") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "branch-answers", DisplayName = "Branch Answers", TransportKind = AgentTransportKind.OpenAIResponses,
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

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (Hold is { } hold)
            {
                Started.TrySetResult();
                await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("answer")]),
            };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
