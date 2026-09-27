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
        var actual = new List<Task>();
        var waits = new List<Task>();
        var caller = new CancellationTokenSource();
        var revision = new AgentHistoryRevision("selected", 100, 7);
        var owner = new OwnedSessionWorkspace(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), Empty,
            (id, cursor, token) =>
            {
                Assert.AreEqual("selected", id); Assert.IsNull(cursor); Assert.AreEqual(CancellationToken.None, token);
                var read = Timeline(); actual.Add(read); return read;
            }, (value, start, end, offset, token) =>
            {
                Assert.AreSame(revision, value); Assert.AreEqual((0L, 100L, 0L), (start, end, offset));
                Assert.AreEqual(CancellationToken.None, token);
                var read = Source(); actual.Add(read); return read;
            });
        Task? close = null;
        try
        {
            Assert.ThrowsExactly<ArgumentException>(() => owner.ReadTimelinePageAsync(" ", null, default));
            Assert.ThrowsExactly<ArgumentException>(() => owner.ReadHistorySourceAsync(revision with { SessionId = " " }, 0, 100, 0, default));
            Assert.ThrowsExactly<OperationCanceledException>(() => owner.ReadTimelinePageAsync("selected", null, new CancellationToken(true)));
            Assert.AreEqual(0, actual.Count);
            var canceled = owner.ReadHistorySourceAsync(revision, 0, 100, 0, caller.Token); waits.Add(canceled);
            for (var i = 0; i < 7; i++) waits.Add(owner.ReadTimelinePageAsync("selected", null, default));
            var cancel = caller.CancelAsync(); waits.Add(cancel); await cancel;
            await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
            Assert.AreEqual(8, actual.Count);
            Assert.ThrowsExactly<InvalidOperationException>(() => owner.ReadHistorySourceAsync(revision, 0, 100, 0, default));
            Assert.ThrowsExactly<InvalidOperationException>(() => owner.ReadHistoryPageAsync("selected", null, default));
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
            var actualObservers = actual.Append(close).Select(task => task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray();
            var waitObservers = waits.Select(ObserveWait).ToArray();
            await Task.WhenAll(actualObservers.Concat(waitObservers));
            caller.Dispose();
        }
        async Task<AgentSessionHistoryPage> Timeline() { await release.Task; return new([], null, false); }
        async Task<AgentHistorySourceChunk> Source() { await release.Task; return new("literal", null); }
        static async Task ObserveWait(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (task.IsCanceled) { }
        }
    }

    private static async IAsyncEnumerable<AgentSessionMetadata> Empty([EnumeratorCancellation] CancellationToken token)
    { await Task.CompletedTask; token.ThrowIfCancellationRequested(); yield break; }
}
