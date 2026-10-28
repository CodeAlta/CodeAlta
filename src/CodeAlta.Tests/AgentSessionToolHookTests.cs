using System.Collections.Concurrent;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

// The tool hooks of a session (AgentToolInterceptor): every tool the session runs goes through them, before the guards,
// the permission prompt and the sandbox of the tool, which still apply to the arguments the hooks leave.
public sealed partial class AgentSessionTests
{
    [TestMethod]
    public async Task AToolHookThatBlocksAShellCommand_KeepsItFromRunning_AndTheModelReadsWhy()
    {
        using var temp = TestTempDirectory.Create();
        var permissions = 0;
        var hooks = new RecordingInterceptor { Block = static call => call.ShellCommand is { } shell ? $"Blocked by Shell check: '{shell.Command}' does not parse." : null };
        var (session, results) = await StartHookedSessionAsync(temp, hooks, ToolCall("call-1", "shell_command", """{"command":"echo ran > ran.txt"}"""),
            onPermission: () => Interlocked.Increment(ref permissions));
        await using (session)
        {
            await session.SendAsync(new() { Input = AgentInput.Text("Run it") });
        }

        var result = results.Single();
        Assert.IsFalse(result.Success);
        Assert.AreEqual("Blocked by Shell check: 'echo ran > ran.txt' does not parse.", result.Error);
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "workspace", "ran.txt")), "a blocked command never runs");
        Assert.AreEqual(0, permissions, "a blocked command is not asked for");
        var call = hooks.Calls.Single();
        Assert.AreEqual(Path.Combine(temp.Path, "workspace"), call.ShellCommand!.WorkingDirectory);
        Assert.AreEqual(AgentToolHookCapabilities.All, call.Capabilities);
        Assert.IsFalse(call.RunByAgent);
        Assert.AreEqual(0, hooks.Results.Count, "a blocked call has no result to hook");
    }

    [TestMethod]
    public async Task ArgumentsAToolHookReplaces_StillGoThroughTheGuardOfTheHost()
    {
        using var temp = TestTempDirectory.Create();
        var guarded = new ConcurrentQueue<string>();
        var hooks = new RecordingInterceptor
        {
            Replace = static call => call.Invocation.ToolName == "shell_command" ? Json("""{"command":"git commit -m sneaky"}""") : null,
        };
        var (session, results) = await StartHookedSessionAsync(temp, hooks, ToolCall("call-1", "shell_command", """{"command":"git status"}"""),
            commandGuard: (command, _, _) =>
            {
                guarded.Enqueue(command);
                return ValueTask.FromResult(AgentGitCommands.IsCommit(command) ? "Request a review first." : null);
            });
        await using (session)
        {
            await session.SendAsync(new() { Input = AgentInput.Text("Status") });
        }

        // A hook makes the policy stricter, never looser: the guard checks the command the hook left.
        CollectionAssert.AreEqual(new[] { "git commit -m sneaky" }, guarded.ToArray());
        Assert.AreEqual("Request a review first.", results.Single().Error);
        Assert.AreEqual("git commit -m sneaky", hooks.Results.Single().Call.ShellCommand!.Command, "the result hook sees the arguments the call ran with");
    }

    [TestMethod]
    public async Task TheCallsOfAScript_GoThroughTheToolHooks()
    {
        using var temp = TestTempDirectory.Create();
        AgentToolResult? nested = null;
        var script = ScriptTool(async (tools, cancellationToken) =>
        {
            nested = await tools.InvokeAsync(new AgentNestedToolCall("shell_command", Json("""{"command":"echo nested > nested.txt"}""")), cancellationToken);
            return new AgentToolResult(true, [new AgentToolResultItem.Text("script done")]);
        });
        var hooks = new RecordingInterceptor { Block = static call => call.Invocation.ParentToolCallId is not null ? "Blocked by Policy: not from scripts." : null };
        var (session, results) = await StartHookedSessionAsync(temp, hooks, ToolCall("call-1", "script", "{}"));
        await using (session)
        {
            await session.SendAsync(new() { Input = AgentInput.Text("Script"), AdditionalTools = [script] });
        }

        Assert.IsTrue(results.Single().Success);
        Assert.IsFalse(nested!.Success);
        Assert.AreEqual("Blocked by Policy: not from scripts.", nested.Error);
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "workspace", "nested.txt")));
        CollectionAssert.AreEqual(new[] { "script:", "shell_command:call-1" },
            hooks.Calls.Select(static call => call.Invocation.ToolName + ":" + call.Invocation.ParentToolCallId).ToArray());
    }

    [TestMethod]
    public async Task WhatAResultHookAppends_ReachesTheModel_AndItsDetailsAreRecordedForTheHostOnly()
    {
        using var temp = TestTempDirectory.Create();
        var probe = new AgentToolDefinition(new AgentToolSpec("probe", "Probes.", Json("""{"type":"object","properties":{}}""")),
            static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("probed")])));
        var hooks = new RecordingInterceptor
        {
            After = static (_, result) => result with
            {
                Items = [.. result.Items, new AgentToolResultItem.Text("Reminder: run the tests.")],
                Details = Json("""{"checker":{"warnings":2}}"""),
            },
        };
        var (session, results) = await StartHookedSessionAsync(temp, hooks, ToolCall("call-1", "probe", "{}"));
        await using (session)
        {
            await session.SendAsync(new() { Input = AgentInput.Text("Probe"), AdditionalTools = [probe] });
            var completed = (await session.GetHistoryAsync()).OfType<AgentActivityEvent>()
                .Single(static e => e is { Name: "probe", Phase: AgentActivityPhase.Completed });
            Assert.AreEqual(2, completed.Details!.Value.GetProperty("result").GetProperty("details").GetProperty("checker").GetProperty("warnings").GetInt32());
        }

        var given = results.Single();
        CollectionAssert.AreEqual(new[] { "probed", "Reminder: run the tests." }, given.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value).ToArray());
        Assert.IsNull(given.Details, "the model is never sent the details of the hooks");
    }

    [TestMethod]
    public async Task APluginBlocksABuiltInShellCommand_AndTheHooksOfItsOwnToolRunOnce()
    {
        using var temp = TestTempDirectory.Create();
        var registry = new PluginContributionRegistry();
        var activation = await new PluginRuntimeActivator(registry).ActivateAsync(
            new DiscoveredPluginType { Type = typeof(ShellCheckPlugin), Descriptor = PluginDescriptorFactory.FromType(typeof(ShellCheckPlugin)) }, null, null,
            new PluginActivationOptions
            {
                HostInfo = new PluginHostInfo
                {
                    ApplicationName = "CodeAlta.Tests",
                    Version = "1.0.0",
                    HostApiVersion = "1.0.0",
                    UserDataDirectory = temp.Path,
                    IsHeadless = true,
                },
            });
        Assert.IsTrue(activation.Succeeded, string.Join(Environment.NewLine, activation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var active = activation.ActivePlugin!;
        var plugin = (ShellCheckPlugin)active.Instance!;
        var bridge = new PluginOrchestrationBridge(new PluginContributionAdapterService(registry), () => [active]);
        var workspace = Path.Combine(temp.Path, "workspace");
        var interceptor = bridge.CreateToolInterceptor(new SessionToolHookScope("session-plugin-hooks", "project", workspace, null));
        var pluginTools = bridge.CreateAgentTools(new PluginAdapterOperationOptions { SessionId = "session-plugin-hooks", ProjectPath = workspace, IsCodeAltaManagedProvider = true });

        var (session, results) = await StartHookedSessionAsync(temp, interceptor,
            [new AgentMessagePart.ToolCall("call-1", "shell_command", Json("""{"command":"Get-ChildItem 'unclosed"}""")), new AgentMessagePart.ToolCall("call-2", "echo_note", Json("""{"text":"hi"}"""))],
            sessionId: "session-plugin-hooks", tools: pluginTools);
        await using (session)
        {
            await session.SendAsync(new() { Input = AgentInput.Text("Check") });
        }

        Assert.AreEqual("Blocked by Shell check: the command does not parse: missing closing quote.", results[0].Error);
        Assert.IsTrue(results[1].Success);
        CollectionAssert.AreEqual(new[] { "echo_note: hi (noted)" }, results[1].Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value).ToArray());
        // Each call is seen once: the tool of the plugin does not run the hooks a second time.
        var pluginId = PluginContributionRegistry.PluginIdOf(active.Descriptor.RuntimeKey, null);
        CollectionAssert.AreEqual(new[] { "shell_command:BuiltIn:", "echo_note:Plugin:" + pluginId }, plugin.Seen.ToArray(), string.Join(", ", plugin.Seen));
        Assert.AreEqual(1, plugin.Results);
        Assert.AreEqual(workspace, plugin.LastShell!.WorkingDirectory);
        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    private static AgentMessagePart.ToolCall[] ToolCall(string id, string name, string arguments) => [new AgentMessagePart.ToolCall(id, name, Json(arguments))];

    // A session whose first answer makes the calls, and whose second records the results the model is given.
    private static async Task<(AgentSession Session, List<AgentToolResult> Results)> StartHookedSessionAsync(
        TestTempDirectory temp,
        AgentToolInterceptor interceptor,
        AgentMessagePart.ToolCall[] calls,
        Action? onPermission = null,
        AgentCommandGuard? commandGuard = null,
        string sessionId = "session-tool-hooks",
        IReadOnlyList<AgentToolDefinition>? tools = null)
    {
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary(sessionId) with { WorkingDirectory = workspace };
        var state = CreateState(sessionId);
        await store.UpsertSessionAsync(summary);
        await store.UpsertStateAsync(state);
        var results = new List<AgentToolResult>();
        var session = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store,
            new ScriptedTurnExecutor(
                (_, _, _) => Task.FromResult(new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [.. calls]) }),
                (request, _, _) =>
                {
                    results.AddRange(request.Conversation.SelectMany(static message => message.Parts).OfType<AgentMessagePart.ToolResult>().Select(static part => part.Result));
                    return Task.FromResult(new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]) });
                }),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = workspace,
                Tools = tools,
                ToolInterceptor = interceptor,
                CommandGuard = commandGuard,
                OnPermissionRequest = (_, _) =>
                {
                    onPermission?.Invoke();
                    return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));
                },
            });
        return (session, results);
    }

    private sealed class RecordingInterceptor : AgentToolInterceptor
    {
        public Func<AgentToolHookCall, string?>? Block { get; init; }

        public Func<AgentToolHookCall, JsonElement?>? Replace { get; init; }

        public Func<AgentToolHookCall, AgentToolResult, AgentToolResult>? After { get; init; }

        public ConcurrentQueue<AgentToolHookCall> Calls { get; } = new();

        public List<(AgentToolHookCall Call, AgentToolResult Result)> Results { get; } = [];

        public override ValueTask<AgentToolCallDecision> BeforeToolCallAsync(AgentToolHookCall call, CancellationToken cancellationToken)
        {
            Calls.Enqueue(call);
            if (Block?.Invoke(call) is { } message) return ValueTask.FromResult(AgentToolCallDecision.Block(message));
            return ValueTask.FromResult(Replace?.Invoke(call) is { } arguments ? AgentToolCallDecision.WithArguments(arguments) : AgentToolCallDecision.Allow);
        }

        public override ValueTask<AgentToolResult> AfterToolCallAsync(AgentToolHookCall call, AgentToolResult result, CancellationToken cancellationToken)
        {
            lock (Results) Results.Add((call, result));
            return ValueTask.FromResult(After?.Invoke(call, result) ?? result);
        }
    }

    // The plugin of the issue: it checks each shell command before it runs (here, an unclosed quote) and blocks it with
    // the diagnostic; it also gives a tool of its own.
    [Plugin("shell-check", DisplayName = "Shell check")]
    public sealed class ShellCheckPlugin : PluginBase
    {
        public ConcurrentQueue<string> Seen { get; } = new();

        private int _results;

        public int Results => Volatile.Read(ref _results);

        public PluginShellCommand? LastShell { get; private set; }

        public override IEnumerable<PluginAgentToolContribution> GetAgentTools()
        {
            yield return AgentTool.Create(new AgentToolDefinition(
                new AgentToolSpec("echo_note", "Echoes a note.", Json("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""")),
                static (invocation, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("echo_note: " + invocation.Arguments.GetProperty("text").GetString())]))));
        }

        public override ValueTask<PluginToolCallResult?> OnToolCallAsync(PluginToolCallContext context, CancellationToken cancellationToken = default)
        {
            Seen.Enqueue($"{context.ToolName}:{context.ToolKind}:{context.ToolPluginId}");
            if (context.ShellCommand is not { } shell) return ValueTask.FromResult<PluginToolCallResult?>(null);
            LastShell = shell;
            return ValueTask.FromResult<PluginToolCallResult?>(shell.Command.Count(static c => c == '\'') % 2 == 1
                ? PluginToolCallResult.Block("the command does not parse: missing closing quote.")
                : null);
        }

        public override ValueTask<PluginToolResult?> OnToolResultAsync(PluginToolResultContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _results);
            return ValueTask.FromResult<PluginToolResult?>(context.ToolName == "echo_note"
                ? PluginToolResult.Replace(context.Result with { Items = [new AgentToolResultItem.Text(((AgentToolResultItem.Text)context.Result.Items[0]).Value + " (noted)")] })
                : null);
        }
    }
}
