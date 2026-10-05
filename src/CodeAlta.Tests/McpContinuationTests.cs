using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

public sealed partial class McpRuntimeServiceTests
{
    [TestMethod]
    [DataRow("continue")]
    [DataRow("plain")]
    [DataRow("stop")]
    [DataRow("failure")]
    [DataRow("discovery-failure")]
    [DataRow("queue")]
    [DataRow("followup-stop")]
    public async Task ActivateContinuation_PreparesFreshRunAndCallsDirectTool_WithoutHumanMessage(string scenario)
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        if (scenario == "discovery-failure")
            File.WriteAllText(Path.Combine(project.Path, ".alta", "mcp.json"),
                """{"mcpServers":{"tiny":{"command":"__missing_codealta_continuation_server__"}}}""");
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var provider = new ContinuationProvider(scenario);
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = Path.Combine(_home.Path, ".alta"),
            CurrentProjectPath = project.Path,
            IsHeadless = true,
            // Exercise the per-send permission lifecycle on both runs, not just auto-approve defaults.
            AutoApproveOwnedPermissions = false,
            EnableOwnedAsks = true,
            EnableOwnedUserInput = true,
            OwnedCommandReceiptCapacity = 16,
            PluginBuiltIns = [new BuiltInPluginDefinition { Id = "mcp", DisplayName = "MCP", PluginType = typeof(McpPlugin), Factory = () => plugin }],
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
        var outputs = new List<string>();
        AgentRunContinuation? retained = null;
        host.Commands.SessionTools = _ =>
        [
            new(new AgentToolSpec("activate", "Activate test MCP.", JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })),
                async (invocation, token) =>
                {
                    retained = invocation.Continuation;
                    var stdout = new StringWriter(CultureInfo.InvariantCulture);
                    var stderr = new StringWriter(CultureInfo.InvariantCulture);
                    var context = CreateAltaContext(stdout, stderr, project.Path, invocation.SessionId) with
                    {
                        Continuation = invocation.Continuation,
                        Stdin = new StringReader("Call the newly activated echo tool."),
                        CancellationToken = token,
                    };
                    var app = new CommandApp("alta", "test") { plugin.GetAltaCommands().Single().CreateCommandNode(context) };
                    string[] args = scenario == "plain" ? ["mcp", "activate", "tiny"] : ["mcp", "activate", "tiny", "--continue", "--stdin"];
                    var code = await app.RunAsync(args, new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
                    outputs.Add(stdout.ToString());
                    Assert.AreEqual(scenario == "discovery-failure" ? 1 : 0, code, stderr.ToString());
                    if (scenario == "continue")
                    {
                        Assert.IsTrue(retained!.TryRequest("Call the newly activated echo tool."), "Identical retry coalesces.");
                        Assert.IsFalse(retained.TryRequest("Different pending continuation"));
                        Assert.IsNull(retained.Take(), "Cannot consume before the current run settles.");
                    }
                    return new AgentToolResult(code == 0, [new AgentToolResultItem.Text(stdout.ToString())]);
                }),
            new(new AgentToolSpec("probe", "Inspect follow-up capability.", JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })),
                (invocation, _) =>
                {
                    provider.FollowUpCapabilityAbsent = invocation.Continuation is null;
                    return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("probed")]));
                }),
        ];
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Continuation");
        try
        {
            var receipt = host.Commands.AdmitSend(new("original", session.SessionId, "Activate tiny then use echo")).Receipt!;
            OwnedSessionCommandReceipt? queued = null;
            long? queuedAttachment = null;
            if (scenario == "queue")
            {
                await provider.AfterActivation.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var state = await host.RuntimeService.GetCurrentStateAsync(session.SessionId);
                queuedAttachment = state.Entry!.AttachmentGeneration;
                queued = host.Commands.AdmitQueue(new("queue", session.SessionId, state.RuntimeInstanceId, queuedAttachment.Value, "Existing queued work")).Receipt!;
                Assert.IsTrue((await queued.QueueInsertion!.WaitAsync(TimeSpan.FromSeconds(20))).Accepted);
                provider.AllowFinish.TrySetResult();
            }
            if (scenario is "stop" or "followup-stop")
            {
                await (scenario == "stop" ? provider.AfterActivation : provider.AfterFollowUp).Task.WaitAsync(TimeSpan.FromSeconds(20));
                var abort = host.Commands.AdmitAbort(new("stop", receipt.OperationId)).Receipt!;
                await abort.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            }
            var result = await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            if (queued is not null)
            {
                provider.AllowQueueFinish.TrySetResult();
                Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await queued.Completion.WaitAsync(TimeSpan.FromSeconds(20))).Outcome);
                var state = await host.RuntimeService.GetCurrentStateAsync(session.SessionId);
                Assert.AreEqual(queuedAttachment, state.Entry!.AttachmentGeneration, "Continuation must not replace an exact-bound queue attachment.");
            }
            Assert.AreEqual(scenario is "failure" or "queue" ? OwnedSessionCommandOutcome.Failed : scenario is "stop" or "followup-stop"
                ? OwnedSessionCommandOutcome.Cancelled : OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
            var runs = provider.Requests.Select(request => request.RunId).Distinct().ToArray();
            Assert.AreEqual(scenario is "continue" or "queue" or "followup-stop" ? 2 : 1, runs.Length);
            Assert.IsTrue(provider.Requests.All(request => request.SessionId == session.SessionId));
            var activation = JsonDocument.Parse(outputs.Single().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
            using (activation)
            {
                Assert.AreEqual(scenario is not ("plain" or "discovery-failure"), activation.RootElement.GetProperty("continuationQueued").GetBoolean());
                Assert.AreEqual(scenario is not ("plain" or "discovery-failure"), activation.RootElement.GetProperty("shouldYield").GetBoolean());
                Assert.IsFalse(activation.RootElement.GetProperty("shouldPoll").GetBoolean());
                Assert.AreEqual(session.SessionId, activation.RootElement.GetProperty("sessionId").GetString());
                Assert.AreEqual(scenario != "discovery-failure", activation.RootElement.GetProperty("discoverySucceeded").GetBoolean());
            }
            Assert.IsNotNull(retained);
            Assert.IsFalse(retained.TryRequest("Stale run must not start work"));
            if (scenario == "continue")
            {
                Assert.IsNull(retained.Take(), "The host consumed the request only once.");
                var next = provider.Requests.First(request => request.RunId == runs[1]);
                Assert.IsTrue(next.Tools.Any(tool => tool.Spec.Name == "mcp__tiny__echo"));
                StringAssert.Contains(next.DeveloperInstructions!, "- Active: `tiny`");
                Assert.IsTrue(provider.Requests.Where(request => request.RunId == runs[0]).All(request => request.Tools.All(tool => tool.Spec.Name != "mcp__tiny__echo")));
                var history = JsonSerializer.Serialize(provider.Requests[^1].Conversation);
                StringAssert.Contains(history, "echo:continued");
                Assert.IsTrue(provider.FollowUpCapabilityAbsent);
            }
        }
        finally
        {
            // Release every fixture gate before host disposal, including when an assertion fails.
            provider.AllowFinish.TrySetResult();
            provider.AllowQueueFinish.TrySetResult();
        }
    }

    [TestMethod]
    [DataRow(false, "prompt", "continuation_unavailable")]
    [DataRow(true, "", "invalid_continuation")]
    [DataRow(true, "oversized", "invalid_continuation")]
    [DataRow(true, "prompt", "continuation_rejected")]
    public async Task ActivateContinuation_ReportsUnavailableInvalidAndRejectedAdmission(bool capability, string text, string error)
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = CreateAltaContext(stdout, stderr, project.Path, "source") with
        {
            Continuation = capability ? new AgentRunContinuation() : null,
            Stdin = new StringReader(text == "oversized" ? new string('x', 8193) : text),
        };
        var app = new CommandApp("alta", "test") { plugin.GetAltaCommands().Single().CreateCommandNode(context) };
        Assert.AreNotEqual(0, await app.RunAsync(["mcp", "activate", "tiny", "--continue", "--stdin"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr }));
        StringAssert.Contains(stdout.ToString(), error);
        var next = await plugin.OnBeforeAgentRunAsync(new PluginBeforeAgentRunContext
        {
            Plugin = CreatePluginDescriptor(), Services = NoopPluginServices.Create(), ProjectPath = project.Path, SessionId = "source",
        });
        if (error == "continuation_rejected")
        {
            Assert.IsNotNull(next, "Activation succeeds independently of continuation admission.");
            using var record = JsonDocument.Parse(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
            Assert.IsTrue(record.RootElement.GetProperty("discoverySucceeded").GetBoolean());
            Assert.IsFalse(record.RootElement.GetProperty("continuationQueued").GetBoolean());
        }
        else Assert.IsNull(next, "Invalid requests must not change activation state.");
    }

    private sealed class ContinuationProvider(string scenario) : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public List<AgentTurnRequest> Requests { get; } = [];
        public TaskCompletionSource AfterActivation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AfterFollowUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowQueueFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FollowUpCapabilityAbsent { get; set; }
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("continuation-fixture"), "Continuation fixture") { DefaultModelId = "fixture" };
        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "continuation-fixture", DisplayName = "Continuation fixture", TransportKind = AgentTransportKind.OpenAIResponses,
        };
        public IModelProviderModelCatalog? ModelCatalog => null;
        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };
        public IModelProviderTurnExecutor CreateTurnExecutor() => this;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });
        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            AgentMessagePart part = new AgentMessagePart.Text("done");
            if (Requests.Count == 1) part = new AgentMessagePart.ToolCall("activate-call", "activate", JsonSerializer.SerializeToElement(new { }));
            if (Requests.Count == 2)
            {
                AfterActivation.TrySetResult();
                if (scenario == "stop") await Task.Delay(Timeout.Infinite, cancellationToken);
                if (scenario == "queue") await AllowFinish.Task.WaitAsync(cancellationToken);
                if (scenario == "failure") throw new InvalidOperationException("failed after activation");
            }
            if (Requests.Count == 3)
            {
                AfterFollowUp.TrySetResult();
                if (scenario == "followup-stop") await Task.Delay(Timeout.Infinite, cancellationToken);
                if (scenario == "queue")
                {
                    Assert.IsFalse(request.Tools.Any(tool => tool.Spec.Name == "mcp__tiny__echo"));
                    await AllowQueueFinish.Task.WaitAsync(cancellationToken);
                    return new AgentTurnResponse { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [part]) };
                }
                part = new AgentMessagePart.ToolCall("probe-call", "probe", JsonSerializer.SerializeToElement(new { }));
            }
            if (Requests.Count == 4)
            {
                var echo = request.Tools.Single(tool => tool.Spec.Name == "mcp__tiny__echo");
                // The tool itself will be dispatched by AgentSession, not manually by this executor.
                part = new AgentMessagePart.ToolCall("echo-call", echo.Spec.Name, JsonSerializer.SerializeToElement(new { text = "continued" }));
            }
            return new AgentTurnResponse { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [part]) };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
