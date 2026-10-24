using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using CodeAlta.Plugin.Acp;

namespace CodeAlta.Tests;

/// <summary>
/// Runs a real ACP agent when <c>CODEALTA_ACP_LIVE_COMMAND</c> names one (for example <c>copilot --acp</c>); skipped
/// otherwise. It sends one short prompt, billed to the account of the agent.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class AcpLiveTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LiveAgent_ListsItsModels_AndCallsATool()
    {
        var commandLine = Environment.GetEnvironmentVariable("CODEALTA_ACP_LIVE_COMMAND");
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            Assert.Inconclusive("Set CODEALTA_ACP_LIVE_COMMAND to run an ACP agent, for example \"copilot --acp\".");
        }

        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var directory = Path.Combine(Path.GetTempPath(), nameof(AcpLiveTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var endpoints = new LoopbackHarnessToolEndpoints();
        var options = new AcpRuntimeOptions
        {
            ProviderKey = "live-acp",
            AgentName = "Live Agent",
            Agent = new AcpAgentSettings(parts[0], parts[1..], new Dictionary<string, string>(), null, null),
            ToolEndpoints = endpoints,
            ProbeDirectory = Path.Combine(directory, ".probe"),
        };
        await using var runtime = new AcpModelProviderRuntime(new ModelProviderDescriptor(new ModelProviderId("live-acp"), "Live Agent", "acp"), options,
            new AcpModelCacheStore(Path.Combine(directory, ".models")));

        var probe = await runtime.ProbeAsync();
        TestContext.WriteLine($"Probe: {probe.Availability} {probe.StatusMessage}");
        TestContext.WriteLine($"Models: {string.Join(", ", probe.Models.Select(static model => model.Id))}");
        TestContext.WriteLine($"Capabilities: {string.Join(", ", probe.Capabilities.Select(static capability => $"{capability.Id}={capability.Supported}"))}");
        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability, probe.StatusMessage);

        var model = Environment.GetEnvironmentVariable("CODEALTA_ACP_LIVE_MODEL") ?? probe.Models.Select(static model => model.Id).FirstOrDefault(static id => id.Contains("haiku", StringComparison.OrdinalIgnoreCase));
        var calls = 0;
        var alta = new AgentToolDefinition(
            new AgentToolSpec("alta", "The command gateway of CodeAlta. Pass the words of a command in args.",
                JsonDocument.Parse("""{"type":"object","properties":{"args":{"type":"array","items":{"type":"string"}}},"required":["args"]}""").RootElement.Clone()),
            (invocation, _) =>
            {
                calls++;
                TestContext.WriteLine($"alta called with {invocation.Arguments.GetRawText()}");
                return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("session-42 Live test session")]));
            });
        var executor = (AcpTurnExecutor)runtime.CreateTurnExecutor();
        executor.AttachRun(new AgentProviderRunContext("live-session", new AgentRunId("live-run"), static (request, _) =>
            Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)), null));
        var conversation = new List<AgentConversationMessage>
        {
            new(AgentConversationRole.User, [new AgentMessagePart.Text("Call the `alta` tool of the `codealta` MCP server with args [\"session\", \"list\"], then reply with exactly the text it returned, nothing else.")]),
        };
        var state = new AgentSessionState { SessionId = "live-session", UpdatedAt = DateTimeOffset.UtcNow };
        for (var turn = 0; turn < 8; turn++)
        {
            var response = await executor.ExecuteTurnAsync(
                new AgentTurnRequest
                {
                    Provider = runtime.RuntimeDescriptor,
                    ProviderId = new ModelProviderId("live-acp"),
                    SessionId = "live-session",
                    RunId = new AgentRunId("live-run"),
                    WorkingDirectory = directory,
                    DeveloperInstructions = "You are tested by CodeAlta. Be brief.",
                    ModelId = model,
                    Conversation = [.. conversation],
                    Tools = [alta],
                    State = state,
                },
                (delta, _) => ValueTask.CompletedTask);
            conversation.Add(response.AssistantMessage);
            state = state with { ProviderState = response.ProviderState ?? state.ProviderState };
            var toolCalls = response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().ToArray();
            foreach (var part in response.AssistantMessage.Parts)
            {
                TestContext.WriteLine(part switch
                {
                    AgentMessagePart.Text text => $"text: {text.Value}",
                    AgentMessagePart.Reasoning reasoning => $"reasoning: {reasoning.Value}",
                    AgentMessagePart.ToolCall call => $"tool call: {call.Name} {call.Arguments.GetRawText()}",
                    _ => part.GetType().Name,
                });
            }

            foreach (var call in toolCalls)
            {
                var definition = executor.ResolveTool("live-session", call, call.Name == "alta" ? alta : null)!;
                var result = await definition.Handler(new AgentToolInvocation(new ModelProviderId("live-acp"), "live-session", call.CallId, call.Name, call.Arguments), CancellationToken.None);
                TestContext.WriteLine($"tool result: {HarnessHistory.RenderToolResult(result)}");
                conversation.Add(new AgentConversationMessage(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult(call.CallId, result)]));
            }

            if (toolCalls.Length == 0 && !response.RequiresProviderFollowUp)
            {
                TestContext.WriteLine($"usage: {response.Usage?.LastOperation?.InputTokens} in, {response.Usage?.LastOperation?.OutputTokens} out, window {response.Usage?.Window?.CurrentTokens}/{response.Usage?.Window?.TokenLimit}");
                break;
            }
        }

        Assert.AreEqual(1, calls, "the agent called alta once through the MCP server of the session");
        StringAssert.Contains(string.Concat(conversation[^1].Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value)), "session-42");
    }
}
