using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;
using CodeAlta.LiveTool;
using CodeAlta.LiveTool.Scripts;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Tests;

// A script that exec runs inline, in the turn, and that waits for the user (an answer of ask(), the approval of a call):
// the turn keeps waiting for it however long the user takes, and exec gives its result in the same turn.
public sealed partial class AgentSessionTests
{
    // exec gives up on a script after 300 ms of its own time; the user takes 900 ms.
    // The yield leaves room for the engine to start and reach ask() on a loaded machine: that time is the script's own and
    // counts. The user then takes far longer than the yield, which must not count.
    private const int ShortYield = 1500;
    private const int UserTime = 4000;

    private static async Task<(AgentToolResult? Result, bool WaitedInTurn)> RunExecInTurnAsync(string code, ICodeModeQuestions? questions,
        Func<AgentPermissionRequest, CancellationToken, Task<AgentPermissionDecision>> permission, CodeModeRunRegistry runs, Task userAsked, TaskCompletionSource userAnswers)
    {
        using var temp = TestTempDirectory.Create();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-exec-wait") with { WorkingDirectory = workspace };
        await store.UpsertSessionAsync(summary);
        await store.UpsertStateAsync(CreateState(summary.SessionId));
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = temp.Path }).Add(registry));
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = workspace },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Questions = questions }).Single(static tool => tool.Spec.Name == "exec");
        using var arguments = new MemoryStream();
        using (var writer = new Utf8JsonWriter(arguments))
        {
            writer.WriteStartObject();
            writer.WriteString("code", $$"""
                // @exec: {"yield_ms": {{ShortYield}}}
                {{code}}
                """);
            writer.WriteEndObject();
        }

        AgentToolResult? execResult = null;
        await using var session = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, CreateState(summary.SessionId), [], store,
            new ScriptedTurnExecutor(
                (_, _, _) => Task.FromResult(new AgentTurnResponse
                {
                    AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.ToolCall("call-1", "exec", JsonDocument.Parse(arguments.ToArray()).RootElement.Clone())]),
                }),
                (request, _, _) =>
                {
                    execResult = request.Conversation.SelectMany(static message => message.Parts).OfType<AgentMessagePart.ToolResult>().Single().Result;
                    return Task.FromResult(new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]) });
                }),
            new AgentSessionCreateOptions { ProviderKey = provider.ProviderKey, Model = "gpt-5.4", WorkingDirectory = workspace, OnPermissionRequest = (request, token) => permission(request, token) });

        var turn = session.SendAsync(new() { Input = AgentInput.Text("Work"), AdditionalTools = [exec] });
        await userAsked.WaitAsync(TimeSpan.FromSeconds(10));
        // The user takes longer than the yield of exec: the turn still runs, and its exec still waits for the script.
        await Task.Delay(UserTime);
        var waitedInTurn = !turn.IsCompleted && execResult is null && runs.List(summary.SessionId).Single() is { Running: true } run && !runs.FindAny(run.Id)!.Backgrounded;
        userAnswers.SetResult();
        await turn.WaitAsync(TimeSpan.FromSeconds(20));
        return (execResult, waitedInTurn);
    }

    private static string Text(AgentToolResult result) => string.Join('\n', result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));

    [TestMethod]
    public async Task AScriptThatAsks_KeepsItsTurnWaiting_AndExecGivesItsAnswerInTheSameTurn()
    {
        await using var runs = new CodeModeRunRegistry(null);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var questions = new SlowUser(runs, asked, answers.Task);

        var (result, waitedInTurn) = await RunExecInTurnAsync("""return "answer: " + await ask("Go?");""", questions,
            static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)), runs, asked.Task, answers);

        Assert.IsTrue(waitedInTurn, "the question did not send the script to the background, nor end the turn");
        Assert.IsNotNull(result);
        StringAssert.Contains(Text(result), "\"answer: yes\"");
        Assert.IsFalse(Text(result).Contains("still running", StringComparison.OrdinalIgnoreCase), Text(result));
    }

    [TestMethod]
    public async Task AScriptWhoseCallWaitsForAnApproval_KeepsItsTurnWaiting_AndExecGivesItsResultInTheSameTurn()
    {
        await using var runs = new CodeModeRunRegistry(null);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The review of the host: a request of a script takes its turn among the questions of its run, then waits for the user.
        async Task<AgentPermissionDecision> Review(AgentPermissionRequest request, CancellationToken token)
        {
            using var wait = await runs.WaitForUserAsync(runs.FindByToolCall(request.SessionId, request.InteractionId)!, "approval", null, null, token);
            asked.TrySetResult();
            await answers.Task.WaitAsync(token);
            return new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);
        }

        var (result, waitedInTurn) = await RunExecInTurnAsync("""return await tools.shell_command({ command: "echo hi" }).then((r) => r.exitCode === 0 ? "ran" : "refused", () => "refused");""",
            null, Review, runs, asked.Task, answers);

        Assert.IsTrue(waitedInTurn, "the approval did not send the script to the background, nor end the turn");
        Assert.IsNotNull(result);
        StringAssert.Contains(Text(result), "\"refused\"");
    }

    // The user of the test: takes the turn of the run's questions, says it was asked, and answers "yes" when the test lets it.
    private sealed class SlowUser(CodeModeRunRegistry runs, TaskCompletionSource asked, Task answers) : ICodeModeQuestions
    {
        public async Task<CodeModeQuestionAnswer> AskAsync(CodeModeQuestionRequest request, CancellationToken cancellationToken)
        {
            using var wait = await runs.WaitForUserAsync(runs.Find(request.SessionId, request.RunId)!, "question", request.StepId, request.Question, cancellationToken);
            asked.TrySetResult();
            await answers.WaitAsync(cancellationToken);
            return new CodeModeQuestionAnswer(CodeModeQuestionOutcome.Answered, "yes", null);
        }
    }
}
