using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>What the running tool calls wrote, as the runtime retains it for a frontend that looks at one of them.</summary>
[TestClass]
public sealed class RuntimeToolOutputProjectionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ARunningCall_GivesWhatItWroteThenWhatItWrites_AndEndsWithIt()
    {
        var output = new RuntimeToolOutputProjection();
        output.Commit(Activity("call"));
        output.Commit(Delta("call", "one\n"));
        output.Commit(Delta("call", "two\n"));
        await using var updates = output.ObserveAsync("session", "call").GetAsyncEnumerator();

        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.AreEqual(new RuntimeToolOutputUpdate("one\ntwo\n", 0, 8, true, false), updates.Current);
        Assert.AreEqual(1, output.SubscriberCount);

        // What is written while nobody reads is one update.
        output.Commit(Delta("call", "three\n"));
        output.Commit(Delta("call", "four\n"));
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.AreEqual(new RuntimeToolOutputUpdate("three\nfour\n", 8, 19, false, false), updates.Current);

        output.Commit(Delta("call", "five\n"));
        output.Commit(Activity("call", AgentActivityPhase.Completed));
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.AreEqual(new RuntimeToolOutputUpdate("five\n", 19, 24, false, true), updates.Current);
        Assert.IsFalse(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.AreEqual(0, output.CallCount);
        Assert.AreEqual(0, output.SubscriberCount);
    }

    [TestMethod]
    public async Task ACallThatIsNotRunning_EndsAtOnce()
    {
        var output = new RuntimeToolOutputProjection();
        output.Commit(Activity("ended"));
        output.Commit(Delta("ended", "text"));
        output.Commit(Activity("ended", AgentActivityPhase.Failed));
        foreach (var activity in new[] { "ended", "never" })
        {
            var updates = await ReadAllAsync(output.ObserveAsync("session", activity));
            Assert.AreEqual(1, updates.Count);
            Assert.AreEqual(new RuntimeToolOutputUpdate(string.Empty, 0, 0, true, true), updates[0]);
        }

        // Another session has its own calls, whatever their identity.
        output.Commit(Activity("call"));
        var other = await ReadAllAsync(output.ObserveAsync("other", "call"));
        Assert.IsTrue(other.Single().IsComplete);
        Assert.AreEqual(0, output.SubscriberCount);
    }

    [TestMethod]
    public async Task OutputNamesItsCallByItsParentOrByItsOwnIdentity_AndTheSessionIgnoresCase()
    {
        var output = new RuntimeToolOutputProjection();
        // The CodeAlta runtime: the output is `<call>:output`, its parent the call.
        output.Commit(Activity("call"));
        output.Commit(Delta("call:output", "by parent", parent: "call"));
        // A provider whose output has the identity of the call itself, without a parent.
        output.Commit(Activity("item", kind: AgentActivityKind.CommandExecution));
        output.Commit(Delta("item", "by identity", parent: null, kind: AgentContentKind.CommandOutput));
        // Output that arrives before any report of its call still opens it.
        output.Commit(Delta("early:output", "early", parent: "early"));
        Assert.AreEqual(3, output.CallCount);

        Assert.AreEqual("by parent", (await FirstAsync(output.ObserveAsync("SESSION", "call"))).Text);
        Assert.AreEqual("by identity", (await FirstAsync(output.ObserveAsync("session", "item"))).Text);
        Assert.AreEqual("early", (await FirstAsync(output.ObserveAsync("session", "early"))).Text);

        // The finished output of a call ends it, like the report of its end; other content does not.
        output.Commit(new SessionAgentEvent("session", new AgentContentCompletedEvent(new("provider"), "session", DateTimeOffset.UnixEpoch, new("run"),
            AgentContentKind.ToolOutput, "call:output", "call", "by parent")));
        output.Commit(new SessionAgentEvent("session", new AgentContentDeltaEvent(new("provider"), "session", DateTimeOffset.UnixEpoch, new("run"),
            AgentContentKind.Assistant, "item", null, "assistant text")));
        Assert.AreEqual(2, output.CallCount);
        Assert.AreEqual("by identity", (await FirstAsync(output.ObserveAsync("session", "item"))).Text);

        // A long identity is the compact one a history row carries.
        var identity = new string('x', 400);
        output.Commit(Activity(identity));
        output.Commit(Delta(identity, "long", parent: null));
        Assert.AreEqual("long", (await FirstAsync(output.ObserveAsync("session", RuntimeDisplayProjection.CompactIdentifier(identity)))).Text);
    }

    [TestMethod]
    public async Task OnlyTheNewestTextIsKept_AndAReaderThatFellBehindStartsAgainFromIt()
    {
        var output = new RuntimeToolOutputProjection();
        output.Commit(Activity("call"));
        output.Commit(Delta("call", "start"));
        await using var updates = output.ObserveAsync("session", "call").GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.AreEqual("start", updates.Current.Text);

        var chunk = new string('a', 64 * 1024);
        var written = 5L;
        for (var index = 0; index < 20; index++, written += chunk.Length) output.Commit(Delta("call", chunk));
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        var update = updates.Current;
        Assert.IsTrue(update.IsReset, "The text after `start` is no longer there: what is kept restarts the output.");
        Assert.AreEqual(written, update.TotalCharacters);
        Assert.AreEqual(written, update.Start + update.Text.Length);
        Assert.IsTrue(update.Text.Length >= RuntimeToolOutputProjection.MaxCharactersPerCall && update.Text.Length <= 2 * RuntimeToolOutputProjection.MaxCharactersPerCall);

        // A pair is never cut in two where the kept text starts.
        var pairs = new RuntimeToolOutputProjection();
        pairs.Commit(Activity("call"));
        // One unit before the pairs and one after: the place where the text is cut falls on the second half of a pair.
        pairs.Commit(Delta("call", "x" + string.Concat(Enumerable.Repeat("😀", RuntimeToolOutputProjection.MaxCharactersPerCall + 8)) + "y"));
        var kept = await FirstAsync(pairs.ObserveAsync("session", "call"));
        Assert.IsTrue(char.IsHighSurrogate(kept.Text[0]));
        Assert.AreEqual(kept.TotalCharacters, kept.Start + kept.Text.Length);
    }

    [TestMethod]
    public async Task TheEndOfARun_EndsItsCalls_AndTheNewestCallsAreTheOnesKept()
    {
        var output = new RuntimeToolOutputProjection();
        output.Commit(Activity("left running"));
        output.Commit(Activity("elsewhere", session: "other"));
        await using var updates = output.ObserveAsync("session", "left running").GetAsyncEnumerator();
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        output.Commit(new SessionLifecycleRuntimeEvent("session", DateTimeOffset.UnixEpoch, new SessionLifecycleEvent { SessionId = "session", Kind = SessionLifecycleEventKind.RunAborted }));
        Assert.IsTrue(await updates.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.IsTrue(updates.Current.IsComplete);
        Assert.AreEqual(1, output.CallCount, "The calls of another session go on.");

        for (var index = 0; index < RuntimeToolOutputProjection.MaxCalls + 3; index++)
        {
            output.Commit(Activity($"call {index}"));
            output.Commit(Delta($"call {index}", "x"));
        }
        Assert.AreEqual(RuntimeToolOutputProjection.MaxCalls, output.CallCount);
        Assert.IsTrue((await FirstAsync(output.ObserveAsync("other", "elsewhere"))).IsComplete, "The call that wrote nothing for the longest left.");
        Assert.AreEqual("x", (await FirstAsync(output.ObserveAsync("session", $"call {RuntimeToolOutputProjection.MaxCalls + 2}"))).Text);
    }

    [TestMethod]
    public async Task Observers_AreLimited_ReleasedWhenTheyStop_AndEndedWithTheRuntime()
    {
        var output = new RuntimeToolOutputProjection();
        output.Commit(Activity("call"));
        var observers = new List<IAsyncEnumerator<RuntimeToolOutputUpdate>>();
        for (var index = 0; index < RuntimeToolOutputProjection.MaxSubscribers; index++)
        {
            var observer = output.ObserveAsync("session", "call").GetAsyncEnumerator();
            Assert.IsTrue(await observer.MoveNextAsync().AsTask().WaitAsync(Wait));
            observers.Add(observer);
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await FirstAsync(output.ObserveAsync("session", "call")));
        await observers[0].DisposeAsync();
        Assert.AreEqual(RuntimeToolOutputProjection.MaxSubscribers - 1, output.SubscriberCount);

        using var cancellation = new CancellationTokenSource();
        var canceled = output.ObserveAsync("session", "call", cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await canceled.MoveNextAsync().AsTask().WaitAsync(Wait));
        var pending = canceled.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(Wait));
        await canceled.DisposeAsync();
        Assert.AreEqual(RuntimeToolOutputProjection.MaxSubscribers - 1, output.SubscriberCount);

        output.Complete();
        Assert.IsTrue(await observers[1].MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.IsTrue(observers[1].Current.IsComplete);
        output.Commit(Activity("after"));
        Assert.AreEqual(0, output.CallCount, "A closed runtime retains nothing.");
        foreach (var observer in observers.Skip(1)) await observer.DisposeAsync();
        Assert.AreEqual(0, output.SubscriberCount);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await FirstAsync(output.ObserveAsync(" ", "call")));
    }

    [TestMethod]
    public async Task ThePublisher_FeedsTheOutputOfTheCallsItPublishes()
    {
        var publisher = new SessionRuntimeEventPublisher();
        Assert.IsTrue(publisher.TryPublish(Activity("call")));
        Assert.IsTrue(publisher.TryPublish(Delta("call", "published")));
        Assert.AreEqual("published", (await FirstAsync(publisher.ToolOutput.ObserveAsync("session", "call"))).Text);
        publisher.Complete();
        Assert.IsTrue((await FirstAsync(publisher.ToolOutput.ObserveAsync("session", "call"))).IsComplete);
    }

    private static async Task<RuntimeToolOutputUpdate> FirstAsync(IAsyncEnumerable<RuntimeToolOutputUpdate> updates)
    {
        await using var enumerator = updates.GetAsyncEnumerator();
        Assert.IsTrue(await enumerator.MoveNextAsync().AsTask().WaitAsync(Wait));
        return enumerator.Current;
    }

    private static async Task<List<RuntimeToolOutputUpdate>> ReadAllAsync(IAsyncEnumerable<RuntimeToolOutputUpdate> updates)
    {
        var result = new List<RuntimeToolOutputUpdate>();
        await foreach (var update in updates.WaitAsync(Wait)) result.Add(update);
        return result;
    }

    private static SessionAgentEvent Activity(string activity, AgentActivityPhase phase = AgentActivityPhase.Started, string session = "session",
        AgentActivityKind kind = AgentActivityKind.ToolCall)
        => new(session, new AgentActivityEvent(new("provider"), session, DateTimeOffset.UnixEpoch, new("run"), kind, phase, activity, null, "tool", null));

    private static SessionAgentEvent Delta(string content, string text, string? parent = "", AgentContentKind kind = AgentContentKind.ToolOutput, string session = "session")
        => new(session, new AgentContentDeltaEvent(new("provider"), session, DateTimeOffset.UnixEpoch, new("run"), kind,
            parent == string.Empty ? content + ":output" : content, parent == string.Empty ? content : parent, text));
}

internal static class AsyncEnumerableWaits
{
    /// <summary>Fails a test whose enumeration does not end in time, instead of hanging it.</summary>
    internal static async IAsyncEnumerable<T> WaitAsync<T>(this IAsyncEnumerable<T> source, TimeSpan timeout)
    {
        await using var enumerator = source.GetAsyncEnumerator();
        while (await enumerator.MoveNextAsync().AsTask().WaitAsync(timeout)) yield return enumerator.Current;
    }
}
