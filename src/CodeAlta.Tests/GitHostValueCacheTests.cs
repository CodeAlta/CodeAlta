using CodeAlta.Plugin.Git;

namespace CodeAlta.Tests;

[TestClass]
public sealed class GitHostValueCacheTests
{
    [TestMethod]
    public async Task AnAnswer_IsKeptForItsDuration_ARefusalIsNot_AndAFullCacheMakesRoom()
    {
        var clock = new Clock();
        var cache = new GitHostValueCache(clock, TimeSpan.FromMinutes(5));
        var reads = 0;
        Task<string?> Read() { reads++; return Task.FromResult<string?>("answer " + reads); }

        Assert.AreEqual("answer 1", await cache.GetAsync("teams", Read, default));
        clock.Now += TimeSpan.FromMinutes(4);
        Assert.AreEqual("answer 1", await cache.GetAsync("teams", Read, default), "Kept while it is fresh.");
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.AreEqual("answer 2", await cache.GetAsync("teams", Read, default), "Read again once it is stale.");

        Assert.IsNull(await cache.GetAsync<string>("refused", static () => Task.FromResult<string?>(null), default));
        Assert.AreEqual("kept now", await cache.GetAsync("refused", static () => Task.FromResult<string?>("kept now"), default), "A refusal is asked again.");

        for (var index = 0; index < 300; index++) await cache.GetAsync("key " + index, static () => Task.FromResult<string?>("value"), default);
        Assert.AreEqual("answer 3", await cache.GetAsync("teams", Read, default), "A full cache was emptied to make room.");
    }

    [TestMethod]
    public async Task CallersOfTheSameKey_WaitForOneRead_AndAFailedReadIsNotKept()
    {
        var cache = new GitHostValueCache(new Clock(), TimeSpan.FromMinutes(5));
        var reads = 0;
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> Read() { Interlocked.Increment(ref reads); return answer.Task; }

        var waiting = Enumerable.Range(0, 5).Select(_ => cache.GetAsync("types", Read, default)).ToArray();
        var other = cache.GetAsync("teams", static () => Task.FromResult<string?>("teams"), default);
        answer.SetResult("types");

        CollectionAssert.AreEqual(Enumerable.Repeat("types", 5).ToArray(), await Task.WhenAll(waiting));
        Assert.AreEqual(1, reads, "One read for all the callers of a key.");
        Assert.AreEqual("teams", await other, "Another key is read by itself.");
        Assert.AreEqual("types", await cache.GetAsync("types", Read, default));
        Assert.AreEqual(1, reads, "And then kept.");

        var failing = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new[] { cache.GetAsync("fields", () => failing.Task, default), cache.GetAsync("fields", () => failing.Task, default) };
        failing.SetException(new HttpRequestException("refused"));
        foreach (var one in failed) await Assert.ThrowsExactlyAsync<HttpRequestException>(() => one);
        Assert.AreEqual("read again", await cache.GetAsync("fields", static () => Task.FromResult<string?>("read again"), default), "A failure is not kept.");
    }

    [TestMethod]
    public async Task ACallerThatWaits_ReadsItself_WhenTheCallerThatReadIsCanceled()
    {
        var cache = new GitHostValueCache(new Clock(), TimeSpan.FromMinutes(5));
        using var canceled = new CancellationTokenSource();
        var reading = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cache.GetAsync("teams", async () => await reading.Task.WaitAsync(canceled.Token), canceled.Token);
        var reads = 0;
        Task<string?> ReadAgain() { Interlocked.Increment(ref reads); return Task.FromResult<string?>("read again"); }
        var others = new[] { cache.GetAsync("teams", ReadAgain, default), cache.GetAsync("teams", ReadAgain, default) };

        await canceled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        CollectionAssert.AreEqual(new[] { "read again", "read again" }, await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, reads, "One of those that waited reads in its place, for the others too.");
    }

    [TestMethod]
    public async Task ACallerThatWaits_StopsWaiting_WhenItIsCanceled_AndTheReadGoesOn()
    {
        var cache = new GitHostValueCache(new Clock(), TimeSpan.FromMinutes(5));
        var reading = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cache.GetAsync("teams", () => reading.Task, default);
        using var canceled = new CancellationTokenSource();
        var second = cache.GetAsync("teams", static () => Task.FromResult<string?>("not read"), canceled.Token);

        await canceled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(10)), "It stops at once, while the read it waited for goes on.");
        Assert.IsFalse(first.IsCompleted);
        reading.SetResult("teams");
        Assert.AreEqual("teams", await first);
    }

    [TestMethod]
    public async Task ATimeoutOfTheRead_IsAFailureForThoseThatWait_AndIsNotReadAgainForThem()
    {
        var cache = new GitHostValueCache(new Clock(), TimeSpan.FromMinutes(5));
        var reads = 0;
        var reading = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> Read() { Interlocked.Increment(ref reads); return reading.Task; }
        var waiting = Enumerable.Range(0, 3).Select(_ => cache.GetAsync("teams", Read, default)).ToArray();

        // What a client throws when its timeout passes: its token was not canceled by any caller.
        reading.SetException(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));

        foreach (var one in waiting) await Assert.ThrowsAsync<TaskCanceledException>(() => one.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, reads, "Those that waited do not wait for another timeout.");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
