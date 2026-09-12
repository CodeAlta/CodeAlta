using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Tests;

/// <summary>Actual session compaction with explicit roots, cached metadata and a text-only scripted executor.</summary>
[TestClass]
public sealed class AgentSessionIdleCompactionTests
{
    [TestMethod]
    public Task IdleCompaction_ActiveRunRefusesWithoutStartingSummary() => Fixture.RunAsync(async f =>
    {
        f.Executor.HoldTurn = true;
        var send = f.Send("held prompt");
        await f.Ready(f.Executor.TurnStarted.Task, send);
        var before = await f.Wait(f.Keep(f.Store.ReadEventsAsync("openai-responses", "idle-compaction-fixture", f.SessionId)));
        var startedBefore = before.OfType<AgentSessionUpdateEvent>().Count(value => value.Kind == AgentSessionUpdateKind.CompactionStarted);
        var compact = f.Compact();
        Assert.IsNull(await f.Wait(compact));
        Assert.AreEqual(0, f.Executor.SummaryCalls);
        var after = await f.Wait(f.Keep(f.Store.ReadEventsAsync("openai-responses", "idle-compaction-fixture", f.SessionId)));
        Assert.AreEqual(startedBefore, after.OfType<AgentSessionUpdateEvent>().Count(value => value.Kind == AgentSessionUpdateKind.CompactionStarted));
        f.Executor.ReleaseTurn.TrySetResult();
        await f.Wait(send);
    });

    [TestMethod]
    public Task IdleCompaction_RealCheckpointAndOccupiedStateGateRefusal() => Fixture.RunAsync(async f =>
    {
        await f.Wait(f.Send("First prompt " + new string('x', 140)));
        await f.Wait(f.Send("Second prompt " + new string('y', 140)));
        f.Executor.HoldSummary = true;
        var compact = f.Compact();
        await f.Ready(f.Executor.SummaryStarted.Task, compact);
        var before = await f.Wait(f.Keep(f.Store.ReadEventsAsync("openai-responses", "idle-compaction-fixture", f.SessionId)));
        Assert.AreEqual(1, before.OfType<AgentSessionUpdateEvent>().Count(value => value.Kind == AgentSessionUpdateKind.CompactionStarted));
        var busy = f.Compact();
        Assert.IsNull(await f.Wait(busy));
        Assert.AreEqual(1, f.Executor.SummaryCalls);
        var after = await f.Wait(f.Keep(f.Store.ReadEventsAsync("openai-responses", "idle-compaction-fixture", f.SessionId)));
        Assert.AreEqual(1, after.OfType<AgentSessionUpdateEvent>().Count(value => value.Kind == AgentSessionUpdateKind.CompactionStarted));
        Assert.IsFalse(compact.IsCompleted);
        f.Executor.ReleaseSummary.TrySetResult();
        var outcome = await f.Wait(compact);
        Assert.IsNotNull(outcome);
        Assert.IsTrue(outcome.Success);
        Assert.IsTrue(outcome.MessagesRemoved > 0, "Must exercise real summarization, not just a no-op.");
        var history = await f.Wait(f.Keep(f.Store.ReadEventsAsync("openai-responses", "idle-compaction-fixture", f.SessionId)));
        var checkpointEvent = history.OfType<AgentRawEvent>().Single(value => value.BackendEventType == "local.compactionCheckpoint");
        var checkpoint = checkpointEvent.Raw.Deserialize(AgentJsonSerializerContext.Default.AgentCompactionCheckpoint);
        Assert.IsNotNull(checkpoint);
        Assert.IsTrue(checkpoint.SummarizedMessageCount > 0);
        Assert.IsTrue(checkpoint.SummaryCallCount > 0);
        Assert.IsTrue(history.OfType<AgentSessionUpdateEvent>().Any(value => value.Kind == AgentSessionUpdateKind.CompactionCompleted));
    });

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _work = [];
        private readonly List<Exception> _failures = [];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-idle-compaction-" + Guid.NewGuid().ToString("N"));
        internal string SessionId { get; } = Guid.CreateVersion7().ToString();
        internal ScriptedExecutor Executor { get; } = new();
        internal AgentSession Session { get; private set; } = null!;
        internal FileSystemAgentSessionStore Store { get; private set; } = null!;
        internal CancellationToken Token { get; private set; }
        private Task? _lifetime;

        internal Task<T> Keep<T>(Task<T> task) { lock (_gate) _work.Add(task); return task; }
        internal Task Keep(Task task) { lock (_gate) _work.Add(task); return task; }
        internal Task<T> Wait<T>(Task<T> task) => Keep(task.WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<AgentRunId> Send(string text) => Keep(Session.SendAsync(new AgentSendOptions { Input = AgentInput.Text(text) }, Token));
        internal Task<AgentCompactionOutcome?> Compact() => Keep(((IAgentIdleCompactionProvider)Session).TryCompactWhenIdleAsync(Token));

        internal async Task Ready(Task signal, Task operation)
        {
            await Wait(Keep(Task.WhenAny(signal, operation)));
            Assert.IsTrue(signal.IsCompletedSuccessfully, "Operation settled before scripted readiness.");
        }

        internal static async Task RunAsync(Func<Fixture, Task> body)
        {
            var f = new Fixture();
            f._lifetime = f.RunOwnedAsync(body);
            try { await f._lifetime.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception ex) { lock (f._gate) f._failures.Add(ex); }
            finally
            {
                // A timeout is permanent failure, never permission to dispose a live session/source.
                // Late fixture stages also see these released gates. The original lifetime owns disposal.
                f.Executor.ReleaseAll();
                try { await f._lifetime.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { lock (f._gate) f._failures.Add(ex); }
            }
            Exception[] failures;
            lock (f._gate) failures = [.. f._failures];
            // Task-owned roots deliberately remain for audit, including all failed/timeout runs.
            if (failures.Length > 0)
            {
                var error = new AggregateException("Idle compaction fixture retained at " + f._root, failures);
                error.Data["RetainedFixture"] = f;
                throw error;
            }
        }

        private async Task RunOwnedAsync(Func<Fixture, Task> body)
        {
            using var cancellation = new CancellationTokenSource();
            Token = cancellation.Token;
            try
            {
                for (var node = new DirectoryInfo(Path.GetDirectoryName(_root)!); node is not null; node = node.Parent)
                    if ((node.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse fixture ancestry.");
                if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root already exists.");
                var working = Path.Combine(_root, "work");
                Directory.CreateDirectory(working);
                Store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(_root, "store")));
                var now = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
                var provider = new ModelProviderRuntimeDescriptor
                {
                    ProtocolFamily = "openai-responses", ProviderKey = "idle-compaction-fixture", DisplayName = "Inert compaction fixture",
                    TransportKind = AgentTransportKind.OpenAIResponses, BaseUri = new Uri("https://idle-compaction-fixture.invalid/"),
                    Profile = new AgentProviderProfile(), Compaction = AgentCompactionSettings.Default with { Enabled = false },
                };
                var summary = new AgentSessionSummary
                {
                    SessionId = SessionId, ProviderId = ModelProviderIds.OpenAIResponses, ProtocolFamily = provider.ProtocolFamily,
                    ProviderKey = provider.ProviderKey, ModelId = "fixture-model", WorkingDirectory = working, CreatedAt = now, UpdatedAt = now,
                };
                var state = new AgentSessionState { SessionId = SessionId, ProtocolFamily = provider.ProtocolFamily, ProviderKey = provider.ProviderKey, UpdatedAt = now };
                await Keep(Store.UpsertSessionAsync(summary, Token));
                await Keep(Store.UpsertStateAsync(state, Token));
                var options = new AgentSessionCreateOptions
                {
                    ProviderKey = provider.ProviderKey, Model = summary.ModelId, WorkingDirectory = working, ProjectRoots = [working],
                    InstructionsAlreadyComposed = true, SystemMessage = "Isolated fixture instructions.", DeveloperInstructions = "No instruction discovery.",
                    Tools = [], OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
                    OnUserInputRequest = static (_, _) => throw new InvalidOperationException("No user input in text-only fixture."),
                };
                Session = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], Store, Executor, options,
                    cachedModels: [new AgentModelInfo("fixture-model", "Fixture model")]);
                // The lifetime awaits this original body; it is not included in its own dependency join.
                await body(this);
            }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
            finally
            {
                Executor.ReleaseAll();
                var cancel = Keep(cancellation.CancelAsync());
                Task[] work;
                lock (_gate) work = [.. _work];
                // Observe every original concurrently; no timeout here can release a still-live source.
                var joins = work.Select(Join).ToArray();
                await Task.WhenAll(joins);
                if (Session is not null)
                {
                    var disposal = Session.DisposeAsync().AsTask();
                    await Join(disposal);
                }
            }
        }

        private async Task Join(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
        }
    }

    private sealed class ScriptedExecutor : IModelProviderTurnExecutor
    {
        internal bool HoldTurn { get; set; }
        internal bool HoldSummary { get; set; }
        internal TaskCompletionSource TurnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SummaryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseTurn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSummary { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _summaryCalls;
        internal int SummaryCalls => Volatile.Read(ref _summaryCalls);
        internal void ReleaseAll() { ReleaseTurn.TrySetResult(); ReleaseSummary.TrySetResult(); }

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summary = request.RunId.Value.StartsWith("compaction-summary:", StringComparison.Ordinal);
            if (summary)
            {
                Assert.AreEqual(0, request.Tools.Count);
                Interlocked.Increment(ref _summaryCalls);
                SummaryStarted.TrySetResult();
                if (HoldSummary) await ReleaseSummary.Task.ConfigureAwait(false);
            }
            else
            {
                TurnStarted.TrySetResult();
                if (HoldTurn) await ReleaseTurn.Task.ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant,
                    [new AgentMessagePart.Text(summary ? """
                        ## Objective
                        Continue the inert fixture task.
                        ## Active User Request
                        Second prompt.
                        ## Constraints
                        - Text only.
                        ## Progress
                        ### Done
                        - First answer recorded.
                        ### In Progress
                        - Second answer recorded.
                        ### Blocked
                        - None.
                        ## Decisions
                        - Preserve recent context.
                        ## Next Steps
                        - Continue from the retained suffix.
                        ## Critical Context
                        - No tools or network.
                        ## Relevant Files
                        - None.
                        """ : "Inert answer " + new string('a', 180))]),
            };
        }
    }
}
