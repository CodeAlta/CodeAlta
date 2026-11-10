using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A session that exists sent to work in another checkout of the repository of its project (#301): it keeps its
/// conversation, its next turn runs there, and its agent is told once, with a note of that prompt.
/// </summary>
[TestClass]
public sealed class SessionWorkFolderTests
{
    [TestMethod]
    public async Task ASession_GoesToAWorktreeAndBack_AndItsNextPromptSaysWhereItWorks()
    {
        using var temp = TempDirectory.Create();
        var tree = temp.Worktree("feature-tree");
        var provider = new RecordingProvider();
        string sessionId;
        await using (var host = await CreateHostAsync(temp, provider))
        {
            sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Discuss the parser")).SessionId;
            await SendAsync(host, sessionId, "Let us plan.");
            Assert.AreEqual(temp.ProjectRoot, provider.LastWorkingDirectory);

            var switched = await host.Commands.SwitchWorkFolderAsync(sessionId, host.CurrentProject.Id, temp.ProjectRoot, tree);
            Assert.AreEqual("ok", switched.Status, string.Join(" ", switched.Warnings));
            Assert.AreEqual(tree, switched.WorktreeDirectory);
            Assert.AreEqual(tree, await host.RuntimeService.GetSessionWorktreeAsync(sessionId));
            Assert.AreEqual("unchanged", (await host.Commands.SwitchWorkFolderAsync(sessionId, host.CurrentProject.Id, temp.ProjectRoot, tree)).Status);

            await SendAsync(host, sessionId, "Now implement it.");
            Assert.AreEqual(tree, provider.LastWorkingDirectory, "The next turn runs in the worktree.");
            StringAssert.Contains(provider.LastUserText, "<codealta-context>Working directory: this session now works in `" + tree + "`");
            StringAssert.Contains(provider.LastUserText, "no longer in `" + temp.ProjectRoot + "`");
            StringAssert.Contains(provider.LastUserText, "Now implement it.");
            Assert.AreEqual(2, provider.Conversations.Last(), "The conversation goes on: both prompts are in it.");
            await SendAsync(host, sessionId, "Go on.");
            Assert.IsFalse(provider.LastUserText.Contains("codealta-context", StringComparison.Ordinal), "Told once.");

            // The timeline says where the session works from then on.
            var journal = File.ReadAllText(Directory.EnumerateFiles(temp.GlobalRoot, sessionId + ".jsonl", SearchOption.AllDirectories).Single());
            StringAssert.Contains(journal, "This session now works in");
            StringAssert.Contains(journal, "\"workFolder\"", "The record names the folder, which the timeline titles its card by.");
        }

        // Where it works survives a restart.
        await using (var host = await CreateHostAsync(temp, provider))
        {
            Assert.AreEqual(tree, await host.RuntimeService.GetSessionWorktreeAsync(sessionId));
            await SendAsync(host, sessionId, "After the restart.");
            Assert.AreEqual(tree, provider.LastWorkingDirectory);

            var back = await host.Commands.SwitchWorkFolderAsync(sessionId, host.CurrentProject.Id, temp.ProjectRoot, null);
            Assert.AreEqual("ok", back.Status, string.Join(" ", back.Warnings));
            Assert.IsNull(back.WorktreeDirectory);
            await SendAsync(host, sessionId, "Back home.");
            Assert.AreEqual(temp.ProjectRoot, provider.LastWorkingDirectory);
            StringAssert.Contains(provider.LastUserText, "this session now works in `" + temp.ProjectRoot + "`, the folder of the project");
        }
    }

    [TestMethod]
    public async Task AFolderOutsideTheRepository_IsRefused_AndTheSessionStaysWhereItWorks()
    {
        using var temp = TempDirectory.Create();
        var clone = Directory.CreateDirectory(Path.Combine(temp.Root, "clone")).FullName;
        Directory.CreateDirectory(Path.Combine(clone, ".git"));
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
        var project = host.CurrentProject;

        Assert.AreEqual("other_repository", (await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot, clone)).Status);
        Assert.AreEqual("not_found", (await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot, Path.Combine(temp.Root, "gone"))).Status);
        Assert.AreEqual("invalid", (await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot, "relative")).Status);
        Assert.AreEqual("session_missing", (await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, clone, null)).Status);
        Assert.AreEqual("session_missing", (await host.Commands.SwitchWorkFolderAsync("unknown-session", project.Id, temp.ProjectRoot, null)).Status);
        Assert.AreEqual("unchanged", (await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot, null)).Status);
        Assert.IsNull(await host.RuntimeService.GetSessionWorktreeAsync(sessionId));
        Assert.IsNull(host.RuntimeService.PeekWorkFolderNote(sessionId));
    }

    [TestMethod]
    public async Task ASessionAtWork_IsRefused_OrChangesFolderWhenItsTurnEnds()
    {
        using var temp = TempDirectory.Create();
        var tree = temp.Worktree("later-tree");
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
        var project = host.CurrentProject;

        var gate = provider.Block();
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "A long turn.")).Receipt;
        Assert.IsNotNull(receipt);
        await gate.Started.WithinAsync(TestDeadlines.Hang);

        Assert.AreEqual("busy", (await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot, tree)).Status,
            "A session at work keeps its folder.");
        // A folder that cannot be used is refused at once, not when the turn ends.
        Assert.AreEqual("other_repository", (await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot,
            temp.Clone(), whenIdle: true)).Status);
        var scheduled = await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot, tree, whenIdle: true);
        Assert.AreEqual(("scheduled", tree), (scheduled.Status, scheduled.WorktreeDirectory));
        Assert.IsNull(await host.RuntimeService.GetSessionWorktreeAsync(sessionId), "Nothing changes while the turn runs.");
        // A prompt queued meanwhile (the answer of a child, the end of a job) waits for the change, and runs after it.
        var session = (await host.RuntimeService.ResolveOwnedSessionAsync(sessionId, CancellationToken.None))!;
        await host.RuntimeService.QueuePromptAsync(session, "Queued meanwhile.", "send", submittedBy: null);

        gate.Release();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await receipt.Completion.WithinAsync(TestDeadlines.Hang)).Outcome);
        // The change is work of the session: it has settled once the change is made and the queue ran.
        await host.RuntimeService.WhenSettledAsync(sessionId).WithinAsync(TestDeadlines.Hang);
        Assert.AreEqual(tree, await host.RuntimeService.GetSessionWorktreeAsync(sessionId));
        Assert.AreEqual(tree, provider.LastWorkingDirectory, "The queued prompt ran in the new folder.");
        StringAssert.Contains(provider.LastUserText, "Queued meanwhile.");
        StringAssert.Contains(provider.LastUserText, "this session now works in `" + tree + "`", "The queued prompt tells the agent.");

        await SendAsync(host, sessionId, "Next.");
        Assert.AreEqual(tree, provider.LastWorkingDirectory);
        Assert.IsFalse(provider.LastUserText.Contains("codealta-context", StringComparison.Ordinal), "Told once.");
    }

    [TestMethod]
    public async Task TwoChangesBeforeAPrompt_AreToldAsOne_AndAChangeBackTellsNothing()
    {
        using var temp = TempDirectory.Create();
        var first = temp.Worktree("first-tree");
        var second = temp.Worktree("second-tree");
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
        var project = host.CurrentProject;

        Assert.AreEqual("ok", (await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot, first)).Status);
        Assert.AreEqual("ok", (await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot, second)).Status);
        var note = host.RuntimeService.PeekWorkFolderNote(sessionId);
        StringAssert.Contains(note, "now works in `" + second + "`");
        StringAssert.Contains(note, "no longer in `" + temp.ProjectRoot + "`", "The agent is told from the folder it knew.");

        Assert.AreEqual("ok", (await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot, null)).Status);
        Assert.IsNull(host.RuntimeService.PeekWorkFolderNote(sessionId), "Back where the agent knows it works: nothing to tell.");
    }

    [TestMethod]
    public async Task ASessionWhoseWorktreeWasDeleted_IsToldWhereItWorksNow_AndItsProviderMovesFromThere()
    {
        using var temp = TempDirectory.Create();
        var tree = temp.Worktree("deleted-tree");
        var provider = new RecordingProvider { Relocations = [] };
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
        var project = host.CurrentProject;
        Assert.AreEqual("ok", (await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot, tree)).Status);
        await SendAsync(host, sessionId, "Work in the tree.");
        provider.Relocations!.Clear();

        // The worktree is removed outside CodeAlta, before the session runs again: the agent still knows it.
        Directory.Delete(tree, recursive: true);
        var back = await host.Commands.SwitchWorkFolderAsync(sessionId, project.Id, temp.ProjectRoot, null);

        Assert.AreEqual("ok", back.Status, string.Join(" ", back.Warnings));
        StringAssert.Contains(host.RuntimeService.PeekWorkFolderNote(sessionId), "no longer in `" + tree + "`");
        CollectionAssert.AreEqual(new[] { (tree, temp.ProjectRoot) }, provider.Relocations);
    }

    [TestMethod]
    public async Task ASessionWithAScriptRunningInTheBackground_KeepsItsFolder()
    {
        using var temp = TempDirectory.Create();
        var tree = temp.Worktree("script-tree");
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
        host.RuntimeService.ScriptRuns = new RunningScripts(sessionId);

        Assert.AreEqual("background_running", (await host.Commands.SwitchWorkFolderAsync(sessionId, host.CurrentProject.Id, temp.ProjectRoot, tree)).Status,
            "A script of Code Mode calls the tools of the session in the folder it works in.");
        Assert.IsNull(await host.RuntimeService.GetSessionWorktreeAsync(sessionId));
    }

    // Scripts of Code Mode, one of which runs in the background for a session.
    private sealed class RunningScripts(string sessionId) : ISessionScriptRuns
    {
        public IReadOnlyCollection<string> ListSessionsWithRunningScripts() => [sessionId];

        public IReadOnlyList<SessionRuntimeBackgroundTask> ListTasks(string session) => [];

        public Task StopAsync(string session) => Task.CompletedTask;

        public bool StopRun(string session, string runId, string? by) => false;

        public Task TurnEndedAsync(string session) => Task.CompletedTask;

        public string CodeModeOf(string? session, string? providerKey, string? modelId) => "off";
    }

    [TestMethod]
    public async Task TheProvider_MovesWhatItKeepsOfTheSession()
    {
        using var temp = TempDirectory.Create();
        var tree = temp.Worktree("relocated-tree");
        var provider = new RecordingProvider { Relocations = [] };
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
        await SendAsync(host, sessionId, "One.");

        var result = await host.Commands.SwitchWorkFolderAsync(sessionId, host.CurrentProject.Id, temp.ProjectRoot, tree);

        Assert.AreEqual("ok", result.Status, string.Join(" ", result.Warnings));
        CollectionAssert.AreEqual(new[] { (temp.ProjectRoot, tree) }, provider.Relocations);
    }

    private static async Task SendAsync(CodeAltaHost host, string sessionId, string prompt)
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, prompt)).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WithinAsync(TestDeadlines.Hang);
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

    private sealed class Gate
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task Released => _released.Task;

        public void Start() => _started.TrySetResult();

        public void Release() => _released.TrySetResult();
    }

    private sealed class RecordingProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor, IAgentSessionRelocationProvider
    {
        private Gate? _gate;

        public string? LastWorkingDirectory { get; private set; }

        public string LastUserText { get; private set; } = string.Empty;

        // The number of user messages of the conversation each turn was given.
        public List<int> Conversations { get; } = [];

        public List<(string From, string To)>? Relocations { get; init; }

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-folder"), "Fake Folder") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-folder", DisplayName = "Fake Folder", TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        // The next turn waits until the gate is released.
        public Gate Block() => _gate = new Gate();

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
            lock (Conversations)
            {
                LastWorkingDirectory = request.WorkingDirectory;
                var user = request.Conversation.Last(static message => message.Role == AgentConversationRole.User);
                LastUserText = string.Join("\n", user.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
                Conversations.Add(request.Conversation.Count(static message => message.Role == AgentConversationRole.User));
            }

            if (Interlocked.Exchange(ref _gate, null) is { } gate)
            {
                gate.Start();
                await gate.Released.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]),
            };
        }

        public Task<string?> RelocateSessionAsync(AgentSessionState state, string fromDirectory, string toDirectory, CancellationToken cancellationToken = default)
        {
            lock (Conversations) Relocations?.Add((fromDirectory, toDirectory));
            return Task.FromResult<string?>(null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A project in the main checkout of a repository, and linked worktrees of it, as git lays them out (without git).
    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string root)
        {
            Root = Directory.CreateDirectory(root).FullName;
            GlobalRoot = Directory.CreateDirectory(Path.Combine(Root, "global")).FullName;
            ProjectRoot = Directory.CreateDirectory(Path.Combine(Root, "project")).FullName;
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".git"));
        }

        public string Root { get; }

        public string GlobalRoot { get; }

        public string ProjectRoot { get; }

        public static TempDirectory Create() => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.WorkFolder.{Guid.NewGuid():N}"));

        public string Worktree(string name)
        {
            var linked = Directory.CreateDirectory(Path.Combine(ProjectRoot, ".git", "worktrees", name)).FullName;
            File.WriteAllText(Path.Combine(linked, "commondir"), "../..");
            var tree = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            File.WriteAllText(Path.Combine(tree, ".git"), $"gitdir: {linked}");
            return tree;
        }

        public string Clone()
        {
            var clone = Directory.CreateDirectory(Path.Combine(Root, "clone-" + Guid.NewGuid().ToString("N")[..6])).FullName;
            Directory.CreateDirectory(Path.Combine(clone, ".git"));
            return clone;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for temporary test files.
            }
        }
    }
}
