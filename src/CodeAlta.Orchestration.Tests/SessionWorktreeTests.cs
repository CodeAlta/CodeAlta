using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A session that works in a git worktree: it belongs to its project, its tools and its instructions are those
/// of the worktree, and it goes back to the folder of its project once the worktree is gone.
/// </summary>
/// <remarks>
/// The sessions are created and sent to the way the desktop does it, with a plugin active: what plugins add to
/// a run is applied to a copy of the options, which must not lose where the session works.
/// </remarks>
[TestClass]
public sealed class SessionWorktreeTests
{
    [TestMethod]
    public async Task SessionInAWorktree_BelongsToItsProject_AndWorksInTheWorktree()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var tools = new List<OwnedSessionToolRequest>();
        host.Commands.SessionTools = request => { lock (tools) tools.Add(request); return []; };

        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "In a worktree", null, temp.Worktree);

        // The session is one of its project: the folder it belongs to is the folder of the project.
        Assert.AreEqual(SessionViewKind.ProjectSession, session.Kind);
        Assert.AreEqual(host.CurrentProject.Id, session.ProjectRef);
        Assert.AreEqual(temp.ProjectRoot, session.WorkingDirectory);
        Assert.AreEqual(temp.Worktree, session.WorktreeDirectory);
        Assert.AreEqual(temp.Worktree, await host.RuntimeService.GetSessionWorktreeAsync(session.SessionId));
        var listed = await ListedAsync(host, session.SessionId);
        Assert.AreEqual(temp.ProjectRoot, listed.WorkspacePath);
        Assert.AreEqual(temp.Worktree, listed.WorktreePath);
        var recovered = await RecoveredAsync(host, session.SessionId);
        Assert.AreEqual(host.CurrentProject.Id, recovered.ProjectRef);
        Assert.AreEqual(temp.ProjectRoot, recovered.WorkingDirectory);
        Assert.AreEqual(temp.Worktree, recovered.WorktreeDirectory);

        // Nothing is at work yet.
        Assert.AreEqual(0, host.RuntimeService.ListBusySessionFolders().Count);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Hold = hold;
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), session.SessionId, "one")).Receipt;
        Assert.IsNotNull(receipt);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var turn = await provider.Turns.Reader.ReadAsync(timeout.Token);

        // The provider runs in the worktree, and the session is told that this is where it works.
        Assert.AreEqual(temp.Worktree, turn.WorkingDirectory);
        StringAssert.Contains(turn.Instructions, $"- Current working directory: `{temp.Worktree}`");
        StringAssert.Contains(turn.Instructions, $"- Project root: `{temp.Worktree}`");
        StringAssert.Contains(turn.Instructions, $"- Git worktree: the working directory is a git worktree of the project, a checkout of its own with its own branch. The main checkout of the project is `{temp.ProjectRoot}`");
        // While it runs, the worktree is in use: this is what keeps it from being removed.
        CollectionAssert.AreEqual(new[] { new SessionWorkFolder(session.SessionId, temp.Worktree, true) }, host.RuntimeService.ListBusySessionFolders().ToArray());
        hold.SetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        while (host.RuntimeService.ListBusySessionFolders().Count > 0) await Task.Delay(20, timeout.Token);

        // The commands of the session resolve paths from the worktree, when it is created and when it is sent to.
        lock (tools)
        {
            Assert.IsTrue(tools.Count >= 2);
            Assert.IsTrue(tools.All(request => request.WorkingDirectory == temp.Worktree && request.ProjectId == host.CurrentProject.Id), string.Join(", ", tools.Select(static request => request.WorkingDirectory)));
        }

        // A second send keeps the attachment: the session still works where it did.
        var second = await SendAsync(host, provider, session, "two");
        Assert.AreEqual(temp.Worktree, second.WorkingDirectory);
        Assert.AreEqual(temp.Worktree, (await ListedAsync(host, session.SessionId)).WorktreePath);
    }

    [TestMethod]
    public async Task SessionWhoseWorktreeIsGone_ContinuesInTheFolderOfItsProject_AndNoLongerRecordsIt()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var tools = new List<OwnedSessionToolRequest>();
        host.Commands.SessionTools = request => { lock (tools) tools.Add(request); return []; };
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Removed", null, temp.Worktree);
        Assert.AreEqual(temp.Worktree, (await SendAsync(host, provider, session, "one")).WorkingDirectory);

        // The worktree is removed while the session is idle, as a user does from a terminal.
        Directory.Delete(temp.Worktree, recursive: true);

        // The session is not in it any more, and still records it until it continues: the window shows that it is gone.
        Assert.IsNull(await host.RuntimeService.GetSessionWorktreeAsync(session.SessionId));
        Assert.AreEqual(temp.Worktree, (await ListedAsync(host, session.SessionId)).WorktreePath);
        lock (tools) tools.Clear();

        var turn = await SendAsync(host, provider, session, "two");

        Assert.AreEqual(temp.ProjectRoot, turn.WorkingDirectory);
        StringAssert.Contains(turn.Instructions, $"- Current working directory: `{temp.ProjectRoot}`");
        Assert.IsFalse(turn.Instructions.Contains("- Git worktree:", StringComparison.Ordinal));
        // What it did so far names the folder that is gone: it is told where it works now.
        StringAssert.Contains(turn.Instructions, $"The git worktree this session worked in, `{temp.Worktree}`, is no longer there.");
        lock (tools) Assert.IsTrue(tools.Count > 0 && tools.All(request => request.WorkingDirectory == temp.ProjectRoot));
        // The record is cleared: the session is an ordinary session of its project again.
        var listed = await ListedAsync(host, session.SessionId);
        Assert.IsNull(listed.WorktreePath);
        Assert.AreEqual(temp.ProjectRoot, listed.WorkspacePath);
        Assert.IsNull((await RecoveredAsync(host, session.SessionId)).WorktreeDirectory);

        // The folder coming back does not bring the session back into it, and the note is for the turn it mattered in.
        Directory.CreateDirectory(temp.Worktree);
        var third = await SendAsync(host, provider, session, "three");
        Assert.AreEqual(temp.ProjectRoot, third.WorkingDirectory);
        Assert.IsNull(await host.RuntimeService.GetSessionWorktreeAsync(session.SessionId));
    }

    [TestMethod]
    public async Task AnswerOfAChild_ForwardedToASessionWhoseWorktreeIsGone_IsReadInTheFolderOfItsProject()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Parent", null, temp.Worktree);
        Assert.AreEqual(temp.Worktree, (await SendAsync(host, provider, session, "one")).WorkingDirectory);
        // The child it started, as `alta session create` starts one: its answers are forwarded to the session.
        var childOptions = new SessionExecutionOptions
        {
            ProviderId = provider.Descriptor.ProviderId, ProviderKey = provider.Descriptor.ProviderId.Value, Model = "fake-model",
            WorkingDirectory = temp.ProjectRoot, ProjectRoots = [temp.ProjectRoot],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        };
        var child = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, childOptions, "Child", session.SessionId, null, CancellationToken.None);

        // The work of the session landed and its worktree is removed, while the child still works.
        Directory.Delete(temp.Worktree, recursive: true);
        await host.RuntimeService.SendAsync(child, childOptions, new AgentSendOptions { Input = AgentInput.Text("work") }, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.AreEqual(temp.ProjectRoot, (await provider.Turns.Reader.ReadAsync(timeout.Token)).WorkingDirectory);

        // The answer of the child starts a run of the session: it is read, in the folder of the project.
        var forwarded = await provider.Turns.Reader.ReadAsync(timeout.Token);
        Assert.AreEqual(temp.ProjectRoot, forwarded.WorkingDirectory);
        StringAssert.Contains(forwarded.Instructions, $"The git worktree this session worked in, `{temp.Worktree}`, is no longer there.");
        while (await host.RuntimeService.HasActiveRunAsync(session, timeout.Token)) await Task.Delay(20, timeout.Token);
        Assert.IsNull((await ListedAsync(host, session.SessionId)).WorktreePath);
    }

    [TestMethod]
    public async Task SessionRestoredAfterTheHostRestarts_WorksInItsWorktreeAgain()
    {
        using var temp = TempDirectory.Create();
        string sessionId;
        {
            var provider = new RecordingProvider();
            await using var host = await CreateHostAsync(temp, provider);
            var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Restored", null, temp.Worktree);
            sessionId = session.SessionId;
            await SendAsync(host, provider, session, "one");
        }

        // Another run of the host: the session is read from what it recorded.
        {
            var provider = new RecordingProvider();
            await using var host = await CreateHostAsync(temp, provider);
            var session = await RecoveredAsync(host, sessionId);
            Assert.AreEqual(temp.Worktree, session.WorktreeDirectory);
            Assert.AreEqual(temp.Worktree, await host.RuntimeService.GetSessionWorktreeAsync(sessionId));

            var turn = await SendAsync(host, provider, session, "two");

            Assert.AreEqual(temp.Worktree, turn.WorkingDirectory);
            StringAssert.Contains(turn.Instructions, "- Git worktree:");
            Assert.AreEqual(temp.Worktree, (await ListedAsync(host, sessionId)).WorktreePath);
        }
    }

    [TestMethod]
    public async Task Worktree_IsTheAbsoluteFolderOfAProjectSession()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);

        // A chat belongs to no project: it has no checkout to work in.
        Assert.ThrowsExactly<ArgumentException>(() => { _ = host.Commands.CreateDraftSessionAsync(null, provider.Descriptor, "Chat", null, temp.Worktree); });
        Assert.ThrowsExactly<ArgumentException>(() => { _ = host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Relative", null, "trees/quiet-heron"); });

        // A folder that is not there is not one to work in: the session works in the folder of its project.
        var missing = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Missing", null, Path.Combine(temp.Root, "trees", "nowhere"));
        Assert.IsNull(missing.WorktreeDirectory);
        Assert.AreEqual(temp.ProjectRoot, (await SendAsync(host, provider, missing, "one")).WorkingDirectory);
        Assert.IsNull((await ListedAsync(host, missing.SessionId)).WorktreePath);

        // A session without a worktree is listed as working in the folder of its project while it runs.
        var plain = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Plain");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Hold = hold;
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), plain.SessionId, "one")).Receipt;
        Assert.IsNotNull(receipt);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await provider.Turns.Reader.ReadAsync(timeout.Token);
        CollectionAssert.AreEqual(new[] { new SessionWorkFolder(plain.SessionId, temp.ProjectRoot, false) }, host.RuntimeService.ListBusySessionFolders().ToArray());
        hold.SetResult();
        await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(10));
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
                PluginBuiltIns = [new BuiltInPluginDefinition { Id = "fixture", DisplayName = "Fixture", PluginType = typeof(GuidancePlugin), Factory = static () => new GuidancePlugin() }],
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            },
            CancellationToken.None);

    private static async Task<RecordedTurn> SendAsync(CodeAltaHost host, RecordingProvider provider, SessionViewDescriptor session, string text)
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), session.SessionId, text)).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var turn = await provider.Turns.Reader.ReadAsync(timeout.Token);
        // The next send may move the session, which the runtime only does for an idle session.
        while (await host.RuntimeService.HasActiveRunAsync(session, timeout.Token)) await Task.Delay(20, timeout.Token);
        return turn;
    }

    private static async Task<AgentSessionMetadata> ListedAsync(CodeAltaHost host, string sessionId)
    {
        await foreach (var session in host.AgentSessionCatalog.ListSessionsAsync(filter: null, cancellationToken: CancellationToken.None))
        {
            if (session.SessionId == sessionId) return session;
        }

        Assert.Fail($"Session '{sessionId}' is not listed.");
        return null!;
    }

    private static async Task<SessionViewDescriptor> RecoveredAsync(CodeAltaHost host, string sessionId)
    {
        await foreach (var session in host.RuntimeService.ListRecoverableSessionsAsync(cancellationToken: CancellationToken.None))
        {
            if (session.SessionId == sessionId) return session;
        }

        Assert.Fail($"Session '{sessionId}' cannot be recovered.");
        return null!;
    }

    private sealed record RecordedTurn(string? WorkingDirectory, string Instructions);

    /// <summary>A plugin that adds a line to every run: the host then copies the options of each run.</summary>
    public sealed class GuidancePlugin : PluginBase
    {
        /// <inheritdoc />
        public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
        {
            yield return Prompt.Static(PluginPromptChannel.Developer, "Fixture plugin guidance.");
        }
    }

    private sealed class RecordingProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public Channel<RecordedTurn> Turns { get; } = Channel.CreateUnbounded<RecordedTurn>();

        /// <summary>Keeps the next turn running until it is completed.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-worktrees"), "Fake Worktrees") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-worktrees", DisplayName = "Fake Worktrees", TransportKind = AgentTransportKind.OpenAIResponses,
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
            Turns.Writer.TryWrite(new(request.WorkingDirectory, string.Join("\n", request.SystemMessage, request.DeveloperInstructions)));
            if (Hold is { } hold)
            {
                Hold = null;
                await hold.Task.WaitAsync(cancellationToken);
            }

            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("done")]),
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
            Worktree = Directory.CreateDirectory(Path.Combine(root, "trees", "quiet-heron")).FullName;
        }

        public string Root { get; }

        public string GlobalRoot { get; }

        public string ProjectRoot { get; }

        /// <summary>A folder that stands for a worktree of the project: the runtime only asks whether it is there.</summary>
        public string Worktree { get; }

        public static TempDirectory Create() => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.Worktrees.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
