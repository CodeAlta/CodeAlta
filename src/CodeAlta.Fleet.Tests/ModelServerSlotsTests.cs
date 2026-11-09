using System.Net;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Fleet.Client;
using CodeAlta.ModelServers;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The slots of the models of a server (<c>doc/fleet/model-servers.md</c>): the requests of inference beyond them wait on the machine
/// of the server, first in first out, each told its place, and the answer of a request holds its slot until it is read.
/// </summary>
[TestClass]
public sealed class ModelServerSlotsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AOneSlotModel_ServesItsRequestsOneAtATime_InOrder_EachToldItsPlace()
    {
        var token = TestContext.CancellationToken;
        var gate = new ModelSlotGate();
        gate.SetLimit(1);
        var first = await gate.AcquireAsync(null, token);
        var places = new List<(string Request, int Before)>();
        var gotSecond = gate.AcquireAsync((before, _) => { lock (places) places.Add(("second", before)); return ValueTask.CompletedTask; }, token).AsTask();
        var gotThird = gate.AcquireAsync((before, _) => { lock (places) places.Add(("third", before)); return ValueTask.CompletedTask; }, token).AsTask();
        await WaitUntilAsync(() => gate.WaitingCount == 2 && places.Count == 2, token);
        Assert.IsFalse(gotSecond.IsCompleted || gotThird.IsCompleted);

        first.Dispose();
        var second = await gotSecond.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.IsFalse(gotThird.IsCompleted, "one slot: the third waits for the second");
        await WaitUntilAsync(() => places.Contains(("third", 0)), token);
        second.Dispose();
        (await gotThird.WaitAsync(TimeSpan.FromSeconds(10), token)).Dispose();

        CollectionAssert.AreEqual(new[] { ("second", 0), ("third", 1), ("third", 0) }, places.ToArray());
        Assert.AreEqual((0, 0), (gate.ActiveCount, gate.WaitingCount));
    }

    [TestMethod]
    public async Task ARequestWhoseCallerGoes_LeavesTheQueue_AndTheNextMovesUp()
    {
        var token = TestContext.CancellationToken;
        var gate = new ModelSlotGate();
        gate.SetLimit(1);
        using var held = await gate.AcquireAsync(null, token);
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(token);
        var leaving = gate.AcquireAsync(null, gone.Token).AsTask();
        var places = new List<int>();
        var staying = gate.AcquireAsync((before, _) => { lock (places) places.Add(before); return ValueTask.CompletedTask; }, token).AsTask();
        await WaitUntilAsync(() => places.Contains(1), token);

        await gone.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => leaving);
        await WaitUntilAsync(() => places.Contains(0), token);
        Assert.AreEqual(1, gate.WaitingCount);

        // A higher limit (the server reports more slots) admits it at once.
        gate.SetLimit(2);
        (await staying.WaitAsync(TimeSpan.FromSeconds(10), token)).Dispose();
    }

    [TestMethod]
    public async Task AWaitWhoseCallbackFails_GivesBackTheSlotItWasGivenMeanwhile()
    {
        var token = TestContext.CancellationToken;
        var gate = new ModelSlotGate();
        gate.SetLimit(1);
        var held = await gate.AcquireAsync(null, token);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Its callback is still at work when the slot comes, then throws a cancellation (its caller went).
        var failing = gate.AcquireAsync(async (_, _) =>
        {
            reported.TrySetResult();
            await release.Task;
            throw new OperationCanceledException();
        }, token).AsTask();
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        held.Dispose();
        release.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => failing);

        Assert.AreEqual((0, 0), (gate.ActiveCount, gate.WaitingCount), "the slot it was given is not lost");
        (await gate.AcquireAsync(null, token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token)).Dispose();
    }

    [TestMethod]
    public async Task TheHost_SizesTheSlotsOfAModelBySettings_AndListsTheRequestsThatWait()
    {
        var token = TestContext.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "codealta-slots-" + Guid.NewGuid().ToString("N"));
        await using var host = new ModelHost(new ModelHostOptions
        {
            SharedRoot = Path.Combine(root, "shared"), InstanceRoot = Path.Combine(root, "instance"), FirstPort = 19951, NetworkHandler = new NotFoundHandler(),
        });
        try
        {
            await host.SetServerAsync(new ServerDefinition
            {
                Id = "coder", Name = "Coder", Backend = "cpu", Enabled = false, Models = [new ServerModel { Model = "a/b:Q4_K_M", Parallel = 1 }],
            }, token);

            // A request that names no model is for the one model of the server.
            var (gate, model) = host.SlotsOf("coder", null)!.Value;
            Assert.AreEqual("a/b:Q4_K_M", model);
            Assert.AreSame(gate, host.SlotsOf("coder", "a/b:Q4_K_M")!.Value.Gate);
            Assert.IsNull(host.SlotsOf("nowhere", null));
            using var busy = await gate.AcquireAsync(null, token);
            var waiting = gate.AcquireAsync(null, token).AsTask();
            await WaitUntilAsync(() => gate.WaitingCount == 1, token);

            var listed = host.ListServers().Single();
            Assert.AreEqual(0, listed.Status.Models.Count, "a server that does not run lists no model");
            Assert.AreEqual(1, host.Slots.Waiting("coder", "a/b:Q4_K_M"));
            busy.Dispose();
            (await waiting.WaitAsync(TimeSpan.FromSeconds(10), token)).Dispose();
            Assert.AreEqual(0, host.Slots.Waiting("coder", "a/b:Q4_K_M"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void ARequestOfInference_TakesASlotOfTheModelItNames()
    {
        Assert.IsTrue(ModelServerSlotWait.TakesSlot("POST", "/v1/chat/completions"));
        Assert.IsTrue(ModelServerSlotWait.TakesSlot("POST", "/v1/embeddings?x=1"));
        Assert.IsFalse(ModelServerSlotWait.TakesSlot("GET", "/v1/models"));
        Assert.IsFalse(ModelServerSlotWait.TakesSlot("POST", "/tokenize"));
        Assert.AreEqual("org/repo:Q8_0", ModelServerSlotWait.ModelOf("{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"model\":\"org/repo:Q8_0\",\"stream\":true}"u8));
        Assert.IsNull(ModelServerSlotWait.ModelOf("{\"messages\":[]}"u8));
        Assert.IsNull(ModelServerSlotWait.ModelOf("not json"u8));
        Assert.IsNull(ModelServerSlotWait.ModelOf([]));
    }

    [TestMethod]
    public async Task TheAnswer_HoldsItsSlot_UntilItsBodyIsRead_AndAWaitTellsTheSession()
    {
        var token = TestContext.CancellationToken;
        var gate = new ModelSlotGate();
        gate.SetLimit(1);
        var updates = new List<AgentTurnSessionUpdate>();
        ValueTask Record(AgentTurnSessionUpdate update, CancellationToken _) { lock (updates) updates.Add(update); return ValueTask.CompletedTask; }

        var slot = await ModelServerSlotWait.AcquireAsync(gate, "pc-ai/coder", Record, token);
        Assert.IsEmpty(updates, "a free slot tells nothing");
        using var response = await ModelServerSlotWait.HoldUntilReadAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: done") }, slot, token);
        var next = ModelServerSlotWait.AcquireAsync(gate, "pc-ai/coder", Record, token);
        await WaitUntilAsync(() => updates.Count == 1, token);
        Assert.AreEqual("Waits for a free slot of pc-ai/coder (next in line)", updates[0].Message);

        Assert.AreEqual("data: done", await response.Content.ReadAsStringAsync(token));
        (await next.WaitAsync(TimeSpan.FromSeconds(10), token)).Dispose();
        await WaitUntilAsync(() => updates.Count == 2, token);
        Assert.IsTrue(AgentWaitNotice.IsEnded(updates[1].Details), "the notice is removed once the request has its slot");
    }

    [TestMethod]
    public async Task TheRequestsOfASession_CarryItsUpdates_ToTheTransportOfTheirModelServer()
    {
        var token = TestContext.CancellationToken;
        var registry = new ModelProviderRegistry();
        var seen = new CapturingHandler();
        registry.ModelServerTransports = _ => seen;
        using var client = new HttpClient(registry.ModelServerTransports!("pc-ai/coder")!);
        ValueTask Updates(AgentTurnSessionUpdate update, CancellationToken _) => ValueTask.CompletedTask;

        using (registry.WaitScope.Enter(Updates))
            using (await client.GetAsync("http://model-server.invalid/v1/chat/completions", token)) { }
        Assert.IsTrue(seen.Carried, "within the request of a session");
        using (await client.GetAsync("http://model-server.invalid/v1/models", token)) { }
        Assert.IsFalse(seen.Carried, "outside of it");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("It did not get there in time.");
            await Task.Delay(10, token);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public bool Carried { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Carried = ModelServerSlotWait.UpdatesOf(request) is not null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request, Content = new StringContent("{}") });
    }
}
