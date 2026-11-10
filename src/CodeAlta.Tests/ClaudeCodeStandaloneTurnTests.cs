using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// Runs turns that belong to no session (a side question) on the Claude Code provider, against the scripted CLI: what
/// the process is started with, what it is refused, and that it ends with the turn.
/// </summary>
[TestClass]
public sealed class ClaudeCodeStandaloneTurnTests
{
    private const string SystemMessage = "You answer a side question from the transcript.";

    [TestMethod]
    public async Task Turn_StreamsTheAnswer_FromACliWithoutToolsNorTranscript()
    {
        var cli = new ClaudeCodeFakeCli
        {
            OnUserMessage = static (process, message) =>
            {
                process.EmitInit();
                process.EmitTextStream("msg_1", 0, "The build ", "failed.");
                process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.TextBlock("The build failed.")));
                process.EmitMessageStop();
                process.EmitResult("The build failed.", message);
                return Task.CompletedTask;
            },
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        var deltas = new List<AgentTurnDelta>();

        var response = await ExecuteAsync(executor, CreateRequest() with
        {
            ModelId = "opus",
            ModelInfo = new AgentModelInfo("opus", SupportedReasoningEfforts: [AgentReasoningEffort.High]),
            ReasoningEffort = AgentReasoningEffort.High,
        }, deltas);

        Assert.AreEqual("The build failed.", Text(response));
        CollectionAssert.AreEqual(new[] { "The build ", "failed." }, deltas.Select(static delta => delta.Text).ToArray());
        Assert.IsTrue(deltas.TrueForAll(static delta => delta.Kind == AgentContentKind.Assistant && delta.ContentId == "claude:msg_1:0"));
        Assert.IsNull(response.ProviderSessionId, "Nothing names a conversation to resume.");
        Assert.IsNull(response.ProviderState);

        var process = cli.Processes.Single();
        Assert.IsTrue(process.IsDisposed, "The process ends with the turn.");
        var arguments = process.Launch.Arguments.ToList();
        CollectionAssert.IsSubsetOf(new[] { "--model", "opus", "--effort", "high", "--no-session-persistence", "--strict-mcp-config", "--disable-slash-commands" }, arguments);
        Assert.AreEqual(string.Empty, arguments[arguments.IndexOf("--tools") + 1], "No built-in tool.");
        Assert.AreEqual("{\"disableAllHooks\":true}", arguments[arguments.IndexOf("--settings") + 1]);
        Assert.AreEqual(SystemMessage, arguments[arguments.IndexOf("--system-prompt") + 1]);
        Assert.AreEqual("default", arguments[arguments.IndexOf("--permission-mode") + 1]);
        Assert.IsFalse(arguments.Exists(static argument =>
            argument is "--mcp-config" || argument.StartsWith("--allowedTools", StringComparison.Ordinal) ||
            argument.StartsWith("--session-id", StringComparison.Ordinal) || argument.StartsWith("--resume", StringComparison.Ordinal)));
        Assert.AreEqual("1", process.Launch.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"]);
        Assert.AreEqual("1", process.Launch.Environment["CLAUDE_CODE_DISABLE_CLAUDE_MDS"]);
        Assert.AreEqual(Path.GetTempPath(), process.Launch.WorkingDirectory);

        var initialize = process.InitializeRequest;
        Assert.IsFalse(initialize.TryGetProperty("sdkMcpServers", out _), "The tools of CodeAlta are not offered.");
        Assert.IsFalse(initialize.TryGetProperty("hooks", out _));
        Assert.AreEqual("Summarize the transcript.", ClaudeCodeFakeProcess.UserText(process.UserMessages.Single()));
    }

    [TestMethod]
    public async Task Turn_GivesNoEffort_ToAModelThatDoesNotListIt()
    {
        var cli = new ClaudeCodeFakeCli();
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());

        await ExecuteAsync(executor, CreateRequest() with
        {
            ModelId = "haiku",
            ModelInfo = new AgentModelInfo("haiku"),
            ReasoningEffort = AgentReasoningEffort.High,
        });

        Assert.IsFalse(cli.Last.Launch.Arguments.Contains("--effort"));
    }

    [TestMethod]
    public async Task RequestsOfTheCli_AreRefused()
    {
        JsonElement permission = default;
        JsonElement mcp = default;
        var cli = new ClaudeCodeFakeCli
        {
            OnUserMessage = async (process, message) =>
            {
                permission = await process.AskPermissionAsync("Write", new JsonObject { ["file_path"] = "a.txt", ["content"] = "x" }, "toolu_1");
                mcp = await process.RequestAsync(new JsonObject { ["subtype"] = "mcp_message", ["server_name"] = "codealta", ["message"] = new JsonObject() });
                process.EmitTextTurn("msg_1", "ok", message);
            },
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());

        var response = await ExecuteAsync(executor, CreateRequest());

        Assert.AreEqual("ok", Text(response));
        Assert.AreEqual("deny", permission.GetProperty("behavior").GetString());
        Assert.AreEqual("error", mcp.GetProperty("subtype").GetString());
    }

    [TestMethod]
    public async Task CancelledTurn_InterruptsAndStopsTheProcess()
    {
        var cli = new ClaudeCodeFakeCli
        {
            OnUserMessage = static (process, _) =>
            {
                process.EmitInit();
                process.EmitTextStream("msg_1", 0, "The answer goes on");
                return Task.CompletedTask;
            },
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        using var cancellation = new CancellationTokenSource();

        var turn = ((IAgentProviderStandaloneTurns)executor).ExecuteStandaloneTurnAsync(CreateRequest(), (_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        }, cancellation.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await turn.WithinAsync(TestTime.Seconds(20)));
        Assert.IsTrue(cli.Last.Interrupted.IsCompleted, "The request to the model is stopped.");
        Assert.IsTrue(cli.Last.IsDisposed, "The process is stopped.");
    }

    [TestMethod]
    public async Task FailedTurn_ThrowsTheFailureOfTheCli_AndStopsTheProcess()
    {
        var cli = new ClaudeCodeFakeCli
        {
            OnUserMessage = static (process, message) =>
            {
                process.EmitInit();
                process.EmitAssistant("msg_1", new JsonArray(ClaudeCodeFakeProcess.TextBlock("Rate limited")), error: "rate_limit");
                process.EmitResult(null, message, isError: true, subtype: "error_during_execution");
                return Task.CompletedTask;
            },
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());

        var failure = await Assert.ThrowsExactlyAsync<AgentProviderFailureException>(() => ExecuteAsync(executor, CreateRequest()));

        StringAssert.Contains(failure.Message, "Rate limited");
        Assert.AreEqual(AgentFailureKind.RateLimited, failure.Failure.Kind);
        Assert.IsTrue(cli.Last.IsDisposed);
    }

    [TestMethod]
    public async Task ProcessThatExitsBeforeItAnswers_FailsTheTurn()
    {
        var cli = new ClaudeCodeFakeCli
        {
            OnUserMessage = static (process, _) =>
            {
                process.Exit(3);
                return Task.CompletedTask;
            },
        };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ExecuteAsync(executor, CreateRequest()));

        StringAssert.Contains(failure.Message, "exited with code 3");
    }

    [TestMethod]
    public async Task ProcessOfTheTurn_IsCodeAltasOwn_WhileItRuns()
    {
        var pid = 0;
        IReadOnlyList<int>? whileRunning = null;
        var cli = new ClaudeCodeFakeCli { AssignProcessId = () => Interlocked.Increment(ref pid) + 4000 };
        await using var executor = new ClaudeCodeTurnExecutor(cli.CreateOptions());
        cli.OnUserMessage = (process, message) =>
        {
            whileRunning = executor.OwnProcesses.Snapshot();
            process.EmitTextTurn("msg_1", "ok", message);
            return Task.CompletedTask;
        };

        await ExecuteAsync(executor, CreateRequest());

        CollectionAssert.AreEqual(new[] { 4001 }, whileRunning!.ToArray(), "Its entry in the registry of live sessions is not another holder.");
        Assert.AreEqual(0, executor.OwnProcesses.Snapshot().Count);
    }

    private static async Task<AgentTurnResponse> ExecuteAsync(ClaudeCodeTurnExecutor executor, AgentTurnRequest request, List<AgentTurnDelta>? deltas = null)
        => await ((IAgentProviderStandaloneTurns)executor).ExecuteStandaloneTurnAsync(request, (delta, _) =>
        {
            deltas?.Add(delta);
            return ValueTask.CompletedTask;
        }, CancellationToken.None).WithinAsync(TestTime.Seconds(20));

    private static string Text(AgentTurnResponse response)
        => string.Concat(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));

    private static AgentTurnRequest CreateRequest()
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
            SessionId = "side-question:1",
            RunId = new AgentRunId("side-question:1"),
            WorkingDirectory = Path.GetTempPath(),
            SystemMessage = SystemMessage,
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Summarize the transcript.")])],
            Tools = [],
            IsStandalone = true,
            State = new AgentSessionState { SessionId = "side-question:1", UpdatedAt = DateTimeOffset.UtcNow },
        };
}
