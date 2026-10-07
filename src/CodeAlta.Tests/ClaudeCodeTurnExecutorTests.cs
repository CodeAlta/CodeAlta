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
        var first = await ExecuteAsync(executor, CreateRequest([User("one")]) with { Tools = [Tool("first_tool")] });

        cli.OnUserMessage = static (process, user) =>
        {
            process.EmitTextTurn("msg_2", "ok", user);
            return Task.CompletedTask;
        };
        await ExecuteAsync(executor, CreateRequest([User("one"), first.AssistantMessage, User("two")], first) with { Tools = [Tool("first_tool"), Tool("second_tool")] });

        var notification = cli.Last.Received.Single(static message =>
            message.GetProperty("type").GetString() == "control_request" &&
            message.GetProperty("request").GetProperty("subtype").GetString() == "mcp_message");
        Assert.AreEqual("notifications/tools/list_changed", notification.GetProperty("request").GetProperty("message").GetProperty("method").GetString());
        var listed = (await cli.Last.McpAsync("tools/list")).GetProperty("result").GetProperty("tools").EnumerateArray().Select(static tool => tool.GetProperty("name").GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "first_tool", "second_tool" }, listed);
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
        Assert.AreEqual(0.01, response.Usage.LastOperation!.Cost);
        Assert.AreEqual(2400, response.Usage.LastOperation.InputTokens);
        Assert.AreEqual(2000, response.Usage.LastOperation.CachedInputTokens);
        Assert.IsTrue(updates.Any(static update => update.Kind == AgentSessionUpdateKind.Warning && update.Message.Contains("nearly reached", StringComparison.Ordinal)));
        Assert.IsTrue(updates.Any(static update => update.Kind == AgentSessionUpdateKind.Reconnecting && update.Message.Contains("retry 1/10", StringComparison.Ordinal)));
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
