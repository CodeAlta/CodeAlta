using System.Text.Json.Serialization;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Tests;

[TestClass]
public sealed class FleetRetryReceiptsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task ACallStillRunning_KeepsItsReceiptPastCapacity_AndARetryWaitsForIt()
    {
        using var folder = new TestFolder();
        var receipts = new FleetRetryReceipts(folder.Path, new ManualTimeProvider(DateTimeOffset.UtcNow), resultCapacity: 1, keyCapacity: 1);
        var gate = new TaskCompletionSource<Item>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var first = receipts.RunOnceAsync("a", "{}", TestJson.Default.Item, () => { Interlocked.Increment(ref runs); return gate.Task; });
        for (var index = 0; index < 4; index++)
            _ = await receipts.RunOnceAsync("other-" + index, "{}", TestJson.Default.Item, () => Task.FromResult(new Item(index.ToString())));

        var retry = receipts.RunOnceAsync("a", "{}", TestJson.Default.Item, () => { Interlocked.Increment(ref runs); return Task.FromResult(new Item("again")); });
        gate.SetResult(new Item("first"));

        Assert.AreEqual("first", (await first.WaitAsync(Patience)).Value);
        Assert.AreEqual("first", (await retry.WaitAsync(Patience)).Value, "A retry of a call that still runs shares it.");
        Assert.AreEqual(1, runs, "The call ran once.");
    }

    [TestMethod]
    public async Task AnEvictedResult_AnswersExpired_AndNeverRunsAgain()
    {
        using var folder = new TestFolder();
        var receipts = new FleetRetryReceipts(folder.Path, new ManualTimeProvider(DateTimeOffset.UtcNow), resultCapacity: 1, keyCapacity: 8);
        _ = await receipts.RunOnceAsync("a", "{}", TestJson.Default.Item, () => Task.FromResult(new Item("a")));
        _ = await receipts.RunOnceAsync("b", "{}", TestJson.Default.Item, () => Task.FromResult(new Item("b")));

        var expired = await Assert.ThrowsExactlyAsync<FleetException>(() => receipts.RunOnceAsync("a", "{}", TestJson.Default.Item,
            () => throw new AssertFailedException("An evicted key never runs again.")));
        Assert.AreEqual(FleetErrorCodes.Expired, expired.Error.Code);
        Assert.AreEqual("b", (await receipts.RunOnceAsync("b", "{}", TestJson.Default.Item, () => throw new AssertFailedException("Kept."))).Value);
    }

    [TestMethod]
    public async Task AfterARestart_AResultIsReplayed_AndACallItInterruptedAnswersExpired()
    {
        using var folder = new TestFolder();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var before = new FleetRetryReceipts(folder.Path, clock);
        _ = await before.RunOnceAsync("done", "{}", TestJson.Default.Item, () => Task.FromResult(new Item("done")));
        var never = new TaskCompletionSource<Item>();
        _ = before.RunOnceAsync("running", "{}", TestJson.Default.Item, () => never.Task);

        var after = new FleetRetryReceipts(folder.Path, clock);

        Assert.AreEqual("done", (await after.RunOnceAsync("done", "{}", TestJson.Default.Item, () => throw new AssertFailedException("Replayed."))).Value);
        var interrupted = await Assert.ThrowsExactlyAsync<FleetException>(() => after.RunOnceAsync("running", "{}", TestJson.Default.Item,
            () => throw new AssertFailedException("A call a stop interrupted may have had its effect: it never runs again.")));
        Assert.AreEqual(FleetErrorCodes.Expired, interrupted.Error.Code);
    }

    [TestMethod]
    public async Task AKey_IsForgottenAfterItsLifetime_AndAFailedCallIsTriedAgain_AndOtherParametersConflict()
    {
        using var folder = new TestFolder();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var receipts = new FleetRetryReceipts(folder.Path, clock, resultCapacity: 1, keyCapacity: 8);
        _ = await receipts.RunOnceAsync("a", "{}", TestJson.Default.Item, () => Task.FromResult(new Item("a")));

        var conflict = await Assert.ThrowsExactlyAsync<FleetException>(() => receipts.RunOnceAsync("a", "{\"other\":1}", TestJson.Default.Item, () => Task.FromResult(new Item("x"))));
        Assert.AreEqual(FleetErrorCodes.InvalidRequest, conflict.Error.Code);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => receipts.RunOnceAsync<Item>("failing", "{}", TestJson.Default.Item, () => throw new InvalidOperationException()));
        Assert.AreEqual("ok", (await receipts.RunOnceAsync("failing", "{}", TestJson.Default.Item, () => Task.FromResult(new Item("ok")))).Value,
            "A call that failed before its effect is tried again.");

        clock.Advance(FleetRetryReceipts.Lifetime + TimeSpan.FromMinutes(1));
        Assert.AreEqual("new", (await receipts.RunOnceAsync("a", "{}", TestJson.Default.Item, () => Task.FromResult(new Item("new")))).Value,
            "A key past the lifetime of a retry token is a new request.");
    }
}

/// <summary>A result of a test call.</summary>
public sealed record Item(string Value);

[JsonSerializable(typeof(Item))]
internal sealed partial class TestJson : JsonSerializerContext;
