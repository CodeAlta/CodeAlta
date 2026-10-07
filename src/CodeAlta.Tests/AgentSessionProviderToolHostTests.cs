using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// A provider that runs tools itself (an agent CLI) gives the session the definition that runs each of its
/// tool calls, and compacts the context it keeps.
/// </summary>
[TestClass]
public sealed class AgentSessionProviderToolHostTests
{
    [TestMethod]
    public async Task ToolCallOfTheProvider_IsRunByTheDefinitionTheProviderResolves()
    {
        using var directory = TestTempDirectory.Create();
        var file = Path.Combine(directory.Path, "note.txt");
        await File.WriteAllTextAsync(file, "before\n");
        var provider = new ToolHostProvider
        {
            Responses =
            [
                Call("call-1", "Edit", new { file_path = file }),
                Call("call-2", "host_tool", new { }),
                new AgentMessagePart.Text("done"),
            ],
            // The provider "runs" its own tool: here it edits the file as the CLI would.
            OnProviderTool = async call =>
            {
                await File.WriteAllTextAsync(file, "after\n");
                return new AgentToolResult(true, [new AgentToolResultItem.Text($"ran {call.Name}")]);
            },
        };
        var hostToolCalls = 0;
        var events = new List<AgentEvent>();
        await using var session = await provider.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "tool-host-fixture",
            WorkingDirectory = directory.Path,
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec("host_tool", "A tool of the session.", JsonSerializer.SerializeToElement(new { type = "object" })),
                    (_, _) =>
                    {
                        hostToolCalls++;
                        return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("host ran")]));
                    }),
            ],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        });
        session.Subscribe(events.Add);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("go"), EnableUserInputTool = false });

        // The run gave its handlers before the first turn.
        Assert.IsNotNull(provider.Run);
        Assert.AreEqual(session.SessionId, provider.Run.SessionId);
        Assert.IsNull(provider.Run.OnUserInputRequest, "A run that does not ask questions gives no way to ask.");

        // A tool the session does not know is run by what the provider resolved, and its file change is shown.
        var edit = events.OfType<AgentActivityEvent>().Single(static e => e.ActivityId == "call-1" && e.Phase == AgentActivityPhase.Completed);
        Assert.AreEqual("Edit", edit.Name);
        StringAssert.Contains(edit.Details!.Value.GetProperty("diff").GetString(), "+after");
        Assert.AreEqual(Path.GetFullPath(file), edit.Details.Value.GetProperty("modifiedFiles")[0].GetString());
        Assert.AreEqual("ran Edit", events.OfType<AgentContentCompletedEvent>().First(static e => e.Kind == AgentContentKind.ToolOutput).Content);

        // A tool of the session stays the session's when the provider resolves nothing for it.
        Assert.AreEqual(1, hostToolCalls);
        CollectionAssert.AreEqual(new[] { "call-1", "call-2" }, provider.Resolved.Select(static call => call.CallId).ToArray());
        Assert.IsTrue(provider.Resolved[1].HadRegisteredTool);
        Assert.IsFalse(provider.Resolved[0].HadRegisteredTool);
    }

    [TestMethod]
    public async Task UnknownToolCall_StillFailsWhenTheProviderResolvesNothing()
    {
        using var directory = TestTempDirectory.Create();
        var provider = new ToolHostProvider { Responses = [Call("call-1", "nobody_runs_this", new { })] };
        await using var session = await provider.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "tool-host-fixture",
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        });

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("go") }));

        StringAssert.Contains(failure.Message, "nobody_runs_this");
    }

    [TestMethod]
    public async Task ManualCompaction_IsDoneByAProviderThatKeepsTheContext()
    {
        using var directory = TestTempDirectory.Create();
        var provider = new ToolHostProvider
        {
            Responses = [new AgentMessagePart.Text("hello")],
            Compaction = new AgentCompactionOutcome(true, "The provider compacted its context.", PreCompactionTokens: 900, PostCompactionTokens: 100),
        };
        var events = new List<AgentEvent>();
        await using var session = await provider.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "tool-host-fixture",
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        });
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("go") });
        session.Subscribe(events.Add);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync();

        Assert.AreSame(provider.Compaction, outcome);
        Assert.AreEqual(1, provider.CompactionRequests, "No summary is asked of the model: the provider compacts.");
        Assert.AreEqual(1, provider.Turns);
        var updates = events.OfType<AgentSessionUpdateEvent>().ToArray();
        CollectionAssert.AreEqual(
            new[] { AgentSessionUpdateKind.CompactionStarted, AgentSessionUpdateKind.CompactionCompleted },
            updates.Select(static update => update.Kind).ToArray());
        Assert.AreEqual("The provider compacted its context.", updates[1].Message);
        Assert.AreEqual(100, updates[1].Usage!.Window!.CurrentTokens);

        // The journal keeps every message: the conversation of the session is not rewritten.
        var history = await session.GetHistoryAsync();
        Assert.IsTrue(history.OfType<AgentContentCompletedEvent>().Any(static e => e.Kind == AgentContentKind.Assistant && e.Content == "hello"));
    }

    [TestMethod]
    public async Task FailedProviderCompaction_IsReportedWithoutFailingTheSession()
    {
        using var directory = TestTempDirectory.Create();
        var provider = new ToolHostProvider { Responses = [new AgentMessagePart.Text("hello")], CompactionFailure = new InvalidOperationException("The CLI is gone.") };
        var events = new List<AgentEvent>();
        await using var session = await provider.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "tool-host-fixture",
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        });
        session.Subscribe(events.Add);

        var outcome = await ((IAgentCompactionOutcomeProvider)session).CompactWithOutcomeAsync();

        Assert.IsNotNull(outcome);
        Assert.IsFalse(outcome.Success);
        Assert.AreEqual("The CLI is gone.", outcome.Message);
        Assert.AreEqual(AgentSessionUpdateKind.Warning, events.OfType<AgentSessionUpdateEvent>().Last().Kind);
    }

    private static AgentMessagePart Call(string id, string name, object arguments)
        => new AgentMessagePart.ToolCall(id, name, JsonSerializer.SerializeToElement(arguments));

    // Answers each model call with the next part, and runs the tools the session does not have.
    private sealed class ToolHostProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor, IAgentProviderToolHost, IAgentProviderCompaction
    {
        public IReadOnlyList<AgentMessagePart> Responses { get; init; } = [];
        public Func<AgentMessagePart.ToolCall, Task<AgentToolResult>>? OnProviderTool { get; init; }
        public AgentCompactionOutcome? Compaction { get; init; }
        public Exception? CompactionFailure { get; init; }
        public AgentProviderRunContext? Run { get; private set; }
        public List<(string CallId, bool HadRegisteredTool)> Resolved { get; } = [];
        public int Turns { get; private set; }
        public int CompactionRequests { get; private set; }

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("tool-host-fixture"), "Tool host fixture") { DefaultModelId = "fixture" };
        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "tool-host-fixture", DisplayName = "Tool host fixture", TransportKind = AgentTransportKind.ClaudeCodeCli,
        };
        public IModelProviderModelCatalog? ModelCatalog => null;
        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };
        public IModelProviderTurnExecutor CreateTurnExecutor() => this;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            var part = Responses[Math.Min(Turns, Responses.Count - 1)];
            Turns++;
            return Task.FromResult(new AgentTurnResponse { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [part]) });
        }

        public void AttachRun(AgentProviderRunContext context) => Run = context;

        public AgentToolDefinition? ResolveTool(string sessionId, AgentMessagePart.ToolCall toolCall, AgentToolDefinition? registered)
        {
            Resolved.Add((toolCall.CallId, registered is not null));
            if (registered is not null || OnProviderTool is null)
            {
                return null;
            }

            return new AgentToolDefinition(
                new AgentToolSpec(toolCall.Name, "A tool of the provider.", JsonSerializer.SerializeToElement(new { type = "object" })),
                (_, _) => OnProviderTool(toolCall));
        }

        public Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken)
        {
            CompactionRequests++;
            return CompactionFailure is not null ? Task.FromException<AgentCompactionOutcome>(CompactionFailure) : Task.FromResult(Compaction!);
        }
    }
}
