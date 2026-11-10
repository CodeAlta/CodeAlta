using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ProviderRequestGateTests
{
    private static readonly TimeSpan Timeout = TestTime.Seconds(10);

    [TestMethod]
    public async Task AcquireAsync_AdmitsWaitersInTheOrderTheyCameAsync()
    {
        var gate = new ProviderRequestGate("spark-vllm", 1);
        var first = await gate.AcquireAsync();
        var order = new ConcurrentQueue<string>();
        var waiters = new List<Task<IDisposable>>();
        foreach (var name in new[] { "b", "c", "d" })
        {
            waiters.Add(TakeAsync(gate, name, order));
            await WaitUntilAsync(() => gate.WaitingCount == waiters.Count);
        }

        first.Dispose();
        (await waiters[0].WithinAsync(Timeout)).Dispose();
        (await waiters[1].WithinAsync(Timeout)).Dispose();
        (await waiters[2].WithinAsync(Timeout)).Dispose();

        CollectionAssert.AreEqual(new[] { "b", "c", "d" }, order.ToArray());
        Assert.AreEqual(0, gate.ActiveCount);
        Assert.AreEqual(0, gate.WaitingCount);
    }

    [TestMethod]
    public async Task AcquireAsync_SaysThePlaceOfEachWaiterAndEachChangeAsync()
    {
        var gate = new ProviderRequestGate("spark-vllm", 1);
        var first = await gate.AcquireAsync();
        var placesOfB = new ConcurrentQueue<int>();
        var placesOfC = new ConcurrentQueue<int>();
        var b = gate.AcquireAsync(Record(placesOfB)).AsTask();
        await WaitUntilAsync(() => placesOfB.Count == 1);
        var c = gate.AcquireAsync(Record(placesOfC)).AsTask();
        await WaitUntilAsync(() => placesOfC.Count == 1);

        first.Dispose();
        var slotOfB = await b.WithinAsync(Timeout);
        await WaitUntilAsync(() => placesOfC.Count == 2);
        slotOfB.Dispose();
        (await c.WithinAsync(Timeout)).Dispose();

        CollectionAssert.AreEqual(new[] { 0 }, placesOfB.ToArray());
        CollectionAssert.AreEqual(new[] { 1, 0 }, placesOfC.ToArray());
    }

    [TestMethod]
    public async Task AcquireAsync_FreeSlotSaysNothingAsync()
    {
        var gate = new ProviderRequestGate("spark-vllm", 2);
        var places = new ConcurrentQueue<int>();

        using var first = await gate.AcquireAsync(Record(places));
        using var second = await gate.AcquireAsync(Record(places));

        Assert.IsEmpty(places);
        Assert.AreEqual(2, gate.ActiveCount);
    }

    [TestMethod]
    public async Task AcquireAsync_NoLimitNeverWaitsAsync()
    {
        var gate = new ProviderRequestGate("unbounded", null);
        var slots = new List<IDisposable>();
        for (var index = 0; index < 50; index++)
        {
            slots.Add(await gate.AcquireAsync());
        }

        Assert.AreEqual(50, gate.ActiveCount);
        slots.ForEach(static slot => slot.Dispose());
        Assert.AreEqual(0, gate.ActiveCount);
    }

    [TestMethod]
    public async Task AcquireAsync_CancelledWaiterLeavesTheQueueWithoutASlotAsync()
    {
        var gate = new ProviderRequestGate("spark-vllm", 1);
        var first = await gate.AcquireAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelled = gate.AcquireAsync(cancellation.Token).AsTask();
        await WaitUntilAsync(() => gate.WaitingCount == 1);
        var placesOfNext = new ConcurrentQueue<int>();
        var next = gate.AcquireAsync(Record(placesOfNext)).AsTask();
        await WaitUntilAsync(() => placesOfNext.Count == 1);

        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.WithinAsync(Timeout));
        await WaitUntilAsync(() => placesOfNext.Count == 2);
        CollectionAssert.AreEqual(new[] { 1, 0 }, placesOfNext.ToArray());
        Assert.AreEqual(1, gate.WaitingCount);
        first.Dispose();
        (await next.WithinAsync(Timeout)).Dispose();
        Assert.AreEqual(0, gate.ActiveCount);
    }

    [TestMethod]
    public async Task AcquireAsync_AlreadyCancelledThrowsAtOnceAsync()
    {
        var gate = new ProviderRequestGate("spark-vllm", 1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => gate.AcquireAsync(cancellation.Token).AsTask());
        Assert.AreEqual(0, gate.ActiveCount);
    }

    [TestMethod]
    public async Task AcquireAsync_CallbackThatFailsLeavesTheQueueAsync()
    {
        var gate = new ProviderRequestGate("spark-vllm", 1);
        var first = await gate.AcquireAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            gate.AcquireAsync((_, _) => throw new InvalidOperationException("session gone")).AsTask());

        Assert.AreEqual(0, gate.WaitingCount);
        first.Dispose();
        Assert.AreEqual(0, gate.ActiveCount);
    }

    [TestMethod]
    public async Task SetLimit_HigherLimitAdmitsWaitersInOrderAsync()
    {
        var gate = new ProviderRequestGate("spark-vllm", 1);
        using var first = await gate.AcquireAsync();
        var order = new ConcurrentQueue<string>();
        var b = TakeAsync(gate, "b", order);
        await WaitUntilAsync(() => gate.WaitingCount == 1);
        var c = TakeAsync(gate, "c", order);
        await WaitUntilAsync(() => gate.WaitingCount == 2);
        var d = TakeAsync(gate, "d", order);
        await WaitUntilAsync(() => gate.WaitingCount == 3);

        // One more slot at a time: the waiters admitted together by one limit record their names in whatever order
        // their continuations run, which says nothing of the order of the gate.
        gate.SetLimit(2);
        using var slotOfB = await b.WithinAsync(Timeout);
        Assert.IsFalse(c.IsCompleted);
        Assert.IsFalse(d.IsCompleted);

        gate.SetLimit(3);
        using var slotOfC = await c.WithinAsync(Timeout);
        Assert.IsFalse(d.IsCompleted);
        Assert.AreEqual(3, gate.ActiveCount);
        Assert.AreEqual(1, gate.WaitingCount);

        gate.SetLimit(null);
        (await d.WithinAsync(Timeout)).Dispose();
        CollectionAssert.AreEqual(new[] { "b", "c", "d" }, order.ToArray());
    }

    [TestMethod]
    public async Task SetLimit_LowerLimitKeepsTheSlotsTakenAndAdmitsNobodyUnderItAsync()
    {
        var gate = new ProviderRequestGate("spark-vllm", 3);
        var first = await gate.AcquireAsync();
        var second = await gate.AcquireAsync();
        var third = await gate.AcquireAsync();

        gate.SetLimit(1);
        var waiter = gate.AcquireAsync().AsTask();
        await WaitUntilAsync(() => gate.WaitingCount == 1);
        first.Dispose();
        second.Dispose();
        Assert.IsFalse(waiter.IsCompleted);
        Assert.AreEqual(1, gate.ActiveCount);

        third.Dispose();
        (await waiter.WithinAsync(Timeout)).Dispose();
        Assert.AreEqual(0, gate.ActiveCount);
    }

    [TestMethod]
    public void Constructor_RefusesALimitOfZero()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProviderRequestGate("spark-vllm", 0));
        var gate = new ProviderRequestGate("spark-vllm", 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => gate.SetLimit(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ModelProviderDescriptor(new ModelProviderId("p"), "P") { MaxConcurrentRequests = 0 });
    }

    [TestMethod]
    public async Task Registry_KeepsTheGateAcrossRegisterOrReplaceAndChangesItsLimitAsync()
    {
        await using var registry = new ModelProviderRegistry();
        var id = new ModelProviderId("spark-vllm");
        Assert.IsNull(registry.GetRequestGate(id));
        var descriptor = new ModelProviderDescriptor(id, "Spark", "openai-chat") { MaxConcurrentRequests = 1 };
        registry.RegisterOrReplace(descriptor, () => throw new InvalidOperationException("not created"));
        var gate = registry.GetRequestGate(id)!;
        Assert.AreEqual(1, gate.Limit);
        Assert.AreEqual("spark-vllm", gate.Name);
        using var held = await gate.AcquireAsync();
        var waiting = gate.AcquireAsync().AsTask();
        await WaitUntilAsync(() => gate.WaitingCount == 1);

        registry.RegisterOrReplace(descriptor with { MaxConcurrentRequests = 2 }, () => throw new InvalidOperationException("not created"));

        Assert.AreSame(gate, registry.GetRequestGate(new ModelProviderId("SPARK-VLLM")));
        Assert.AreEqual(2, gate.Limit);
        (await waiting.WithinAsync(Timeout)).Dispose();

        registry.RegisterOrReplace(descriptor with { MaxConcurrentRequests = null }, () => throw new InvalidOperationException("not created"));
        Assert.IsNull(gate.Limit);
    }

    [TestMethod]
    public async Task GatedExecutor_HoldsTheSlotForTheStreamAndSaysTheWaitAsync()
    {
        var gate = new ProviderRequestGate("spark-vllm", 1);
        var inner = new BlockingTurnExecutor();
        var executor = new ProviderRequestGatedTurnExecutor(inner, gate);
        var updatesOfSecond = new ConcurrentQueue<AgentTurnSessionUpdate>();

        var first = executor.ExecuteTurnAsync(CreateRequest("one"), NoDelta, NoUpdate);
        await inner.StartOf("one").WithinAsync(Timeout);
        var second = executor.ExecuteTurnAsync(CreateRequest("two"), NoDelta, (update, _) =>
        {
            updatesOfSecond.Enqueue(update);
            return ValueTask.CompletedTask;
        });
        await WaitUntilAsync(() => updatesOfSecond.Count == 1);
        Assert.IsFalse(inner.Started.ContainsKey("two") && inner.Started["two"].Task.IsCompleted, "The second request waits while the first one streams.");

        inner.Finish("one");
        await first.WithinAsync(Timeout);
        await inner.StartOf("two").WithinAsync(Timeout);
        inner.Finish("two");
        await second.WithinAsync(Timeout);

        var updates = updatesOfSecond.ToArray();
        Assert.HasCount(2, updates);
        Assert.AreEqual(AgentSessionUpdateKind.Info, updates[0].Kind);
        Assert.AreEqual("Waits for a free request of spark-vllm (next in line)", updates[0].Message);
        Assert.IsFalse(AgentWaitNotice.IsEnded(updates[0].Details));
        Assert.IsTrue(AgentWaitNotice.IsEnded(updates[1].Details));
        Assert.AreEqual(updates[0].Details!.Value.GetProperty("waitId").GetString(), updates[1].Details!.Value.GetProperty("waitId").GetString());
        Assert.IsFalse(updates[0].Durable);
        Assert.AreEqual(0, gate.ActiveCount);
    }

    [TestMethod]
    public async Task GatedExecutor_ReleasesTheSlotWhenTheRequestFailsAsync()
    {
        var gate = new ProviderRequestGate("spark-vllm", 1);
        var executor = new ProviderRequestGatedTurnExecutor(new FailingTurnExecutor(), gate);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteTurnAsync(CreateRequest("one"), NoDelta));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteTurnAsync(CreateRequest("two"), NoDelta, NoUpdate));

        Assert.AreEqual(0, gate.ActiveCount);
        Assert.AreEqual(0, gate.WaitingCount);
    }

    [TestMethod]
    public void WaitNotice_NamesTheWaitAndItsPlace()
    {
        var waiting = AgentWaitNotice.CreateUpdate(AgentWaitKind.ModelServerSlot, "w1", "pc-ai/coder", 2);
        var ended = AgentWaitNotice.CreateUpdate(AgentWaitKind.ModelServerSlot, "w1", "pc-ai/coder", null);

        Assert.AreEqual("Waits for a free slot of pc-ai/coder (2 before it)", waiting.Message);
        Assert.AreEqual("model-server-slot", waiting.Details!.Value.GetProperty("wait").GetString());
        Assert.AreEqual(2, waiting.Details!.Value.GetProperty("before").GetInt32());
        Assert.AreEqual("waiting", waiting.Details!.Value.GetProperty("state").GetString());
        Assert.IsTrue(AgentWaitNotice.IsEnded(ended.Details));
        Assert.IsFalse(AgentWaitNotice.IsEnded(null));
    }

    [TestMethod]
    public async Task WaitNotice_SaysEachPlaceOnceAndEndsOnlyWhenShownAsync()
    {
        var updates = new List<AgentTurnSessionUpdate>();
        var notice = new AgentWaitNotice(AgentWaitKind.ProviderRequest, "spark-vllm", (update, _) =>
        {
            updates.Add(update);
            return ValueTask.CompletedTask;
        });

        await notice.EndAsync();
        Assert.IsEmpty(updates);
        await notice.ReportAsync(1);
        await notice.ReportAsync(1);
        await notice.ReportAsync(0);
        Assert.IsTrue(notice.IsShown);
        await notice.EndAsync();
        await notice.EndAsync();

        CollectionAssert.AreEqual(
            new[] { "Waits for a free request of spark-vllm (1 before it)", "Waits for a free request of spark-vllm (next in line)", "Got a free request of spark-vllm" },
            updates.Select(static update => update.Message).ToArray());
        Assert.IsFalse(notice.IsShown);
    }

    private static Func<int, CancellationToken, ValueTask> Record(ConcurrentQueue<int> places)
        => (before, _) =>
        {
            places.Enqueue(before);
            return ValueTask.CompletedTask;
        };

    private static async Task<IDisposable> TakeAsync(ProviderRequestGate gate, string name, ConcurrentQueue<string> order)
    {
        var slot = await gate.AcquireAsync().ConfigureAwait(false);
        order.Enqueue(name);
        return slot;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private static ValueTask NoDelta(AgentTurnDelta delta, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private static ValueTask NoUpdate(AgentTurnSessionUpdate update, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private static AgentTurnRequest CreateRequest(string sessionId)
    {
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "openai-chat",
            ProviderKey = "spark-vllm",
            DisplayName = "Spark",
            TransportKind = AgentTransportKind.OpenAIChatCompletions,
        };

        return new AgentTurnRequest
        {
            Provider = provider,
            ProviderId = new ModelProviderId(provider.ProviderKey),
            SessionId = sessionId,
            RunId = new AgentRunId("run-" + sessionId),
            ModelId = "model",
            Conversation = [],
            Tools = [],
            State = new AgentSessionState
            {
                SessionId = sessionId,
                ProtocolFamily = provider.ProtocolFamily,
                ProviderKey = provider.ProviderKey,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };
    }

    private static AgentTurnResponse Answer()
        => new() { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, []) };

    // Streams until the test finishes it: the request is in progress, as a stream is, between its start and its end.
    private sealed class BlockingTurnExecutor : IModelProviderTurnExecutor
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _finished = new();

        public ConcurrentDictionary<string, TaskCompletionSource> Started { get; } = new();

        public Task StartOf(string sessionId) => Started.GetOrAdd(sessionId, static _ => NewSignal()).Task;

        public void Finish(string sessionId) => _finished.GetOrAdd(sessionId, static _ => NewSignal()).TrySetResult();

        public async Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            Started.GetOrAdd(request.SessionId, static _ => NewSignal()).TrySetResult();
            await _finished.GetOrAdd(request.SessionId, static _ => NewSignal()).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Answer();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FailingTurnExecutor : IModelProviderTurnExecutor
    {
        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
            => Task.FromException<AgentTurnResponse>(new InvalidOperationException("The endpoint failed."));
    }
}
