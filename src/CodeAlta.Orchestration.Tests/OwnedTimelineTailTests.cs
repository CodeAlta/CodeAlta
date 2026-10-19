using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The newest timeline page read again only when its journal changed, with a literal page reader over a real file.</summary>
[TestClass]
public sealed class OwnedTimelineTailTests
{
    [TestMethod]
    public async Task UnchangedJournal_IsAnsweredWithoutReading_AndEveryChangeReadsAgain()
    {
        using var f = new Fixture();
        await using var owner = f.CreateOwner();
        var first = await owner.ReadTimelineTailAsync("selected", null, default);
        Assert.IsFalse(first.Unchanged);
        Assert.IsNotNull(first.Stamp);
        Assert.AreEqual(1, f.Reads);

        var again = await owner.ReadTimelineTailAsync("selected", first.Stamp, default);
        Assert.IsTrue(again.Unchanged);
        Assert.AreEqual(first.Stamp, again.Stamp);
        Assert.AreEqual(1, f.Reads, "An unchanged journal is not read.");

        // An append changes the length; a replacement of the same length its times; each is read again.
        File.AppendAllText(f.Journal, "{\"appended\":true}\n");
        var appended = await owner.ReadTimelineTailAsync("selected", first.Stamp, default);
        Assert.IsFalse(appended.Unchanged);
        Assert.AreNotEqual(first.Stamp, appended.Stamp);
        Assert.AreEqual(2, f.Reads);

        var content = File.ReadAllText(f.Journal);
        var replacement = f.Journal + ".replacement";
        File.WriteAllText(replacement, content.Replace("appended", "APPENDED", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(f.Journal).AddSeconds(1));
        File.Move(replacement, f.Journal, overwrite: true);
        var replaced = await owner.ReadTimelineTailAsync("selected", appended.Stamp, default);
        Assert.IsFalse(replaced.Unchanged);
        Assert.AreEqual(3, f.Reads);

        // A journal that is gone is read, so that the read reports it.
        File.Delete(f.Journal);
        var missing = await owner.ReadTimelineTailAsync("selected", replaced.Stamp, default);
        Assert.IsFalse(missing.Unchanged);
        Assert.IsNull(missing.Stamp);
        Assert.AreEqual(4, f.Reads);
    }

    [TestMethod]
    public async Task UncertainReads_GiveNoStamp_AndStampsOfAnotherHostOrSessionDoNotMatch()
    {
        using var f = new Fixture();
        await using var owner = f.CreateOwner();
        // The page names another revision than the journal has: it is not certified.
        f.Revision = (_, length, ticks) => new AgentHistoryRevision("selected", length + 1, ticks);
        Assert.IsNull((await owner.ReadTimelineTailAsync("selected", null, default)).Stamp);
        // The journal changes while it is read.
        f.Revision = (_, length, ticks) => new AgentHistoryRevision("selected", length, ticks);
        f.During = () => File.AppendAllText(f.Journal, "\n");
        Assert.IsNull((await owner.ReadTimelineTailAsync("selected", null, default)).Stamp);
        f.During = null;
        var stamp = (await owner.ReadTimelineTailAsync("selected", null, default)).Stamp;
        Assert.IsNotNull(stamp);

        await using var other = f.CreateOwner();
        Assert.IsFalse((await other.ReadTimelineTailAsync("selected", stamp, default)).Unchanged, "Another host's stamp is not this one's.");
        Assert.IsFalse((await owner.ReadTimelineTailAsync("selected", stamp + "x", default)).Unchanged);
        Assert.IsTrue((await owner.ReadTimelineTailAsync("selected", stamp, default)).Unchanged);
        // Without a journal path the page is always read and given no stamp.
        await using var pathless = f.CreateOwner(paths: false);
        Assert.IsNull((await pathless.ReadTimelineTailAsync("selected", null, default)).Stamp);
    }

    [TestMethod]
    public async Task TailRead_SharesTheAdmissionOfReads()
    {
        using var f = new Fixture();
        var release = new TaskCompletionSource<AgentSessionHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new OwnedSessionWorkspace(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), Empty,
            (_, _, _) => release.Task, (_, _, _, _, _) => Task.FromResult(new AgentHistorySourceChunk("", null)))
        { ReadWait = TimeSpan.FromMilliseconds(50), JournalPaths = (_, _) => Task.FromResult<string?>(f.Journal) };
        var held = Enumerable.Range(0, OwnedSessionWorkspace.ReadSlots).Select(_ => owner.ReadTimelinePageAsync("selected", null, default)).ToArray();
        await Assert.ThrowsExactlyAsync<OwnedWorkspaceBusyException>(() => owner.ReadTimelineTailAsync("selected", null, default));
        release.TrySetResult(new([], null, false));
        await Task.WhenAll(held);
    }

    private static async IAsyncEnumerable<AgentSessionMetadata> Empty([EnumeratorCancellation] CancellationToken token)
    { await Task.CompletedTask; token.ThrowIfCancellationRequested(); yield break; }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "codealta-timeline-tail-" + Guid.NewGuid().ToString("N"));
        public string Journal { get; }
        public int Reads;
        public Func<string, long, long, AgentHistoryRevision> Revision = (id, length, ticks) => new(id, length, ticks);
        public Action? During;

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Journal = Path.Combine(_root, "selected.jsonl");
            File.WriteAllText(Journal, "{\"header\":true}\n");
        }

        // A page reader that reports the journal's revision as the store does, under no lock.
        public OwnedSessionWorkspace CreateOwner(bool paths = true) => new(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), Empty,
            (id, cursor, token) =>
            {
                Interlocked.Increment(ref Reads);
                Assert.IsNull(cursor);
                var info = new FileInfo(Journal);
                var revision = info.Exists ? Revision(id, info.Length, info.LastWriteTimeUtc.Ticks) : null;
                During?.Invoke();
                return Task.FromResult(new AgentSessionHistoryPage([], null, false) { Revision = revision });
            }, (_, _, _, _, _) => Task.FromResult(new AgentHistorySourceChunk("", null)))
        { JournalPaths = paths ? (_, _) => Task.FromResult<string?>(Journal) : null };

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
