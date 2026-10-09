using CodeAlta.Agent;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentInputTokenUsageTests
{
    [TestMethod]
    public void From_TakesTheCachedPartsOutOfTheInputThatHoldsThem()
    {
        // A request of 26,317 input tokens of which 26,003 came from the cache and 312 were written to it.
        var usage = AgentInputTokenUsage.From(new AgentOperationUsageSnapshot(
            InputTokens: 26_317, OutputTokens: 123, CacheWriteTokens: 312, CachedInputTokens: 26_003));

        Assert.AreEqual(new AgentInputTokenUsage(Total: 26_317, Uncached: 2, CacheRead: 26_003, CacheWrite: 312), usage);
    }

    [TestMethod]
    public void From_ReadsTheCacheReadCountAsAnotherNameOfTheCachedInput()
    {
        var both = AgentInputTokenUsage.From(new AgentOperationUsageSnapshot(
            InputTokens: 2_400, CacheReadTokens: 2_000, CacheWriteTokens: 300, CachedInputTokens: 2_000));
        var readOnly = AgentInputTokenUsage.From(new AgentOperationUsageSnapshot(InputTokens: 47_307, CacheReadTokens: 47_104, CacheWriteTokens: 0));

        Assert.AreEqual(new AgentInputTokenUsage(2_400, 100, 2_000, 300), both);
        Assert.AreEqual(new AgentInputTokenUsage(47_307, 203, 47_104, 0), readOnly);
    }

    [TestMethod]
    public void From_ReadsAnOlderRecordThatCountedTheCacheBesideTheInput()
    {
        // Less input than cache: the input of such a record is what was not cached.
        var usage = AgentInputTokenUsage.From(new AgentOperationUsageSnapshot(
            InputTokens: 100, CacheReadTokens: 2_000, CacheWriteTokens: 300, CachedInputTokens: 2_000));

        Assert.AreEqual(new AgentInputTokenUsage(2_400, 100, 2_000, 300), usage);
    }

    [TestMethod]
    public void From_KeepsAnInputWithoutCacheWhole_AndGivesNothingForNoInput()
    {
        Assert.AreEqual(new AgentInputTokenUsage(120, 120, 0, 0), AgentInputTokenUsage.From(new AgentOperationUsageSnapshot(InputTokens: 120, OutputTokens: 30)));
        Assert.AreEqual(new AgentInputTokenUsage(50, 0, 50, 0), AgentInputTokenUsage.From(new AgentOperationUsageSnapshot(CachedInputTokens: 50)));
        Assert.IsNull(AgentInputTokenUsage.From(new AgentOperationUsageSnapshot(OutputTokens: 30)));
        Assert.IsNull(AgentInputTokenUsage.From(null));
    }
}
