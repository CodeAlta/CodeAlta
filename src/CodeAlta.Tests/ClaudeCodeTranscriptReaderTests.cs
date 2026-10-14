using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// The reader of the transcripts the CLI keeps, on synthetic transcripts with the shapes of real ones.
/// </summary>
[TestClass]
public sealed class ClaudeCodeTranscriptReaderTests
{
    private const string SessionId = "0a1b2c3d-4e5f-4789-abcd-ef0123456789";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CodeAlta-claude-transcript-" + Guid.NewGuid().ToString("N"));
    private int _clock;

    private string TranscriptPath => Path.Combine(_folder, SessionId + ".jsonl");

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_folder);

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [TestMethod]
    public void Read_MergesTheLinesOfOneModelMessage()
    {
        Write(
            User("u1", null, "Fix the bug."),
            Assistant("a1", "u1", "msg_1", Thinking("Looking at it.")),
            Assistant("a2", "a1", "msg_1", Text("Let me read the file.")),
            Assistant("a3", "a2", "msg_1", ToolUse("toolu_1", "Read", new JsonObject { ["file_path"] = "a.cs" })),
            ToolResult("r1", "a3", "toolu_1", "class A {}", source: "a3"),
            Assistant("a4", "r1", "msg_2", Text("Fixed.")));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual(ClaudeCodeTranscriptReadStatus.Read, result.Status);
        var turn = result.Turns.Single();
        Assert.AreEqual(4, turn.Messages.Count);
        Assert.AreEqual(AgentConversationRole.User, turn.Messages[0].Role);
        var first = turn.Messages[1];
        Assert.AreEqual(AgentConversationRole.Assistant, first.Role);
        Assert.AreEqual("Looking at it.", ((AgentMessagePart.Reasoning)first.Parts[0]).Value);
        Assert.AreEqual("Let me read the file.", ((AgentMessagePart.Text)first.Parts[1]).Value);
        var call = (AgentMessagePart.ToolCall)first.Parts[2];
        Assert.AreEqual("toolu_1", call.CallId);
        Assert.AreEqual("Read", call.Name);
        Assert.AreEqual("a.cs", call.Arguments.GetProperty("file_path").GetString());
        var tool = (AgentMessagePart.ToolResult)turn.Messages[2].Parts.Single();
        Assert.AreEqual(AgentConversationRole.Tool, turn.Messages[2].Role);
        Assert.AreEqual("toolu_1", tool.CallId);
        Assert.AreEqual("class A {}", TextOf(tool.Result));
        Assert.AreEqual("Fixed.", ((AgentMessagePart.Text)turn.Messages[3].Parts.Single()).Value);
        Assert.AreEqual(1, result.Users);
        Assert.AreEqual(2, result.Assistants, "One assistant message per model message, tool steps included.");
        Assert.AreEqual("a4", result.Leaf);
        Assert.AreEqual("u1", turn.FirstUuid);
        Assert.AreEqual("a4", turn.LastUuid);
    }

    [TestMethod]
    public void Read_PutsBackTheResultsOfParallelToolCalls()
    {
        Write(
            User("u1", null, "Read both."),
            Assistant("a1", "u1", "msg_1", ToolUse("toolu_1", "Read", new JsonObject { ["file_path"] = "a.cs" })),
            Assistant("a2", "a1", "msg_1", ToolUse("toolu_2", "Read", new JsonObject { ["file_path"] = "b.cs" })),
            ToolResult("r1", "a1", "toolu_1", "content of a", source: "a1"),
            ToolResult("r2", "a2", "toolu_2", "content of b", source: "a2"),
            Assistant("a3", "r2", "msg_2", Text("Both read.")));

        var turn = ClaudeCodeTranscriptReader.Read(TranscriptPath).Turns.Single();

        CollectionAssert.AreEqual(
            new[] { AgentConversationRole.User, AgentConversationRole.Assistant, AgentConversationRole.Tool, AgentConversationRole.Tool, AgentConversationRole.Assistant },
            turn.Messages.Select(static message => message.Role).ToArray());
        CollectionAssert.AreEqual(new[] { "toolu_1", "toolu_2" }, turn.Messages[1].Parts.OfType<AgentMessagePart.ToolCall>().Select(static call => call.CallId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "content of a", "content of b" },
            turn.Messages.Skip(2).Take(2).Select(static message => TextOf(((AgentMessagePart.ToolResult)message.Parts.Single()).Result)).ToArray());
    }

    [TestMethod]
    public void Read_FollowsTheNewestBranchAfterARewind()
    {
        Write(
            User("u1", null, "First question."),
            Assistant("a1", "u1", "msg_1", Text("First answer.")),
            User("u2", "a1", "Abandoned question."),
            Assistant("a2", "u2", "msg_2", Text("Abandoned answer.")),
            User("u3", "a1", "Second question."),
            Assistant("a3", "u3", "msg_3", Text("Second answer.")));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual("a3", result.Leaf);
        CollectionAssert.AreEqual(
            new[] { "First question.", "First answer.", "Second question.", "Second answer." },
            AllTexts(result).ToArray());
    }

    [TestMethod]
    public void Read_ResolvesAToolResultSavedBesideTheTranscript()
    {
        var saved = Path.Combine(_folder, SessionId, "tool-results");
        Directory.CreateDirectory(saved);
        File.WriteAllText(Path.Combine(saved, "toolu_big.txt"), "the whole output");
        Write(
            User("u1", null, "List everything."),
            Assistant("a1", "u1", "msg_1", ToolUse("toolu_big", "Bash", new JsonObject { ["command"] = "ls" })),
            Assistant("a2", "a1", "msg_1", ToolUse("toolu_gone", "Bash", new JsonObject { ["command"] = "ls -R" })),
            ToolResult("r1", "a1", "toolu_big", Persisted(@"C:\Users\someone\.claude\projects\X\" + SessionId + @"\tool-results\toolu_big.txt"), source: "a1"),
            ToolResult("r2", "a2", "toolu_gone", Persisted("/home/someone/.claude/projects/X/" + SessionId + "/tool-results/toolu_gone.txt"), source: "a2"),
            Assistant("a3", "r2", "msg_2", Text("Done.")));

        var results = ClaudeCodeTranscriptReader.Read(TranscriptPath).Turns.Single().Messages
            .SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolResult>())
            .ToDictionary(static part => part.CallId, static part => TextOf(part.Result));

        Assert.AreEqual("the whole output", results["toolu_big"]);
        StringAssert.Contains(results["toolu_gone"], "Preview (first 2KB):", "A file that is gone keeps the preview.");
    }

    [TestMethod]
    public void Read_KeepsImagesAndReasoning()
    {
        var imageData = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]);
        Write(
            User("u1", null, new JsonArray(Text("What is this?"), Image(imageData, "image/jpeg"))),
            Assistant("a1", "u1", "msg_1", Thinking("It looks like a logo."), Thinking("   ")),
            Assistant("a2", "a1", "msg_1", ToolUse("toolu_1", "Screenshot", new JsonObject())),
            ToolResultLine("r1", "a2", new JsonArray(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = "toolu_1",
                ["content"] = new JsonArray(Image(imageData, "image/png")),
            })),
            Assistant("a3", "r1", "msg_2", Text("A logo.")));

        var turn = ClaudeCodeTranscriptReader.Read(TranscriptPath).Turns.Single();

        var image = (AgentMessagePart.Data)turn.Messages[0].Parts[1];
        Assert.AreEqual(imageData, image.Base64Data);
        Assert.AreEqual("image/jpeg", image.MediaType);
        Assert.AreEqual("It looks like a logo.", ((AgentMessagePart.Reasoning)turn.Messages[1].Parts.Single(static part => part is AgentMessagePart.Reasoning)).Value);
        var toolImage = (AgentToolResultItem.Image)((AgentMessagePart.ToolResult)turn.Messages[2].Parts.Single()).Result.Items.Single();
        Assert.AreEqual("image/png", toolImage.MediaType);
    }

    [TestMethod]
    public void Read_ShowsACompactionOnceWithTheHistoryBeforeIt()
    {
        Write(
            User("u1", null, "Old question."),
            Assistant("a1", "u1", "msg_1", Text("Old answer.")),
            User("u2", "a1", "Kept question."),
            Assistant("a2", "u2", "msg_2", Text("Kept answer.")),
            Line(new JsonObject
            {
                ["parentUuid"] = null,
                ["logicalParentUuid"] = "a2",
                ["isSidechain"] = false,
                ["type"] = "system",
                ["subtype"] = "compact_boundary",
                ["content"] = "Conversation compacted",
                ["compactMetadata"] = new JsonObject
                {
                    ["trigger"] = "auto",
                    ["preTokens"] = 900000,
                    ["postTokens"] = 8000,
                    ["preservedSegment"] = new JsonObject { ["headUuid"] = "u2", ["anchorUuid"] = "s1", ["tailUuid"] = "a2" },
                },
                ["uuid"] = "b1",
                ["timestamp"] = NextTimestamp(),
            }),
            User("s1", "b1", "Summary of the conversation so far.", extra: line => line["isCompactSummary"] = true),
            User("u3", "s1", "New question."),
            Assistant("a3", "u3", "msg_3", Text("New answer.")));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual("a3", result.Leaf);
        Assert.AreEqual(4, result.Turns.Count);
        Assert.AreEqual("Old question.", TextOf(result.Turns[0].Messages[0]));
        Assert.AreEqual("Kept question.", TextOf(result.Turns[1].Messages[0]));
        var compaction = result.Turns[2].Compaction;
        Assert.IsNotNull(compaction);
        Assert.AreEqual("auto", compaction.Trigger);
        Assert.AreEqual(900000L, compaction.PreTokens);
        Assert.AreEqual(8000L, compaction.PostTokens);
        Assert.AreEqual(0, result.Turns[2].Messages.Count);
        Assert.AreEqual("New question.", TextOf(result.Turns[3].Messages[0]));
        Assert.IsFalse(AllTexts(result).Any(static text => text.Contains("Summary", StringComparison.Ordinal)));
        Assert.AreEqual(3, result.Users);
        Assert.AreEqual(3, result.Assistants);

        // The turns after an entry before the compaction include the compaction and what followed.
        var after = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, "a2", null);
        Assert.AreEqual(ClaudeCodeTranscriptReadStatus.Read, after.Status);
        Assert.AreEqual(2, after.Turns.Count);
        Assert.IsNotNull(after.Turns[0].Compaction);
        Assert.AreEqual("New question.", TextOf(after.Turns[1].Messages[0]));
    }

    [TestMethod]
    public void Read_ReparentsTheMessagesACompactionPreserved()
    {
        Write(
            User("u1", null, "Kept question."),
            Assistant("a1", "u1", "msg_1", Text("Kept answer.")),
            Line(new JsonObject
            {
                ["parentUuid"] = null,
                ["logicalParentUuid"] = "a1",
                ["type"] = "system",
                ["subtype"] = "compact_boundary",
                ["compactMetadata"] = new JsonObject
                {
                    ["trigger"] = "manual",
                    ["preservedMessages"] = new JsonObject { ["anchorUuid"] = "s1", ["uuids"] = new JsonArray("u1", "a1") },
                },
                ["uuid"] = "b1",
                ["timestamp"] = NextTimestamp(),
            }),
            User("s1", "b1", "Summary.", extra: line => line["isCompactSummary"] = true),
            User("u2", "s1", "New question."),
            Assistant("a2", "u2", "msg_2", Text("New answer.")));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        CollectionAssert.AreEqual(new[] { "Kept question.", "Kept answer.", "New question.", "New answer." }, AllTexts(result).ToArray());
        Assert.AreEqual(3, result.Turns.Count);
        Assert.IsNotNull(result.Turns[1].Compaction);
    }

    [TestMethod]
    public void Read_ShowsASubagentAsItsToolCallAndResult()
    {
        Write(
            User("u1", null, "Explore the code."),
            Assistant("a1", "u1", "msg_1", ToolUse("toolu_agent", "Agent", new JsonObject { ["description"] = "Explore", ["prompt"] = "Look around." })),
            // An older CLI wrote the lines of a subagent in the transcript of the conversation.
            User("x1", null, "Look around.", extra: line => { line["isSidechain"] = true; line["agentId"] = "a88"; }),
            Assistant("x2", "x1", "msg_side", Text("Inside the subagent."), extra: line => { line["isSidechain"] = true; line["agentId"] = "a88"; }),
            ToolResultLine("r1", "a1", new JsonArray(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = "toolu_agent",
                ["content"] = new JsonArray(Text("The report of the subagent.")),
            }), extra: line => line["toolUseResult"] = new JsonObject { ["agentId"] = "a88", ["status"] = "completed" }),
            Assistant("a2", "r1", "msg_2", Text("Explored.")));
        Directory.CreateDirectory(Path.Combine(_folder, SessionId, "subagents"));
        File.WriteAllText(Path.Combine(_folder, SessionId, "subagents", "agent-a88.jsonl"), "{\"type\":\"user\",\"uuid\":\"y1\",\"isSidechain\":true}\n");

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual("a2", result.Leaf);
        var turn = result.Turns.Single();
        Assert.AreEqual("Agent", turn.Messages[1].Parts.OfType<AgentMessagePart.ToolCall>().Single().Name);
        Assert.AreEqual("The report of the subagent.", TextOf(((AgentMessagePart.ToolResult)turn.Messages[2].Parts.Single()).Result));
        Assert.IsFalse(AllTexts(result).Any(static text => text.Contains("Inside the subagent", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Read_SkipsWhatItDoesNotKnowAndBadLines()
    {
        Write(
            Line(new JsonObject { ["type"] = "queue-operation", ["operation"] = "enqueue", ["sessionId"] = SessionId }),
            User("u1", null, new JsonArray(Text("Read this."), new JsonObject { ["type"] = "document", ["source"] = new JsonObject() }), extra: line => line["brandNewField"] = new JsonObject { ["x"] = 1 }),
            "{\"type\":\"assistant\",\"uuid\":\"broken\"",
            "not json at all",
            Line(new JsonObject { ["type"] = "brand-new-entry", ["uuid"] = "n1", ["parentUuid"] = "u1" }),
            Line(new JsonObject { ["type"] = "attachment", ["uuid"] = "t1", ["parentUuid"] = "u1", ["attachment"] = new JsonObject { ["type"] = "brand_new_attachment" } }),
            Assistant("a1", "t1", "msg_1", Text("Read."), new JsonObject { ["type"] = "server_tool_use", ["id"] = "srv_1" }),
            User("m1", "a1", "Injected context.", extra: line => line["isMeta"] = true),
            User("i1", "m1", "[Request interrupted by user]"),
            Line(new JsonObject { ["type"] = "system", ["subtype"] = "turn_duration", ["uuid"] = "d1", ["parentUuid"] = "i1" }));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual(ClaudeCodeTranscriptReadStatus.Read, result.Status);
        Assert.AreEqual("i1", result.Leaf);
        var turn = result.Turns.Single();
        Assert.AreEqual(2, turn.Messages.Count, "The meta message and the stop are not prompts.");
        Assert.AreEqual("[Claude Code document block]", ((AgentMessagePart.Text)turn.Messages[0].Parts[1]).Value);
        Assert.AreEqual("[Claude Code server_tool_use block]", ((AgentMessagePart.Text)turn.Messages[1].Parts[1]).Value);
    }

    [TestMethod]
    public void Read_ShowsAQueuedPromptAndSkipsOtherAttachments()
    {
        Write(
            User("u1", null, "Start."),
            Assistant("a1", "u1", "msg_1", Text("Started.")),
            Line(new JsonObject
            {
                ["type"] = "attachment",
                ["uuid"] = "q1",
                ["parentUuid"] = "a1",
                ["entrypoint"] = "claude-vscode",
                ["attachment"] = new JsonObject { ["type"] = "queued_command", ["prompt"] = new JsonArray(Text("And then this.")), ["commandMode"] = "prompt" },
            }),
            Line(new JsonObject
            {
                ["type"] = "attachment",
                ["uuid"] = "e1",
                ["parentUuid"] = "q1",
                ["attachment"] = new JsonObject { ["type"] = "edited_text_file", ["filename"] = "a.cs" },
            }),
            Assistant("a2", "e1", "msg_2", Text("Done that too.")));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual(2, result.Turns.Count);
        Assert.AreEqual("And then this.", TextOf(result.Turns[1].Messages[0]));
        Assert.AreEqual(2, result.Users);
    }

    [TestMethod]
    public void Read_ReturnsTitleCostEntrypointAndOriginOfEachTurn()
    {
        Write(
            User("u1", null, "Started in VS Code.", entrypoint: "claude-vscode", cwd: @"C:\work\project"),
            Assistant("a1", "u1", "msg_1", Text("Answer.")),
            Line(new JsonObject { ["type"] = "ai-title", ["aiTitle"] = "A generated title", ["sessionId"] = SessionId }),
            Line(new JsonObject { ["type"] = "cost-state", ["sessionId"] = SessionId, ["totalCostUSD"] = 0.5 }),
            User("u2", "a1", "Continued in a terminal.", entrypoint: "cli", extra: line => line["origin"] = new JsonObject { ["kind"] = "human" }),
            Assistant("a2", "u2", "msg_2", Text("Answer."), entrypoint: "cli"),
            Line(new JsonObject { ["type"] = "custom-title", ["customTitle"] = "The user's title", ["sessionId"] = SessionId }),
            Line(new JsonObject { ["type"] = "cost-state", ["sessionId"] = SessionId, ["totalCostUSD"] = 1.25 }),
            Line(new JsonObject { ["type"] = "ai-title", ["aiTitle"] = "Another generated title", ["sessionId"] = SessionId }));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual("The user's title", result.Title);
        Assert.AreEqual(1.25, result.TotalCostUsd);
        Assert.AreEqual("claude-vscode", result.Entrypoint);
        Assert.AreEqual(ClaudeCodeTranscriptOrigin.VsCode, result.Origin);
        Assert.AreEqual(@"C:\work\project", result.Cwd);
        Assert.AreEqual(ClaudeCodeTranscriptOrigin.VsCode, result.Turns[0].Origin);
        Assert.AreEqual(ClaudeCodeTranscriptOrigin.Terminal, result.Turns[1].Origin);
        Assert.AreEqual("cli", result.Turns[1].Entrypoint);
        Assert.AreEqual("human", result.Turns[1].OriginKind);
        Assert.IsNotNull(result.Turns[1].Timestamp);
    }

    [TestMethod]
    public void Read_ShowsATurnTheCliStartedWithoutAUserMessage()
    {
        Write(
            User("u1", null, "Start a background build."),
            Assistant("a1", "u1", "msg_1", Text("Started.")),
            Line(new JsonObject
            {
                ["type"] = "attachment",
                ["uuid"] = "q1",
                ["parentUuid"] = "a1",
                ["attachment"] = new JsonObject
                {
                    ["type"] = "queued_command",
                    ["prompt"] = "<task-notification>the build ended</task-notification>",
                    ["commandMode"] = "task-notification",
                },
            }),
            Assistant("a2", "q1", "msg_2", Text("The build passed.")),
            User("u2", "a2", "<task-notification>the tests ended</task-notification>", extra: line => line["origin"] = new JsonObject { ["kind"] = "task-notification" }),
            Assistant("a3", "u2", "msg_3", Text("The tests passed.")));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual(3, result.Turns.Count);
        Assert.AreEqual(1, result.Users, "A notification is not a user message.");
        Assert.AreEqual(3, result.Assistants);
        Assert.AreEqual("task-notification", result.Turns[1].OriginKind);
        Assert.AreEqual("<task-notification>the build ended</task-notification>", result.Turns[1].TriggerText);
        Assert.AreEqual(AgentConversationRole.Assistant, result.Turns[1].Messages.Single().Role);
        Assert.AreEqual("<task-notification>the tests ended</task-notification>", result.Turns[2].TriggerText);
        Assert.IsNull(result.Turns[0].TriggerText);
    }

    [TestMethod]
    public void Read_GoesOnOverAnEntryTheCliDidNotWrite()
    {
        // The CLI does not write the results of its own tool searches, but writes the next entry below them.
        Write(
            User("u1", null, "Question."),
            Assistant("a1", "u1", "msg_1", ToolUse("toolu_search", "ToolSearch", new JsonObject { ["query"] = "select:Read" })),
            Line(new JsonObject { ["type"] = "attachment", ["uuid"] = "t1", ["parentUuid"] = "never-written", ["entrypoint"] = "claude-vscode", ["attachment"] = new JsonObject { ["type"] = "deferred_tools_record" } }),
            Assistant("a2", "t1", "msg_2", Text("Answer.")));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual("a2", result.Leaf);
        var turn = result.Turns.Single();
        Assert.AreEqual(1, turn.Users);
        Assert.AreEqual(2, turn.Assistants);
    }

    [TestMethod]
    public void Read_TitleFallsBackToTheFileThenToTheGeneratedThenToThePrompt()
    {
        Write(
            User("u1", null, "<ide_opened_file>a.cs</ide_opened_file>", entrypoint: "claude-vscode"),
            User("u2", "u1", "The first real prompt\nof the session."),
            Assistant("a1", "u2", "msg_1", Text("Answer.")));
        Assert.AreEqual("The first real prompt of the session.", ClaudeCodeTranscriptReader.Read(TranscriptPath).Title);

        Append(Line(new JsonObject { ["type"] = "last-prompt", ["lastPrompt"] = "The last prompt", ["sessionId"] = SessionId }));
        Assert.AreEqual("The last prompt", ClaudeCodeTranscriptReader.Read(TranscriptPath).Title);

        Append(Line(new JsonObject { ["type"] = "ai-title", ["aiTitle"] = "Generated", ["sessionId"] = SessionId }));
        Assert.AreEqual("Generated", ClaudeCodeTranscriptReader.Read(TranscriptPath).Title);

        Directory.CreateDirectory(Path.Combine(_folder, SessionId));
        File.WriteAllText(Path.Combine(_folder, SessionId, "custom-title.json"), "{\"customTitle\":\"  Named in VS Code  \"}");
        Assert.AreEqual("Named in VS Code", ClaudeCodeTranscriptReader.Read(TranscriptPath).Title);
        Assert.AreEqual("Named in VS Code", ClaudeCodeTranscriptReader.ReadHeader(TranscriptPath)!.Title);
    }

    [TestMethod]
    public void Read_IgnoresAHalfWrittenLastLineUntilItEnds()
    {
        Write(
            User("u1", null, "Question."),
            Assistant("a1", "u1", "msg_1", Text("Answer.")));
        var complete = new FileInfo(TranscriptPath).Length;
        var next = User("u2", "a1", "Next question.");
        File.AppendAllText(TranscriptPath, next[..(next.Length / 2)]);

        var first = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual(1, first.Turns.Count);
        Assert.AreEqual("a1", first.Leaf);
        Assert.AreEqual(complete, first.Position!.Offset);
        Assert.AreEqual(new FileInfo(TranscriptPath).Length, first.Position.Length);

        File.AppendAllText(TranscriptPath, next[(next.Length / 2)..] + "\n" + Assistant("a2", "u2", "msg_2", Text("Next answer.")) + "\n");
        var second = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, first.Leaf, first.Position);

        Assert.AreEqual(ClaudeCodeTranscriptReadStatus.Read, second.Status);
        Assert.IsFalse(second.FullRead);
        Assert.AreEqual("a2", second.Leaf);
        CollectionAssert.AreEqual(new[] { "Next question.", "Next answer." }, AllTexts(second).ToArray());
    }

    [TestMethod]
    public void ReadAfter_ReadsOnlyWhatFollowsThePosition()
    {
        Write(
            User("u1", null, "Question.", entrypoint: "claude-vscode", cwd: @"C:\work"),
            Assistant("a1", "u1", "msg_1", Text("Answer.")),
            // Lines the CLI writes after an answer: the next prompt hangs from the last one.
            Line(new JsonObject { ["type"] = "system", ["subtype"] = "turn_duration", ["uuid"] = "d1", ["parentUuid"] = "a1" }),
            Line(new JsonObject { ["type"] = "attachment", ["uuid"] = "t1", ["parentUuid"] = "d1", ["attachment"] = new JsonObject { ["type"] = "total_tokens_reminder" } }));
        var first = ClaudeCodeTranscriptReader.Read(TranscriptPath);
        Assert.AreEqual("a1", first.Leaf);
        Assert.AreEqual("t1", first.Position!.Tail);

        var unchanged = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, first.Leaf, first.Position);
        Assert.AreEqual(ClaudeCodeTranscriptReadStatus.Unchanged, unchanged.Status);
        Assert.AreEqual(0, unchanged.Turns.Count);
        Assert.AreEqual("a1", unchanged.Leaf);

        Append(
            User("u2", "t1", "Another question.", entrypoint: "cli"),
            Assistant("a2", "u2", "msg_2", ToolUse("toolu_1", "Read", new JsonObject())),
            ToolResult("r1", "a2", "toolu_1", "content", source: "a2"),
            Assistant("a3", "r1", "msg_3", Text("Another answer.")),
            Line(new JsonObject { ["type"] = "cost-state", ["sessionId"] = SessionId, ["totalCostUSD"] = 2.0 }));
        var second = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, first.Leaf, first.Position);

        Assert.AreEqual(ClaudeCodeTranscriptReadStatus.Read, second.Status);
        Assert.IsFalse(second.FullRead);
        Assert.AreEqual("a3", second.Leaf);
        Assert.AreEqual(1, second.Users);
        Assert.AreEqual(2, second.Assistants);
        Assert.AreEqual(ClaudeCodeTranscriptOrigin.Terminal, second.Turns.Single().Origin);
        Assert.AreEqual(2.0, second.TotalCostUsd);
        Assert.AreEqual(ClaudeCodeTranscriptOrigin.VsCode, second.Origin, "The origin of the session is the one of its first entry.");
        Assert.AreEqual(@"C:\work", second.Cwd);
        Assert.AreEqual(new FileInfo(TranscriptPath).Length, second.Position!.Offset);

        // The same turns as a whole read gives after that entry.
        var whole = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, first.Leaf, null);
        Assert.IsTrue(whole.FullRead);
        CollectionAssert.AreEqual(AllTexts(whole).ToArray(), AllTexts(second).ToArray());
    }

    [TestMethod]
    public void ReadAfter_ReadsTheWholeFileWhenItWasReplaced()
    {
        Write(
            User("u1", null, "Question."),
            Assistant("a1", "u1", "msg_1", Text("Answer.")),
            User("u2", "a1", "Second."),
            Assistant("a2", "u2", "msg_2", Text("Second answer.")));
        var first = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        // Another file with the same entries up to a1, shorter: the position does not apply.
        Write(
            User("u1", null, "Question.", extra: line => line["sessionId"] = "other"),
            Assistant("a1", "u1", "msg_1", Text("Answer.")),
            User("u3", "a1", "Third."));
        var shrunk = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, "a1", first.Position);
        Assert.IsTrue(shrunk.FullRead);
        Assert.AreEqual("Third.", TextOf(shrunk.Turns.Single().Messages[0]));

        // A first line that changed, in a file that grew.
        var position = shrunk.Position!;
        Write(
            User("u1", null, "Question changed."),
            Assistant("a1", "u1", "msg_1", Text("Answer.")),
            User("u3", "a1", "Third."),
            Assistant("a3", "u3", "msg_3", Text("Third answer, long enough to grow the file past the position.")));
        var replaced = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, "u3", position);
        Assert.IsTrue(replaced.FullRead);
        Assert.AreEqual("Third answer, long enough to grow the file past the position.", AllTexts(replaced).Single());
    }

    [TestMethod]
    public void ReadAfter_CannotFollowWhenTheEntryIsNoLongerAnAncestor()
    {
        Write(
            User("u1", null, "Question."),
            Assistant("a1", "u1", "msg_1", Text("Answer.")),
            User("u2", "a1", "Second."),
            Assistant("a2", "u2", "msg_2", Text("Second answer.")));
        var first = ClaudeCodeTranscriptReader.Read(TranscriptPath);
        Assert.AreEqual("a2", first.Leaf);

        // Rewound elsewhere to a1, and continued from there.
        Append(
            User("u3", "a1", "Instead."),
            Assistant("a3", "u3", "msg_3", Text("Other answer.")));
        var result = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, first.Leaf, first.Position);

        Assert.AreEqual(ClaudeCodeTranscriptReadStatus.CannotFollow, result.Status);
        Assert.AreEqual(0, result.Turns.Count);
        Assert.IsTrue(result.FullRead);
        Assert.AreEqual("a3", result.Leaf);
    }

    [TestMethod]
    public void ReadAfter_ReportsAMissingTranscript()
    {
        var result = ClaudeCodeTranscriptReader.ReadAfter(Path.Combine(_folder, Guid.NewGuid() + ".jsonl"), "a1", null);

        Assert.AreEqual(ClaudeCodeTranscriptReadStatus.Missing, result.Status);
        Assert.IsNull(result.Position);
    }

    [TestMethod]
    public void ReadHeader_ReadsTheHeadAndTheTailOfALargeFile()
    {
        var lines = new List<string>
        {
            Line(new JsonObject { ["type"] = "queue-operation", ["operation"] = "enqueue", ["sessionId"] = SessionId }),
            User("u0", null, "A first prompt.", entrypoint: "claude-vscode", cwd: @"C:\work\vscode"),
        };
        var parent = "u0";
        for (var index = 0; index < 400; index++)
        {
            var uuid = "a" + index;
            lines.Add(Assistant(uuid, parent, "msg_" + index, Text(new string('x', 400)), entrypoint: "cli"));
            parent = uuid;
        }

        lines.Add(Line(new JsonObject { ["type"] = "ai-title", ["aiTitle"] = "Generated \"quoted\" title", ["sessionId"] = SessionId }));
        Write([.. lines]);

        var header = ClaudeCodeTranscriptReader.ReadHeader(TranscriptPath)!;

        Assert.IsTrue(header.Length > 2 * 64 * 1024);
        Assert.AreEqual(SessionId, header.SessionId);
        Assert.AreEqual(@"C:\work\vscode", header.Cwd);
        Assert.AreEqual("claude-vscode", header.Entrypoint);
        Assert.AreEqual(ClaudeCodeTranscriptOrigin.VsCode, header.Origin);
        Assert.AreEqual("Generated \"quoted\" title", header.Title);
        Assert.IsFalse(header.IsSidechain);
        Assert.IsFalse(header.IsProgrammatic);
        Assert.IsTrue(header.IsListedByVsCode);
        Assert.AreEqual(File.GetLastWriteTimeUtc(TranscriptPath), header.LastWriteTime);
    }

    [TestMethod]
    public void ReadHeader_AppliesTheFilterOfTheSessionListOfVsCode()
    {
        // A session of a program (CodeAlta is one).
        Write(User("u1", null, "Question.", entrypoint: "sdk-cli"));
        var programmatic = ClaudeCodeTranscriptReader.ReadHeader(TranscriptPath)!;
        Assert.AreEqual(ClaudeCodeTranscriptOrigin.Programmatic, programmatic.Origin);
        Assert.IsTrue(programmatic.IsProgrammatic);
        Assert.IsFalse(programmatic.IsListedByVsCode);

        // A daemon session.
        Write(User("u1", null, "Question.", entrypoint: "cli", extra: line => line["sessionKind"] = "daemon-worker"));
        var daemon = ClaudeCodeTranscriptReader.ReadHeader(TranscriptPath)!;
        Assert.AreEqual("daemon-worker", daemon.SessionKind);
        Assert.IsFalse(daemon.IsListedByVsCode);

        // The transcript of a subagent.
        Write(User("u1", null, "Question.", entrypoint: "cli", extra: line => line["isSidechain"] = true));
        Assert.IsTrue(ClaudeCodeTranscriptReader.ReadHeader(TranscriptPath)!.IsSidechain);
        Assert.IsFalse(ClaudeCodeTranscriptReader.ReadHeader(TranscriptPath)!.IsListedByVsCode);

        // Continued in another session: listed while that one does not exist.
        var target = Guid.NewGuid().ToString();
        Write(
            User("u1", null, "Question.", entrypoint: "cli"),
            Line(new JsonObject { ["type"] = "continued-in", ["continuedInSessionId"] = target, ["sessionId"] = SessionId }));
        var continued = ClaudeCodeTranscriptReader.ReadHeader(TranscriptPath)!;
        Assert.AreEqual(target, continued.ContinuedIn);
        Assert.IsFalse(continued.ContinuedInExists);
        Assert.IsTrue(continued.IsListedByVsCode);
        File.WriteAllText(Path.Combine(_folder, target + ".jsonl"), User("v1", null, "Next.", entrypoint: "cli") + "\n");
        Assert.IsFalse(ClaudeCodeTranscriptReader.ReadHeader(TranscriptPath)!.IsListedByVsCode);

        // A message after the record: the session goes on here.
        Append(User("u2", "u1", "Back here.", entrypoint: "cli"));
        Assert.IsNull(ClaudeCodeTranscriptReader.ReadHeader(TranscriptPath)!.ContinuedIn);

        // Nothing to show as a title.
        Write(Line(new JsonObject { ["type"] = "attachment", ["uuid"] = "t1", ["parentUuid"] = null, ["entrypoint"] = "cli", ["attachment"] = new JsonObject { ["type"] = "date" } }));
        Assert.IsFalse(ClaudeCodeTranscriptReader.ReadHeader(TranscriptPath)!.IsListedByVsCode);

        // Only <uuid>.jsonl files are sessions.
        var conflict = Path.Combine(_folder, SessionId + "_HOST_2026-10-01_Conflict.jsonl");
        File.Copy(TranscriptPath, conflict);
        Assert.IsNull(ClaudeCodeTranscriptReader.ReadHeader(conflict));
    }

    [TestMethod]
    public void ReadAfter_ReadsTheWholeFileWhenThePositionIsInsideAModelMessageWithParallelCalls()
    {
        Write(
            User("u1", null, "Read both."),
            Assistant("a1a", "u1", "msg_1", ToolUse("toolu_1", "Read", new JsonObject { ["file_path"] = "a.cs" })));
        var first = ClaudeCodeTranscriptReader.Read(TranscriptPath);
        Assert.AreEqual("a1a", first.Leaf);
        Assert.AreEqual("msg_1", first.Position!.LeafMessageId);

        Append(
            Assistant("a1b", "a1a", "msg_1", ToolUse("toolu_2", "Read", new JsonObject { ["file_path"] = "b.cs" })),
            ToolResult("r1", "a1a", "toolu_1", "content of a", source: "a1a"),
            ToolResult("r2", "a1b", "toolu_2", "content of b", source: "a1b"),
            Assistant("a2", "r2", "msg_2", Text("Both read.")));
        var result = AssertSameAsWholeRead("a1a", first.Position);

        Assert.IsTrue(result.FullRead, "More lines of the model message the position is in.");
        var messages = result.Turns.Single().Messages;
        CollectionAssert.AreEqual(
            new[] { AgentConversationRole.Assistant, AgentConversationRole.Tool, AgentConversationRole.Tool, AgentConversationRole.Assistant },
            messages.Select(static message => message.Role).ToArray());
        CollectionAssert.AreEqual(
            new[] { "toolu_1", "toolu_2" },
            messages.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolResult>()).Select(static part => part.CallId).ToArray());
    }

    [TestMethod]
    public void ReadAfter_ReadsTheWholeFileForAToolResultOfACallBeforeThePosition()
    {
        // The position is after the call and a line the CLI wrote below it; the result comes later, below the call.
        Write(
            User("u1", null, "Read."),
            Assistant("a1", "u1", "msg_1", ToolUse("toolu_1", "Read", new JsonObject { ["file_path"] = "a.cs" })),
            Line(new JsonObject { ["type"] = "attachment", ["uuid"] = "t1", ["parentUuid"] = "a1", ["attachment"] = new JsonObject { ["type"] = "total_tokens_reminder" } }));
        var first = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Append(
            ToolResult("r1", "a1", "toolu_1", "content", source: "a1"),
            Assistant("a2", "t1", "msg_2", Text("Read.")));
        var result = AssertSameAsWholeRead("a1", first.Position);

        Assert.IsTrue(result.FullRead);
    }

    [TestMethod]
    public void ReadAfter_ReadsTheWholeFileWhenTheNewestEntryHangsBeforeThePosition()
    {
        Write(
            User("u1", null, "Question."),
            Assistant("a1", "u1", "msg_1", Text("Answer.")));
        var first = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        // A branch from a1, then a line below an older entry: that line is the newest end of a chain, and its leaf u1.
        Append(
            User("u2", "a1", "Next."),
            Assistant("a2", "u2", "msg_2", Text("Next answer.")),
            Line(new JsonObject { ["type"] = "attachment", ["uuid"] = "z1", ["parentUuid"] = "u1", ["attachment"] = new JsonObject { ["type"] = "date" } }));
        var result = AssertSameAsWholeRead("a1", first.Position);

        Assert.IsTrue(result.FullRead);
        Assert.AreEqual("u1", result.Leaf);
        Assert.AreEqual(ClaudeCodeTranscriptReadStatus.CannotFollow, result.Status);
    }

    [TestMethod]
    public void ReadAfter_StaysOnThePositionOverALineWhoseParentWasNotWritten()
    {
        Write(
            User("u1", null, "Question."),
            Assistant("a1", "u1", "msg_1", ToolUse("toolu_search", "ToolSearch", new JsonObject { ["query"] = "select:Read" })));
        var first = ClaudeCodeTranscriptReader.Read(TranscriptPath);
        Assert.AreEqual("a1", first.Position!.LastLines!.Single().Uuid);

        Append(
            Line(new JsonObject { ["type"] = "attachment", ["uuid"] = "t1", ["parentUuid"] = "never-written", ["entrypoint"] = "claude-vscode", ["attachment"] = new JsonObject { ["type"] = "deferred_tools_record" } }),
            Assistant("a2", "t1", "msg_2", Text("Answer.")));
        var result = AssertSameAsWholeRead("a1", first.Position);

        Assert.IsFalse(result.FullRead);
        Assert.AreEqual("a2", result.Leaf);
        Assert.AreEqual("Answer.", AllTexts(result).Single());
    }

    [TestMethod]
    public void ReadAfter_FollowsTheTailOverSystemLinesWrittenAfterThePosition()
    {
        Write(
            User("u1", null, "Question."),
            Assistant("a1", "u1", "msg_1", Text("Answer.")),
            Line(new JsonObject { ["type"] = "system", ["subtype"] = "turn_duration", ["uuid"] = "d1", ["parentUuid"] = "a1" }));
        var first = ClaudeCodeTranscriptReader.Read(TranscriptPath);
        Assert.AreEqual("d1", first.Position!.Tail);

        // Only a system line first: the leaf stays, and the tail moves to it.
        Append(Line(new JsonObject { ["type"] = "system", ["subtype"] = "informational", ["uuid"] = "s2", ["parentUuid"] = "d1" }));
        var quiet = AssertSameAsWholeRead("a1", first.Position);
        Assert.IsFalse(quiet.FullRead);
        Assert.AreEqual(0, quiet.Turns.Count);
        Assert.AreEqual("s2", quiet.Position!.Tail);

        Append(
            User("u2", "s2", "Next."),
            Assistant("a2", "u2", "msg_2", Text("Next answer.")));
        var result = AssertSameAsWholeRead("a1", quiet.Position);

        Assert.IsFalse(result.FullRead);
        CollectionAssert.AreEqual(new[] { "Next.", "Next answer." }, AllTexts(result).ToArray());
    }

    [TestMethod]
    public void Read_LeavesARootALineWhoseUnwrittenParentWouldGraftItOnAnotherBranch()
    {
        // The last line of the writer has a child already (a line of another process): the bridge would graft on it.
        Write(
            User("u1", null, "Question."),
            Assistant("a1", "u1", "msg_1", Text("Answer.")),
            User("x1", "a1", "Another process.", entrypoint: "cli"),
            Line(new JsonObject { ["type"] = "attachment", ["uuid"] = "t1", ["parentUuid"] = "never-written", ["entrypoint"] = "claude-vscode", ["attachment"] = new JsonObject { ["type"] = "deferred_tools_record" } }),
            Assistant("a2", "t1", "msg_2", Text("Second answer.")));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual("a2", result.Leaf);
        Assert.AreEqual("Second answer.", AllTexts(result).Single(), "The chain starts at the line left a root.");
    }

    [TestMethod]
    public void Read_BridgesAnUnwrittenParentToTheLastLineOfTheSameWriter()
    {
        // VS Code and a terminal write the same transcript: the line of VS Code goes below the last line of VS Code,
        // not below the line the terminal wrote just before it. A line of a subagent below that line does not stop it.
        Write(
            User("u1", null, "Question."),
            Assistant("a1", "u1", "msg_1", ToolUse("toolu_search", "ToolSearch", new JsonObject())),
            User("s1", "a1", "Inside a subagent.", extra: line => line["isSidechain"] = true),
            User("c1", "u1", "From the terminal.", entrypoint: "cli"),
            Assistant("c2", "c1", "msg_c", Text("Terminal answer."), entrypoint: "cli"),
            Line(new JsonObject { ["type"] = "attachment", ["uuid"] = "t1", ["parentUuid"] = "never-written", ["entrypoint"] = "claude-vscode", ["attachment"] = new JsonObject { ["type"] = "deferred_tools_record" } }),
            Assistant("a2", "t1", "msg_2", Text("VS Code answer.")));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual("a2", result.Leaf);
        CollectionAssert.AreEqual(new[] { "Question.", "VS Code answer." }, AllTexts(result).ToArray());
        CollectionAssert.AreEqual(new[] { "toolu_search" }, result.Turns.Single().Messages[1].Parts.OfType<AgentMessagePart.ToolCall>().Select(static call => call.CallId).ToArray());
        CollectionAssert.AreEqual(
            new[] { ("claude-vscode", "a2"), ("cli", "c2") },
            result.Position!.LastLines!.Select(static line => (line.Entrypoint, line.Uuid)).ToArray());
    }

    [TestMethod]
    public void ReadAfter_DoesNotShowAgainAResultTheReadOfTheLeafShowed()
    {
        // The leaf ends a model message with parallel calls; the result of its first call, written before the leaf,
        // is put back after the leaf. The read that found the leaf showed it: the turns after the leaf do not.
        Write(
            User("u1", null, "Read them."),
            Assistant("a1", "u1", "msg_1", ToolUse("toolu_1", "Read", new JsonObject())),
            Assistant("a2", "a1", "msg_1", ToolUse("toolu_2", "Read", new JsonObject())),
            ToolResult("r1", "a1", "toolu_1", "first", source: "a1"),
            ToolResult("r2", "a2", "toolu_2", "second", source: "a2"),
            Assistant("a3", "r2", "msg_1", ToolUse("toolu_3", "Read", new JsonObject())));
        var first = ClaudeCodeTranscriptReader.Read(TranscriptPath);
        Assert.AreEqual("a3", first.Leaf);
        Assert.AreEqual(1, first.Turns.Single().Messages.Count(static message => message.Parts is [AgentMessagePart.ToolResult { CallId: "toolu_1" }]));

        Append(
            ToolResult("r3", "a3", "toolu_3", "third", source: "a3"),
            Assistant("a4", "r3", "msg_2", Text("Read.")));
        var result = AssertSameAsWholeRead("a3", first.Position);

        Assert.IsFalse(result.FullRead);
        CollectionAssert.AreEqual(
            new[] { "toolu_3" },
            result.Turns.Single().Messages.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolResult>()).Select(static part => part.CallId).ToArray());
    }

    [TestMethod]
    public void ReadAfter_StartsBelowALeafTheCliNeverWrote()
    {
        // CodeAlta records as its leaf the uuid of a tool result it was streamed, which the CLI may not write.
        Write(
            User("u1", null, "Search."),
            Assistant("a1", "u1", "msg_1", ToolUse("toolu_search", "ToolSearch", new JsonObject())));
        var first = ClaudeCodeTranscriptReader.Read(TranscriptPath);
        Append(
            Line(new JsonObject { ["type"] = "attachment", ["uuid"] = "t1", ["parentUuid"] = "streamed-result", ["entrypoint"] = "claude-vscode", ["attachment"] = new JsonObject { ["type"] = "deferred_tools_record" } }),
            Assistant("a2", "t1", "msg_2", Text("Found.")),
            User("u2", "a2", "Next."),
            Assistant("a3", "u2", "msg_3", Text("Next answer.")));

        var whole = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, "streamed-result", null);
        Assert.AreEqual(ClaudeCodeTranscriptReadStatus.Read, whole.Status);
        CollectionAssert.AreEqual(new[] { "Found.", "Next.", "Next answer." }, AllTexts(whole).ToArray());
        Assert.AreEqual(1, whole.Users);

        var fromPosition = AssertSameAsWholeRead("streamed-result", first.Position);
        Assert.IsTrue(fromPosition.FullRead, "Where an entry never written leads is decided on the whole file.");

        var lost = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, "nobody-names-it", first.Position);
        Assert.AreEqual(ClaudeCodeTranscriptReadStatus.LeafNotFound, lost.Status);
        Assert.AreEqual(0, lost.Turns.Count);
        Assert.AreEqual("a3", lost.Leaf);
    }

    [TestMethod]
    public void Read_ShowsCommandsAsCodeAltaRecordsThem()
    {
        Write(
            User("c1", null, "<command-name>/model</command-name>\n            <command-message>model</command-message>\n            <command-args>sonnet</command-args>"),
            User("o1", "c1", "<local-command-stdout>Set model to sonnet</local-command-stdout>"),
            User("v1", "o1", "<local-command-caveat>Caveat: do not answer.</local-command-caveat>", extra: line => line["isMeta"] = true),
            User("c2", "v1", "<command-message>review</command-message>\n<command-name>/review</command-name>\n<command-args>the diff</command-args>"),
            User("e2", "c2", "The prompt the command stands for.", extra: line => line["isMeta"] = true),
            Assistant("a2", "e2", "msg_2", Text("Reviewed.")),
            User("b1", "a2", "<bash-input>ls</bash-input>"),
            User("b2", "b1", "<bash-stdout>a.txt</bash-stdout><bash-stderr></bash-stderr>"),
            User("c3", "b2", "<command-name>/cost</command-name>\n<command-message>cost</command-message>\n<command-args></command-args>"),
            Line(new JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "local_command",
                ["uuid"] = "o3",
                ["parentUuid"] = "c3",
                ["content"] = "<local-command-stdout>Total cost: $0.00</local-command-stdout>",
            }),
            User("u4", "o3", "A plain question."),
            Assistant("a4", "u4", "msg_4", Text("A plain answer.")));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual("a4", result.Leaf);
        Assert.AreEqual(5, result.Turns.Count);
        string[][] expected =
        [
            ["User:/model sonnet", "Assistant:Set model to sonnet"],
            ["User:/review the diff", "Assistant:Reviewed."],
            ["User:! ls|a.txt"],
            ["User:/cost", "Assistant:Total cost: $0.00"],
            ["User:A plain question.", "Assistant:A plain answer."],
        ];
        for (var index = 0; index < expected.Length; index++)
        {
            CollectionAssert.AreEqual(
                expected[index],
                result.Turns[index].Messages.Select(static message => $"{message.Role}:{string.Join("|", message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value))}").ToArray(),
                $"Turn {index}");
        }

        Assert.AreEqual(5, result.Users);
        Assert.AreEqual(4, result.Assistants);
    }

    [TestMethod]
    public void Read_IgnoresACompactionWithoutAnAnchor()
    {
        Write(
            User("u1", null, "Old question."),
            Assistant("a1", "u1", "msg_1", Text("Old answer.")),
            Line(new JsonObject
            {
                ["parentUuid"] = null,
                ["logicalParentUuid"] = "a1",
                ["type"] = "system",
                ["subtype"] = "compact_boundary",
                ["compactMetadata"] = new JsonObject
                {
                    ["preservedMessages"] = new JsonObject { ["uuids"] = new JsonArray("u1", "a1") },
                },
                ["uuid"] = "b1",
            }),
            User("s1", "b1", "Summary.", extra: line => line["isCompactSummary"] = true),
            User("u2", "s1", "New question."),
            Assistant("a2", "u2", "msg_2", Text("New answer.")),
            // A root written last: with every root taken for a child of the missing anchor, it would end the chain.
            Line(new JsonObject { ["type"] = "attachment", ["uuid"] = "r9", ["parentUuid"] = null, ["attachment"] = new JsonObject { ["type"] = "date" } }));

        var result = ClaudeCodeTranscriptReader.Read(TranscriptPath);

        Assert.AreEqual("a2", result.Leaf);
        Assert.AreEqual(3, result.Turns.Count);
        Assert.IsNotNull(result.Turns[1].Compaction);
        CollectionAssert.AreEqual(new[] { "Old question.", "Old answer.", "New question.", "New answer." }, AllTexts(result).ToArray());
    }

    [TestMethod]
    public void Read_ReadsAToolResultSavedBesideTheTranscriptOnlyFromItsFolderAndUpToItsLimit()
    {
        var saved = Path.Combine(_folder, SessionId, "tool-results");
        Directory.CreateDirectory(saved);
        File.WriteAllText(Path.Combine(_folder, "secret.txt"), "outside the folder");
        File.WriteAllText(Path.Combine(_folder, SessionId, "secret.txt"), "outside the folder");
        using (var huge = File.Create(Path.Combine(saved, "huge.txt")))
        {
            huge.SetLength(4 * 1024 * 1024 + 1);
        }

        Write(
            User("u1", null, "Run them."),
            Assistant("a1", "u1", "msg_1", ToolUse("toolu_dots", "Bash", new JsonObject()), ToolUse("toolu_up", "Bash", new JsonObject()), ToolUse("toolu_huge", "Bash", new JsonObject())),
            ToolResult("r1", "a1", "toolu_dots", Persisted(@"C:\Users\someone\.claude\projects\X\" + SessionId + @"\tool-results\.."), source: "a1"),
            ToolResult("r2", "r1", "toolu_up", Persisted("/home/someone/.claude/projects/X/" + SessionId + "/tool-results/../../secret.txt"), source: "a1"),
            ToolResult("r3", "r2", "toolu_huge", Persisted(@"C:\elsewhere\" + SessionId + @"\tool-results\huge.txt"), source: "a1"),
            Assistant("a2", "r3", "msg_2", Text("Done.")));

        var results = ClaudeCodeTranscriptReader.Read(TranscriptPath).Turns.Single().Messages
            .SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolResult>())
            .ToDictionary(static part => part.CallId, static part => TextOf(part.Result));

        Assert.AreEqual(3, results.Count);
        foreach (var text in results.Values)
        {
            StringAssert.Contains(text, "Preview (first 2KB):", "The preview is kept.");
        }
    }

    // Reads from the position and as a whole after the same entry, and checks that both give the same result.
    private ClaudeCodeTranscriptReadResult AssertSameAsWholeRead(string afterUuid, ClaudeCodeTranscriptPosition? position)
    {
        var incremental = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, afterUuid, position);
        var whole = ClaudeCodeTranscriptReader.ReadAfter(TranscriptPath, afterUuid, null);
        Assert.IsNull(Difference(incremental, whole));
        return incremental;
    }

    /// <summary>
    /// Reads every transcript of this computer (<c>~/.claude/projects</c>, or <c>CLAUDE_CONFIG_DIR</c>) and checks that
    /// none makes the reader fail. Skipped unless <c>CODEALTA_TEST_CLAUDE_TRANSCRIPTS=1</c>: the transcripts are the
    /// user's own. Only counts are printed, never content.
    /// </summary>
    [TestMethod]
    public void Smoke_ReadsEveryLocalTranscript()
    {
        if (Environment.GetEnvironmentVariable("CODEALTA_TEST_CLAUDE_TRANSCRIPTS") != "1")
        {
            Assert.Inconclusive("Set CODEALTA_TEST_CLAUDE_TRANSCRIPTS=1 to read the Claude Code transcripts of this computer.");
        }

        var projects = ClaudeCodeProjectFolders.GetProjectsFolder(ClaudeCodeProjectFolders.ResolveConfigRoot());
        if (!Directory.Exists(projects))
        {
            Assert.Inconclusive($"No Claude Code projects folder at {projects}.");
        }

        int files = 0, sessions = 0, turns = 0, users = 0, assistants = 0, compactions = 0, cannotFollow = 0, emptyLeaf = 0, listed = 0, failures = 0;
        int comparisons = 0, incrementalReads = 0, incrementalMismatches = 0;
        var origins = new Dictionary<ClaudeCodeTranscriptOrigin, int>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.TopDirectoryOnly).Concat(
                     Directory.EnumerateDirectories(projects).SelectMany(static folder => Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly))))
        {
            files++;
            try
            {
                var header = ClaudeCodeTranscriptReader.ReadHeader(path);
                if (header is null || !seen.Add(header.SessionId))
                {
                    continue;
                }

                sessions++;
                listed += header.IsListedByVsCode ? 1 : 0;
                var result = ClaudeCodeTranscriptReader.Read(path);
                turns += result.Turns.Count;
                users += result.Users;
                assistants += result.Assistants;
                compactions += result.Turns.Count(static turn => turn.Compaction is not null);
                emptyLeaf += result.Leaf is null ? 1 : 0;
                origins[result.Origin] = origins.GetValueOrDefault(result.Origin) + 1;
                if (result.Leaf is { } leaf)
                {
                    // Read again from the position, as a catch-up does when nothing changed.
                    var again = ClaudeCodeTranscriptReader.ReadAfter(path, leaf, result.Position);
                    cannotFollow += again.Status == ClaudeCodeTranscriptReadStatus.CannotFollow ? 1 : 0;
                }

                // Copies cut at line boundaries, read, then completed and read from the positions, one step after the
                // other: each read from a position gives what a whole read after the same entry gives (the turns, the
                // tool results and their texts, the cut of the model messages, the leaf, the next position). One cut is
                // made just after a tool call, inside a model message.
                var bytes = File.ReadAllBytes(path);
                var copy = Path.Combine(_folder, header.SessionId + ".jsonl");
                foreach (var cuts in (int[][])[[CutAt(bytes, 0.4), CutAt(bytes, 0.75)], [CutAfterToolUse(bytes, 0.55)]])
                {
                    var written = cuts[0];
                    if (written <= 0)
                    {
                        continue;
                    }

                    File.WriteAllBytes(copy, bytes[..written]);
                    var last = ClaudeCodeTranscriptReader.Read(copy);
                    foreach (var next in cuts.Skip(1).Append(bytes.Length))
                    {
                        if (last.Leaf is not { } lastLeaf || next <= written)
                        {
                            break;
                        }

                        using (var stream = new FileStream(copy, FileMode.Append))
                        {
                            stream.Write(bytes, written, next - written);
                        }

                        written = next;
                        var incremental = ClaudeCodeTranscriptReader.ReadAfter(copy, lastLeaf, last.Position);
                        var whole = ClaudeCodeTranscriptReader.ReadAfter(copy, lastLeaf, null);
                        comparisons++;
                        incrementalReads += incremental.FullRead ? 0 : 1;
                        if (Difference(incremental, whole) is { } difference)
                        {
                            incrementalMismatches++;
                            Console.WriteLine($"A read from a position differs from a whole read: {difference}");
                        }

                        last = incremental;
                    }
                }

                File.Delete(copy);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"Failed on a transcript: {ex.GetType().Name}");
            }
        }

        Console.WriteLine($"files={files} sessions={sessions} listedByVsCode={listed} turns={turns} users={users} assistants={assistants} compactions={compactions} noLeaf={emptyLeaf} cannotFollowAgain={cannotFollow} failures={failures}");
        Console.WriteLine("origins: " + string.Join(", ", origins.Select(static pair => $"{pair.Key}={pair.Value}")));
        Console.WriteLine($"incremental: comparisons={comparisons} readFromPosition={incrementalReads} differentFromWholeRead={incrementalMismatches}");
        Assert.AreEqual(0, failures);
        Assert.AreEqual(0, cannotFollow);
        Assert.AreEqual(0, incrementalMismatches);
    }

    private static int CutAt(byte[] bytes, double fraction)
        => Array.LastIndexOf(bytes, (byte)'\n', Math.Max(0, (int)(bytes.Length * fraction))) + 1;

    // Just after the first line with a tool call from that point: inside a model message, before the results.
    private static int CutAfterToolUse(byte[] bytes, double fraction)
    {
        var from = (int)(bytes.Length * fraction);
        var call = bytes.AsSpan(from).IndexOf("\"type\":\"tool_use\""u8);
        if (call < 0)
        {
            return 0;
        }

        var newline = bytes.AsSpan(from + call).IndexOf((byte)'\n');
        return newline < 0 ? 0 : from + call + newline + 1;
    }

    // What differs between two reads, named without content; null when they are the same.
    private static string? Difference(ClaudeCodeTranscriptReadResult incremental, ClaudeCodeTranscriptReadResult whole)
    {
        if (incremental.Status != whole.Status)
        {
            return $"status {incremental.Status} / {whole.Status}";
        }

        if (incremental.Leaf != whole.Leaf)
        {
            return "leaf";
        }

        if (incremental.Turns.Count != whole.Turns.Count)
        {
            return $"turns {incremental.Turns.Count} / {whole.Turns.Count}";
        }

        if (Signature(incremental) != Signature(whole))
        {
            // Where, by the kind of what differs only.
            var mine = Signature(incremental).Split('\n');
            var theirs = Signature(whole).Split('\n');
            var line = Enumerable.Range(0, Math.Min(mine.Length, theirs.Length)).FirstOrDefault(index => mine[index] != theirs[index]);
            static string Shape(string text) => string.Join("|", text.Split('|').Select(static part => part.Split(' ')[0]));
            return $"messages at line {line} of {mine.Length}/{theirs.Length}: {Shape(mine[line])} / {Shape(theirs[line])} (read from the position: {!incremental.FullRead})";
        }

        if (incremental.Position is not { } left || whole.Position is not { } right ||
            left with { LastLines = null } != right with { LastLines = null } ||
            !(left.LastLines ?? []).SequenceEqual(right.LastLines ?? []))
        {
            return "position";
        }

        return null;
    }

    // The turns, their messages and every part, tool results with their texts: two reads that give the same signature
    // give the same conversation, cut into the same messages.
    private static string Signature(ClaudeCodeTranscriptReadResult result)
    {
        var builder = new StringBuilder();
        foreach (var turn in result.Turns)
        {
            builder.Append("turn ").Append(turn.FirstUuid).Append(' ').Append(turn.LastUuid).Append(' ')
                .Append(turn.OriginKind).Append(' ').Append(turn.TriggerText).Append(' ').Append(turn.Compaction).Append('\n');
            foreach (var message in turn.Messages)
            {
                builder.Append(message.Role).Append(':');
                foreach (var part in message.Parts)
                {
                    builder.Append(part switch
                    {
                        AgentMessagePart.Text text => "text " + text.Value,
                        AgentMessagePart.Reasoning reasoning => "reasoning " + reasoning.Value,
                        AgentMessagePart.ToolCall call => $"call {call.CallId} {call.Name} {call.Arguments.GetRawText()}",
                        AgentMessagePart.ToolResult toolResult => $"result {toolResult.CallId} {toolResult.Result.Success} {TextOf(toolResult.Result)}",
                        AgentMessagePart.Data data => $"data {data.MediaType} {data.Base64Data.Length}",
                        AgentMessagePart.Uri uri => "uri " + uri.Value,
                        _ => part.GetType().Name,
                    }).Append('|');
                }

                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    // Fixtures: lines with the shape of the ones the CLI writes, without real content.

    private void Write(params string[] lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }

        File.WriteAllText(TranscriptPath, builder.ToString());
    }

    private void Append(params string[] lines)
        => File.AppendAllText(TranscriptPath, string.Concat(lines.Select(static line => line + "\n")));

    private string NextTimestamp()
        => new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero).AddSeconds(++_clock).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    private static string Line(JsonObject line) => line.ToJsonString();

    private string Node(string type, string uuid, string? parent, string entrypoint, string? cwd, Action<JsonObject> fill, Action<JsonObject>? extra)
    {
        var line = new JsonObject
        {
            ["parentUuid"] = parent,
            ["isSidechain"] = false,
            ["type"] = type,
        };
        fill(line);
        line["uuid"] = uuid;
        line["timestamp"] = NextTimestamp();
        line["userType"] = "external";
        line["entrypoint"] = entrypoint;
        line["cwd"] = cwd ?? @"C:\work";
        line["sessionId"] = SessionId;
        line["version"] = "2.1.296";
        extra?.Invoke(line);
        return Line(line);
    }

    private string User(string uuid, string? parent, string text, string entrypoint = "claude-vscode", string? cwd = null, Action<JsonObject>? extra = null)
        => User(uuid, parent, new JsonArray(Text(text)), entrypoint, cwd, extra);

    private string User(string uuid, string? parent, JsonArray content, string entrypoint = "claude-vscode", string? cwd = null, Action<JsonObject>? extra = null)
        => Node("user", uuid, parent, entrypoint, cwd, line => line["message"] = new JsonObject { ["role"] = "user", ["content"] = content }, extra);

    private string ToolResult(string uuid, string parent, string toolUseId, string content, string source)
        => ToolResultLine(
            uuid,
            parent,
            new JsonArray(new JsonObject { ["tool_use_id"] = toolUseId, ["type"] = "tool_result", ["content"] = content, ["is_error"] = false }),
            extra: line => line["sourceToolAssistantUUID"] = source);

    private string ToolResultLine(string uuid, string parent, JsonArray content, Action<JsonObject>? extra = null)
        => Node("user", uuid, parent, "claude-vscode", null, line => line["message"] = new JsonObject { ["role"] = "user", ["content"] = content }, extra);

    private string Assistant(string uuid, string parent, string messageId, params JsonObject[] blocks)
        => Assistant(uuid, parent, messageId, blocks, "claude-vscode", null);

    private string Assistant(string uuid, string parent, string messageId, JsonObject block, string entrypoint)
        => Assistant(uuid, parent, messageId, [block], entrypoint, null);

    private string Assistant(string uuid, string parent, string messageId, JsonObject block, Action<JsonObject> extra)
        => Assistant(uuid, parent, messageId, [block], "claude-vscode", extra);

    private string Assistant(string uuid, string parent, string messageId, JsonObject[] blocks, string entrypoint, Action<JsonObject>? extra)
        => Node(
            "assistant",
            uuid,
            parent,
            entrypoint,
            null,
            line => line["message"] = new JsonObject
            {
                ["model"] = "claude-test",
                ["id"] = messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new JsonArray([.. blocks]),
                ["stop_reason"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
            },
            extra);

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject Thinking(string text) => new() { ["type"] = "thinking", ["thinking"] = text, ["signature"] = "c2lnbmF0dXJl" };

    private static JsonObject ToolUse(string id, string name, JsonObject input) => new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input };

    private static JsonObject Image(string data, string mediaType)
        => new() { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = data } };

    private static string Persisted(string savedPath)
        => $"<persisted-output>\nOutput too large (21.6KB). Full output saved to: {savedPath}\n\nPreview (first 2KB):\nfirst lines\n</persisted-output>";

    private static string TextOf(AgentToolResult result)
        => string.Join("\n", result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));

    private static string TextOf(AgentConversationMessage message)
        => string.Join("\n", message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));

    private static IEnumerable<string> AllTexts(ClaudeCodeTranscriptReadResult result)
        => result.Turns.SelectMany(static turn => turn.Messages).SelectMany(static message => message.Parts.OfType<AgentMessagePart.Text>()).Select(static part => part.Value);
}
