using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A headless run (<c>altacli run</c>) whose turn started a script that went on in the background: the run is over once the
/// script ended and the session answered its result, not when the turn that started it ended.
/// </summary>
[TestClass]
public sealed class CodeModeHeadlessRunTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    [TestMethod]
    public async Task AHeadlessRun_DeliversAScriptResult_WhenItsNestedCallEndsAfterIdle()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, headlessInstructions: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new TaskCompletionSource<AgentRunId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nestedOutput = new TaskCompletionSource<AgentRunId?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? runId = null;
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        var schemaElement = schema.RootElement.Clone();
        // The tool of the test does what exec does at its yield: the script goes on in the background, and the turn goes on.
        host.Commands.SessionTools = _ =>
        [
            new AgentToolDefinition(new AgentToolSpec("nested", "Answers after the turn ends.", schemaElement), async (_, token) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return new AgentToolResult(true, [new AgentToolResultItem.Text("nested answer")]);
            }),
            new AgentToolDefinition(new AgentToolSpec("start_script", "Starts a script.", schemaElement) { Exposure = AgentToolExposure.Model }, async (invocation, token) =>
            {
                var run = host.CodeModeRuns.TryStart(invocation.SessionId, "Slow", 1000, invocation.ToolCallId, async (_, scriptToken) =>
                {
                    await invocation.Tools!.InvokeAsync(new AgentNestedToolCall("nested", invocation.Arguments), scriptToken);
                    // The runtime has observed the late activity before the script gives its result.
                    await nestedOutput.Task.WaitAsync(scriptToken);
                    return new CodeModeRunResult(CodeModeRunStatus.Completed, TimeSpan.FromSeconds(1)) { ValueJson = "42" };
                }, out var refusal);
                if (run is null) return new AgentToolResult(false, [new AgentToolResultItem.Text(refusal!)], refusal);
                await entered.Task.WaitAsync(token);
                await host.CodeModeRuns.BackgroundAsync(run);
                runId = run.Id;
                return new AgentToolResult(true, [new AgentToolResultItem.Text($"Script {run.Id} goes on in the background.")]);
            }),
        ];
        await using var driver = new HeadlessSessionDriver(host, new HeadlessSessionDriverOptions
        {
            Observer = (item, _) =>
            {
                if (item.Event is SessionAgentEvent { Event: AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle, RunId: { } turn } })
                    idle.TrySetResult(turn);
                if (item.Event is SessionAgentEvent { Event: AgentContentCompletedEvent { Kind: AgentContentKind.ToolOutput } output }
                    && output.Content.Contains("nested answer", StringComparison.Ordinal))
                    nestedOutput.TrySetResult(output.RunId);
                return ValueTask.CompletedTask;
            },
        });
        var opened = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = root.ProjectRoot, ModelRef = "scripted:scripted-model" });
        Assert.AreEqual(HeadlessOpenStatus.Created, opened.Status, opened.Message);
        var session = opened.Session!;
        provider.Script(session.SessionId,
            ScriptedTurnProvider.CallTool("start_script", "{}"),
            ScriptedTurnProvider.Reply("Started it."),
            ScriptedTurnProvider.Reply("The script said 42."));

        var running = driver.RunAsync(session, new HeadlessPrompt("Go."));
        var startingTurn = await idle.Task.WithinAsync(Hang);
        Assert.IsFalse(running.IsCompleted, "the run waits for the script that went on in the background");
        Assert.IsNotNull(runId);
        release.SetResult();
        Assert.AreEqual(startingTurn, await nestedOutput.Task.WithinAsync(Hang), "the journal event still belongs to its original turn");
        var result = await running.WithinAsync(Hang);

        Assert.AreEqual(HeadlessRunStatus.Completed, result.Status, result.FailureMessage);
        var requests = provider.RequestsOf(session.SessionId);
        Assert.AreEqual(3, requests.Length, "the result of the script came back as a prompt, and the session answered it before the run ended");
        StringAssert.Contains(JsonSerializer.Serialize(requests[2].Conversation), $"Script {runId}");
        Assert.AreEqual("The script said 42.", result.FinalMessage);
    }
}
