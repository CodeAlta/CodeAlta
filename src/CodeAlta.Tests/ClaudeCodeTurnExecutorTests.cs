using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// Calls the turn executor of the Claude Code provider the way the session of CodeAlta does, one model call
/// at a time, to state what it sends to the CLI and when it starts another process.
/// </summary>
[TestClass]
public sealed class ClaudeCodeTurnExecutorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task ConversationOfAnotherProvider_IsGivenAsContextWithTheFirstPrompt()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var request = CreateRequest(
        [
            User("what is in a.txt?"),
            new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("It holds one line.")]),
            User("and b.txt?"),
        ]);

        var response = await ExecuteAsync(executor, request);

        var process = cli.Last;
        Assert.IsNull(process.ResumedSessionId, "The CLI has no transcript for a conversation it did not take part in.");
        var sent = ClaudeCodeFakeProcess.UserText(process.UserMessages.Single());
        StringAssert.Contains(sent, "<codealta_previous_conversation>");
        StringAssert.Contains(sent, "what is in a.txt?");
        StringAssert.Contains(sent, "It holds one line.");
        StringAssert.EndsWith(sent, "and b.txt?");

        // What the session records lets the next process resume instead of starting over.
        var state = ClaudeCodeProviderState.Read(new AgentSessionState { SessionId = "session-1", ProviderState = response.ProviderState, UpdatedAt = DateTimeOffset.UtcNow });
        Assert.IsNotNull(state);
        Assert.AreEqual(process.SessionId, state.SessionId);
        Assert.AreEqual(2, state.Users);
        Assert.AreEqual(2, state.Assistants);
        Assert.AreEqual(process.SessionId, response.ProviderSessionId);
    }

    [TestMethod]
    public async Task LaterTurns_SendOnlyTheNewUserMessages()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var first = await ExecuteAsync(executor, CreateRequest([User("one")]));
        var conversation = new List<AgentConversationMessage> { User("one"), first.AssistantMessage, User("two"), User("three") };

        await ExecuteAsync(executor, CreateRequest(conversation, first));

        Assert.AreEqual(1, cli.Processes.Count, "The process of a session is kept between its turns.");
        CollectionAssert.AreEqual(
            new[] { "one", "two", "three" },
            cli.Last.UserMessages.Select(ClaudeCodeFakeProcess.UserText).ToArray());
    }

    [TestMethod]
    public async Task TurnTheCliStartsByItself_IsReadByTheRunStartedForIt()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = executor.OnProviderTurn("session-1", () => asked.TrySetResult());
        var first = await ExecuteAsync(executor, CreateRequest([User("run it in the background")]));
        Assert.IsNull(executor.GetPendingProviderTurn("session-1"), "A turn that answers a prompt is not one the CLI started by itself.");

        // The background command of the first turn ends: Claude Code starts a turn by itself, with a tool call.
        var process = cli.Last;
        EmitOwnTurn(process);
        await asked.Task.WaitAsync(Timeout);
        var notice = executor.GetPendingProviderTurn("session-1");
        Assert.AreEqual("Claude Code started a turn by itself: Background command \"Run the tests\" completed (exit code 0)", notice);

        // The run that is started for it records that message as its prompt. The CLI is not sent it.
        executor.AttachRun(new AgentProviderRunContext("session-1", new AgentRunId("run-2"), AllowAsync, null, ProviderInitiated: true));
        var conversation = new List<AgentConversationMessage> { User("run it in the background"), first.AssistantMessage, User(notice!) };
        var second = await ExecuteAsync(executor, CreateRequest(conversation, first));

        var call = second.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("toolu_bg", call.CallId);
        var tool = executor.ResolveTool("session-1", call, null);
        Assert.IsNotNull(tool, "The tool call of the turn is run by the run that shows it.");
        var result = await tool.Handler(new AgentToolInvocation(new ModelProviderId("claude-code"), "session-1", call.CallId, call.Name, call.Arguments), CancellationToken.None).WaitAsync(Timeout);
        Assert.AreEqual("done", result.Items.OfType<AgentToolResultItem.Text>().Single().Value);

        conversation.AddRange([second.AssistantMessage, new(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult(call.CallId, result)])]);
        var third = await ExecuteAsync(executor, CreateRequest(conversation, second));

        Assert.AreEqual("The background command finished.", third.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
        Assert.IsFalse(third.RequiresProviderFollowUp);
        Assert.AreEqual(1, process.UserMessages.Count);
        Assert.IsNull(executor.GetPendingProviderTurn("session-1"), "The turn was read: no other run is started for it.");

        // The next prompt is answered as before, by the same process.
        cli.OnUserMessage = static (fake, user) =>
        {
            fake.EmitTextTurn("msg_2", "second answer", user);
            return Task.CompletedTask;
        };
        executor.AttachRun(new AgentProviderRunContext("session-1", new AgentRunId("run-3"), AllowAsync, null));
        conversation.AddRange([third.AssistantMessage, User("second question")]);
        var fourth = await ExecuteAsync(executor, CreateRequest(conversation, third));

        Assert.AreEqual("second answer", fourth.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
        Assert.AreSame(process, cli.Last);
        CollectionAssert.AreEqual(
            new[] { "run it in the background", "second question" },
            process.UserMessages.Select(ClaudeCodeFakeProcess.UserText).ToArray());
    }

    [TestMethod]
    public async Task TurnTheCliStartsByItself_IsReadWithTheNextPrompt_WhenNoRunWasStartedForIt()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var first = await ExecuteAsync(executor, CreateRequest([User("run it in the background")]));
        var process = cli.Last;
        EmitOwnTurn(process);
        cli.OnUserMessage = static (fake, user) =>
        {
            fake.EmitTextTurn("msg_2", "second answer", user);
            return Task.CompletedTask;
        };

        // The process answers a request only after it wrote the lines above: they were all read by then.
        await process.McpAsync("ping");
        executor.AttachRun(new AgentProviderRunContext("session-1", new AgentRunId("run-2"), AllowAsync, null));
        var conversation = new List<AgentConversationMessage> { User("run it in the background"), first.AssistantMessage, User("second question") };
        var second = await ExecuteAsync(executor, CreateRequest(conversation, first));

        // What the CLI did by itself comes first, and is not taken for the answer to the prompt.
        var call = second.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("toolu_bg", call.CallId);
        Assert.IsNotNull(executor.ResolveTool("session-1", call, null));
        conversation.AddRange([second.AssistantMessage, new(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult(call.CallId, new AgentToolResult(true, [new AgentToolResultItem.Text("done")]))])]);
        var third = await ExecuteAsync(executor, CreateRequest(conversation, second));

        Assert.AreEqual("The background command finished.", third.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
        Assert.IsTrue(third.RequiresProviderFollowUp, "The turn of the prompt follows.");

        conversation.Add(third.AssistantMessage);
        var fourth = await ExecuteAsync(executor, CreateRequest(conversation, third));

        Assert.AreEqual("second answer", fourth.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
        Assert.IsFalse(fourth.RequiresProviderFollowUp);
        Assert.AreSame(process, cli.Last);
        Assert.IsNull(executor.GetPendingProviderTurn("session-1"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PromptSentWhileATurnOfTheCliRuns_IsAnsweredByItsOwnTurn(bool turnOfTheCliFails)
    {
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var first = await ExecuteAsync(executor, CreateRequest([User("run it in the background")]));

        // The CLI is in a turn it started by itself when the prompt is sent: it queues the prompt and ends its turn
        // with a result that names no message.
        var process = cli.Last;
        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "session_state_changed", ["state"] = "running", ["session_id"] = process.SessionId });
        if (!turnOfTheCliFails)
        {
            process.EmitAssistant("msg_bg", new JsonArray(ClaudeCodeFakeProcess.TextBlock("The background command finished.")));
        }
        else
        {
            process.EmitStreamEvent(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "msg_bg" } });
        }

        cli.OnUserMessage = (fake, user) =>
        {
            fake.Emit(new JsonObject
            {
                ["type"] = "result",
                ["subtype"] = turnOfTheCliFails ? "error_during_execution" : "success",
                ["is_error"] = turnOfTheCliFails,
                ["result"] = turnOfTheCliFails ? "The request failed." : "The background command finished.",
                ["session_id"] = fake.SessionId,
                ["origin"] = new JsonObject { ["kind"] = "task-notification" },
            });
            fake.EmitTextTurn("msg_2", "second answer", user);
            return Task.CompletedTask;
        };
        await process.McpAsync("ping");
        executor.AttachRun(new AgentProviderRunContext("session-1", new AgentRunId("run-2"), AllowAsync, null));
        var conversation = new List<AgentConversationMessage> { User("run it in the background"), first.AssistantMessage, User("second question") };
        var response = await ExecuteAsync(executor, CreateRequest(conversation, first));

        if (!turnOfTheCliFails)
        {
            Assert.AreEqual("The background command finished.", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
            Assert.IsTrue(response.RequiresProviderFollowUp);
            conversation.Add(response.AssistantMessage);
            response = await ExecuteAsync(executor, CreateRequest(conversation, response));
        }

        // The failure of a turn nobody asked for is not the failure of the prompt.
        Assert.AreEqual("second answer", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
        Assert.IsFalse(response.RequiresProviderFollowUp);
        Assert.IsNull(executor.GetPendingProviderTurn("session-1"));
    }

    [TestMethod]
    public async Task RunStartedForATurnOfTheCli_HasNothingToRead_WhenAnotherRunReadIt()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var first = await ExecuteAsync(executor, CreateRequest([User("one")]));

        executor.AttachRun(new AgentProviderRunContext("session-1", new AgentRunId("run-2"), AllowAsync, null, ProviderInitiated: true));
        var second = await ExecuteAsync(executor, CreateRequest([User("one"), first.AssistantMessage, User("Claude Code started a turn by itself.")], first));

        // It ends at once instead of waiting for a turn that will not come, and sends nothing to the CLI.
        Assert.IsFalse(second.RequiresProviderFollowUp);
        Assert.AreEqual(1, second.AssistantMessage.Parts.Count);
        Assert.AreEqual(1, cli.Last.UserMessages.Count);
    }

    [TestMethod]
    public async Task ChangeOfModel_RestartsTheCliAndResumesTheConversation()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var first = await ExecuteAsync(executor, CreateRequest([User("one")]) with { ModelId = "sonnet" });
        var conversation = new List<AgentConversationMessage> { User("one"), first.AssistantMessage, User("two") };

        await ExecuteAsync(executor, CreateRequest(conversation, first) with { ModelId = "opus", ReasoningEffort = AgentReasoningEffort.Max });

        Assert.AreEqual(2, cli.Processes.Count);
        var (before, after) = (cli.Processes[0], cli.Processes[1]);
        Assert.IsTrue(before.IsDisposed);
        Assert.AreEqual(before.SessionId, after.ResumedSessionId);
        CollectionAssert.IsSubsetOf(new[] { "--model", "opus", "--effort", "max" }, after.Launch.Arguments.ToArray());
        Assert.AreEqual("two", ClaudeCodeFakeProcess.UserText(after.UserMessages.Single()));
    }

    [TestMethod]
    public async Task Effort_IsOnlyGivenToAModelThatSupportsIt()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var request = CreateRequest([User("one")]) with { ReasoningEffort = AgentReasoningEffort.High };

        await ExecuteAsync(executor, request with { SessionId = "without", ModelId = "haiku", ModelInfo = new AgentModelInfo("haiku") });
        Assert.IsFalse(cli.Last.Launch.Arguments.Contains("--effort"));

        await ExecuteAsync(executor, request with { SessionId = "other-levels", ModelId = "sonnet", ModelInfo = new AgentModelInfo("sonnet", SupportedReasoningEfforts: [AgentReasoningEffort.Low]) });
        Assert.IsFalse(cli.Last.Launch.Arguments.Contains("--effort"));

        await ExecuteAsync(executor, request with { SessionId = "with", ModelId = "opus", ModelInfo = new AgentModelInfo("opus", SupportedReasoningEfforts: [AgentReasoningEffort.High]) });
        CollectionAssert.IsSubsetOf(new[] { "--effort", "high" }, cli.Last.Launch.Arguments.ToArray());

        // A model the CLI did not list (a full model name the user typed) is given what was asked.
        await ExecuteAsync(executor, request with { SessionId = "unlisted", ModelId = "claude-custom-9" });
        CollectionAssert.IsSubsetOf(new[] { "--effort", "high" }, cli.Last.Launch.Arguments.ToArray());
    }

    [TestMethod]
    public async Task CliWithoutTheReasoningDisplayOption_IsStartedWithoutIt()
    {
        var cli = new ClaudeCodeFakeCli { RefuseReasoningDisplay = true };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());

        var first = await ExecuteAsync(executor, CreateRequest([User("one")]));

        Assert.AreEqual(2, cli.Processes.Count);
        CollectionAssert.IsSubsetOf(new[] { "--thinking-display", "summarized" }, cli.Processes[0].Launch.Arguments.ToArray());
        Assert.IsFalse(cli.Processes[1].Launch.Arguments.Contains("--thinking-display"));
        Assert.AreEqual(cli.Processes[0].SessionId, cli.Processes[1].SessionId, "The conversation is the one that was being started.");
        Assert.AreEqual("ok", first.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);

        // The session remembers: a later restart does not try the option again.
        await ExecuteAsync(executor, CreateRequest([User("one"), first.AssistantMessage, User("two")], first) with { ModelId = "opus" });
        Assert.AreEqual(3, cli.Processes.Count);
        Assert.IsFalse(cli.Last.Launch.Arguments.Contains("--thinking-display"));
    }

    [TestMethod]
    public async Task IdleSession_ClosesItsProcessAndResumesWithTheNextTurn()
    {
        var cli = new ClaudeCodeFakeCli();
        var options = cli.CreateOptions();
        await using var executor = new ClaudeCodeTurnExecutor(new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = options.ProviderKey,
            TransportFactory = cli,
            ResolveCli = options.ResolveCli,
            IdleTimeout = TimeSpan.FromMilliseconds(50),
        });
        var first = await ExecuteAsync(executor, CreateRequest([User("one")]));
        var idle = cli.Last;
        for (var attempt = 0; attempt < 200 && !idle.IsDisposed; attempt++)
        {
            await Task.Delay(25);
        }

        Assert.IsTrue(idle.IsDisposed, "The process of an idle session is not kept forever.");

        await ExecuteAsync(executor, CreateRequest([User("one"), first.AssistantMessage, User("two")], first));

        Assert.AreEqual(idle.SessionId, cli.Last.ResumedSessionId);
    }

    [TestMethod]
    public async Task IdleSession_KeepsAProcessWhoseBackgroundCommandStillRuns()
    {
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = static (process, user) =>
        {
            process.Emit(new JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "background_tasks_changed",
                ["tasks"] = new JsonArray(new JsonObject { ["task_id"] = "b1", ["task_type"] = "local_bash", ["description"] = "Run the tests" }),
                ["session_id"] = process.SessionId,
            });
            process.EmitTextTurn("msg_1", "started", user);
            return Task.CompletedTask;
        };
        var options = cli.CreateOptions();
        await using var executor = new ClaudeCodeTurnExecutor(new ClaudeCodeModelProviderRuntimeOptions
        {
            ProviderKey = options.ProviderKey,
            TransportFactory = cli,
            ResolveCli = options.ResolveCli,
            IdleTimeout = TimeSpan.FromMilliseconds(50),
        });
        await ExecuteAsync(executor, CreateRequest([User("run it in the background")]));
        var process = cli.Last;

        // Closing the process would end the command, and the turn its end starts.
        await Task.Delay(500);
        Assert.IsFalse(process.IsDisposed);

        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "background_tasks_changed", ["tasks"] = new JsonArray(), ["session_id"] = process.SessionId });
        for (var attempt = 0; attempt < 200 && !process.IsDisposed; attempt++)
        {
            await Task.Delay(25);
        }

        Assert.IsTrue(process.IsDisposed, "Once nothing runs any more, the process of an idle session is closed.");
    }

    [TestMethod]
    public async Task ToolRegisteredDuringARun_IsAnnouncedToTheCli()
    {
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = async (process, user) =>
        {
            // The CLI lists the tools when it connects to the server, as the real one does.
            await process.McpAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-11-25" });
            await process.RequestAsync(new JsonObject
            {
                ["subtype"] = "mcp_message",
                ["server_name"] = "codealta",
                ["message"] = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
            });
            await process.McpAsync("tools/list");
            process.EmitTextTurn("msg_1", "ok", user);
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var first = await ExecuteAsync(executor, CreateRequest([User("one")]) with { Tools = [Tool("alta"), Tool("first_tool")] });

        cli.OnUserMessage = static (process, user) =>
        {
            process.EmitTextTurn("msg_2", "ok", user);
            return Task.CompletedTask;
        };
        await ExecuteAsync(executor, CreateRequest([User("one"), first.AssistantMessage, User("two")], first) with { Tools = [Tool("alta"), Tool("first_tool"), Tool("second_tool")] });

        var notification = cli.Last.Received.Single(static message =>
            message.GetProperty("type").GetString() == "control_request" &&
            message.GetProperty("request").GetProperty("subtype").GetString() == "mcp_message");
        Assert.AreEqual("notifications/tools/list_changed", notification.GetProperty("request").GetProperty("message").GetProperty("method").GetString());
        var tools = (await cli.Last.McpAsync("tools/list")).GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { "alta", "first_tool", "second_tool" }, tools.Select(static tool => tool.GetProperty("name").GetString()).ToArray());

        // Claude Code defers the tools of a server until the model searches for them. A session of CodeAlta
        // carries the tools it was given, a tool that joins a run included: none of them is deferred.
        Assert.IsTrue(tools.All(static tool => tool.GetProperty("_meta").GetProperty("anthropic/alwaysLoad").GetBoolean()));
    }

    [TestMethod]
    public async Task ToolsOfTheFirstRequest_AreTheOnesTheCliListsWhenItStarts()
    {
        var cli = new ClaudeCodeFakeCli { ListsToolsAtStart = true };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());

        await ExecuteAsync(executor, CreateRequest([User("one")]) with { Tools = [Tool("alta"), Tool("take_snapshot")] });

        // The first request of the model has them: they do not arrive with a later notification.
        CollectionAssert.AreEqual(new[] { "alta", "take_snapshot" }, await cli.Last.ToolsListedAtStart.WaitAsync(Timeout));
        Assert.IsFalse(cli.Last.Received.Any(static message =>
            message.GetProperty("type").GetString() == "control_request" &&
            message.GetProperty("request").GetProperty("subtype").GetString() == "mcp_message"));
    }

    [TestMethod]
    public async Task InstructionsThatChangeBetweenTwoPrompts_AreGivenWithTheNextPrompt()
    {
        const string Brief = "# Agent Prompt\n\nYou are the Default agent.\n\nBe brief.\n\n# Runtime Context\n\n- Platform: test";
        const string Plan = "# Agent Prompt\n\nYou are the Plan agent.\n\nPlan only.\n\n# Runtime Context\n\n- Platform: test";
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var first = await ExecuteAsync(executor, CreateRequest([User("one")]) with { DeveloperInstructions = Brief });
        var process = cli.Last;
        StringAssert.Contains(process.InitializeRequest.GetProperty("appendSystemPrompt").GetString(), "Be brief.");

        var second = await ExecuteAsync(executor, CreateRequest([User("one"), first.AssistantMessage, User("two")], first) with { DeveloperInstructions = Brief });

        Assert.AreEqual("two", ClaudeCodeFakeProcess.UserText(process.UserMessages[1]), "The same instructions are not given twice.");

        // Claude Code keeps the system prompt a conversation started with, also when it is resumed. Another agent
        // prompt, an activated skill or tools that were turned on reach it with the prompt that follows them.
        var third = await ExecuteAsync(
            executor,
            CreateRequest([User("one"), first.AssistantMessage, User("two"), second.AssistantMessage, User("three")], second) with { DeveloperInstructions = Plan });

        Assert.AreSame(process, cli.Last, "The process is kept: starting another one would change nothing.");
        var blocks = process.UserMessages[2].GetProperty("message").GetProperty("content").EnumerateArray().Select(static block => block.GetProperty("text").GetString()!).ToArray();
        Assert.AreEqual(2, blocks.Length);
        StringAssert.StartsWith(blocks[0], "<codealta_instructions_update>");
        StringAssert.Contains(blocks[0], "You are the Plan agent.");
        StringAssert.Contains(blocks[0], "Plan only.");
        StringAssert.Contains(blocks[0], "Be brief.", "What no longer applies is named.");
        Assert.IsFalse(blocks[0].Contains("Platform: test", StringComparison.Ordinal), "What did not change is not repeated.");
        Assert.IsFalse(blocks[0].Contains("driven by CodeAlta", StringComparison.Ordinal));
        Assert.AreEqual("three", blocks[1], "The prompt of the user stays the last block.");

        await ExecuteAsync(
            executor,
            CreateRequest([User("one"), first.AssistantMessage, User("two"), second.AssistantMessage, User("three"), third.AssistantMessage, User("four")], third) with { DeveloperInstructions = Plan });

        Assert.AreEqual("four", ClaudeCodeFakeProcess.UserText(process.UserMessages[3]));
    }

    [TestMethod]
    public async Task InstructionsOfAResumedConversation_AreGivenAgainOnlyWhenTheyChanged()
    {
        var cli = new ClaudeCodeFakeCli();
        AgentTurnResponse first;
        await using (var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions()))
        {
            first = await ExecuteAsync(executor, CreateRequest([User("one")]) with { DeveloperInstructions = "Be brief." });
        }

        // The application was started again: what the conversation was told is known by its state alone.
        AgentTurnResponse second;
        await using (var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions()))
        {
            second = await ExecuteAsync(executor, CreateRequest([User("one"), first.AssistantMessage, User("two")], first) with { DeveloperInstructions = "Be brief." });
            Assert.IsNotNull(cli.Last.ResumedSessionId);
            Assert.AreEqual("two", ClaudeCodeFakeProcess.UserText(cli.Last.UserMessages.Single()));
        }

        await using (var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions()))
        {
            await ExecuteAsync(
                executor,
                CreateRequest([User("one"), first.AssistantMessage, User("two"), second.AssistantMessage, User("three")], second) with { DeveloperInstructions = "Plan only." });

            // Without the text the conversation was given, all the instructions are given again.
            var sent = ClaudeCodeFakeProcess.UserText(cli.Last.UserMessages.Single());
            StringAssert.StartsWith(sent, "<codealta_instructions_update>");
            StringAssert.Contains(sent, "replaces all the instructions of CodeAlta");
            StringAssert.Contains(sent, "driven by CodeAlta");
            StringAssert.Contains(sent, "Plan only.");
            StringAssert.EndsWith(sent, "three");
        }
    }

    [TestMethod]
    public async Task InstructionsThatChangeDuringARun_WaitForTheNextPrompt()
    {
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = static (process, user) =>
        {
            process.EmitInit();
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "Read", new JsonObject { ["file_path"] = "a.txt" })));
            process.EmitToolResult("toolu_1", "content");
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("done")));
            process.EmitResult("done", user);
            return Task.CompletedTask;
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var first = await ExecuteAsync(executor, CreateRequest([User("one")]) with { DeveloperInstructions = "Be brief." });
        var call = first.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().Single();
        var toolMessage = new AgentConversationMessage(
            AgentConversationRole.Tool,
            [new AgentMessagePart.ToolResult(call.CallId, new AgentToolResult(true, [new AgentToolResultItem.Text("content")]))]);

        // The CLI is in the middle of its turn: nothing is sent to it for instructions only the next prompt needs.
        var second = await ExecuteAsync(executor, CreateRequest([User("one"), first.AssistantMessage, toolMessage], first) with { DeveloperInstructions = "Changed meanwhile." });

        Assert.AreEqual(1, cli.Processes.Count);
        Assert.AreEqual(1, cli.Last.UserMessages.Count);
        Assert.AreEqual("done", second.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task ResultWithoutTheMessagesOfTheTurn_StillEndsTheTurn()
    {
        // A CLI that predates the message lifecycle does not name the user messages a result answers.
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = static (process, _) =>
        {
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.TextBlock("plain")));
            process.Emit(new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["result"] = "plain", ["session_id"] = process.SessionId });
            return Task.CompletedTask;
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());

        var response = await ExecuteAsync(executor, CreateRequest([User("one")]));

        Assert.AreEqual("plain", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
        Assert.IsFalse(response.RequiresProviderFollowUp);
    }

    [TestMethod]
    public async Task OutputOfACommandOfTheCli_IsTheAnswer()
    {
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = static (process, user) =>
        {
            // `/context` and similar commands write no assistant message.
            process.EmitResult("Context: 12% used", user);
            return Task.CompletedTask;
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());

        var response = await ExecuteAsync(executor, CreateRequest([User("/context")]));

        Assert.AreEqual("Context: 12% used", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
    }

    [TestMethod]
    public async Task TheTwoWindowsOfThePlan_AreReportedTogether_WhenTheCliSendsThem()
    {
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = static (process, user) =>
        {
            process.EmitInit();
            process.EmitStreamEvent(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "msg_1" } });
            process.EmitTextStream("msg_1", 0, "an", "swer");
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.TextBlock("answer")));
            process.EmitMessageStop();
            process.Emit(new JsonObject
            {
                ["type"] = "rate_limit_event",
                ["rate_limit_info"] = new JsonObject
                {
                    ["status"] = "allowed", ["rateLimitType"] = "seven_day", ["utilization"] = 0.52, ["resetsAt"] = 1_800_500_000,
                    ["unifiedWindows"] = new JsonObject
                    {
                        ["five_hour"] = new JsonObject { ["utilization"] = 0.06, ["resetsAt"] = 1_800_000_000 },
                        ["seven_day"] = new JsonObject { ["utilization"] = 0.52, ["resetsAt"] = 1_800_500_000 },
                    },
                },
            });
            process.EmitResult("answer", user);
            return Task.CompletedTask;
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());

        var response = await executor.ExecuteTurnAsync(CreateRequest([User("one")]), static (_, _) => ValueTask.CompletedTask, static (_, _) => ValueTask.CompletedTask).WaitAsync(Timeout);

        // The five hours first and the week second, whichever of them is the one that limits now.
        var limits = response.Usage!.RateLimits!;
        Assert.AreEqual(new AgentRateLimitWindow(6, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), 300), limits.Primary);
        Assert.AreEqual(new AgentRateLimitWindow(52, DateTimeOffset.FromUnixTimeSeconds(1_800_500_000), 7 * 24 * 60), limits.Secondary);
        Assert.AreEqual("seven_day", limits.Name);
    }

    [TestMethod]
    public async Task ReasoningAndLimits_AreReported()
    {
        var cli = new ClaudeCodeFakeCli();
        cli.OnUserMessage = static (process, user) =>
        {
            process.EmitInit();
            process.EmitStreamEvent(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "msg_1" } });
            process.EmitStreamEvent(new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "thinking" } });
            process.EmitStreamEvent(new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = "let me see" } });
            process.EmitAssistant("msg_1", new JsonArray(new JsonObject { ["type"] = "thinking", ["thinking"] = "let me see", ["signature"] = "sig" }));
            process.EmitTextStream("msg_1", 1, "an", "swer");
            process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.TextBlock("answer")));
            process.EmitMessageStop();
            process.Emit(new JsonObject
            {
                ["type"] = "rate_limit_event",
                ["rate_limit_info"] = new JsonObject { ["status"] = "allowed_warning", ["rateLimitType"] = "five_hour", ["utilization"] = 0.91, ["resetsAt"] = 1_800_000_000 },
            });
            process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "api_retry", ["attempt"] = 1, ["max_retries"] = 10, ["retry_delay_ms"] = 1500, ["error"] = "server_error" });
            process.EmitResult("answer", user);
            return Task.CompletedTask;
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var deltas = new List<AgentTurnDelta>();
        var updates = new List<AgentTurnSessionUpdate>();

        var response = await executor.ExecuteTurnAsync(
            CreateRequest([User("one")]),
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            },
            (update, _) =>
            {
                updates.Add(update);
                return ValueTask.CompletedTask;
            }).WaitAsync(Timeout);

        var reasoning = response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Single();
        Assert.AreEqual("let me see", reasoning.Value);
        Assert.IsNull(reasoning.ProtectedData, "The signature belongs to the conversation of the CLI.");
        Assert.AreEqual("answer", response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Single().Value);
        CollectionAssert.AreEqual(new[] { AgentContentKind.Reasoning, AgentContentKind.Assistant, AgentContentKind.Assistant }, deltas.Select(static delta => delta.Kind).ToArray());
        CollectionAssert.AreEqual(response.AssistantPartContentIds!.ToArray(), new[] { deltas[0].ContentId, deltas[1].ContentId });

        Assert.AreEqual(91, response.Usage!.RateLimits!.Primary!.UsedPercent);
        Assert.AreEqual(300, response.Usage.RateLimits.Primary.WindowDurationMinutes, "A window the CLI names says how long it is.");
        Assert.IsNull(response.Usage.RateLimits.Secondary);
        Assert.AreEqual(0.01, response.Usage.LastOperation!.Cost);
        // The input holds what was read from and written to the cache with the 100 tokens that were not cached, as
        // for every provider.
        Assert.AreEqual(2400, response.Usage.LastOperation.InputTokens);
        Assert.AreEqual(new AgentInputTokenUsage(2400, 100, 2000, 300), AgentInputTokenUsage.From(response.Usage.LastOperation));
        Assert.AreEqual(2000, response.Usage.LastOperation.CacheReadTokens);
        Assert.AreEqual(300, response.Usage.LastOperation.CacheWriteTokens);
        Assert.AreEqual(2000, response.Usage.LastOperation.CachedInputTokens);
        Assert.AreEqual(12, response.Usage.LastOperation.OutputTokens);
        Assert.AreEqual(2412, response.Usage.Window!.CurrentTokens);
        Assert.IsTrue(updates.Any(static update => update.Kind == AgentSessionUpdateKind.Warning && update.Message.Contains("nearly reached", StringComparison.Ordinal)));
        Assert.IsTrue(updates.Any(static update => update.Kind == AgentSessionUpdateKind.Reconnecting && update.Message.Contains("retry 1/10", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CostOfATurn_IsReportedOnceWithItsAnswer()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var first = await ExecuteAsync(executor, CreateRequest([User("one")]));
        Assert.AreEqual(0.01, first.Usage!.LastOperation!.Cost);
        Assert.AreEqual(42, first.Usage.LastOperation.DurationMs);

        // The first request of the next turn calls a tool: the turn has no cost yet.
        cli.OnUserMessage = static (process, _) =>
        {
            process.EmitAssistant("msg_2", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_1", "Bash", new JsonObject { ["command"] = "ls" })));
            process.EmitMessageStop("tool_use");
            return Task.CompletedTask;
        };
        var second = await ExecuteAsync(executor, CreateRequest([User("one"), first.AssistantMessage, User("two")], first));

        Assert.IsInstanceOfType<AgentMessagePart.ToolCall>(second.AssistantMessage.Parts.Single());
        Assert.IsNull(second.Usage!.LastOperation!.Cost);
        Assert.IsNull(second.Usage.LastOperation.DurationMs);
    }

    [TestMethod]
    public async Task CostOfATurn_IsWhatTheConversationCostSinceTheTurnBefore()
    {
        // The CLI reports the cost of the whole conversation with each result, also after it resumed it.
        var cli = new ClaudeCodeFakeCli();
        AgentTurnResponse first, second, third;
        await using (var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions()))
        {
            first = await ExecuteAsync(executor, CreateRequest([User("one")]));
            second = await ExecuteAsync(executor, CreateRequest([User("one"), first.AssistantMessage, User("two")], first));
        }

        Assert.AreEqual(0.01, first.Usage!.LastOperation!.Cost!.Value, 1e-9);
        Assert.AreEqual(0.01, second.Usage!.LastOperation!.Cost!.Value, 1e-9, "Not the 0.02 the conversation cost so far.");

        // The application was started again: the total the turn is counted from is in the state of the session.
        await using (var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions()))
        {
            third = await ExecuteAsync(executor, CreateRequest([User("one"), first.AssistantMessage, User("two"), second.AssistantMessage, User("three")], second));
        }

        Assert.IsNotNull(cli.Last.ResumedSessionId);
        Assert.AreEqual(0.01, third.Usage!.LastOperation!.Cost!.Value, 1e-9);

        // A session saved before that total was kept: its next turn has no cost rather than the cost of all of them.
        var legacy = JsonNode.Parse(third.ProviderState!.Value.GetRawText())!.AsObject();
        Assert.IsTrue(legacy.Remove("cost"));
        var saved = third with { ProviderState = JsonDocument.Parse(legacy.ToJsonString()).RootElement.Clone() };
        await using (var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions()))
        {
            var conversation = new List<AgentConversationMessage> { User("one"), first.AssistantMessage, User("two"), second.AssistantMessage, User("three"), third.AssistantMessage, User("four") };
            var fourth = await ExecuteAsync(executor, CreateRequest(conversation, saved));
            Assert.IsNull(fourth.Usage!.LastOperation!.Cost);

            conversation.AddRange([fourth.AssistantMessage, User("five")]);
            var fifth = await ExecuteAsync(executor, CreateRequest(conversation, fourth));
            Assert.AreEqual(0.01, fifth.Usage!.LastOperation!.Cost!.Value, 1e-9);
        }
    }

    [TestMethod]
    public async Task ContextWindow_IsTheOneOfTheModelThatAnswered()
    {
        // A result names every model the conversation used, a subagent's included, whatever the order.
        var cli = new ClaudeCodeFakeCli
        {
            ModelUsage = static () => new JsonObject
            {
                ["claude-test-1"] = new JsonObject { ["contextWindow"] = 200000 },
                ["claude-other-9"] = new JsonObject { ["contextWindow"] = 1000000 },
            },
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());

        var first = await ExecuteAsync(executor, CreateRequest([User("one")]));

        Assert.AreEqual("claude-test-1", first.Usage!.LastOperation!.Model);
        Assert.AreEqual(200_000, first.Usage.Window!.TokenLimit);
    }

    [TestMethod]
    public async Task SummaryRequestOfALocalCompaction_IsRefused()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var request = CreateRequest([User("Summarize this conversation.")]) with { RunId = new AgentRunId("compaction-summary:1") };

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask));

        Assert.AreEqual(0, cli.Processes.Count, "The context of a Claude Code session is the CLI's to summarize.");
    }

    [TestMethod]
    public async Task DisposedSession_StopsItsProcess()
    {
        var cli = new ClaudeCodeFakeCli();
        var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        await ExecuteAsync(executor, CreateRequest([User("one")]));
        await ExecuteAsync(executor, CreateRequest([User("one")]) with { SessionId = "session-2" });

        await ((IAgentProviderSessionCleanup)executor).DisposeProviderSessionAsync("session-1");

        Assert.IsTrue(cli.Processes[0].IsDisposed);
        Assert.IsFalse(cli.Processes[1].IsDisposed, "Each session has its own process.");

        await executor.DisposeAsync();
        Assert.IsTrue(cli.Processes[1].IsDisposed);
    }

    private static Task<AgentTurnResponse> ExecuteAsync(ClaudeCodeTurnExecutor executor, AgentTurnRequest request)
        => executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask).WaitAsync(Timeout);

    private static Task<AgentPermissionDecision> AllowAsync(AgentPermissionRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));

    // What Claude Code 2.1.292 writes when a background command ends while no turn runs: it says which one, then
    // runs a turn that no user message started and whose result names none.
    private static void EmitOwnTurn(ClaudeCodeFakeProcess process)
    {
        process.Emit(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "task_notification",
            ["task_id"] = "b1",
            ["status"] = "completed",
            ["summary"] = "Background command \"Run the tests\" completed (exit code 0)",
            ["session_id"] = process.SessionId,
        });
        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "background_tasks_changed", ["tasks"] = new JsonArray(), ["session_id"] = process.SessionId });
        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "session_state_changed", ["state"] = "running", ["session_id"] = process.SessionId });
        process.EmitAssistant("msg_bg", new JsonArray(ClaudeCodeFakeProcess.ToolUseBlock("toolu_bg", "Read", new JsonObject { ["file_path"] = "/tmp/out" })));
        process.EmitToolResult("toolu_bg", "done");
        process.EmitAssistant("msg_bg2", new JsonArray(ClaudeCodeFakeProcess.TextBlock("The background command finished.")));
        process.Emit(new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["result"] = "The background command finished.",
            ["session_id"] = process.SessionId,
            ["origin"] = new JsonObject { ["kind"] = "task-notification", ["producer"] = "session-task" },
        });
        process.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "session_state_changed", ["state"] = "idle", ["session_id"] = process.SessionId });
    }

    private static AgentConversationMessage User(string text)
        => new(AgentConversationRole.User, [new AgentMessagePart.Text(text)]);

    private static AgentToolDefinition Tool(string name)
        => new(
            new AgentToolSpec(name, $"The tool {name}.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
            static (_, _) => Task.FromResult(new AgentToolResult(true, [])));

    private static AgentTurnRequest CreateRequest(IReadOnlyList<AgentConversationMessage> conversation, AgentTurnResponse? previous = null)
        => new()
        {
            Provider = new ModelProviderRuntimeDescriptor
            {
                ProtocolFamily = ClaudeCodeModelProviderRuntime.ProtocolFamily,
                ProviderKey = "claude-code",
                DisplayName = "Claude Code",
                TransportKind = AgentTransportKind.ClaudeCodeCli,
            },
            ProviderId = new ModelProviderId("claude-code"),
            SessionId = "session-1",
            RunId = new AgentRunId("run-1"),
            WorkingDirectory = Path.GetTempPath(),
            Conversation = conversation,
            Tools = [],
            State = new AgentSessionState
            {
                SessionId = "session-1",
                ProviderSessionId = previous?.ProviderSessionId,
                ProviderState = previous?.ProviderState,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };
}
