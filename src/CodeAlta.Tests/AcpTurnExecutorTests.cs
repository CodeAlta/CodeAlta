using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using CodeAlta.Plugin.Acp;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AcpTurnExecutorTests
{
    private static readonly ModelProviderId ProviderId = new("fake-acp");

    [TestMethod]
    public async Task FirstTurn_StartsASessionWithTheToolsOfCodeAlta_AndGivesTheInstructionsWithThePrompt()
    {
        var agent = new AcpFakeAgent();
        await using var fixture = new Fixture(agent);

        var conversation = await fixture.RunAsync([User("hello")]);

        var newSession = agent.RequestsOf("session/new").Single()!;
        var server = newSession["mcpServers"]!.AsArray().Single()!;
        Assert.AreEqual("codealta", server["name"]!.GetValue<string>());
        StringAssert.StartsWith(server["url"]!.GetValue<string>(), "http://127.0.0.1:");
        StringAssert.StartsWith(server["headers"]![0]!["value"]!.GetValue<string>(), "Bearer ");
        Assert.IsNull(newSession["systemPrompt"]);
        var prompt = PromptText(agent.RequestsOf("session/prompt").Single());
        StringAssert.Contains(prompt, AcpPrompts.HarnessOpening);
        StringAssert.Contains(prompt, "The instructions of CodeAlta for this session:\n\nBe brief.");
        StringAssert.Contains(prompt, "the MCP server named `codealta`");
        StringAssert.EndsWith(prompt, "hello");
        Assert.AreEqual("ok", Text(conversation[^1]));
        Assert.AreEqual("first-prompt", fixture.Executor.GetInstructionsChannel("session-1"));
        var state = AcpProviderStateOf(fixture.State);
        Assert.AreEqual("agent-session-1", state.GetProperty("sessionId").GetString());
        Assert.AreEqual(1, state.GetProperty("users").GetInt32());
        Assert.AreEqual(1, state.GetProperty("assistants").GetInt32());
    }

    [TestMethod]
    public async Task AgentThatTakesASystemPrompt_IsGivenTheInstructionsThere()
    {
        var agent = new AcpFakeAgent { SystemPrompt = true };
        await using var fixture = new Fixture(agent);

        await fixture.RunAsync([User("hello")]);

        var systemPrompt = agent.RequestsOf("session/new").Single()!["systemPrompt"]!;
        Assert.AreEqual("append", systemPrompt["mode"]!.GetValue<string>());
        StringAssert.Contains(systemPrompt["content"]![0]!["text"]!.GetValue<string>(), "Be brief.");
        Assert.IsFalse(PromptText(agent.RequestsOf("session/prompt").Single()).Contains(AcpPrompts.HarnessOpening, StringComparison.Ordinal));
        Assert.AreEqual("acp-system-prompt", fixture.Executor.GetInstructionsChannel("session-1"));
    }

    [TestMethod]
    public async Task ToolOfCodeAlta_IsRunByTheSessionWhenTheAgentCallsItThroughMcp()
    {
        JsonNode? mcpResult = null;
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                await prompt.SendTextAsync("Looking.");
                await prompt.StartToolAsync("call-1", "codealta/alta", "other", new JsonObject { ["args"] = new JsonArray("session", "list") });
                mcpResult = await prompt.CallMcpToolAsync("alta", new JsonObject { ["args"] = new JsonArray("session", "list") });
                await prompt.EndToolAsync("call-1", "two sessions");
                await prompt.SendTextAsync("Done.");
                return "end_turn";
            },
        };
        var runs = new List<JsonElement>();
        await using var fixture = new Fixture(agent)
        {
            Tools = [Tool("alta", invocation =>
            {
                runs.Add(invocation.Arguments.Clone());
                return new AgentToolResult(true, [new AgentToolResultItem.Text("session-1, session-2")]);
            })],
        };

        var conversation = await fixture.RunAsync([User("list the sessions")]);

        var call = conversation[1].Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("Looking.", ((AgentMessagePart.Text)conversation[1].Parts[0]).Value);
        Assert.AreEqual("alta", call.Name);
        Assert.AreEqual("call-1", call.CallId);
        Assert.AreEqual("session", call.Arguments.GetProperty("args")[0].GetString());
        Assert.AreEqual(1, runs.Count, "the tool runs once, for the call the session records");
        Assert.AreEqual("session-1, session-2", mcpResult!["content"]![0]!["text"]!.GetValue<string>());
        Assert.AreEqual("session-1, session-2", ToolResult(conversation[2]));
        Assert.AreEqual("Done.", Text(conversation[^1]));
    }

    [TestMethod]
    public async Task ToolAddedDuringTheRun_IsAnnouncedToTheAgent_ThatListsTheToolsAgain()
    {
        string[]? before = null;
        string[]? after = null;
        JsonNode? announced = null;
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                before = await prompt.ListMcpToolNamesAsync();
                await using var events = await prompt.OpenMcpEventsAsync();
                await prompt.StartToolAsync("call-1", "codealta/alta", "other", new JsonObject { ["args"] = new JsonArray("ui", "activate") });
                await prompt.CallMcpToolAsync("alta", new JsonObject { ["args"] = new JsonArray("ui", "activate") });
                await prompt.EndToolAsync("call-1", "activated");
                announced = await events.ReadMessageAsync(TimeSpan.FromSeconds(10));
                after = await prompt.ListMcpToolNamesAsync();
                return "end_turn";
            },
        };
        // `alta ui activate` registers the UI tools in the running turn.
        var tools = new List<AgentToolDefinition>();
        tools.Add(Tool("alta", _ =>
        {
            tools.Add(Tool("take_screenshot", static _ => new AgentToolResult(true, [new AgentToolResultItem.Text("picture")])));
            return new AgentToolResult(true, [new AgentToolResultItem.Text("activated")]);
        }));
        await using var fixture = new Fixture(agent) { Tools = tools };

        await fixture.RunAsync([User("look at the window")]);

        CollectionAssert.AreEqual(new[] { "alta" }, before);
        Assert.AreEqual("notifications/tools/list_changed", announced?["method"]?.GetValue<string>());
        CollectionAssert.AreEquivalent(new[] { "alta", "take_screenshot" }, after);
    }

    [TestMethod]
    public async Task ToolAddedDuringTheRun_IsAnnouncedWithTheResultOfTheCall_ToAnAgentWithoutAStreamOfEvents()
    {
        // Copilot opens no stream of events: it posts, and takes a response that is a stream of events.
        string[]? notified = null;
        string[]? after = null;
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                await prompt.ListMcpToolNamesAsync();
                await prompt.StartToolAsync("call-1", "codealta/alta", "other", new JsonObject { ["args"] = new JsonArray("ui", "activate") });
                var result = await prompt.CallMcpToolAsync("alta", new JsonObject { ["args"] = new JsonArray("ui", "activate") });
                await prompt.EndToolAsync("call-1", result!["content"]![0]!["text"]!.GetValue<string>());
                notified = [.. prompt.McpNotifications.Select(static message => message["method"]!.GetValue<string>())];
                after = await prompt.ListMcpToolNamesAsync();
                return "end_turn";
            },
        };
        var tools = new List<AgentToolDefinition>();
        tools.Add(Tool("alta", _ =>
        {
            tools.Add(Tool("take_screenshot", static _ => new AgentToolResult(true, [new AgentToolResultItem.Text("picture")])));
            return new AgentToolResult(true, [new AgentToolResultItem.Text("activated")]);
        }));
        await using var fixture = new Fixture(agent) { Tools = tools };

        var conversation = await fixture.RunAsync([User("look at the window")]);

        CollectionAssert.AreEqual(new[] { "notifications/tools/list_changed" }, notified);
        CollectionAssert.AreEquivalent(new[] { "alta", "take_screenshot" }, after);
        Assert.AreEqual("activated", ToolResult(conversation[2]));
    }

    [TestMethod]
    public async Task ToolOfTheAgent_IsShownByItsKind_WithTheResultTheAgentReports()
    {
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                await prompt.StartToolAsync("run-1", "Run dotnet --version", "execute", new JsonObject { ["command"] = "dotnet --version" });
                await prompt.EndToolAsync("run-1", "10.0.100");
                await prompt.StartToolAsync("edit-1", "Edit a.cs", "edit", new JsonObject { ["oldText"] = "a" }, new JsonArray(new JsonObject { ["path"] = "a.cs" }));
                await prompt.EndToolAsync("edit-1", "edited");
                return "end_turn";
            },
        };
        await using var fixture = new Fixture(agent);

        var conversation = await fixture.RunAsync([User("build")]);

        var shell = conversation[1].Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("shell", shell.Name);
        Assert.AreEqual("dotnet --version", shell.Arguments.GetProperty("command").GetString());
        Assert.AreEqual("Run dotnet --version", shell.Arguments.GetProperty("title").GetString());
        Assert.AreEqual("10.0.100", ToolResult(conversation[2]));
        var edit = conversation[3].Parts.OfType<AgentMessagePart.ToolCall>().Single();
        Assert.AreEqual("Edit", edit.Name);
        Assert.AreEqual(Path.Combine(fixture.WorkingDirectory, "a.cs"), edit.Arguments.GetProperty("file_path").GetString());
        Assert.AreEqual("a", edit.Arguments.GetProperty("oldText").GetString());
    }

    [TestMethod]
    public async Task PermissionRequest_IsAnsweredByTheUser_WithTheOptionOfTheDecision()
    {
        JsonNode? outcome = null;
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                await prompt.StartToolAsync("run-1", "Remove the build folder", "execute", new JsonObject { ["command"] = "rm -rf bin" });
                outcome = await prompt.RequestPermissionAsync("run-1", "Remove the build folder", "execute", new JsonObject { ["command"] = "rm -rf bin" });
                await prompt.EndToolAsync("run-1", "refused", success: false);
                return "end_turn";
            },
        };
        AgentPermissionRequest? asked = null;
        await using var fixture = new Fixture(agent)
        {
            OnPermission = (request, _) =>
            {
                asked = request;
                return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny));
            },
        };

        await fixture.RunAsync([User("clean")]);

        var command = (AgentCommandPermissionRequest)asked!;
        Assert.AreEqual("rm -rf bin", command.Command);
        Assert.AreEqual(fixture.WorkingDirectory, command.WorkingDirectory);
        Assert.AreEqual("selected", outcome!["outcome"]!["outcome"]!.GetValue<string>());
        Assert.AreEqual("no", outcome["outcome"]!["optionId"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task PermissionMode_AnswersWithoutAsking()
    {
        var outcomes = new List<string>();
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                outcomes.Add((await prompt.RequestPermissionAsync("e-1", "Edit a.cs", "edit"))!["outcome"]!["optionId"]!.GetValue<string>());
                outcomes.Add((await prompt.RequestPermissionAsync("r-1", "Read a.cs", "read"))!["outcome"]!["optionId"]!.GetValue<string>());
                return "end_turn";
            },
        };
        var asked = 0;
        await using var fixture = new Fixture(agent)
        {
            PermissionMode = "acceptEdits",
            OnPermission = (_, _) =>
            {
                asked++;
                return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny));
            },
        };

        await fixture.RunAsync([User("edit")]);

        CollectionAssert.AreEqual(new[] { "yes", "yes" }, outcomes);
        Assert.AreEqual(0, asked);
    }

    [TestMethod]
    public async Task LaterTurn_KeepsTheProcess_AndSendsOnlyTheNewMessage()
    {
        var agent = new AcpFakeAgent();
        await using var fixture = new Fixture(agent);
        var conversation = await fixture.RunAsync([User("one")]);

        conversation.Add(User("two"));
        await fixture.RunAsync(conversation);

        Assert.AreEqual(1, agent.Connections.Count);
        Assert.AreEqual(1, agent.RequestsOf("session/new").Count());
        var second = PromptText(agent.RequestsOf("session/prompt").Last());
        Assert.AreEqual("two", second);
    }

    [TestMethod]
    public async Task NewProcess_LoadsTheRecordedSession_WithoutItsReplay()
    {
        var agent = new AcpFakeAgent();
        List<AgentConversationMessage> conversation;
        AgentSessionState state;
        await using (var first = new Fixture(agent))
        {
            conversation = await first.RunAsync([User("one")]);
            state = first.State;
        }

        await using var fixture = new Fixture(agent) { State = state };
        conversation.Add(User("two"));
        conversation = await fixture.RunAsync(conversation);

        Assert.AreEqual(2, agent.Connections.Count);
        Assert.AreEqual("agent-session-1", agent.RequestsOf("session/load").Single()!["sessionId"]!.GetValue<string>());
        Assert.AreEqual(1, agent.RequestsOf("session/new").Count());
        Assert.AreEqual("ok", Text(conversation[^1]), "the replay of the loaded session is not part of the answer");
        Assert.AreEqual("two", PromptText(agent.RequestsOf("session/prompt").Last()));
    }

    [TestMethod]
    public async Task SessionReadAgainWithoutItsConversation_LoadsTheRecordedSession_WithoutReadingIt()
    {
        var agent = new AcpFakeAgent();
        List<AgentConversationMessage> conversation;
        AgentSessionState state;
        await using (var first = new Fixture(agent))
        {
            conversation = await first.RunAsync([User("one")]);
            state = first.State;
        }

        var loads = 0;
        var earlier = new AgentEarlierConversation(state, _ =>
        {
            loads++;
            return Task.FromResult<IReadOnlyList<AgentConversationMessage>>(conversation);
        });
        await using var fixture = new Fixture(agent) { State = state, Earlier = earlier };
        var resumed = await fixture.RunAsync([User("two")]);
        resumed.Add(User("three"));
        await fixture.RunAsync(resumed);

        Assert.AreEqual(0, loads, "The agent holds the conversation: it is not read.");
        Assert.AreEqual("agent-session-1", agent.RequestsOf("session/load").Single()!["sessionId"]!.GetValue<string>());
        Assert.AreEqual(1, agent.RequestsOf("session/new").Count());
        Assert.AreEqual("three", PromptText(agent.RequestsOf("session/prompt").Last()));
    }

    [TestMethod]
    public async Task AgentThatResumes_IsAskedToResume()
    {
        var agent = new AcpFakeAgent { ResumeSession = true };
        AgentSessionState state;
        List<AgentConversationMessage> conversation;
        await using (var first = new Fixture(agent))
        {
            conversation = await first.RunAsync([User("one")]);
            state = first.State;
        }

        await using var fixture = new Fixture(agent) { State = state };
        conversation.Add(User("two"));
        await fixture.RunAsync(conversation);

        Assert.AreEqual(1, agent.RequestsOf("session/resume").Count());
        Assert.AreEqual(0, agent.RequestsOf("session/load").Count());
    }

    [TestMethod]
    public async Task ConversationOfAnotherProvider_StartsANewSession_ThatIsToldWhatWasSaid()
    {
        var agent = new AcpFakeAgent();
        await using var fixture = new Fixture(agent);

        await fixture.RunAsync([
            User("what is in a.txt?"),
            new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("It holds one line.")]),
            User("and b.txt?"),
        ]);

        var prompt = PromptText(agent.RequestsOf("session/prompt").Single());
        StringAssert.Contains(prompt, HarnessHistory.PreviousConversationOpening);
        StringAssert.Contains(prompt, "It holds one line.");
        StringAssert.EndsWith(prompt, "and b.txt?");
    }

    [TestMethod]
    public async Task SessionContinuedByAnotherProvider_StartsANewSessionWhenItComesBack()
    {
        var agent = new AcpFakeAgent();
        await using var fixture = new Fixture(agent);
        var conversation = await fixture.RunAsync([User("one")]);
        // Another provider answers the next turn: the agent did not see it.
        conversation.Add(User("two"));
        conversation.Add(new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("answered by codex")]));
        conversation.Add(User("three"));

        await using var back = new Fixture(agent) { State = fixture.State };
        await back.RunAsync(conversation);

        Assert.AreEqual(2, agent.RequestsOf("session/new").Count(), "the recorded session lacks a turn: a new one is told the conversation");
        var prompt = PromptText(agent.RequestsOf("session/prompt").Last());
        StringAssert.Contains(prompt, "answered by codex");
        StringAssert.EndsWith(prompt, "three");
    }

    [TestMethod]
    public async Task ModelAndEffort_AreSetThroughTheConfigurationOptions()
    {
        var agent = new AcpFakeAgent();
        await using var fixture = new Fixture(agent) { ModelId = "smart", Effort = AgentReasoningEffort.Medium };

        await fixture.RunAsync([User("hello")]);

        var sets = agent.RequestsOf("session/set_config_option").ToArray();
        Assert.AreEqual("model", sets[0]!["configId"]!.GetValue<string>());
        Assert.AreEqual("smart", sets[0]!["value"]!.GetValue<string>());
        Assert.AreEqual("reasoning", sets[1]!["configId"]!.GetValue<string>());
        Assert.AreEqual("medium", sets[1]!["value"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UserMessageDuringATurn_IsTheNextPromptOfTheSameRun()
    {
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                if (prompt.Text.EndsWith("steer", StringComparison.Ordinal))
                {
                    await prompt.SendTextAsync("steered");
                    return "end_turn";
                }

                await prompt.StartToolAsync("run-1", "Run tests", "execute", new JsonObject { ["command"] = "dotnet test" });
                await prompt.EndToolAsync("run-1", "passed");
                await prompt.SendTextAsync("first");
                return "end_turn";
            },
        };
        await using var fixture = new Fixture(agent) { BeforeNextTurn = conversation => conversation.Add(User("steer")) };

        var conversation = await fixture.RunAsync([User("go")]);

        Assert.AreEqual(2, agent.RequestsOf("session/prompt").Count());
        Assert.AreEqual("steer", PromptText(agent.RequestsOf("session/prompt").Last()));
        Assert.AreEqual("steered", Text(conversation[^1]));
    }

    [TestMethod]
    public async Task StoppedTurn_CancelsThePromptOfTheAgent_AndTheNextTurnGoesOn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AcpFakeAgent
        {
            OnPrompt = async prompt =>
            {
                if (prompt.Text.EndsWith("again", StringComparison.Ordinal))
                {
                    await prompt.SendTextAsync("back");
                    return "end_turn";
                }

                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, prompt.CancellationToken);
                return "end_turn";
            },
        };
        await using var fixture = new Fixture(agent);
        using var cancellation = new CancellationTokenSource();
        var run = fixture.RunAsync([User("wait")], cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        Assert.AreEqual(1, agent.RequestsOf("session/cancel").Count());

        var conversation = await fixture.RunAsync([User("wait"), User("again")]);
        Assert.AreEqual("back", Text(conversation[^1]));
        Assert.AreEqual(1, agent.Connections.Count, "the agent stopped its prompt: its process is kept");
    }

    [TestMethod]
    public async Task AgentThatExits_FailsTheTurnAsUnavailable()
    {
        var agent = new AcpFakeAgent
        {
            OnPrompt = static async prompt =>
            {
                await prompt.SendTextAsync("partial");
                prompt.Connection.Exit();
                await Task.Delay(Timeout.Infinite, prompt.CancellationToken);
                return "end_turn";
            },
        };
        await using var fixture = new Fixture(agent);

        var error = await Assert.ThrowsExactlyAsync<AgentProviderFailureException>(() => fixture.RunAsync([User("hello")]));
        Assert.AreEqual(AgentFailureKind.Unavailable, error.Failure.Kind);
    }

    [TestMethod]
    public async Task AgentThatIsNotSignedIn_FailsTheTurnAsAuth()
    {
        var agent = new AcpFakeAgent { NewSessionError = -32000 };
        await using var fixture = new Fixture(agent);

        var error = await Assert.ThrowsExactlyAsync<AgentProviderFailureException>(() => fixture.RunAsync([User("hello")]));
        Assert.AreEqual(AgentFailureKind.Auth, error.Failure.Kind);
        StringAssert.Contains(error.Message, "is not signed in");
    }

    [TestMethod]
    public async Task Probe_ListsTheModels_AndWhatTheAgentCanDo_InASessionItCloses()
    {
        var agent = new AcpFakeAgent { McpHttp = false };
        await using var fixture = new Fixture(agent);

        var probe = await fixture.Runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability, probe.StatusMessage);
        CollectionAssert.AreEqual(new[] { "fast", "smart" }, probe.Models.Select(static model => model.Id).ToArray());
        Assert.AreEqual("FAST", probe.Models[0].DisplayName);
        CollectionAssert.AreEqual(new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High }, probe.Models[0].SupportedReasoningEfforts!.ToArray());
        Assert.IsFalse(probe.Capabilities.Single(static capability => capability.Id == "codealta-tools").Supported);
        Assert.IsTrue(probe.Capabilities.Single(static capability => capability.Id == "resume").Supported);
        Assert.AreEqual(1, agent.RequestsOf("session/close").Count());
        StringAssert.Contains(probe.StatusMessage, "Fake Agent 0.1.0");

        // The list is kept for the pickers.
        var models = await fixture.Runtime.ListModelsAsync(fixture.Runtime.RuntimeDescriptor);
        Assert.AreEqual(2, models.Count);
    }

    [TestMethod]
    public async Task AgentWithoutHttpMcp_RunsWithoutTheToolsOfCodeAlta_AndIsToldSo()
    {
        var agent = new AcpFakeAgent { McpHttp = false };
        await using var fixture = new Fixture(agent);

        await fixture.RunAsync([User("hello")]);

        Assert.AreEqual(0, agent.RequestsOf("session/new").Single()!["mcpServers"]!.AsArray().Count);
        StringAssert.Contains(PromptText(agent.RequestsOf("session/prompt").Single()), "are not available in this session");
    }

    [TestMethod]
    public void SignInMethods_GiveTheCommandOfATerminalSignIn()
    {
        var agent = new AcpAgentSettings("npx", ["--yes", "@scope/agent@1.0.0", "--acp"], new Dictionary<string, string>(), "agent", "1.0.0");
        var methods = AcpAccount.CreateMethods(agent,
        [
            JsonDocument.Parse("""{"id":"copilot-login","name":"Log in","_meta":{"terminal-auth":{"command":"C:\\copilot.exe","args":["login"],"label":"Copilot Login"}}}""").RootElement,
            JsonDocument.Parse("""{"id":"term","name":"Terminal","type":"terminal","args":["--login"],"env":{"A":"1"}}""").RootElement,
            JsonDocument.Parse("""{"id":"browser","name":"Browser"}""").RootElement,
        ]);

        CollectionAssert.AreEqual(new[] { "C:\\copilot.exe", "login" }, methods[0].TerminalCommand!.ToArray());
        Assert.AreEqual("Copilot Login", methods[0].Label);
        CollectionAssert.AreEqual(new[] { "npx", "--yes", "@scope/agent@1.0.0", "--login" }, methods[1].TerminalCommand!.ToArray());
        Assert.AreEqual("1", methods[1].TerminalEnvironment!["A"]);
        Assert.IsNull(methods[2].TerminalCommand);
    }

    private static AgentConversationMessage User(string text) => new(AgentConversationRole.User, [new AgentMessagePart.Text(text)]);

    private static string Text(AgentConversationMessage message) => string.Concat(message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));

    private static string ToolResult(AgentConversationMessage message)
        => string.Concat(message.Parts.OfType<AgentMessagePart.ToolResult>().Single().Result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));

    private static string PromptText(JsonNode? prompt)
        => string.Join("\n", prompt!["prompt"]!.AsArray().Select(static block => block?["text"]?.GetValue<string>()).Where(static text => text is not null)).ReplaceLineEndings("\n");

    private static JsonElement AcpProviderStateOf(AgentSessionState state) => state.ProviderState!.Value;

    private static AgentToolDefinition Tool(string name, Func<AgentToolInvocation, AgentToolResult> handler)
        => new(new AgentToolSpec(name, $"The tool {name}.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
            (invocation, _) => Task.FromResult(handler(invocation)));

    /// <summary>An ACP provider runtime on the fake agent, and a driver that runs turns as the agent session does.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly LoopbackHarnessToolEndpoints _endpoints = new();

        public Fixture(AcpFakeAgent agent)
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), nameof(AcpTurnExecutorTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorkingDirectory);
            var options = new AcpRuntimeOptions
            {
                ProviderKey = ProviderId.Value,
                AgentName = "Fake Agent",
                Agent = new AcpAgentSettings("dotnet", [], new Dictionary<string, string>(), null, null),
                ToolEndpoints = _endpoints,
                ProbeDirectory = Path.Combine(WorkingDirectory, ".probe"),
                ConnectionFactory = agent.CreateFactory(),
                CancelTimeout = TimeSpan.FromSeconds(5),
                RequestTimeout = TimeSpan.FromSeconds(20),
            };
            Runtime = new AcpModelProviderRuntime(
                new ModelProviderDescriptor(ProviderId, "Fake Agent", "acp"),
                options,
                new AcpModelCacheStore(Path.Combine(WorkingDirectory, ".models")));
            Executor = (AcpTurnExecutor)Runtime.CreateTurnExecutor();
        }

        public AcpModelProviderRuntime Runtime { get; }

        public AcpTurnExecutor Executor { get; }

        public string WorkingDirectory { get; }

        public AgentSessionState State { get; set; } = new() { SessionId = "session-1", UpdatedAt = DateTimeOffset.UtcNow };

        public AgentEarlierConversation? Earlier { get; init; }

        public IReadOnlyList<AgentToolDefinition> Tools { get; init; } = [];

        public string? ModelId { get; init; }

        public AgentReasoningEffort? Effort { get; init; }

        public string? PermissionMode { get; init; }

        public AgentPermissionRequestHandler OnPermission { get; init; } = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));

        public Action<List<AgentConversationMessage>>? BeforeNextTurn { get; init; }

        public async Task<List<AgentConversationMessage>> RunAsync(IReadOnlyList<AgentConversationMessage> start, CancellationToken cancellationToken = default)
        {
            var conversation = new List<AgentConversationMessage>(start);
            Executor.AttachRun(new AgentProviderRunContext("session-1", new AgentRunId("run-1"), OnPermission, null));
            var steered = false;
            while (true)
            {
                var response = await Executor.ExecuteTurnAsync(CreateRequest(conversation), static (_, _) => ValueTask.CompletedTask, cancellationToken);
                conversation.Add(response.AssistantMessage);
                if (response.ProviderState is { } providerState)
                {
                    State = State with { ProviderState = providerState };
                }

                var calls = response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().ToArray();
                foreach (var call in calls)
                {
                    var registered = Tools.FirstOrDefault(tool => tool.Spec.Name == call.Name);
                    var definition = Executor.ResolveTool("session-1", call, registered) ?? registered
                        ?? throw new InvalidOperationException($"No tool {call.Name}.");
                    var result = await definition.Handler(new AgentToolInvocation(ProviderId, "session-1", call.CallId, call.Name, call.Arguments), cancellationToken);
                    conversation.Add(new AgentConversationMessage(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult(call.CallId, result)]));
                }

                if (calls.Length > 0 && !steered && BeforeNextTurn is { } before)
                {
                    steered = true;
                    before(conversation);
                }

                if (calls.Length == 0 && !response.RequiresProviderFollowUp)
                {
                    return conversation;
                }
            }
        }

        private AgentTurnRequest CreateRequest(IReadOnlyList<AgentConversationMessage> conversation)
            => new()
            {
                Provider = Runtime.RuntimeDescriptor,
                ProviderId = ProviderId,
                SessionId = "session-1",
                RunId = new AgentRunId("run-1"),
                WorkingDirectory = WorkingDirectory,
                DeveloperInstructions = "Be brief.",
                ModelId = ModelId,
                ReasoningEffort = Effort,
                PermissionMode = PermissionMode,
                Conversation = [.. conversation],
                EarlierConversation = Earlier,
                Tools = Tools,
                State = State,
            };

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await _endpoints.DisposeAsync();
            try
            {
                Directory.Delete(WorkingDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
