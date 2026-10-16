using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// A conversation of the Claude Code CLI that another Claude process holds (VS Code, a terminal, a background session)
/// when a session of CodeAlta is to resume it: the run asks what to do, through a scripted CLI and a fixture registry of
/// live sessions.
/// </summary>
[TestClass]
public sealed class ClaudeCodeHeldConversationTests
{
    private const int VsCodePid = 4100;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task HeldConversation_AsksWithTheHolder_AndOpenAnywayResumesIt()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        cli.HoldConversation(VsCodePid, conversation);
        var asked = new List<AgentConversationHeldRequest>();

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        await session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.OpenAnyway))).WaitAsync(Timeout);

        var request = asked.Single();
        Assert.AreEqual(sessionId, request.SessionId);
        Assert.AreEqual(conversation, request.ConversationId);
        Assert.IsNotNull(request.RunId);
        Assert.AreEqual(AgentConversationHolderOrigin.VsCode, request.Holder.Origin);
        Assert.AreEqual("VS Code", request.Holder.OriginText);
        Assert.AreEqual(VsCodePid, request.Holder.ProcessId);
        Assert.AreEqual("claude-vscode", request.Holder.Entrypoint);
        CollectionAssert.AreEqual(
            new[] { AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.OpenAnyway, AgentConversationHeldChoice.Cancel },
            request.Choices.ToArray());
        var process = SessionProcess(cli);
        Assert.AreEqual(conversation, process.ResumedSessionId);
        Assert.IsFalse(process.ForksSession);
        Assert.AreEqual("second", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()));
    }

    [TestMethod]
    public async Task HeldConversation_ContinueAsCopy_ForksIt_AndTheSessionFollowsTheCopy()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        cli.HoldConversation(VsCodePid, conversation);
        var asked = new List<AgentConversationHeldRequest>();

        string copy;
        await using (var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions()))
        await using (var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory)))
        {
            await session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.ContinueAsCopy))).WaitAsync(Timeout);
            var process = SessionProcess(cli);
            Assert.AreEqual(conversation, process.ResumedSessionId);
            Assert.IsTrue(process.ForksSession, "The conversation VS Code holds is not written.");
            Assert.IsNull(process.ResumedAt, "The whole conversation is copied.");
            Assert.AreEqual("second", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()));
            copy = process.SessionId;
            Assert.AreNotEqual(conversation, copy);

            // The same session goes on with the copy, which nobody else holds: nothing is asked.
            await session.SendAsync(Send("third", Answer(asked, AgentConversationHeldChoice.Cancel))).WaitAsync(Timeout);
            Assert.AreEqual("third", ClaudeCodeFakeProcess.UserText(SessionProcess(cli).UserMessages.Last()));
        }

        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(directory.Path));
        var state = (await store.GetStateAsync(sessionId))!;
        Assert.AreEqual(copy, ClaudeCodeProviderState.Read(state)!.SessionId, "The session records the copy after its first answer.");
        Assert.IsNull(state.ProviderBranch);

        // Resumed later, the session continues the copy, still without asking: VS Code holds the original.
        await using var laterRuntime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var later = await laterRuntime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        await later.SendAsync(Send("fourth", Answer(asked, AgentConversationHeldChoice.Cancel))).WaitAsync(Timeout);
        var resumed = SessionProcess(cli);
        Assert.AreEqual(copy, resumed.ResumedSessionId);
        Assert.IsFalse(resumed.ForksSession);
        Assert.AreEqual("fourth", ClaudeCodeFakeProcess.UserText(resumed.UserMessages.Single()), "The copy holds the conversation: no prompt is sent again.");
        Assert.AreEqual(1, asked.Count);
    }

    [TestMethod]
    public async Task HeldConversation_Wait_ShowsTheWait_AndResumesOnceReleased()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        cli.HoldConversation(VsCodePid, conversation);
        var asked = new List<AgentConversationHeldRequest>();
        var started = SessionProcesses(cli).Count;

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        var events = new EventLog();
        session.Subscribe(events.Add);
        var send = session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.Wait)));

        await events.WaitForAsync(static e => IsWait(e, AgentConversationHeldUpdates.Waiting), Timeout);
        var waiting = events.Snapshot().OfType<AgentSessionUpdateEvent>().Single(static e => IsWait(e, AgentConversationHeldUpdates.Waiting));
        Assert.AreEqual(AgentSessionUpdateKind.Info, waiting.Kind);
        Assert.AreEqual("Waiting for VS Code to release the Claude Code conversation.", waiting.Message);
        Assert.IsTrue(AgentConversationHeldUpdates.TryRead(waiting.Details, out _, out var originText));
        Assert.AreEqual("VS Code", originText);
        await Task.Delay(200);
        Assert.IsFalse(send.IsCompleted, "The run waits while VS Code holds the conversation.");
        Assert.AreEqual(started, SessionProcesses(cli).Count, "Nothing is started meanwhile.");

        cli.ReleaseConversation(VsCodePid);
        await send.WaitAsync(Timeout);

        Assert.IsTrue(events.Snapshot().Any(static e => IsWait(e, AgentConversationHeldUpdates.Released)));
        var process = SessionProcess(cli);
        Assert.AreEqual(conversation, process.ResumedSessionId);
        Assert.IsFalse(process.ForksSession);
        Assert.AreEqual("second", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()));
        Assert.AreEqual(1, asked.Count);
    }

    [TestMethod]
    public async Task HeldConversation_Wait_EndsTheRun_WhenTheHolderAddedTurnsMeanwhile()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation, firstAnswer) = await ClaudeCodeExternalTurnsTests.StartConversationAsync(directory);
        var transcript = new ClaudeCodeExternalTurnsTests.Transcript(ClaudeCodeExternalTurnsTests.TranscriptPath(cli, directory, conversation), conversation, directory.Path);
        transcript.Write(
            transcript.User("u1", null, "first", "sdk-cli"),
            transcript.Assistant(firstAnswer, "u1", "msg_1", ClaudeCodeExternalTurnsTests.Text("Answer."), "sdk-cli"));
        cli.HoldConversation(VsCodePid, conversation);
        var asked = new List<AgentConversationHeldRequest>();

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        var events = new EventLog();
        session.Subscribe(events.Add);
        var send = session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.Wait)));
        await events.WaitForAsync(static e => IsWait(e, AgentConversationHeldUpdates.Waiting), Timeout);

        // VS Code goes on with the conversation while the run waits, then releases it.
        transcript.Append(
            transcript.User("u2", firstAnswer, "From VS Code."),
            transcript.Assistant("a2", "u2", "msg_2", ClaudeCodeExternalTurnsTests.Text("Answered there.")));
        var started = SessionProcesses(cli).Count;
        cli.ReleaseConversation(VsCodePid);

        // A failure of its own: the window gives the prompt back to its composer and marks it as not sent.
        var failure = await Assert.ThrowsExactlyAsync<AgentConversationChangedException>(() => send.WaitAsync(Timeout));
        StringAssert.Contains(failure.Message, $"Turns were added to the Claude Code conversation {conversation} in VS Code while this run waited for it.");
        StringAssert.Contains(failure.Message, "send the message again");
        Assert.AreEqual(started, SessionProcesses(cli).Count, "The conversation is not resumed before the session has those turns.");

        // The catch-up the next message starts with writes them after the prompt of the run that ended; the next turn
        // then resumes the conversation, which has them, and is sent only its own prompt.
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(directory.Path));
        var state = (await store.GetStateAsync(sessionId))!;
        var read = ClaudeCodeExternalTurns.Read(cli.ConfigRoot, state, directory.Path, CancellationToken.None);
        Assert.AreEqual(AgentExternalTurnOrigin.VsCode, read.Turns.Single().Origin);
        var prepared = await AgentExternalTurnJournal.PrepareAsync(new ModelProviderId("claude-code"), state, read, directory.Path,
            static (_, _) => throw new InvalidOperationException(), static (result, _) => Task.FromResult(result), DateTimeOffset.UtcNow, CancellationToken.None);
        await store.AppendEventsAsync(ClaudeCodeModelProviderRuntime.ProtocolFamily, "claude-code", sessionId, prepared.Events);
        await using var nextRuntime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var next = await nextRuntime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        await next.SendAsync(new AgentSendOptions { Input = AgentInput.Text("third") }).WaitAsync(Timeout);
        var process = SessionProcess(cli);
        Assert.AreEqual(conversation, process.ResumedSessionId);
        Assert.AreEqual("third", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()));
    }

    [TestMethod]
    public async Task RewoundSession_ContinueAsCopy_ForksTheConversationAtThePointOfTheRewind()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation, firstAnswer) = await StartRewoundConversationAsync(directory);
        cli.HoldConversation(VsCodePid, conversation);
        var asked = new List<AgentConversationHeldRequest>();

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        await session.SendAsync(Send("another second", Answer(asked, AgentConversationHeldChoice.ContinueAsCopy))).WaitAsync(Timeout);

        Assert.AreEqual(1, asked.Count);
        var process = SessionProcess(cli);
        Assert.AreEqual(conversation, process.ResumedSessionId);
        Assert.IsTrue(process.ForksSession);
        Assert.AreEqual(firstAnswer, process.ResumedAt, "The copy does not hold the turns the session was rewound before.");
        Assert.AreEqual("another second", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()));
    }

    [TestMethod]
    public async Task RewoundSession_Wait_ResumesAtThePointOfTheRewind_OnceReleased()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation, firstAnswer) = await StartRewoundConversationAsync(directory);
        cli.HoldConversation(VsCodePid, conversation);
        var asked = new List<AgentConversationHeldRequest>();

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        var events = new EventLog();
        session.Subscribe(events.Add);
        var send = session.SendAsync(Send("another second", Answer(asked, AgentConversationHeldChoice.Wait)));
        await events.WaitForAsync(static e => IsWait(e, AgentConversationHeldUpdates.Waiting), Timeout);
        cli.ReleaseConversation(VsCodePid);

        // The turns after the point are the session's own, which the rewind removed: not turns made while it waited.
        await send.WaitAsync(Timeout);
        var process = SessionProcess(cli);
        Assert.AreEqual(conversation, process.ResumedSessionId);
        Assert.IsFalse(process.ForksSession);
        Assert.AreEqual(firstAnswer, process.ResumedAt);
        Assert.AreEqual("another second", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()));
    }

    [TestMethod]
    public async Task HeldConversation_CancelledWhileWaiting_EndsTheRun_AndKeepsThePrompt()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        cli.HoldConversation(VsCodePid, conversation);
        var asked = new List<AgentConversationHeldRequest>();
        var started = SessionProcesses(cli).Count;

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        var events = new EventLog();
        session.Subscribe(events.Add);
        using var stop = new CancellationTokenSource();
        var send = session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.Wait)), stop.Token);
        await events.WaitForAsync(static e => IsWait(e, AgentConversationHeldUpdates.Waiting), Timeout);

        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => send.WaitAsync(Timeout));
        Assert.AreEqual(started, SessionProcesses(cli).Count);
        Assert.IsFalse(events.Snapshot().Any(static e => IsWait(e, AgentConversationHeldUpdates.Released)));

        await AssertNextTurnSendsBothPromptsAsync(cli, session, conversation, VsCodePid);
    }

    [TestMethod]
    public async Task HeldConversation_Cancel_EndsTheRunAsCancelled_AndKeepsThePrompt()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        cli.HoldConversation(VsCodePid, conversation);
        var asked = new List<AgentConversationHeldRequest>();
        var started = SessionProcesses(cli).Count;

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        var events = new EventLog();
        session.Subscribe(events.Add);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.Cancel))).WaitAsync(Timeout));

        Assert.AreEqual(started, SessionProcesses(cli).Count, "Nothing is started.");
        // The run ends as one the user stopped.
        StringAssert.Contains(events.Snapshot().OfType<AgentErrorEvent>().Single().Message, "cancelled");
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(directory.Path));
        Assert.AreEqual(conversation, ClaudeCodeProviderState.Read((await store.GetStateAsync(sessionId))!)!.SessionId);

        await AssertNextTurnSendsBothPromptsAsync(cli, session, conversation, VsCodePid);
    }

    [TestMethod]
    public async Task HeldConversation_WithNobodyToAsk_FailsTheRunWithTheWaysOut()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        cli.HoldConversation(VsCodePid, conversation);
        var started = SessionProcesses(cli).Count;

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("second") }).WaitAsync(Timeout));

        StringAssert.Contains(failure.Message, conversation);
        StringAssert.Contains(failure.Message, "VS Code (process 4100)");
        StringAssert.Contains(failure.Message, "wait until it is released");
        StringAssert.Contains(failure.Message, "as a copy");
        StringAssert.Contains(failure.Message, "open it here anyway");
        Assert.AreEqual(started, SessionProcesses(cli).Count, "It is never resumed silently.");

        await AssertNextTurnSendsBothPromptsAsync(cli, session, conversation, VsCodePid);
    }

    [TestMethod]
    public async Task HeldConversation_TheHandlerOfTheSession_IsAskedWhenTheSendHasNone()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        cli.HoldConversation(VsCodePid, conversation, entrypoint: "cli");
        var asked = new List<AgentConversationHeldRequest>();

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        var options = ResumeOptions(directory);
        await using var session = await runtime.ResumeSessionAsync(sessionId, new AgentSessionResumeOptions
        {
            ProviderKey = options.ProviderKey,
            WorkingDirectory = options.WorkingDirectory,
            OnPermissionRequest = options.OnPermissionRequest,
            OnConversationHeld = Answer(asked, AgentConversationHeldChoice.OpenAnyway),
        });
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("second") }).WaitAsync(Timeout);

        Assert.AreEqual(AgentConversationHolderOrigin.Terminal, asked.Single().Holder.Origin);
        Assert.AreEqual("a terminal", asked.Single().Holder.OriginText);
        Assert.AreEqual(conversation, SessionProcess(cli).ResumedSessionId);
    }

    [TestMethod]
    public async Task UnknownRegistry_ResumesWithoutAsking()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        // The registry is a file, not a folder: whether the conversation is held is not known.
        Directory.CreateDirectory(cli.ConfigRoot);
        File.WriteAllText(Path.Combine(cli.ConfigRoot, "sessions"), "not a folder");
        var asked = new List<AgentConversationHeldRequest>();

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        await session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.Cancel))).WaitAsync(Timeout);

        Assert.AreEqual(0, asked.Count);
        Assert.AreEqual(conversation, SessionProcess(cli).ResumedSessionId);
    }

    [TestMethod]
    public async Task ForkOfAHeldConversation_IsNotChecked()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(directory.Path));
        var whole = await store.ReadBranchPointAsync(sessionId, null, null, CancellationToken.None);
        var forkId = Guid.CreateVersion7().ToString();
        var now = DateTimeOffset.UtcNow;
        await store.CommitForkAsync(whole, whole.Summary with { SessionId = forkId, CreatedAt = now, UpdatedAt = now },
            whole.State with { SessionId = forkId, ProviderBranch = new(AgentProviderBranchKind.Fork, AtEnd: true) { CurrentProviderState = whole.CurrentState.ProviderState } },
            header: null, [], includeRemainder: false, CancellationToken.None);
        cli.HoldConversation(VsCodePid, conversation);
        var asked = new List<AgentConversationHeldRequest>();

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var fork = await runtime.ResumeSessionAsync(forkId, ResumeOptions(directory));
        await fork.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.Cancel))).WaitAsync(Timeout);

        Assert.AreEqual(0, asked.Count, "A fork writes a new conversation, which nobody holds.");
        var process = SessionProcess(cli);
        Assert.AreEqual(conversation, process.ResumedSessionId);
        Assert.IsTrue(process.ForksSession);
    }

    [TestMethod]
    public async Task RunningProcessAndTurnTheCliStartsByItself_AreNotChecked()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli { ConfigRoot = Path.Combine(directory.Path, "claude") };
        var asked = new List<AgentConversationHeldRequest>();
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = runtime.Descriptor.ProviderId.Value,
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnConversationHeld = Answer(asked, AgentConversationHeldChoice.Cancel),
        });
        var runs = (IAgentProviderInitiatedRuns)session;
        var ownTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = runs.OnProviderInitiatedRun(() => ownTurn.TrySetResult());
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("run the tests in the background") }).WaitAsync(Timeout);
        var process = SessionProcess(cli);
        // Another process opens the conversation while the process of the session still runs.
        cli.HoldConversation(VsCodePid, process.SessionId);

        // The CLI starts a turn by itself: the run that shows it reads the process that wrote it.
        process.Emit(new JsonObject
        {
            ["type"] = "system", ["subtype"] = "task_notification", ["task_id"] = "b1", ["status"] = "completed",
            ["summary"] = "Background command \"Run the tests\" completed (exit code 0)", ["session_id"] = process.SessionId,
        });
        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "session_state_changed", ["state"] = "running", ["session_id"] = process.SessionId });
        process.EmitTextStream("msg_bg", 0, "The tests pass.");
        process.EmitAssistant("msg_bg", new JsonArray(ClaudeCodeFakeProcess.TextBlock("The tests pass.")));
        process.EmitMessageStop();
        process.EmitResult("The tests pass.");
        await ownTurn.Task.WaitAsync(Timeout);
        await session.SendAsync(runs.TakeProviderInitiatedRun()!).WaitAsync(Timeout);

        // A prompt of the user goes to the process that runs: it holds the conversation already.
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("and now?") }).WaitAsync(Timeout);

        Assert.AreEqual(0, asked.Count);
        Assert.AreSame(process, SessionProcess(cli));
        Assert.AreEqual("and now?", ClaudeCodeFakeProcess.UserText(process.UserMessages.Last()));
    }

    [TestMethod]
    public async Task HeldByABackgroundSessionInTheRegistry_OffersOnlyWaitAndCopy()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        cli.HoldConversation(VsCodePid, conversation, entrypoint: "cli", kind: "bg");
        var asked = new List<AgentConversationHeldRequest>();

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        await session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.ContinueAsCopy))).WaitAsync(Timeout);

        var request = asked.Single();
        Assert.AreEqual(AgentConversationHolderOrigin.BackgroundSession, request.Holder.Origin);
        CollectionAssert.AreEqual(
            new[] { AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.Cancel },
            request.Choices.ToArray());
        Assert.IsTrue(SessionProcess(cli).ForksSession);
    }

    [TestMethod]
    public async Task ChoicesOfTheQuestion_CannotBeChangedByWhoIsAsked()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        var asked = new List<AgentConversationHeldRequest>();
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));

        cli.HoldConversation(VsCodePid, conversation);
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.Cancel))).WaitAsync(Timeout));
        cli.HoldConversation(VsCodePid, conversation, entrypoint: "cli", kind: "bg");
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.SendAsync(Send("third", Answer(asked, AgentConversationHeldChoice.Cancel))).WaitAsync(Timeout));

        Assert.AreEqual(2, asked.Count);
        foreach (var request in asked)
        {
            // The choices of every question are shared: a handler cannot change those of the next one.
            var choices = (IList<AgentConversationHeldChoice>)request.Choices;
            Assert.ThrowsExactly<NotSupportedException>(() => choices[0] = choices[0]);
        }
    }

    [TestMethod]
    public async Task BackgroundRefusal_AsksWaitOrCopy_AndCopyContinuesAFork()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        // The registry says nothing; the CLI refuses the resume because a background session runs the conversation.
        cli.HeldByBackground = true;
        var asked = new List<AgentConversationHeldRequest>();

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        await session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.ContinueAsCopy))).WaitAsync(Timeout);

        var request = asked.Single();
        Assert.AreEqual(conversation, request.ConversationId);
        Assert.AreEqual(AgentConversationHolderOrigin.BackgroundSession, request.Holder.Origin);
        CollectionAssert.AreEqual(
            new[] { AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.Cancel },
            request.Choices.ToArray());
        var process = SessionProcess(cli);
        Assert.AreEqual(conversation, process.ResumedSessionId);
        Assert.IsTrue(process.ForksSession);
        Assert.AreEqual("second", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()));
    }

    [TestMethod]
    public async Task BackgroundRefusal_Wait_TriesAgainUntilTheCliResumesIt()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation) = await StartConversationAsync(directory);
        cli.HeldByBackground = true;
        var asked = new List<AgentConversationHeldRequest>();
        var started = SessionProcesses(cli).Count;

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        var send = session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.Wait)));
        // The CLI is tried again while the background session runs, without asking again.
        while (SessionProcesses(cli).Count < started + 3)
        {
            await Task.Delay(20);
        }

        Assert.IsFalse(send.IsCompleted);
        cli.HeldByBackground = false;
        await send.WaitAsync(Timeout);

        Assert.AreEqual(1, asked.Count);
        var process = SessionProcess(cli);
        Assert.AreEqual(conversation, process.ResumedSessionId);
        Assert.IsFalse(process.ForksSession);
        Assert.AreEqual("second", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()));
    }

    [TestMethod]
    public async Task BackgroundRefusal_Wait_ShowsOneWait_TriesAgainLaterAndLater_AndIsReleasedOnlyWhenResumed()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, _) = await StartConversationAsync(directory);
        // The registry does not list the background session: only the CLI tells when it ends.
        cli.HeldByBackground = true;
        var asked = new List<AgentConversationHeldRequest>();
        var started = SessionProcesses(cli).Count;

        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        var events = new EventLog();
        session.Subscribe(events.Add);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var send = session.SendAsync(Send("second", Answer(asked, AgentConversationHeldChoice.Wait)));
        await events.WaitForAsync(static e => IsWait(e, AgentConversationHeldUpdates.Waiting), Timeout);
        var refused = started + 5;
        while (SessionProcesses(cli).Count < refused)
        {
            await Task.Delay(10);
        }

        // Four waits after the first refusal: 20, 40, 80 and 160 ms with the poll interval of the tests.
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(280), $"The CLI is started again later and later: {clock.Elapsed.TotalMilliseconds} ms.");
        Assert.AreEqual(1, events.Snapshot().Count(static e => IsWait(e, AgentConversationHeldUpdates.Waiting)), "The wait is shown once.");
        Assert.IsFalse(events.Snapshot().Any(static e => IsWait(e, AgentConversationHeldUpdates.Released)), "Nothing was released while the CLI refuses.");

        cli.HeldByBackground = false;
        await send.WaitAsync(Timeout);
        Assert.AreEqual(1, events.Snapshot().Count(static e => IsWait(e, AgentConversationHeldUpdates.Released)));
        Assert.AreEqual(1, asked.Count);
    }

    // The conversation is released; the next turn resumes it and sends the prompt that did not reach it with its own.
    private static async Task AssertNextTurnSendsBothPromptsAsync(ClaudeCodeFakeCli cli, IAgentSession session, string conversation, int pid)
    {
        cli.ReleaseConversation(pid);
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("third") }).WaitAsync(Timeout);
        var process = SessionProcess(cli);
        Assert.AreEqual(conversation, process.ResumedSessionId);
        CollectionAssert.AreEqual(new[] { "second", "third" }, process.UserMessages.Select(ClaudeCodeFakeProcess.UserText).ToArray());
    }

    // A session with one answered turn, whose process has exited: its next turn resumes the conversation of the CLI.
    private static async Task<(ClaudeCodeFakeCli Cli, string SessionId, string Conversation)> StartConversationAsync(TestTempDirectory directory)
    {
        var cli = new ClaudeCodeFakeCli { ConfigRoot = Path.Combine(directory.Path, "claude") };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = runtime.Descriptor.ProviderId.Value,
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        });
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("first") }).WaitAsync(Timeout);
        return (cli, session.SessionId, SessionProcess(cli).SessionId);
    }

    // A session with two answered turns, rewound to the first: its next turn resumes the conversation at the first answer.
    // The transcript holds both turns, as the CLI wrote them.
    private static async Task<(ClaudeCodeFakeCli Cli, string SessionId, string Conversation, string FirstAnswer)> StartRewoundConversationAsync(TestTempDirectory directory)
    {
        var cli = new ClaudeCodeFakeCli { ConfigRoot = Path.Combine(directory.Path, "claude") };
        string sessionId, conversation, firstAnswer, secondAnswer;
        await using (var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions()))
        await using (var session = await runtime.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = runtime.Descriptor.ProviderId.Value,
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }))
        {
            await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("first") }).WaitAsync(Timeout);
            firstAnswer = SessionProcess(cli).Entries.Single();
            await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("second") }).WaitAsync(Timeout);
            secondAnswer = SessionProcess(cli).Entries.Last();
            sessionId = session.SessionId;
            conversation = SessionProcess(cli).SessionId;
        }

        var transcript = new ClaudeCodeExternalTurnsTests.Transcript(ClaudeCodeExternalTurnsTests.TranscriptPath(cli, directory, conversation), conversation, directory.Path);
        transcript.Write(
            transcript.User("u1", null, "first", "sdk-cli"),
            transcript.Assistant(firstAnswer, "u1", "msg_1", ClaudeCodeExternalTurnsTests.Text("ok"), "sdk-cli"),
            transcript.User("u2", firstAnswer, "second", "sdk-cli"),
            transcript.Assistant(secondAnswer, "u2", "msg_2", ClaudeCodeExternalTurnsTests.Text("ok"), "sdk-cli"));

        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(directory.Path));
        var turns = await store.ReadTurnsAsync(sessionId);
        var point = await store.ReadBranchPointAsync(sessionId, turns.Turns[1].RunId, null, CancellationToken.None);
        await store.CommitRewindAsync(
            point,
            point.Summary,
            point.State with { ProviderBranch = new(AgentProviderBranchKind.Rewind, AtEnd: false) { CurrentProviderState = point.CurrentState.ProviderState } },
            header: null, [], CancellationToken.None);
        return (cli, sessionId, conversation, firstAnswer);
    }

    private static AgentConversationHeldHandler Answer(List<AgentConversationHeldRequest> asked, AgentConversationHeldChoice choice)
        => (request, _) =>
        {
            lock (asked)
            {
                asked.Add(request);
            }

            return Task.FromResult(choice);
        };

    private static AgentSendOptions Send(string text, AgentConversationHeldHandler handler)
        => new() { Input = AgentInput.Text(text), OnConversationHeld = handler };

    private static AgentSessionResumeOptions ResumeOptions(TestTempDirectory directory)
        => new()
        {
            ProviderKey = "claude-code",
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

    private static bool IsWait(AgentEvent e, string state)
        => e is AgentSessionUpdateEvent update && AgentConversationHeldUpdates.TryRead(update.Details, out var read, out _) && read == state;

    private static ClaudeCodeFakeProcess SessionProcess(ClaudeCodeFakeCli cli)
        => cli.Processes.Last(static process => process.Launch.Arguments.Contains("--mcp-config"));

    private static IReadOnlyList<ClaudeCodeFakeProcess> SessionProcesses(ClaudeCodeFakeCli cli)
        => [.. cli.Processes.Where(static process => process.Launch.Arguments.Contains("--mcp-config"))];

    private sealed class EventLog
    {
        private readonly List<AgentEvent> _events = [];
        private readonly List<(Func<AgentEvent, bool> Match, TaskCompletionSource Completion)> _waiters = [];

        public void Add(AgentEvent @event)
        {
            lock (_events)
            {
                _events.Add(@event);
                foreach (var (match, completion) in _waiters)
                {
                    if (match(@event))
                    {
                        completion.TrySetResult();
                    }
                }
            }
        }

        public AgentEvent[] Snapshot()
        {
            lock (_events)
            {
                return [.. _events];
            }
        }

        public Task WaitForAsync(Func<AgentEvent, bool> match, TimeSpan timeout)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_events)
            {
                if (_events.Exists(e => match(e)))
                {
                    return Task.CompletedTask;
                }

                _waiters.Add((match, completion));
            }

            return completion.Task.WaitAsync(timeout);
        }
    }
}
