using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A session that runs in the cloud: recorded without turns once its provider started the cloud session, written to
/// from CodeAlta, never attached to its provider here, and brought back into itself, with the turns made in the cloud,
/// after which it goes on as any session.
/// </summary>
[TestClass]
public sealed class SessionCloudTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ACloudSession_HasNoTurn_RefusesLocalPrompts_AndOnceBroughtBackGoesOnWithTheTurnsOfTheCloud()
    {
        using var temp = TempDirectory.Create();
        var provider = new CloudProvider();
        await using var host = await CreateHostAsync(temp, provider);

        var started = await host.Commands.CreateCloudSessionAsync(host.CurrentProject, provider.Descriptor, null, null, "Fix the flaky test", new NoTerminal(), null)
            .WaitAsync(Timeout);
        Assert.AreEqual("created", started.Status);
        Assert.AreEqual(CloudProvider.CloudId, started.CloudSessionId);
        var sessionId = started.Session!.SessionId;
        Assert.AreEqual("Fixed by the cloud", started.Session.Title, "The title Claude Code gave the cloud session.");
        Assert.AreEqual("Fix the flaky test", provider.StartedTask);

        var listed = await ListedAsync(host, sessionId);
        Assert.AreEqual(new AgentSessionCloud(CloudProvider.CloudId, "https://claude.ai/code/" + CloudProvider.CloudId, "Fixed by the cloud", false, false), listed.Cloud,
            "The listings know it runs in the cloud, without reading its journal.");
        var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
        Assert.AreEqual(0, (await store.ReadTurnsAsync(sessionId)).Turns.Count, "Its prompts are not turns: the cloud has them.");

        // Nothing starts a local conversation of it: a prompt is refused with what to do, and its provider is not called.
        var refused = await host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "local prompt")).Receipt!.Completion.WaitAsync(Timeout);
        Assert.AreEqual((OwnedSessionCommandOutcome.Failed, SessionRuntimeService.InCloudCode), (refused.Outcome, refused.Code));
        Assert.AreEqual(0, provider.Turns);
        Assert.IsNull(await host.RuntimeService.TryGetActiveSessionDescriptorAsync(sessionId), "It is never attached.");

        Assert.AreEqual(new CloudSessionSendResult("ok"), await host.Commands.SendToCloudSessionAsync(sessionId, "Also add a test.").WaitAsync(Timeout));
        Assert.AreEqual((CloudProvider.CloudId, "Also add a test."), provider.LastSent);
        var details = (await host.Commands.GetCloudSessionAsync(sessionId).WaitAsync(Timeout))!;
        Assert.AreEqual("Fix the flaky test", details.Task);
        Assert.AreEqual("Also add a test.", details.Sent.Single().Text);
        Assert.IsNull(details.ContinuedAt);

        var continued = await host.Commands.ContinueCloudSessionHereAsync(sessionId, null, fromTerminal: false).WaitAsync(Timeout);
        Assert.AreEqual("ok", continued.Status, continued.Message);
        Assert.AreEqual(temp.ProjectRoot, provider.BroughtInto, "Into the folder of the session.");

        var turns = (await store.ReadTurnsAsync(sessionId)).Turns;
        Assert.AreEqual(2, turns.Count, "The two turns made in the cloud.");
        Assert.IsTrue(turns.All(static turn => turn.MadeElsewhere));
        Assert.AreEqual("Fix the flaky test", turns[0].PromptPreview);
        var state = (await store.GetStateAsync(sessionId))!;
        Assert.AreEqual(provider.Conversation, state.ProviderSessionId, "The session names the conversation the cloud session was brought into.");
        Assert.AreEqual("""{"kind":"fake","users":2,"assistants":2}""", state.ProviderState!.Value.GetRawText());
        var back = (await host.Commands.GetCloudSessionAsync(sessionId).WaitAsync(Timeout))!;
        Assert.IsNotNull(back.ContinuedAt);
        Assert.IsFalse(back.Continuing);
        Assert.IsTrue((await ListedAsync(host, sessionId)).Cloud!.Continued);

        // It goes on as any session: the next prompt follows the turns of the cloud.
        var sent = await host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "next")).Receipt!.Completion.WaitAsync(Timeout);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, sent.Outcome, sent.Code);
        CollectionAssert.AreEqual(new[] { "Fix the flaky test", "Fixed.", "Also add a test.", "Added.", "next" }, provider.LastConversation());
        Assert.AreEqual(new CloudSessionSendResult("not_cloud"), await host.Commands.SendToCloudSessionAsync(sessionId, "late").WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task AFailedStart_RecordsNothing_AndAFailedReturn_LeavesTheSessionInTheCloud()
    {
        using var temp = TempDirectory.Create();
        var provider = new CloudProvider { Start = new AgentCloudStart("failed", Message: "Error: Cloud sessions need a claude.ai sign-in.", TerminalId: "t1") };
        await using var host = await CreateHostAsync(temp, provider);

        var failed = await host.Commands.CreateCloudSessionAsync(host.CurrentProject, provider.Descriptor, null, null, "Fix it", new NoTerminal(), null).WaitAsync(Timeout);
        Assert.AreEqual(("needs_claude_ai", "t1"), (failed.Status, failed.TerminalId), "Claude Code said why: the account needs claude.ai.");
        Assert.IsNull(failed.Session);
        await foreach (var session in host.RuntimeService.ListRecoverableSessionsAsync(cancellationToken: CancellationToken.None))
            Assert.IsNull(session.Cloud, "No session for a cloud session that does not exist.");

        provider.Start = null;
        var sessionId = (await host.Commands.CreateCloudSessionAsync(host.CurrentProject, provider.Descriptor, null, "Named", "Fix it", new NoTerminal(), null)
            .WaitAsync(Timeout)).Session!.SessionId;
        provider.Send = new AgentCloudSendResult("archived", "cloud session is archived and cannot accept new messages");
        Assert.AreEqual("archived", (await host.Commands.SendToCloudSessionAsync(sessionId, "more").WaitAsync(Timeout)).Status);
        Assert.AreEqual(0, (await host.Commands.GetCloudSessionAsync(sessionId).WaitAsync(Timeout))!.Sent.Count, "What did not reach the cloud is not recorded as sent.");

        provider.Return = new AgentCloudReturn("failed", "Error: branch claude/x was not found on the remote.");
        var refused = await host.Commands.ContinueCloudSessionHereAsync(sessionId, null, false).WaitAsync(Timeout);
        Assert.AreEqual(("failed", "Error: branch claude/x was not found on the remote."), (refused.Status, refused.Message));
        var details = (await host.Commands.GetCloudSessionAsync(sessionId).WaitAsync(Timeout))!;
        Assert.IsFalse(details.Continuing, "The reservation is cleared.");
        Assert.IsNull(details.ContinuedAt, "It is still in the cloud.");
        var sent = await host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "local")).Receipt!.Completion.WaitAsync(Timeout);
        Assert.AreEqual(SessionRuntimeService.InCloudCode, sent.Code);
    }

    [TestMethod]
    public async Task ACloudSessionBroughtBackInATerminal_GivesTheCommand_ThenReadsWhatItWrote_IntoTheSameConversation()
    {
        using var temp = TempDirectory.Create();
        var provider = new CloudProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateCloudSessionAsync(host.CurrentProject, provider.Descriptor, null, null, "Fix it", new NoTerminal(), null)
            .WaitAsync(Timeout)).Session!.SessionId;

        var command = await host.Commands.ContinueCloudSessionHereAsync(sessionId, null, fromTerminal: true).WaitAsync(Timeout);
        Assert.AreEqual("terminal", command.Status);
        var waiting = (await host.Commands.GetCloudSessionAsync(sessionId).WaitAsync(Timeout))!;
        Assert.IsTrue(waiting.Continuing, "Reserved: the conversation and its folder are kept.");
        Assert.IsTrue(waiting.InTerminal, "and the card knows it waits on the terminal.");
        Assert.AreEqual(0, provider.Returns, "Nothing is run for the user: the user runs it.");
        var reserved = command.Command!.Split(' ')[^1];

        var again = await host.Commands.ContinueCloudSessionHereAsync(sessionId, null, true).WaitAsync(Timeout);
        Assert.AreEqual(("terminal", command.Command), (again.Status, again.Command), "Asked again before it ran: the same command, into the same conversation.");
        provider.Written = reserved;
        var read = await host.Commands.ContinueCloudSessionHereAsync(sessionId, null, fromTerminal: true).WaitAsync(Timeout);
        Assert.AreEqual("ok", read.Status, read.Message);
        Assert.AreEqual(reserved, (await host.SessionViewCatalog.JournalStore.CreateSessionStore().GetStateAsync(sessionId))!.ProviderSessionId,
            "The conversation the command named.");
    }

    [TestMethod]
    public async Task ACloudSessionStartedElsewhere_IsRecordedFromItsLink_Once()
    {
        using var temp = TempDirectory.Create();
        var provider = new CloudProvider();
        await using var host = await CreateHostAsync(temp, provider);
        Assert.AreEqual("invalid_link", (await host.Commands.AdoptCloudSessionAsync(host.CurrentProject, provider.Descriptor, null, null, "not a link").WaitAsync(Timeout)).Status);
        var adopted = await host.Commands.AdoptCloudSessionAsync(host.CurrentProject, provider.Descriptor, null, null, CloudProvider.CloudId).WaitAsync(Timeout);
        Assert.AreEqual("created", adopted.Status);
        var details = (await host.Commands.GetCloudSessionAsync(adopted.Session!.SessionId).WaitAsync(Timeout))!;
        Assert.AreEqual((CloudProvider.CloudId, (string?)null), (details.Id, details.Task), "Nothing is read from the cloud session: no task is known.");
        var again = await host.Commands.AdoptCloudSessionAsync(host.CurrentProject, provider.Descriptor, null, null, CloudProvider.CloudId).WaitAsync(Timeout);
        Assert.AreEqual(("existing", adopted.Session.SessionId), (again.Status, again.Session!.SessionId), "A cloud session is held by one session.");
    }

    [TestMethod]
    public async Task ACloudSession_IsNotBroughtIntoAFolderWithChangesOfTrackedFiles()
    {
        using var temp = TempDirectory.Create();
        var git = (string folder, params string[] arguments) => GitWorktreeService.RunGitAsync(folder, arguments, TimeSpan.FromSeconds(30), CancellationToken.None);
        if ((await git(temp.ProjectRoot, "init", "-q")).ExitCode != 0) Assert.Inconclusive("git is not available.");
        await File.WriteAllTextAsync(Path.Combine(temp.ProjectRoot, "a.txt"), "one");
        await git(temp.ProjectRoot, "add", "a.txt");
        await git(temp.ProjectRoot, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "init");
        var provider = new CloudProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateCloudSessionAsync(host.CurrentProject, provider.Descriptor, null, null, "Fix it", new NoTerminal(), null)
            .WaitAsync(Timeout)).Session!.SessionId;
        Assert.IsNotNull((await host.Commands.GetCloudSessionAsync(sessionId).WaitAsync(Timeout))!.Branch, "The branch it started from is kept.");

        await File.WriteAllTextAsync(Path.Combine(temp.ProjectRoot, "a.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(temp.ProjectRoot, "untracked.txt"), "new");
        var refused = await host.Commands.ContinueCloudSessionHereAsync(sessionId, null, false).WaitAsync(Timeout);
        Assert.AreEqual(("uncommitted_changes", 1), (refused.Status, refused.ChangedFiles), "Only the tracked file counts: Claude Code refuses for it alone.");
        Assert.AreEqual(0, provider.Returns, "Claude Code is not run: it would fail without a word.");
    }

    private static async Task<SessionViewDescriptor> ListedAsync(CodeAltaHost host, string sessionId)
    {
        await foreach (var session in host.RuntimeService.ListRecoverableSessionsAsync(cancellationToken: CancellationToken.None))
        {
            if (session.SessionId == sessionId) return session;
        }

        Assert.Fail($"Session '{sessionId}' is not listed.");
        return null!;
    }

    // The project is in the catalog, as every project a window shows: its sessions are listed.
    private static async Task<CodeAltaHost> CreateHostAsync(TempDirectory temp, CloudProvider provider)
    {
        var host = await CreateBareHostAsync(temp, provider);
        await host.ProjectCatalog.SaveAsync(host.CurrentProject, CancellationToken.None);
        return host;
    }

    private static Task<CodeAltaHost> CreateBareHostAsync(TempDirectory temp, CloudProvider provider)
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

        public static TempDirectory Create() => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.Cloud.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            // git marks the files of its objects read-only.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class NoTerminal : IAgentInteractiveTerminal
    {
        public Task<AgentInteractiveOutcome> RunAsync(AgentInteractiveProgram program, Func<IReadOnlyList<string>, bool> asks, Action? onShown, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The fake provider runs nothing.");
    }

    // A provider whose cloud sessions are what the test says: what it starts, sends and brings back is recorded.
    private sealed class CloudProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor, IAgentProviderCloudSessions
    {
        public const string CloudId = "session_01ABCDEFGHJKLMNP";
        private readonly List<IReadOnlyList<AgentConversationMessage>> _conversations = [];

        public AgentCloudStart? Start { get; set; }

        public AgentCloudSendResult? Send { get; set; }

        public AgentCloudReturn? Return { get; set; }

        public string? StartedTask { get; private set; }

        public (string Id, string Message)? LastSent { get; private set; }

        public string? BroughtInto { get; private set; }

        public string? Conversation { get; private set; }

        // The conversation a bring-back in a terminal wrote, which a read then finds.
        public string? Written { get; set; }

        public int Returns { get; private set; }

        public int Turns { get; private set; }

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-cloud"), "Fake Cloud")
        {
            DefaultModelId = "fake-model",
            SupportsCloudSessions = true,
        };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-cloud", DisplayName = "Fake Cloud", TransportKind = AgentTransportKind.OpenAIResponses,
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
        {
            Turns++;
            lock (_conversations) _conversations.Add([.. request.Conversation]);
            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("answer")]),
            });
        }

        public Task<AgentCloudStart> StartCloudSessionAsync(AgentCloudStartRequest request, IAgentInteractiveTerminal? terminal, Action? onShown, CancellationToken cancellationToken)
        {
            StartedTask = request.Task;
            return Task.FromResult(Start ?? new AgentCloudStart("created", CloudId, "https://claude.ai/code/" + CloudId, "Fixed by the cloud"));
        }

        public Task<AgentCloudSendResult> SendToCloudSessionAsync(string cloudSessionId, string message, string workingDirectory, CancellationToken cancellationToken)
        {
            if (Send is { } refused) return Task.FromResult(refused);
            LastSent = (cloudSessionId, message);
            return Task.FromResult(new AgentCloudSendResult("ok"));
        }

        public Task<AgentCloudReturn> BringCloudSessionBackAsync(string cloudSessionId, string conversationId, string workingDirectory, CancellationToken cancellationToken)
        {
            Returns++;
            if (Return is { } failed) return Task.FromResult(failed);
            BroughtInto = workingDirectory;
            Written = conversationId;
            return Task.FromResult(new AgentCloudReturn("ok"));
        }

        public AgentExternalTurns ReadBroughtBackTurns(string conversationId, string workingDirectory, int users, int assistants, bool fromTerminal, CancellationToken cancellationToken)
        {
            if (!string.Equals(Written, conversationId, StringComparison.Ordinal)) return AgentExternalTurns.Missing;
            Conversation = conversationId;
            return new AgentExternalTurns(AgentExternalTurnsStatus.Read,
                [Turn("Fix the flaky test", "Fixed."), Turn("Also add a test.", "Added.")],
                (_, writtenUsers, writtenAssistants) => JsonDocument.Parse($$"""{"kind":"fake","users":{{users + writtenUsers}},"assistants":{{assistants + writtenAssistants}}}""").RootElement.Clone());
        }

        public (string Command, string? ConfigDirectory) BringBackCommand(string cloudSessionId, string conversationId)
            => ($"claude --teleport {cloudSessionId} --session-id {conversationId}", null);

        public (string Id, string Url)? FindCloudSession(string text)
            => text.StartsWith("session_", StringComparison.Ordinal) ? (text, "https://claude.ai/code/" + text) : null;

        public string[] LastConversation()
        {
            lock (_conversations)
            {
                return [.. _conversations[^1].Select(static message => string.Concat(message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value)))];
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static AgentExternalTurn Turn(string prompt, string answer)
            => new(
                [
                    new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text(prompt)]),
                    new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(answer)]),
                ],
                AgentExternalTurnOrigin.Cloud, "sdk-cli", DateTimeOffset.UtcNow);
    }
}
