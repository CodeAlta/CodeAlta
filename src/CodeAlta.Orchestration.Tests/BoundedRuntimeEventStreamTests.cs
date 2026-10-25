using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class BoundedRuntimeEventStreamTests
{
    [TestMethod]
    public async Task TryPublish_DropsNewestEventWhenCapacityIsFull()
    {
        var stream = new BoundedRuntimeEventStream<int>(capacity: 1);

        Assert.IsTrue(stream.TryPublish(1));
        Assert.IsFalse(stream.TryPublish(2));
        Assert.AreEqual(1, stream.DroppedCount);
        stream.Complete();

        var events = new List<int>();
        await foreach (var item in stream.ReadAllAsync())
        {
            events.Add(item);
        }

        CollectionAssert.AreEqual(new[] { 1 }, events);
    }

    [TestMethod]
    public void TryPublish_ReturnsFalseAfterCompletion()
    {
        var stream = new BoundedRuntimeEventStream<string>(capacity: 1);
        stream.Complete();

        Assert.IsFalse(stream.TryPublish("event"));
        Assert.AreEqual(1, stream.DroppedCount);
    }

    [TestMethod]
    public async Task PublishAsync_WaitsForRoom_AfterTheEventsAcceptedBefore_AndStampsTheDrops()
    {
        var stream = new BoundedRuntimeEventStream<int>(capacity: 1);
        Assert.IsTrue(stream.TryPublish(1));
        Assert.IsFalse(stream.TryPublish(2));

        var waiting = stream.PublishAsync(3, CancellationToken.None).AsTask();
        Assert.IsFalse(waiting.IsCompleted, "A full stream makes it wait instead of dropping it.");
        Assert.IsFalse(stream.TryPublish(4), "An event published while it waits does not pass it.");
        await using var reader = stream.ReadStampedAsync().GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(new BoundedRuntimeEventStream<int>.StampedEvent(1, 0), reader.Current);
        Assert.IsTrue(await waiting);
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(new BoundedRuntimeEventStream<int>.StampedEvent(3, 1), reader.Current, "One event was dropped before it.");

        stream.Complete();
        Assert.IsFalse(await stream.PublishAsync(5, CancellationToken.None), "A complete stream takes nothing.");
    }

    [TestMethod]
    public void Constructor_RejectsInvalidCapacity()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BoundedRuntimeEventStream<object>(0));
    }
}
