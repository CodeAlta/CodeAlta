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

        Assert.AreEqual("answer 1", await cache.GetAsync("teams", Read));
        clock.Now += TimeSpan.FromMinutes(4);
        Assert.AreEqual("answer 1", await cache.GetAsync("teams", Read), "Kept while it is fresh.");
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.AreEqual("answer 2", await cache.GetAsync("teams", Read), "Read again once it is stale.");

        Assert.IsNull(await cache.GetAsync<string>("refused", static () => Task.FromResult<string?>(null)));
        Assert.AreEqual("kept now", await cache.GetAsync("refused", static () => Task.FromResult<string?>("kept now")), "A refusal is asked again.");

        for (var index = 0; index < 300; index++) await cache.GetAsync("key " + index, static () => Task.FromResult<string?>("value"));
        Assert.AreEqual("answer 3", await cache.GetAsync("teams", Read), "A full cache was emptied to make room.");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
