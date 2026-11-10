using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SideQuestionRpcTests
{
    [TestMethod]
    public async Task Stream_ReportsTextThenAnswer_AndRejectsInvalidOrStaleRequests()
    {
        var calls = 0;
        var service = new SideQuestionService(async (session, question, onText, token) =>
        {
            calls++;
            Assert.AreEqual("session", session);
            Assert.AreEqual("question", question);
            await onText("first", token);
            await onText(" second", token);
            return "first second";
        }, "epoch");
        foreach (var request in new SideQuestionRequest?[] { null, new("epoch", "", "q"), new("epoch", "session", new string('x', SideQuestionService.MaximumQuestionLength + 1)) })
        {
            var events = await CollectAsync(service.AskAsync(request, default));
            Assert.AreEqual("invalid_request", events.Single().Status);
        }
        Assert.AreEqual("stale_epoch", (await CollectAsync(service.AskAsync(new("old", "session", "q"), default))).Single().Status);
        Assert.AreEqual(0, calls);
        var received = await CollectAsync(service.AskAsync(new("epoch", "session", " question "), default));
        CollectionAssert.AreEqual(new[] { "text", "text", "done" }, received.Select(static e => e.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { "first", " second", "first second" }, received.Select(static e => e.Value).ToArray());
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ClosingStream_CancelsProvider()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SideQuestionService(async (_, _, onText, token) =>
        {
            await onText("partial", token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return "never";
        }, "epoch");
        await using (var reader = service.AskAsync(new("epoch", "session", "q"), default).GetAsyncEnumerator())
        {
            Assert.IsTrue(await reader.MoveNextAsync());
            Assert.AreEqual("partial", reader.Current.Value);
        }
        await cancelled.Task.WithinAsync(TestTime.Seconds(10));
    }

    [TestMethod]
    public async Task ClosingAFullStream_UnblocksTheWriter()
    {
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SideQuestionService(async (_, _, onText, token) =>
        {
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    if (i == 65) writing.TrySetResult();
                    await onText("x", CancellationToken.None);
                }
            }
            finally { cancelled.TrySetResult(); }
            return "done";
        }, "epoch");
        await using (var reader = service.AskAsync(new("epoch", "session", "q"), default).GetAsyncEnumerator())
        {
            Assert.IsTrue(await reader.MoveNextAsync());
            await writing.Task.WithinAsync(TestTime.Seconds(10));
        }
        await cancelled.Task.WithinAsync(TestTime.Seconds(10));
    }

    [TestMethod]
    public async Task Failure_ProducesAnErrorEvent()
    {
        var service = new SideQuestionService((_, _, _, _) => Task.FromException<string>(new NotSupportedException("provider")), "epoch");
        var result = await CollectAsync(service.AskAsync(new("epoch", "session", "q"), default));
        Assert.AreEqual("unsupported", result.Single().Status);
    }

    private static async Task<List<SideQuestionEvent>> CollectAsync(IAsyncEnumerable<SideQuestionEvent> events)
    {
        var result = new List<SideQuestionEvent>();
        await foreach (var item in events) result.Add(item);
        return result;
    }
}
