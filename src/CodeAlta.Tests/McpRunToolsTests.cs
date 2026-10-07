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
    public async Task Activate_InsideARun_RegistersTheToolsForTheNextStepOfThatRun()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var provider = new ActivatingProvider();
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = Path.Combine(_home.Path, ".alta"),
            CurrentProjectPath = project.Path,
            IsHeadless = true,
            OwnedCommandReceiptCapacity = 16,
            PluginBuiltIns = [new BuiltInPluginDefinition { Id = "mcp", DisplayName = "MCP", PluginType = typeof(McpPlugin), Factory = () => plugin }],
            ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
        }, CancellationToken.None);
        var activations = new List<string>();
        host.Commands.SessionTools = _ =>
        [
            // What `alta mcp activate tiny` does when the model calls it: the command gets the tools of the calling run.
            new(new AgentToolSpec("activate", "Activate the test MCP server.", JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })),
                async (invocation, token) =>
                {
                    Assert.IsNotNull(invocation.RunTools, "A run gives its tool calls the tools of the run.");
                    var stdout = new StringWriter(CultureInfo.InvariantCulture);
                    var stderr = new StringWriter(CultureInfo.InvariantCulture);
                    var context = CreateAltaContext(stdout, stderr, project.Path, invocation.SessionId) with { RunTools = invocation.RunTools, CancellationToken = token };
                    var app = new CommandApp("alta", "test") { plugin.GetAltaCommands().Single().CreateCommandNode(context) };
                    var code = await app.RunAsync(["mcp", "activate", "tiny"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
                    Assert.AreEqual(0, code, stderr.ToString());
                    activations.Add(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
                    return new AgentToolResult(true, [new AgentToolResultItem.Text(stdout.ToString())]);
                }),
        ];
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Run tools");

        var first = await host.Commands.AdmitSend(new("first", session.SessionId, "Activate tiny, then use echo")).Receipt!.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, first.Outcome, first.Code);
        // One run, three model requests: the activation, the call of the new tool, the answer.
        Assert.AreEqual(3, provider.Requests.Count);
        Assert.AreEqual(1, provider.Requests.Select(request => request.RunId).Distinct().Count(), "No second run and no message from the user were needed.");
        Assert.IsFalse(provider.Requests[0].Tools.Any(tool => tool.Spec.Name == "mcp__tiny__echo"), "The server is not active when the run starts.");
        Assert.IsTrue(provider.Requests[1].Tools.Any(tool => tool.Spec.Name == "mcp__tiny__echo"), "The step after the activation offers the tool.");
        // The tools the run started with keep their place: only the new ones are appended.
        CollectionAssert.AreEqual(
            provider.Requests[0].Tools.Select(tool => tool.Spec.Name).ToArray(),
            provider.Requests[1].Tools.Take(provider.Requests[0].Tools.Count).Select(tool => tool.Spec.Name).ToArray());
        StringAssert.Contains(JsonSerializer.Serialize(provider.Requests[2].Conversation), "echo:same turn", "The agent session ran the tool the model called.");
        using (var activation = JsonDocument.Parse(activations.Single()))
        {
            Assert.AreEqual("now", activation.RootElement.GetProperty("toolsAvailable").GetString());
            Assert.AreEqual(activation.RootElement.GetProperty("activeToolCount").GetInt32(), activation.RootElement.GetProperty("registeredToolCount").GetInt32());
            Assert.IsTrue(activation.RootElement.GetProperty("registeredToolCount").GetInt32() > 0);
            Assert.IsFalse(activation.RootElement.GetProperty("nextTurnRequired").GetBoolean());
            StringAssert.Contains(activation.RootElement.GetProperty("note").GetString()!, "in your next step");
        }

        // The activation outlives the run: the next one has the tool from its first request.
        var second = await host.Commands.AdmitSend(new("second", session.SessionId, "Use echo again")).Receipt!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, second.Outcome, second.Code);
        Assert.AreEqual(1, provider.Requests[3].Tools.Count(tool => tool.Spec.Name == "mcp__tiny__echo"));
        Assert.AreNotEqual(provider.Requests[0].RunId, provider.Requests[3].RunId);
    }

    [TestMethod]
    public async Task Activate_WithTheToolsOfARun_RegistersEachToolOnce()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var runTools = new AgentRunTools();

        async Task<JsonDocument> ActivateAsync()
        {
            var stdout = new StringWriter(CultureInfo.InvariantCulture);
            var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var context = CreateAltaContext(stdout, stderr, project.Path, "source") with { RunTools = runTools };
            var app = new CommandApp("alta", "test") { plugin.GetAltaCommands().Single().CreateCommandNode(context) };
            Assert.AreEqual(0, await app.RunAsync(["mcp", "activate", "tiny"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr }), stderr.ToString());
            return JsonDocument.Parse(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        }

        using (var first = await ActivateAsync())
        {
            Assert.AreEqual("now", first.RootElement.GetProperty("toolsAvailable").GetString());
            Assert.AreEqual(first.RootElement.GetProperty("activeToolCount").GetInt32(), first.RootElement.GetProperty("registeredToolCount").GetInt32());
            Assert.IsTrue(first.RootElement.GetProperty("registeredToolCount").GetInt32() > 0);
        }
        Assert.IsTrue(runTools.Contains("mcp__tiny__echo"));

        // Activating again changes nothing for the run, and still says the tools are there.
        using var again = await ActivateAsync();
        Assert.AreEqual("now", again.RootElement.GetProperty("toolsAvailable").GetString());
        Assert.AreEqual(0, again.RootElement.GetProperty("registeredToolCount").GetInt32());
        Assert.IsFalse(again.RootElement.GetProperty("nextTurnRequired").GetBoolean());
    }

    [TestMethod]
    public void RunTools_AddEachNameOnce()
    {
        static AgentToolDefinition Tool(string name) => new(
            new AgentToolSpec(name, name, JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })),
            (_, _) => Task.FromResult(new AgentToolResult(true, [])));
        var runTools = new AgentRunTools();

        CollectionAssert.AreEqual(new[] { "one", "two" }, runTools.Add([Tool("one"), Tool("two"), Tool("one")]).ToArray());
        CollectionAssert.AreEqual(new[] { "three" }, runTools.Add([Tool("two"), Tool("three")]).ToArray());
        Assert.IsTrue(runTools.Contains("three"));
        Assert.IsFalse(runTools.Contains("four"));
        Assert.ThrowsExactly<ArgumentNullException>(() => runTools.Add(null!));
    }

    [TestMethod]
    public void RunTools_SetGivesTheRunTheLastToolOfAName()
    {
        static AgentToolDefinition Tool(string name, string version) => new(
            new AgentToolSpec(name, version, JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })),
            (_, _) => Task.FromResult(new AgentToolResult(true, [])));
        // A run that starts with a tool of its session, and that was given another one since its last request.
        var runTools = new AgentRunTools(["given"]);
        runTools.Add([Tool("one", "first")]);

        CollectionAssert.AreEqual(new[] { "one", "given", "two" }, runTools.Set([Tool("one", "second"), Tool("given", "new"), Tool("two", "only")]).ToArray());
        CollectionAssert.AreEqual(new[] { "second", "new", "only" }, runTools.Take()!.Select(static tool => tool.Spec.Description).ToArray());
        Assert.IsNull(runTools.Take());

        // Set again once the run has taken them: the run is given the tool again, and takes it in place of the one it has.
        runTools.Set([Tool("one", "third")]);
        Assert.AreEqual("third", runTools.Take()!.Single().Spec.Description);
        Assert.IsTrue(runTools.Contains("two"));
        Assert.ThrowsExactly<ArgumentNullException>(() => runTools.Set(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => runTools.Set([null!]));
    }

    // First request: calls `activate`. Second: calls the tool the activation registered. Then: answers.
    private sealed class ActivatingProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public List<AgentTurnRequest> Requests { get; } = [];
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("run-tools-fixture"), "Run tools fixture") { DefaultModelId = "fixture" };
        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "run-tools-fixture", DisplayName = "Run tools fixture", TransportKind = AgentTransportKind.OpenAIResponses,
        };
        public IModelProviderModelCatalog? ModelCatalog => null;
        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };
        public IModelProviderTurnExecutor CreateTurnExecutor() => this;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });
        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            AgentMessagePart part = Requests.Count switch
            {
                1 => new AgentMessagePart.ToolCall("activate-call", "activate", JsonSerializer.SerializeToElement(new { })),
                2 => new AgentMessagePart.ToolCall("echo-call", request.Tools.Single(tool => tool.Spec.Name == "mcp__tiny__echo").Spec.Name,
                    JsonSerializer.SerializeToElement(new { text = "same turn" })),
                _ => new AgentMessagePart.Text("done"),
            };
            return Task.FromResult(new AgentTurnResponse { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [part]) });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
