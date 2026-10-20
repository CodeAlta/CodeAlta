using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// The catch-up of a Claude Code session: the turns VS Code or a terminal added to the transcript of its conversation
/// are read, written in the journal as turns made elsewhere, and the next turn resumes the conversation without
/// sending them again nor being billed for them.
/// </summary>
[TestClass]
public sealed class ClaudeCodeExternalTurnsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task TurnsMadeInVsCode_AreWrittenAsTurnsMadeElsewhere_AndTheNextTurnResumesWithoutThem()
    {
        using var directory = TestTempDirectory.Create();
        var (cli, sessionId, conversation, firstAnswer) = await StartConversationAsync(directory);
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(directory.Path));
        var recorded = (await store.GetStateAsync(sessionId))!;
        Assert.AreEqual(firstAnswer, ClaudeCodeProviderState.Read(recorded)!.Anchor, "The last assistant entry is recorded with the leaf.");

        // What the CLI wrote of the turn of CodeAlta, then a turn made in VS Code, which cost 0.25.
        var transcript = new Transcript(TranscriptPath(cli, directory, conversation), conversation, directory.Path);
        transcript.Write(
            transcript.User("u1", null, "first", "sdk-cli"),
            transcript.Assistant(firstAnswer, "u1", "msg_1", Text("Answer."), "sdk-cli"),
            transcript.User("u2", firstAnswer, "Read the file."),
            transcript.Assistant("a2", "u2", "msg_2", ToolUse("toolu_1", "Read", new JsonObject { ["file_path"] = Path.Combine(directory.Path, "a.txt") })),
            transcript.ToolResult("r1", "a2", "toolu_1", "contents"),
            transcript.Assistant("a3", "r1", "msg_3", Text("It says contents.")),
            Transcript.Line(new JsonObject { ["type"] = "cost-state", ["sessionId"] = conversation, ["totalCostUSD"] = cli.AddCost(conversation, 0.25) }));

        var read = await ReadAsync(cli, recorded, directory);
        Assert.AreEqual(AgentExternalTurnsStatus.Read, read.Status);
        var turn = read.Turns.Single();
        Assert.AreEqual(AgentExternalTurnOrigin.VsCode, turn.Origin);
        Assert.AreEqual("claude-vscode", turn.Entrypoint);
        CollectionAssert.AreEqual(
            new[] { AgentConversationRole.User, AgentConversationRole.Assistant, AgentConversationRole.Tool, AgentConversationRole.Assistant },
            turn.Messages.Select(static message => message.Role).ToArray());

        var prepared = await PrepareAsync(recorded, read, directory);
        Assert.AreEqual((1, 1, 2), (prepared.Turns, prepared.Users, prepared.Assistants));
        Assert.AreEqual("It says contents.", prepared.Preview);
        await store.AppendEventsAsync(ClaudeCodeModelProviderRuntime.ProtocolFamily, "claude-code", sessionId, prepared.Events);

        var before = ClaudeCodeProviderState.Read(recorded)!;
        var after = ClaudeCodeProviderState.Read((await store.GetStateAsync(sessionId))!)!;
        Assert.AreEqual(before.Users + 1, after.Users, "The counts grow by exactly what was written.");
        Assert.AreEqual(before.Assistants + 2, after.Assistants);
        Assert.AreEqual("a3", after.Leaf);
        Assert.AreEqual("a3", after.Anchor);
        Assert.AreEqual(0.26, after.Cost!.Value, 1e-9, "The cost of the turns made in VS Code is the conversation's, not the next turn's.");
        Assert.IsNotNull(after.Position, "The next catch-up reads only what is written after this one.");
        Assert.AreEqual(conversation, after.SessionId);

        // The turn shows in the journal as a turn of its own, made in VS Code.
        var turns = (await store.ReadTurnsAsync(sessionId)).Turns;
        Assert.AreEqual(2, turns.Count);
        var external = turns[1];
        Assert.IsTrue(external.MadeElsewhere);
        Assert.IsFalse(turns[0].MadeElsewhere);
        Assert.IsFalse(external.StartedByProvider);
        StringAssert.StartsWith(external.PromptContentId, "external:vscode:");
        Assert.AreEqual("Read the file.", external.PromptPreview);
        Assert.AreEqual(AgentSessionTurnStatus.Completed, external.Status);
        Assert.AreEqual(1, external.AssistantMessageCount, "One assistant text; the call has no text.");
        Assert.AreEqual(1, external.ToolCallCount);

        // The next turn resumes the conversation, which holds the turns made in VS Code: only its own prompt is sent,
        // without the history told as text, and it is counted only its own cost.
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.ResumeSessionAsync(sessionId, ResumeOptions(directory));
        var events = new List<AgentEvent>();
        session.Subscribe(e => { lock (events) events.Add(e); });
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("third") }).WaitAsync(Timeout);

        var process = SessionProcess(cli);
        Assert.AreEqual(conversation, process.ResumedSessionId);
        Assert.IsFalse(process.ForksSession);
        Assert.AreEqual("third", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()));
        AgentSessionUpdateEvent usage;
        lock (events) usage = events.OfType<AgentSessionUpdateEvent>().Last(static e => e.Usage?.LastOperation?.Cost is not null);
        Assert.AreEqual(0.01, usage.Usage!.LastOperation!.Cost!.Value, 1e-9);

        // The position of the catch-up is kept with the answers of CodeAlta, for the next one.
        var latest = ClaudeCodeProviderState.Read((await store.GetStateAsync(sessionId))!)!;
        Assert.AreEqual(after.Position! with { LastLines = null }, latest.Position! with { LastLines = null });
        Assert.AreEqual(process.Entries.Last(), latest.Leaf);
    }

    [TestMethod]
    public async Task PromptImages_AreSavedLikePastedImages_AndCompactionsAreNotices()
    {
        using var directory = TestTempDirectory.Create();
        var transcript = Transcript.For(directory, "c1");
        transcript.Write(
            transcript.User("u1", null, "first", "sdk-cli"),
            transcript.Assistant("a1", "u1", "msg_1", Text("Answer."), "sdk-cli"),
            transcript.User("u2", "a1", new JsonArray(Text("Look."), Image("aGVsbG8=", "image/png")), "cli"),
            transcript.Assistant("a2", "u2", "msg_2", Text("Seen."), "cli"));
        var read = ClaudeCodeExternalTurns.Read(directory.Path, new ClaudeCodeProviderState("c1", 1, 1, Leaf: "a1"), directory.Path, CancellationToken.None);
        var saved = new List<AgentMessagePart.Data>();
        var prepared = await AgentExternalTurnJournal.PrepareAsync(
            new ModelProviderId("claude-code"), CreateState("s1", new ClaudeCodeProviderState("c1", 1, 1, Leaf: "a1")), read, directory.Path,
            (data, _) => { saved.Add(data); return Task.FromResult<AgentInputItem>(new AgentInputItem.LocalImage(@"C:\attachments\image.png", "image", data.MediaType)); },
            static (result, _) => Task.FromResult(result),
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual("image/png", saved.Single().MediaType);
        var prompt = prepared.Events.OfType<AgentContentCompletedEvent>().Single(static e => e.Kind == AgentContentKind.User);
        StringAssert.StartsWith(prompt.ContentId, "external:terminal:");
        var input = prepared.Events.OfType<AgentRawEvent>().Single(static e => e.BackendEventType == "local.userMessage").Raw
            .Deserialize(AgentJsonSerializerContext.Default.AgentInput)!;
        Assert.IsInstanceOfType<AgentInputItem.LocalImage>(input.Items[1]);
        Assert.IsTrue(prepared.Events.Where(static e => e.RunId is { } run && !run.Value.StartsWith("external:", StringComparison.Ordinal)).All(static e => e is AgentRawEvent { BackendEventType: "local.sessionState" }),
            "Every record of the turn is under its run made elsewhere.");
        var idle = prepared.Events.OfType<AgentSessionUpdateEvent>().Single(static e => e.Kind == AgentSessionUpdateKind.Idle);
        Assert.IsNull(idle.Usage, "No usage is reported for a turn made elsewhere.");

        var compaction = new AgentExternalTurns(AgentExternalTurnsStatus.Read,
            [new AgentExternalTurn([], AgentExternalTurnOrigin.VsCode, "claude-vscode", null, IsCompaction: true)],
            (_, users, assistants) => new ClaudeCodeProviderState("c1", 1 + users, 1 + assistants, Leaf: "c9").ToJson());
        var compacted = await AgentExternalTurnJournal.PrepareAsync(
            new ModelProviderId("claude-code"), CreateState("s1", new ClaudeCodeProviderState("c1", 1, 1, Leaf: "a1")), compaction, directory.Path,
            static (_, _) => throw new InvalidOperationException(), static (result, _) => Task.FromResult(result), DateTimeOffset.UtcNow, CancellationToken.None);
        var notice = compacted.Events.OfType<AgentSessionUpdateEvent>().Single();
        Assert.AreEqual(AgentSessionUpdateKind.CompactionCompleted, notice.Kind);
        Assert.AreEqual("The conversation was compacted in VS Code.", notice.Message);
        Assert.AreEqual((0, 0), (compacted.Users, compacted.Assistants));
        Assert.AreEqual("c9", ClaudeCodeProviderState.Read(compacted.State)!.Leaf, "The state of the whole read follows the compaction.");
    }

    [TestMethod]
    public void TurnsWithoutACost_KeepTheCostRecorded()
    {
        using var directory = TestTempDirectory.Create();
        var transcript = Transcript.For(directory, "c1");
        transcript.Write(
            transcript.User("u1", null, "first", "sdk-cli"),
            transcript.Assistant("a1", "u1", "msg_1", Text("Answer."), "sdk-cli"),
            transcript.User("u2", "a1", "From VS Code."),
            transcript.Assistant("a2", "u2", "msg_2", Text("Answered there.")));

        var read = ClaudeCodeExternalTurns.Read(directory.Path, new ClaudeCodeProviderState("c1", 1, 1, Cost: 0.5, Leaf: "a1"), directory.Path, CancellationToken.None);

        // A CLI that resumes the conversation counts from the last cost the transcript has: these turns are not in it.
        var state = ClaudeCodeProviderState.Read(read.ProviderStateAfter!(1, 1, 1))!;
        Assert.AreEqual(0.5, state.Cost);
        Assert.AreEqual((2, 2, "a2"), (state.Users, state.Assistants, state.Leaf));
    }

    [TestMethod]
    public void ARewoundConversation_CannotBeFollowed_AndIsFollowedFromWhereItIsNow()
    {
        using var directory = TestTempDirectory.Create();
        var transcript = Transcript.For(directory, "c1");
        transcript.Write(
            transcript.User("u1", null, "first", "sdk-cli"),
            transcript.Assistant("a1", "u1", "msg_1", Text("Answer."), "sdk-cli"),
            // VS Code went back to the first prompt and continued from there.
            transcript.User("u2", null, "Another first."),
            transcript.Assistant("a2", "u2", "msg_2", Text("Another answer.")),
            Transcript.Line(new JsonObject { ["type"] = "cost-state", ["sessionId"] = "c1", ["totalCostUSD"] = 0.75 }));

        var read = ClaudeCodeExternalTurns.Read(directory.Path, new ClaudeCodeProviderState("c1", 1, 1, Cost: 0.5, Leaf: "a1", Anchor: "a1"), directory.Path, CancellationToken.None);

        Assert.AreEqual(AgentExternalTurnsStatus.CannotFollow, read.Status);
        Assert.AreEqual(0, read.Turns.Count);
        var state = ClaudeCodeProviderState.Read(read.ProviderStateAfter!(0, 0, 0))!;
        Assert.AreEqual((1, 1), (state.Users, state.Assistants), "Nothing is written: the counts stay.");
        Assert.AreEqual("a2", state.Leaf, "What is written from now on is followed from the leaf the conversation has now.");
        Assert.AreEqual(0.75, state.Cost);
        Assert.IsNotNull(state.Position);
        Assert.AreEqual(AgentExternalTurnsStatus.Unchanged,
            ClaudeCodeExternalTurns.Read(directory.Path, state, directory.Path, CancellationToken.None).Status, "The notice is not written twice.");
    }

    [TestMethod]
    public async Task ACannotFollowRead_WritesTheNoticeAndTheStateOnly()
    {
        var read = new AgentExternalTurns(AgentExternalTurnsStatus.CannotFollow, [], (_, _, _) => new ClaudeCodeProviderState("c1", 1, 1, Leaf: "a2").ToJson());
        var prepared = await AgentExternalTurnJournal.PrepareAsync(
            new ModelProviderId("claude-code"), CreateState("s1", new ClaudeCodeProviderState("c1", 1, 1, Leaf: "a1")), read, null,
            static (_, _) => throw new InvalidOperationException(), static (result, _) => Task.FromResult(result), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(2, prepared.Events.Count);
        var notice = (AgentSessionUpdateEvent)prepared.Events[0];
        Assert.AreEqual(AgentSessionUpdateKind.Warning, notice.Kind);
        Assert.AreEqual("This conversation was changed outside CodeAlta in a way it cannot follow; the model has the full conversation.", notice.Message);
        Assert.AreEqual("local.sessionState", ((AgentRawEvent)prepared.Events[1]).BackendEventType);
        Assert.AreEqual("a2", ClaudeCodeProviderState.Read(prepared.State)!.Leaf);
        Assert.AreEqual((0, 0, 0), (prepared.Turns, prepared.Users, prepared.Assistants));
    }

    [TestMethod]
    public void ALeafTheCliNeverWrote_IsFollowedFromTheLastAssistantEntry_WithoutTheResultsTheSessionHas()
    {
        using var directory = TestTempDirectory.Create();
        var transcript = Transcript.For(directory, "c1");
        // CodeAlta's run ended on the result of a tool, which the CLI wrote under another identifier than it streamed.
        transcript.Write(
            transcript.User("u1", null, "first", "sdk-cli"),
            transcript.Assistant("a1", "u1", "msg_1", ToolUse("toolu_1", "Read", new JsonObject()), "sdk-cli"),
            transcript.ToolResult("r1", "a1", "toolu_1", "contents", "sdk-cli"),
            transcript.User("u2", "r1", "Go on from VS Code."),
            transcript.Assistant("a2", "u2", "msg_2", Text("Went on.")));

        var unanchored = ClaudeCodeExternalTurns.Read(directory.Path, new ClaudeCodeProviderState("c1", 1, 1, Leaf: "streamed-result"), directory.Path, CancellationToken.None);
        Assert.AreEqual(AgentExternalTurnsStatus.CannotFollow, unanchored.Status, "Without an entry the CLI wrote, where it went on is not known.");

        var read = ClaudeCodeExternalTurns.Read(directory.Path, new ClaudeCodeProviderState("c1", 1, 1, Leaf: "streamed-result", Anchor: "a1"), directory.Path, CancellationToken.None);
        Assert.AreEqual(AgentExternalTurnsStatus.Read, read.Status);
        var turn = read.Turns.Single();
        CollectionAssert.AreEqual(new[] { AgentConversationRole.User, AgentConversationRole.Assistant }, turn.Messages.Select(static m => m.Role).ToArray(),
            "The result of the call of the anchor, which the session has, is not written again.");
        var state = ClaudeCodeProviderState.Read(read.ProviderStateAfter!(1, 1, 1))!;
        Assert.AreEqual(("a2", "a2"), (state.Leaf, state.Anchor));
    }

    [TestMethod]
    public void NothingNew_IsUnchanged_AndAMissingTranscript_IsMissing()
    {
        using var directory = TestTempDirectory.Create();
        var transcript = Transcript.For(directory, "c1");
        transcript.Write(
            transcript.User("u1", null, "first", "sdk-cli"),
            transcript.Assistant("a1", "u1", "msg_1", Text("Answer."), "sdk-cli"));

        var first = ClaudeCodeExternalTurns.Read(directory.Path, new ClaudeCodeProviderState("c1", 1, 1, Leaf: "a1"), directory.Path, CancellationToken.None);
        Assert.AreEqual(AgentExternalTurnsStatus.Read, first.Status, "Without a position the whole file is read once.");
        Assert.AreEqual(0, first.Turns.Count);
        var positioned = ClaudeCodeProviderState.Read(first.ProviderStateAfter!(0, 0, 0))!;
        Assert.AreEqual(AgentExternalTurnsStatus.Unchanged, ClaudeCodeExternalTurns.Read(directory.Path, positioned, directory.Path, CancellationToken.None).Status);

        Assert.AreEqual(AgentExternalTurnsStatus.Missing,
            ClaudeCodeExternalTurns.Read(directory.Path, new ClaudeCodeProviderState("c2", 1, 1, Leaf: "a1"), directory.Path, CancellationToken.None).Status);
        Assert.AreEqual(AgentExternalTurnsStatus.None,
            ClaudeCodeExternalTurns.Read(directory.Path, new ClaudeCodeProviderState("c1", 1, 1), directory.Path, CancellationToken.None).Status,
            "A state recorded before entries were followed has no point to follow from.");
        Assert.AreEqual(AgentExternalTurnsStatus.None,
            ClaudeCodeExternalTurns.Read(directory.Path, new ClaudeCodeProviderState(@"..\c1", 1, 1, Leaf: "a1"), directory.Path, CancellationToken.None).Status);
    }

    [TestMethod]
    public void ProviderState_KeepsThePositionAndTheAnchor()
    {
        var position = new ClaudeCodeTranscriptPosition(120, 130, new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc), "ABCDEF", "a1", "t1", "msg_1",
            [new ClaudeCodeTranscriptLastLine("claude-vscode", "a1", false), new ClaudeCodeTranscriptLastLine(null, "x1", true)]);
        var state = new ClaudeCodeProviderState("c1", 2, 3, "hash", 0.5, "a1", position, "a0");

        var read = ClaudeCodeProviderState.Read(state.ToJson())!;

        Assert.AreEqual(state with { Position = null }, read with { Position = null });
        Assert.AreEqual(position with { LastLines = null }, read.Position! with { LastLines = null });
        CollectionAssert.AreEqual(position.LastLines!.ToArray(), read.Position!.LastLines!.ToArray());
        Assert.IsNull(ClaudeCodeProviderState.Read(new ClaudeCodeProviderState("c1", 1, 1).ToJson())!.Position);
    }

    [TestMethod]
    public async Task ACompactionOfCodeAlta_IsNotReadBackAsACompactionMadeElsewhere()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli { ConfigRoot = Path.Combine(directory.Path, "claude") };
        cli.OnUserMessage = (process, user) =>
        {
            if (ClaudeCodeFakeProcess.UserText(user) != "/compact")
            {
                process.EmitTextTurn("msg_1", "ok", user);
                return Task.CompletedTask;
            }

            // What the CLI writes of the compaction it was asked for, before it answers the command.
            var answered = process.Entries.Last();
            var transcript = new Transcript(TranscriptPath(cli, directory, process.SessionId), process.SessionId, directory.Path);
            transcript.Write(
                transcript.User("u1", null, "first", "sdk-cli"),
                transcript.Assistant(answered, "u1", "msg_1", Text("ok"), "sdk-cli"),
                transcript.Boundary("b1", answered, "sdk-cli"),
                transcript.Summary("s1", "b1", "Summary of the conversation.", "sdk-cli"),
                transcript.User("c1", "s1", "<command-name>/compact</command-name>", "sdk-cli"),
                transcript.User("c2", "c1", "<local-command-stdout>Compacted</local-command-stdout>", "sdk-cli"));
            process.Emit(new JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "compact_boundary",
                ["session_id"] = process.SessionId,
                ["uuid"] = "b1",
                ["compact_metadata"] = new JsonObject { ["trigger"] = "manual", ["pre_tokens"] = 50000, ["post_tokens"] = 4000 },
            });
            process.EmitResult(string.Empty, user);
            return Task.CompletedTask;
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.CreateSessionAsync(CreateOptions(runtime, directory));
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("first") }).WaitAsync(Timeout);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync().WaitAsync(Timeout);
        Assert.IsTrue(outcome!.Success);

        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(directory.Path));
        var read = await ReadAsync(cli, (await store.GetStateAsync(session.SessionId))!, directory);
        Assert.IsTrue(read.Status is AgentExternalTurnsStatus.Unchanged or AgentExternalTurnsStatus.Read, read.Status.ToString());
        Assert.AreEqual(0, read.Turns.Count, "The compaction CodeAlta asked for is recorded as the point the conversation goes on from.");
    }

    [TestMethod]
    public async Task AStoppedTurn_IsNotReadBackAsATurnMadeElsewhere()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli { ConfigRoot = Path.Combine(directory.Path, "claude") };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = 0;
        cli.OnUserMessage = async (process, user) =>
        {
            if (Interlocked.Increment(ref turn) == 1)
            {
                process.EmitTextTurn("msg_1", "ok", user);
                return;
            }

            // What the CLI writes of the prompt and of the answer it began, then of the stop.
            var answered = process.Entries.Last();
            var transcript = new Transcript(TranscriptPath(cli, directory, process.SessionId), process.SessionId, directory.Path);
            transcript.Write(
                transcript.User("u1", null, "first", "sdk-cli"),
                transcript.Assistant(answered, "u1", "msg_1", Text("ok"), "sdk-cli"),
                transcript.User("u2", answered, "second", "sdk-cli"),
                transcript.Assistant("a2", "u2", "msg_2", Text("Half an"), "sdk-cli"),
                transcript.User("i1", "a2", "[Request interrupted by user]", "sdk-cli"));
            process.EmitInit();
            process.EmitTextStream("msg_2", 0, "Half an");
            started.TrySetResult();
            await process.Interrupted;
        };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.CreateSessionAsync(CreateOptions(runtime, directory));
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("first") }).WaitAsync(Timeout);

        var send = session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("second") });
        await started.Task.WaitAsync(Timeout);
        await session.AbortAsync().WaitAsync(Timeout);
        await Assert.ThrowsAsync<OperationCanceledException>(() => send.WaitAsync(Timeout));

        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(directory.Path));
        var read = await ReadAsync(cli, (await store.GetStateAsync(session.SessionId))!, directory);
        Assert.IsTrue(read.Status is AgentExternalTurnsStatus.Unchanged or AgentExternalTurnsStatus.Read, read.Status.ToString());
        Assert.AreEqual(0, read.Turns.Count, "The prompt the journal has is not written again as a turn made elsewhere.");
    }

    [TestMethod]
    public async Task AnAttachedSession_IsReadOnce_AndItsNextAnswerKeepsWhereTheReadStopped()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli { ConfigRoot = Path.Combine(directory.Path, "claude") };
        await using var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        await using var session = await runtime.CreateSessionAsync(CreateOptions(runtime, directory));
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("first") }).WaitAsync(Timeout);
        var process = SessionProcess(cli);
        var transcript = new Transcript(TranscriptPath(cli, directory, process.SessionId), process.SessionId, directory.Path);
        transcript.Write(
            transcript.User("u1", null, "first", "sdk-cli"),
            transcript.Assistant(process.Entries.Last(), "u1", "msg_1", Text("ok"), "sdk-cli"));
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(directory.Path));
        var recorded = (await store.GetStateAsync(session.SessionId))!;
        Assert.IsNull(ClaudeCodeProviderState.Read(recorded)!.Position);
        var executor = (IAgentProviderExternalTurns)runtime.CreateTurnExecutor();

        var first = await executor.ReadExternalTurnsAsync(recorded, directory.Path, CancellationToken.None);
        Assert.AreEqual((AgentExternalTurnsStatus.Read, 0), (first.Status, first.Turns.Count), "Without a position the whole file is read once.");

        // The session is attached: no state recorded where the read stopped. The next read does not read the file again.
        Assert.AreEqual(AgentExternalTurnsStatus.Unchanged, (await executor.ReadExternalTurnsAsync(recorded, directory.Path, CancellationToken.None)).Status);

        // The next answer of the session records it.
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("second") }).WaitAsync(Timeout);
        Assert.IsNotNull(ClaudeCodeProviderState.Read((await store.GetStateAsync(session.SessionId))!)!.Position);
    }

    [TestMethod]
    public async Task TurnsReadButNotWritten_AreReadAgain_ByTheNextReadOfTheSameState()
    {
        // A catch-up whose write was refused (the session started a run meanwhile) is tried again with the leaf the
        // session recorded: the read of the executor that stopped after those turns does not hide them.
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli { ConfigRoot = Path.Combine(directory.Path, "claude") };
        var transcript = new Transcript(TranscriptPath(cli, directory, "c1"), "c1", directory.Path);
        transcript.Write(
            transcript.User("u1", null, "first", "sdk-cli"),
            transcript.Assistant("a1", "u1", "msg_1", Text("Answer."), "sdk-cli"));
        var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        var executor = (IAgentProviderExternalTurns)runtime.CreateTurnExecutor();
        var first = await executor.ReadExternalTurnsAsync(CreateState("s1", new ClaudeCodeProviderState("c1", 1, 1, Leaf: "a1", Anchor: "a1")), directory.Path,
            CancellationToken.None);
        var positioned = ClaudeCodeProviderState.Read(first.ProviderStateAfter!(0, 0, 0))!;
        Assert.AreEqual("a1", positioned.Position?.Leaf);
        transcript.Append(
            transcript.User("u2", "a1", "From VS Code."),
            transcript.Assistant("a2", "u2", "msg_2", Text("Answered there.")));
        var state = CreateState("s1", positioned);

        var read = await executor.ReadExternalTurnsAsync(state, directory.Path, CancellationToken.None);
        Assert.AreEqual(1, read.Turns.Count);

        var again = await executor.ReadExternalTurnsAsync(state, directory.Path, CancellationToken.None);
        Assert.AreEqual(AgentExternalTurnsStatus.Read, again.Status, "Nothing was written: the turns are read again.");
        Assert.AreEqual("From VS Code.", ((AgentMessagePart.Text)again.Turns.Single().Messages[0].Parts[0]).Value);

        // Without a recorded position either.
        var unpositioned = CreateState("s1", positioned with { Position = null });
        Assert.AreEqual(1, (await executor.ReadExternalTurnsAsync(unpositioned, directory.Path, CancellationToken.None)).Turns.Count);
        Assert.AreEqual(1, (await executor.ReadExternalTurnsAsync(unpositioned, directory.Path, CancellationToken.None)).Turns.Count);
    }

    [TestMethod]
    public void PositionOfTheExecutor_IsNotChosen_WhenItStoppedAtAnotherLeafThanTheRecordedOne()
    {
        var positions = new ClaudeCodeTranscriptPositions();
        var recorded = new ClaudeCodeTranscriptPosition(100, 100, DateTime.UtcNow, "H", "a1", null);
        var later = new ClaudeCodeTranscriptPosition(300, 300, DateTime.UtcNow, "H", "a3", "t3");
        positions.Note("c1", later);

        Assert.AreSame(recorded, positions.Choose("c1", recorded, "a1"), "The turns after a1 are before the position of the executor.");
        Assert.AreSame(later, positions.Choose("c1", recorded, "a3"));
        Assert.AreSame(later, positions.Choose("c1", recorded, "t3"), "The tail below the leaf is where the read stopped too.");
        Assert.AreSame(later, positions.Choose("c1", null, "a1"), "Without a recorded position, the reader decides from the latest one.");
    }

    [TestMethod]
    public async Task AConversationAnotherProcessIsRunning_IsNotRead_UntilThatProcessIsIdle()
    {
        using var directory = TestTempDirectory.Create();
        var cli = new ClaudeCodeFakeCli { ConfigRoot = Path.Combine(directory.Path, "claude") };
        var transcript = new Transcript(TranscriptPath(cli, directory, "c1"), "c1", directory.Path);
        // VS Code is in the middle of a turn: its tool call has no result yet.
        transcript.Write(
            transcript.User("u1", null, "first", "sdk-cli"),
            transcript.Assistant("a1", "u1", "msg_1", Text("Answer."), "sdk-cli"),
            transcript.User("u2", "a1", "Read the file."),
            transcript.Assistant("a2", "u2", "msg_2", ToolUse("toolu_1", "Read", new JsonObject())));
        cli.HoldConversation(4242, "c1", status: "busy");
        var state = CreateState("s1", new ClaudeCodeProviderState("c1", 1, 1, Leaf: "a1", Anchor: "a1"));
        var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        var executor = (IAgentProviderExternalTurns)runtime.CreateTurnExecutor();

        Assert.AreEqual(AgentExternalTurnsStatus.Held, (await executor.ReadExternalTurnsAsync(state, directory.Path, CancellationToken.None)).Status,
            "Half a turn is not written: what follows it would split it.");

        transcript.Append(
            transcript.ToolResult("r1", "a2", "toolu_1", "contents"),
            transcript.Assistant("a3", "r1", "msg_3", Text("It says contents.")));
        cli.HoldConversation(4242, "c1", status: "idle");
        var read = await executor.ReadExternalTurnsAsync(state, directory.Path, CancellationToken.None);
        Assert.AreEqual(AgentExternalTurnsStatus.Read, read.Status);
        Assert.AreEqual(4, read.Turns.Single().Messages.Count, "The whole turn, once the process that made it is idle.");
    }

    private static AgentSessionCreateOptions CreateOptions(ClaudeCodeModelProviderRuntime runtime, TestTempDirectory directory)
        => new()
        {
            ProviderKey = runtime.Descriptor.ProviderId.Value,
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

    // A session with one answered turn, whose process has exited, and the entry the CLI wrote for its answer.
    internal static async Task<(ClaudeCodeFakeCli Cli, string SessionId, string Conversation, string FirstAnswer)> StartConversationAsync(TestTempDirectory directory)
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
        var process = SessionProcess(cli);
        return (cli, session.SessionId, process.SessionId, process.Entries.Last());
    }

    internal static string TranscriptPath(ClaudeCodeFakeCli cli, TestTempDirectory directory, string conversation)
        => Path.Combine(ClaudeCodeProjectFolders.GetProjectFolder(cli.ConfigRoot, directory.Path), conversation + ".jsonl");

    private static Task<AgentExternalTurns> ReadAsync(ClaudeCodeFakeCli cli, AgentSessionState state, TestTempDirectory directory)
    {
        var runtime = new ClaudeCodeModelProviderRuntime(cli.CreateOptions());
        return ((IAgentProviderExternalTurns)runtime.CreateTurnExecutor()).ReadExternalTurnsAsync(state, directory.Path, CancellationToken.None);
    }

    private static Task<AgentExternalTurnJournal.Prepared> PrepareAsync(AgentSessionState state, AgentExternalTurns read, TestTempDirectory directory)
        => AgentExternalTurnJournal.PrepareAsync(new ModelProviderId("claude-code"), state, read, directory.Path,
            static (_, _) => throw new InvalidOperationException("No image."), static (result, _) => Task.FromResult(result),
            DateTimeOffset.UtcNow, CancellationToken.None);

    private static AgentSessionState CreateState(string sessionId, ClaudeCodeProviderState providerState)
        => new()
        {
            SessionId = sessionId,
            ProviderKey = "claude-code",
            ProtocolFamily = ClaudeCodeModelProviderRuntime.ProtocolFamily,
            ProviderState = providerState.ToJson(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static AgentSessionResumeOptions ResumeOptions(TestTempDirectory directory)
        => new()
        {
            ProviderKey = "claude-code",
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

    private static ClaudeCodeFakeProcess SessionProcess(ClaudeCodeFakeCli cli)
        => cli.Processes.Last(static process => process.Launch.Arguments.Contains("--mcp-config"));

    internal static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    internal static JsonObject ToolUse(string id, string name, JsonObject input) => new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input };

    private static JsonObject Image(string data, string mediaType)
        => new() { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = data } };

    /// <summary>A transcript with the shape of the lines the CLI writes, without real content.</summary>
    internal sealed class Transcript(string path, string conversation, string cwd)
    {
        private int _clock;

        // The transcript of a conversation of the CLI whose configuration folder and working directory are the folder.
        public static Transcript For(TestTempDirectory directory, string conversation)
            => new(Path.Combine(ClaudeCodeProjectFolders.GetProjectFolder(directory.Path, directory.Path), conversation + ".jsonl"), conversation, directory.Path);

        public void Write(params string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Concat(lines.Select(static line => line + "\n")), new UTF8Encoding(false));
        }

        public void Append(params string[] lines)
            => File.AppendAllText(path, string.Concat(lines.Select(static line => line + "\n")), new UTF8Encoding(false));

        public static string Line(JsonObject line) => line.ToJsonString();

        public string User(string uuid, string? parent, string text, string entrypoint = "claude-vscode")
            => User(uuid, parent, new JsonArray(Text(text)), entrypoint);

        public string User(string uuid, string? parent, JsonArray content, string entrypoint = "claude-vscode")
            => Node("user", uuid, parent, entrypoint, line => line["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
                line => line["origin"] = new JsonObject { ["kind"] = "human" });

        // The boundary of a compaction, which ends the chain of what it compacted, and the summary the chain goes on from.
        public string Boundary(string uuid, string logicalParent, string entrypoint = "claude-vscode")
            => Node("system", uuid, null, entrypoint, line =>
            {
                line["logicalParentUuid"] = logicalParent;
                line["subtype"] = "compact_boundary";
                line["content"] = "Conversation compacted";
                line["compactMetadata"] = new JsonObject { ["trigger"] = "manual", ["preTokens"] = 50000, ["postTokens"] = 4000 };
            }, null);

        public string Summary(string uuid, string parent, string text, string entrypoint = "claude-vscode")
            => Node("user", uuid, parent, entrypoint, line => line["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(Text(text)) },
                line => line["isCompactSummary"] = true);

        public string ToolResult(string uuid, string parent, string toolUseId, string content, string entrypoint = "claude-vscode")
            => Node("user", uuid, parent, entrypoint, line => line["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["tool_use_id"] = toolUseId, ["type"] = "tool_result", ["content"] = content, ["is_error"] = false }),
            }, line => line["sourceToolAssistantUUID"] = parent);

        public string Assistant(string uuid, string parent, string messageId, JsonObject block, string entrypoint = "claude-vscode")
            => Node("assistant", uuid, parent, entrypoint, line => line["message"] = new JsonObject
            {
                ["model"] = "claude-test",
                ["id"] = messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new JsonArray(block),
                ["stop_reason"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
            }, null);

        private string Node(string type, string uuid, string? parent, string entrypoint, Action<JsonObject> fill, Action<JsonObject>? extra)
        {
            var line = new JsonObject { ["parentUuid"] = parent, ["isSidechain"] = false, ["type"] = type };
            fill(line);
            line["uuid"] = uuid;
            line["timestamp"] = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero).AddSeconds(++_clock)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            line["userType"] = "external";
            line["entrypoint"] = entrypoint;
            line["cwd"] = cwd;
            line["sessionId"] = conversation;
            line["version"] = "2.1.296";
            extra?.Invoke(line);
            return Line(line);
        }
    }
}
