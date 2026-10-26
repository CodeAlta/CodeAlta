using System.Collections.Concurrent;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Tests;

// A script of Code Mode calls the run's own tools through the tool call that runs it (IAgentRunToolInvoker): the
// calls take the same path as calls of the model, and are recorded as activities of the calling tool call.
public sealed partial class AgentSessionTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static AgentToolDefinition ScriptTool(Func<IAgentRunToolInvoker, CancellationToken, Task<AgentToolResult>> body)
        => new(new AgentToolSpec("script", "Runs nested calls.", Json("""{"type":"object","properties":{}}""")) { Exposure = AgentToolExposure.Model },
            (invocation, cancellationToken) => body(invocation.Tools ?? throw new InvalidOperationException("no invoker"), cancellationToken));

    private static Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>> CallScript()
        => (_, _, _) => Task.FromResult(new AgentTurnResponse
        {
            AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.ToolCall("call-1", "script", Json("{}"))]),
        });

    [TestMethod]
    public async Task NestedCalls_TakeThePathOfTheModelsCalls_AndAreActivitiesOfTheirToolCall()
    {
        using var temp = TestTempDirectory.Create();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-nested-calls") with { WorkingDirectory = workspace };
        var state = CreateState(summary.SessionId);
        await store.UpsertSessionAsync(summary);
        await store.UpsertStateAsync(state);

        var permissions = new ConcurrentQueue<string>();
        AgentToolResult? write = null, shell = null, self = null, hidden = null;
        string[] listed = [];
        var script = ScriptTool(async (tools, cancellationToken) =>
        {
            listed = [.. tools.ListTools().Select(static spec => spec.Name)];
            write = await tools.InvokeAsync(new AgentNestedToolCall("write_file", Json("""{"path":"created.txt","content":"hello"}""")) { StepId = "make" }, cancellationToken);
            shell = await tools.InvokeAsync(new AgentNestedToolCall("shell_command", Json("""{"command":"exit 3"}""")), cancellationToken);
            self = await tools.InvokeAsync(new AgentNestedToolCall("script", Json("{}")), cancellationToken);
            hidden = await tools.InvokeAsync(new AgentNestedToolCall("scripts_only", Json("{}")), cancellationToken);
            return new AgentToolResult(true, [new AgentToolResultItem.Text("script done")]);
        });
        var scriptsOnly = new AgentToolDefinition(
            new AgentToolSpec("scripts_only", "Only for scripts.", Json("""{"type":"object","properties":{}}""")) { Exposure = AgentToolExposure.Scripts },
            static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("hidden ran")])));

        IReadOnlyList<string> offered = [];
        IReadOnlyList<AgentConversationMessage> secondConversation = [];
        await using var session = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store,
            new ScriptedTurnExecutor(
                (request, update, token) =>
                {
                    offered = [.. request.Tools.Select(static tool => tool.Spec.Name)];
                    return CallScript()(request, update, token);
                },
                (request, _, _) =>
                {
                    secondConversation = request.Conversation;
                    return Task.FromResult(new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]) });
                }),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = workspace,
                OnPermissionRequest = (request, _) =>
                {
                    permissions.Enqueue(request.GetType().Name);
                    return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));
                },
            });

        await session.SendAsync(new() { Input = AgentInput.Text("Work"), AdditionalTools = [script, scriptsOnly] });

        // The model is never offered the tool of scripts only; a script is never offered the tool of the model only.
        CollectionAssert.DoesNotContain(offered.ToArray(), "scripts_only");
        CollectionAssert.Contains(listed, "scripts_only");
        CollectionAssert.DoesNotContain(listed, "script");

        // The nested calls ask as the calls of the model do, and run the run's handlers.
        Assert.AreEqual("hello", await File.ReadAllTextAsync(Path.Combine(workspace, "created.txt")));
        Assert.IsTrue(write!.Success);
        Assert.IsGreaterThanOrEqualTo(2, permissions.Count, "the write and the command ask, as calls of the model do");
        Assert.IsFalse(shell!.Success);
        Assert.AreEqual(3, shell.Structured!.Value.GetProperty("exitCode").GetInt32(), "a script receives the exit code of a failed command");
        Assert.IsFalse(self!.Success, "a script cannot call the tool that runs it");
        Assert.IsTrue(hidden!.Success);

        // They are activities of the calling tool call, and messages of no conversation.
        var history = await session.GetHistoryAsync();
        var nested = history.OfType<AgentActivityEvent>().Where(static e => e.ParentActivityId == "call-1").ToArray();
        Assert.IsTrue(nested.Any(static e => e is { Name: "write_file", Phase: AgentActivityPhase.Completed, ActivityId: "call-1/1" }));
        Assert.IsTrue(nested.Any(static e => e is { Name: "shell_command", Phase: AgentActivityPhase.Failed }));
        Assert.AreEqual("make", nested.First(static e => e.Name == "write_file" && e.Phase == AgentActivityPhase.Completed).Details!.Value.GetProperty("script").GetProperty("stepId").GetString());
        var toolResults = secondConversation.SelectMany(static message => message.Parts).OfType<AgentMessagePart.ToolResult>().ToArray();
        Assert.AreEqual(1, toolResults.Length, "only the call of the model is in the conversation");
        Assert.AreEqual("call-1", toolResults[0].CallId);

        // What a script wrote is in the diff of the turn.
        var diff = history.OfType<AgentSessionUpdateEvent>().Single(static e => e.Kind == AgentSessionUpdateKind.DiffUpdated);
        StringAssert.Contains(diff.Details!.Value.GetProperty("diff").GetString(), "created.txt");
    }

    [TestMethod]
    public async Task NestedCalls_OfToolsTheSessionDoesWithout_AreRefused_AndRoutedToolsRunWhereTheRouterSays()
    {
        using var temp = TestTempDirectory.Create();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "a.txt"), "line");
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-nested-gates") with { WorkingDirectory = workspace };
        var state = CreateState(summary.SessionId);
        await store.UpsertSessionAsync(summary);
        await store.UpsertStateAsync(state);
        var router = new CountingRouter();
        AgentToolResult? shell = null, read = null;
        string[] listed = [];
        var script = ScriptTool(async (tools, cancellationToken) =>
        {
            listed = [.. tools.ListTools().Select(static spec => spec.Name)];
            shell = await tools.InvokeAsync(new AgentNestedToolCall("shell_command", Json("""{"command":"echo hi"}""")), cancellationToken);
            read = await tools.InvokeAsync(new AgentNestedToolCall("read_file", Json("""{"path":"a.txt"}""")), cancellationToken);
            return new AgentToolResult(true, [new AgentToolResultItem.Text("done")]);
        });

        await using var session = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store,
            new ScriptedTurnExecutor(CallScript(),
                static (_, _, _) => Task.FromResult(new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]) })),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = workspace,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                DisabledBuiltInTools = new HashSet<string>(StringComparer.Ordinal) { "shell_command" },
                BuiltInToolRouter = router,
            });

        await session.SendAsync(new() { Input = AgentInput.Text("Work"), AdditionalTools = [script] });

        CollectionAssert.DoesNotContain(listed, "shell_command");
        Assert.IsFalse(shell!.Success, "a script cannot run a tool its session does without");
        Assert.IsTrue(read!.Success);
        Assert.AreEqual(1, router.ReadCalls, "the routed handler ran the nested read");
    }

    [TestMethod]
    public async Task NestedCalls_ReachTheJournalTogether_BeforeTheirToolCallEndsThere()
    {
        using var temp = TestTempDirectory.Create();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "a.txt"), "line");
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-nested-journal") with { WorkingDirectory = workspace };
        var state = CreateState(summary.SessionId);
        await store.UpsertSessionAsync(summary);
        await store.UpsertStateAsync(state);
        var script = ScriptTool(async (tools, cancellationToken) =>
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => tools.InvokeAsync(new AgentNestedToolCall("read_file", Json("""{"path":"a.txt"}""")), cancellationToken)));
            return new AgentToolResult(true, [new AgentToolResultItem.Text("done")]);
        });

        await using (var session = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store,
            new ScriptedTurnExecutor(CallScript(),
                static (_, _, _) => Task.FromResult(new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]) })),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = workspace,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }))
        {
            await session.SendAsync(new() { Input = AgentInput.Text("Work"), AdditionalTools = [script] });
        }

        // Shown at once, written a little later and together: every start and end is in the journal, before the end of
        // the call of the model that ran the script.
        var journal = (await store.ReadEventsAsync(provider.ProtocolFamily, provider.ProviderKey, summary.SessionId)).OfType<AgentActivityEvent>().ToList();
        var nested = journal.Where(static e => e.ParentActivityId == "call-1").ToList();
        Assert.AreEqual(20, nested.Count(static e => e.Phase == AgentActivityPhase.Started));
        Assert.AreEqual(20, nested.Count(static e => e.Phase == AgentActivityPhase.Completed));
        var parentEnd = journal.FindIndex(static e => e is { ActivityId: "call-1", Phase: AgentActivityPhase.Completed });
        Assert.IsGreaterThan(journal.IndexOf(nested[^1]), parentEnd);
    }

    private sealed class CountingRouter : IAgentBuiltInToolRouter
    {
        private int _readCalls;

        public int ReadCalls => Volatile.Read(ref _readCalls);

        public IAgentProcessHost? ProcessHost => null;

        public IReadOnlyList<AgentToolDefinition> Route(AgentBuiltInToolOptions options, IReadOnlyList<AgentToolDefinition> tools)
            => [.. tools.Select(tool => tool.Spec.Name != "read_file" ? tool : tool with
            {
                Handler = (invocation, cancellationToken) =>
                {
                    Interlocked.Increment(ref _readCalls);
                    return tool.Handler(invocation, cancellationToken);
                },
            })];

        public ValueTask<string?> RefuseRunAsync(bool providerRunsItsOwnTools, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    }
}
