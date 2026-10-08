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
    public async Task ToolCallsTheProviderRunsAtTheSameTime_AreEachShownFromWhenTheyStart()
    {
        using var directory = TestTempDirectory.Create();
        var results = new[] { "call-1", "call-2", "call-3" }.ToDictionary(
            static id => id,
            static _ => new TaskCompletionSource<AgentToolResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        var secondStarts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ToolHostProvider
        {
            // One message of the model makes three calls: the provider runs the first two at the same time.
            Messages =
            [
                [Call("call-1", "Agent", new { }), Call("call-2", "Agent", new { }), Call("call-3", "Bash", new { })],
                [new AgentMessagePart.Text("done")],
            ],
            OnProviderToolWithToken = (call, cancellationToken) => results[call.CallId].Task.WaitAsync(cancellationToken),
            // The first call runs at once, the second when the provider says so, and the provider never says
            // when the third starts.
            Started = call => call.CallId switch
            {
                "call-1" => Task.CompletedTask,
                "call-2" => secondStarts.Task,
                _ => new TaskCompletionSource().Task,
            },
        };
        var events = new EventLog();
        await using var session = await provider.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "tool-host-fixture",
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        });
        session.Subscribe(events.Add);

        var run = session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("go"), EnableUserInputTool = false });

        await events.WaitForAsync("call-1", AgentActivityPhase.Started);
        Assert.IsFalse(events.Has("call-2", AgentActivityPhase.Started), "A call the provider has not started is not shown as running.");

        // The second call starts while the first still runs, and ends before it.
        secondStarts.SetResult();
        await events.WaitForAsync("call-2", AgentActivityPhase.Started);
        Assert.IsFalse(events.Has("call-1", AgentActivityPhase.Completed));
        results["call-2"].SetResult(new AgentToolResult(true, [new AgentToolResultItem.Text("two")]));
        await events.WaitForAsync("call-2", AgentActivityPhase.Completed);
        Assert.IsFalse(events.Has("call-1", AgentActivityPhase.Completed));

        // A call whose start the provider does not tell starts when the calls before it ended, as it always did.
        Assert.IsFalse(events.Has("call-3", AgentActivityPhase.Started));
        results["call-1"].SetResult(new AgentToolResult(true, [new AgentToolResultItem.Text("one")]));
        await events.WaitForAsync("call-3", AgentActivityPhase.Started);
        results["call-3"].SetResult(new AgentToolResult(true, [new AgentToolResultItem.Text("three")]));
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        // The model is called again once every call of the message has its result, each recorded with its call.
        Assert.AreEqual(2, provider.Turns);
        var outputs = events.Snapshot().OfType<AgentContentCompletedEvent>().Where(static e => e.Kind == AgentContentKind.ToolOutput)
            .ToDictionary(static e => e.ParentActivityId!, static e => e.Content);
        Assert.AreEqual("one", outputs["call-1"]);
        Assert.AreEqual("two", outputs["call-2"]);
        Assert.AreEqual("three", outputs["call-3"]);
        var toolResults = provider.LastRequest!.Conversation
            .SelectMany(static message => message.Parts).OfType<AgentMessagePart.ToolResult>().Select(static result => result.CallId).ToArray();
        CollectionAssert.AreEquivalent(new[] { "call-1", "call-2", "call-3" }, toolResults);
    }

    [TestMethod]
    public async Task AbortWhileToolCallsRunAtTheSameTime_EndsThemAllWithTheRun()
    {
        using var directory = TestTempDirectory.Create();
        var cancelled = 0;
        var provider = new ToolHostProvider
        {
            Messages = [[Call("call-1", "Agent", new { }), Call("call-2", "Agent", new { })]],
            Started = static _ => Task.CompletedTask,
        };
        provider.OnProviderToolWithToken = async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }

            return new AgentToolResult(true, []);
        };
        var events = new EventLog();
        await using var session = await provider.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "tool-host-fixture",
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        });
        session.Subscribe(events.Add);

        var run = session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("go"), EnableUserInputTool = false });
        await events.WaitForAsync("call-1", AgentActivityPhase.Started);
        await events.WaitForAsync("call-2", AgentActivityPhase.Started);

        await session.AbortAsync().WaitAsync(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.AreEqual(2, Volatile.Read(ref cancelled), "No call is left running when the run ends.");
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

    [TestMethod]
    public async Task ContextUsage_StaysTheProvidersAfterATurnWithAnImage()
    {
        // A 1x1 PNG, as a tool that takes a picture returns one.
        const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        using var directory = TestTempDirectory.Create();
        var provider = new ToolHostProvider
        {
            Responses = [Call("call-1", "take_picture", new { }), new AgentMessagePart.Text("done")],
            OnProviderTool = static _ => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Image(Png, "image/png")])),
            // What the provider counted holds its own prompt and tools, which the session knows nothing of.
            Usage = static request => new AgentSessionUsage(
                Window: new AgentWindowUsageSnapshot(CurrentTokens: 39_000, TokenLimit: 1_000_000, MessageCount: request.Conversation.Count, Label: "Active context window"),
                Scope: AgentUsageScope.CurrentWindow,
                Source: AgentUsageSource.ProviderUsage,
                UpdatedAt: DateTimeOffset.UtcNow),
        };
        var events = new List<AgentEvent>();
        await using var session = await provider.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "tool-host-fixture",
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        });
        session.Subscribe(events.Add);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("go"), EnableUserInputTool = false });

        // The image leaves the conversation the session would send again once the run ended. A provider that
        // keeps the context still has it: its count is not replaced by an estimate of what the session holds.
        var usage = events.OfType<AgentSessionUpdateEvent>().Last(static update => update.Usage?.Window is not null).Usage!;
        Assert.IsTrue(usage.CurrentTokens >= 39_000, $"Expected the count of the provider, got {usage.CurrentTokens}.");
        Assert.AreEqual(1_000_000L, usage.TokenLimit);
    }

    [TestMethod]
    public async Task ContextUsage_GoesDownWhenTheProviderTrimsTheContextItKeeps()
    {
        using var directory = TestTempDirectory.Create();
        var counts = new Queue<long>([57_000, 44_000]);
        var provider = new ToolHostProvider
        {
            Responses = [new AgentMessagePart.Text("one"), new AgentMessagePart.Text("two")],
            // A CLI clears old tool results and compacts by itself: its second request reads less than its first.
            Usage = request => new AgentSessionUsage(
                Window: new AgentWindowUsageSnapshot(CurrentTokens: counts.Dequeue(), TokenLimit: 200_000, MessageCount: request.Conversation.Count, Label: "Active context window"),
                Scope: AgentUsageScope.CurrentWindow,
                Source: AgentUsageSource.ProviderUsage,
                UpdatedAt: DateTimeOffset.UtcNow),
        };
        var events = new List<AgentEvent>();
        await using var session = await provider.CreateSessionAsync(new AgentSessionCreateOptions
        {
            ProviderKey = "tool-host-fixture",
            WorkingDirectory = directory.Path,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        });
        session.Subscribe(events.Add);

        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("first"), EnableUserInputTool = false });
        await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("second"), EnableUserInputTool = false });

        // A smaller count is not the count of a part of the context, as it is for a provider that is sent the
        // conversation again: it is what the provider holds now.
        var usage = events.OfType<AgentSessionUpdateEvent>().Last(static update => update.Usage?.Window is not null).Usage!;
        Assert.IsTrue(usage.CurrentTokens is >= 44_000 and < 50_000, $"Expected the count of the provider, got {usage.CurrentTokens}.");
    }

    private static AgentMessagePart Call(string id, string name, object arguments)
        => new AgentMessagePart.ToolCall(id, name, JsonSerializer.SerializeToElement(arguments));

    // The events of a session, which calls that run at the same time write from several threads.
    private sealed class EventLog
    {
        private readonly List<AgentEvent> _events = [];

        public void Add(AgentEvent @event)
        {
            lock (_events)
            {
                _events.Add(@event);
            }
        }

        public AgentEvent[] Snapshot()
        {
            lock (_events)
            {
                return [.. _events];
            }
        }

        public bool Has(string callId, AgentActivityPhase phase)
            => Snapshot().OfType<AgentActivityEvent>().Any(e => e.ActivityId == callId && e.Phase == phase);

        public async Task WaitForAsync(string callId, AgentActivityPhase phase)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!Has(callId, phase))
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    // Answers each model call with the next part, and runs the tools the session does not have.
    private sealed class ToolHostProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor, IAgentProviderToolHost, IAgentProviderCompaction
    {
        public IReadOnlyList<AgentMessagePart> Responses { get; init; } = [];
        // The messages of the model when one of them holds several parts: they replace Responses.
        public IReadOnlyList<AgentMessagePart[]>? Messages { get; init; }
        public Func<AgentMessagePart.ToolCall, Task<AgentToolResult>>? OnProviderTool { get; init; }
        public Func<AgentMessagePart.ToolCall, CancellationToken, Task<AgentToolResult>>? OnProviderToolWithToken { get; set; }
        // When the provider starts a call; nothing is said of a call when it is not set.
        public Func<AgentMessagePart.ToolCall, Task?>? Started { get; init; }
        public AgentTurnRequest? LastRequest { get; private set; }
        public Func<AgentTurnRequest, AgentSessionUsage?>? Usage { get; init; }
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
            AgentMessagePart[] parts = Messages is { } messages
                ? messages[Math.Min(Turns, messages.Count - 1)]
                : [Responses[Math.Min(Turns, Responses.Count - 1)]];
            Turns++;
            LastRequest = request;
            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, parts),
                Usage = Usage?.Invoke(request),
            });
        }

        public void AttachRun(AgentProviderRunContext context) => Run = context;

        public AgentToolDefinition? ResolveTool(string sessionId, AgentMessagePart.ToolCall toolCall, AgentToolDefinition? registered)
        {
            Resolved.Add((toolCall.CallId, registered is not null));
            if (registered is not null || (OnProviderTool is null && OnProviderToolWithToken is null))
            {
                return null;
            }

            return new AgentToolDefinition(
                new AgentToolSpec(toolCall.Name, "A tool of the provider.", JsonSerializer.SerializeToElement(new { type = "object" })),
                (_, cancellationToken) => OnProviderToolWithToken is { } run ? run(toolCall, cancellationToken) : OnProviderTool!(toolCall));
        }

        public Task? WhenToolStarts(string sessionId, AgentMessagePart.ToolCall toolCall) => Started?.Invoke(toolCall);

        public Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken)
        {
            CompactionRequests++;
            return CompactionFailure is not null ? Task.FromException<AgentCompactionOutcome>(CompactionFailure) : Task.FromResult(Compaction!);
        }
    }
}
