using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Literal read callbacks only; no host, catalogs, provider or journal acquisition.</summary>
[TestClass]
public sealed class OwnedWorkspaceTimelineTests
{
    [TestMethod]
    public async Task TimelineAndSource_ShareAdmission_RetainCanceledWait_AndJoinOnClose()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The owner launches each admitted read asynchronously after releasing its gate, so the literal
        // callbacks run concurrently on pool threads: publish them under a lock and signal the eighth start.
        var actualGate = new object();
        var eightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actual = new List<Task>();
        var waits = new List<Task>();
        var caller = new CancellationTokenSource();
        var revision = new AgentHistoryRevision("selected", 100, 7);
        var owner = new OwnedSessionWorkspace(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), Empty,
            (id, cursor, token) =>
            {
                Assert.AreEqual("selected", id); Assert.IsNull(cursor); Assert.AreEqual(CancellationToken.None, token);
                return Track(Timeline());
            }, (value, start, end, offset, token) =>
            {
                Assert.AreSame(revision, value); Assert.AreEqual((0L, 100L, 0L), (start, end, offset));
                Assert.AreEqual(CancellationToken.None, token);
                return Track(Source());
            }) { ReadWait = TimeSpan.FromMilliseconds(50) };
        Task? close = null;
        try
        {
            Assert.ThrowsExactly<ArgumentException>(() => owner.ReadTimelinePageAsync(" ", null, default));
            Assert.ThrowsExactly<ArgumentException>(() => owner.ReadHistorySourceAsync(revision with { SessionId = " " }, 0, 100, 0, default));
            Assert.ThrowsExactly<OperationCanceledException>(() => owner.ReadTimelinePageAsync("selected", null, new CancellationToken(true)));
            Assert.AreEqual(0, ActualCount());
            var canceled = owner.ReadHistorySourceAsync(revision, 0, 100, 0, caller.Token); waits.Add(canceled);
            for (var i = 0; i < 7; i++) waits.Add(owner.ReadTimelinePageAsync("selected", null, default));
            var cancel = caller.CancelAsync(); waits.Add(cancel); await cancel;
            await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
            // The canceled wait does not cancel its admitted read: all eight actual reads still start.
            await eightStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(8, ActualCount());
            // A ninth read waits its bounded time for a slot, then is refused as busy without starting.
            await Assert.ThrowsExactlyAsync<OwnedWorkspaceBusyException>(() => owner.ReadHistorySourceAsync(revision, 0, 100, 0, default));
            await Assert.ThrowsExactlyAsync<OwnedWorkspaceBusyException>(() => owner.ReadHistoryPageAsync("selected", null, default));
            Assert.AreEqual(8, ActualCount());
            close = owner.DisposeAsync().AsTask();
            Assert.IsFalse(close.IsCompleted);
            Assert.ThrowsExactly<ObjectDisposedException>(() => owner.ReadTimelinePageAsync("selected", null, default));
            Assert.ThrowsExactly<ObjectDisposedException>(() => owner.ReadHistorySourceAsync(revision, 0, 100, 0, default));
            release.TrySetResult();
            await close.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            close ??= owner.DisposeAsync().AsTask();
            // Start independent finite observations before joining; canceled caller wait is expected,
            // original callbacks and owner drain must succeed before disposing caller resources.
            // A read that starts after this snapshot is still joined by the owner drain observed below.
            Task[] started;
            lock (actualGate) started = actual.ToArray();
            var actualObservers = started.Append(close).Select(task => task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray();
            var waitObservers = waits.Select(ObserveWait).ToArray();
            await Task.WhenAll(actualObservers.Concat(waitObservers));
            caller.Dispose();
        }
        int ActualCount() { lock (actualGate) return actual.Count; }
        Task<T> Track<T>(Task<T> read)
        {
            lock (actualGate)
            {
                actual.Add(read);
                if (actual.Count == 8) eightStarted.TrySetResult();
            }
            return read;
        }
        async Task<AgentSessionHistoryPage> Timeline() { await release.Task; return new([], null, false); }
        async Task<AgentHistorySourceChunk> Source() { await release.Task; return new("literal", null); }
        static async Task ObserveWait(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (task.IsCanceled) { }
        }
    }

    [TestMethod]
    public async Task UsageUpdates_ShareAdmission_AndTheirCallerCancelsTheReadItself()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = new AgentSessionUsageUpdate("run", new(InputTokens: 1));
        var owner = new OwnedSessionWorkspace(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), Empty,
            (_, _, _) => Task.FromResult(new AgentSessionHistoryPage([], null, false)))
        {
            ReadWait = TimeSpan.FromMilliseconds(50),
            UsageUpdates = async (id, token) =>
            {
                Assert.AreEqual("selected", id);
                if (!token.CanBeCanceled) return [update];
                started.TrySetResult(token);
                // A whole journal is being read: only the cancellation of the caller ends it.
                await Task.Delay(Timeout.Infinite, token);
                return [];
            },
        };
        Assert.ThrowsExactly<ArgumentException>(() => owner.ReadUsageUpdatesAsync(" ", default));
        Assert.ThrowsExactly<OperationCanceledException>(() => owner.ReadUsageUpdatesAsync("selected", new CancellationToken(true)));
        Assert.AreSame(update, (await owner.ReadUsageUpdatesAsync("selected", default)).Single());

        using var caller = new CancellationTokenSource();
        var reads = Enumerable.Range(0, 8).Select(_ => owner.ReadUsageUpdatesAsync("selected", caller.Token)).ToArray();
        Assert.AreEqual(caller.Token, await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsExactlyAsync<OwnedWorkspaceBusyException>(() => owner.ReadUsageUpdatesAsync("selected", default));
        await caller.CancelAsync();
        foreach (var read in reads) await Assert.ThrowsAsync<OperationCanceledException>(() => read);
        // The reads ended as their caller asked: the drain joins them and reports no failure.
        await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.ThrowsExactly<ObjectDisposedException>(() => owner.ReadUsageUpdatesAsync("selected", default));

        var unconfigured = new OwnedSessionWorkspace(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), Empty,
            (_, _, _) => Task.FromResult(new AgentSessionHistoryPage([], null, false)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => unconfigured.ReadUsageUpdatesAsync("selected", default));
    }

    [TestMethod]
    public async Task SystemPrompts_ShareAdmission_AndTheirCallerCancelsTheReadItself()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new AgentSystemPromptEvent(new("provider"), "selected", DateTimeOffset.UnixEpoch, null, "session_start", "hash", null, "System", "Developer",
            new("native-system-and-developer", true, false), null, new(1, 2, 3, 6, 9), new("initial", [], [], []));
        var record = new AgentSystemPromptRecord(prompt, null, null);
        var owner = new OwnedSessionWorkspace(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), Empty,
            (_, _, _) => Task.FromResult(new AgentSessionHistoryPage([], null, false)))
        {
            ReadWait = TimeSpan.FromMilliseconds(50),
            SystemPrompts = async (id, offset, token) =>
            {
                Assert.AreEqual(("selected", 40L), (id, offset));
                if (!token.CanBeCanceled) return record;
                started.TrySetResult(token);
                // The journal is being read up to the record: only the cancellation of the caller ends it.
                await Task.Delay(Timeout.Infinite, token);
                return record;
            },
        };
        Assert.ThrowsExactly<ArgumentException>(() => owner.ReadSystemPromptAsync(" ", 40, default));
        Assert.ThrowsExactly<OperationCanceledException>(() => owner.ReadSystemPromptAsync("selected", 40, new CancellationToken(true)));
        Assert.AreSame(record, await owner.ReadSystemPromptAsync("selected", 40, default));

        using var caller = new CancellationTokenSource();
        var reads = Enumerable.Range(0, 8).Select(_ => owner.ReadSystemPromptAsync("selected", 40, caller.Token)).ToArray();
        Assert.AreEqual(caller.Token, await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsExactlyAsync<OwnedWorkspaceBusyException>(() => owner.ReadSystemPromptAsync("selected", 40, default));
        await caller.CancelAsync();
        foreach (var read in reads) await Assert.ThrowsAsync<OperationCanceledException>(() => read);
        await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.ThrowsExactly<ObjectDisposedException>(() => owner.ReadSystemPromptAsync("selected", 40, default));

        var unconfigured = new OwnedSessionWorkspace(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), Empty,
            (_, _, _) => Task.FromResult(new AgentSessionHistoryPage([], null, false)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => unconfigured.ReadSystemPromptAsync("selected", 40, default));
    }

    private static async IAsyncEnumerable<AgentSessionMetadata> Empty([EnumeratorCancellation] CancellationToken token)
    { await Task.CompletedTask; token.ThrowIfCancellationRequested(); yield break; }
}
