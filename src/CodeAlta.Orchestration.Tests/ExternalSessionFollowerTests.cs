using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The sessions another process writes into the sessions folder of an instance: listed from the markers the command line
/// drops, and followed, read-only, while another process holds them.
/// </summary>
[TestClass]
public sealed class ExternalSessionFollowerTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    [TestMethod]
    public async Task Marker_ProjectsItsSession_AndIsDeleted_AndAMarkerWithoutJournalIsDropped()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var (cli, desktop) = await CreateHostsAsync(root, provider);
        // The instance has listed its sessions before the command line writes one: its database is complete without it.
        Assert.IsFalse(await ListsAsync(desktop, "none"));
        var session = await RunInCliAsync(cli, provider, root, "hello");
        Assert.IsTrue(await cli.RuntimeService.DetachRuntimeSessionAsync(session.SessionId));
        var markers = new SessionMarkerStore(desktop.CatalogOptions);
        markers.Write(MarkerOf(cli, session.SessionId));
        var missing = Guid.CreateVersion7().ToString();
        markers.Write(new SessionMarker(missing, $"2026/10/25/{missing}.jsonl", DateTimeOffset.UtcNow, SessionLeaseOwner.CliKind, Environment.ProcessId));
        await using var follower = CreateFollower(desktop);
        var changes = Record(follower);
        Assert.IsFalse(await ListsAsync(desktop, session.SessionId), "The instance does not list a session it never projected.");

        await follower.RefreshAsync().WaitAsync(Hang);

        Assert.AreEqual(0, markers.List().Count, "The markers are deleted: the one projected and the one whose journal is gone.");
        Assert.IsTrue(await ListsAsync(desktop, session.SessionId), "The session the marker named is listed.");
        CollectionAssert.AreEqual(new[] { (session.SessionId, ExternalSessionChangeKind.Listed) },
            changes.Select(static change => (change.SessionId, change.Kind)).ToArray());
    }

    [TestMethod]
    public async Task Follow_ProjectsAGrowingJournalWhileHeldElsewhere_AndOnceMoreWhenReleased()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var (cli, desktop) = await CreateHostsAsync(root, provider);
        Assert.IsFalse(await ListsAsync(desktop, "none"));
        var session = await RunInCliAsync(cli, provider, root, "first");
        Assert.IsFalse(await ListsAsync(desktop, session.SessionId));
        await using var follower = CreateFollower(desktop);
        var changes = Record(follower);

        await follower.RefreshAsync().WaitAsync(Hang);

        var hold = follower.GetHold(session.SessionId);
        Assert.IsNotNull(hold, "The session the command line runs is followed.");
        Assert.AreEqual(SessionLeaseOwner.CliKind, hold.HolderKind);
        Assert.IsTrue(await ListsAsync(desktop, session.SessionId), "A session held elsewhere is projected and listed without a marker.");
        Assert.AreEqual(SessionLeaseOwner.CliKind, desktop.RuntimeService.GetSessionLeaseHolderElsewhere(session.SessionId)?.OwnerKind);
        var first = hold.JournalLength;
        Assert.IsNotNull(first);

        // Nothing changed: nothing is projected again.
        changes.Clear();
        await follower.RefreshAsync().WaitAsync(Hang);
        Assert.AreEqual(0, changes.Count);

        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Second answer."));
        await cli.RuntimeService.SendAsync(session, HeadlessTestRoot.Options(provider, root.ProjectRoot), new AgentSendOptions { Input = AgentInput.Text("second") });
        await cli.RuntimeService.WhenSettledAsync(session.SessionId).WaitAsync(Hang);
        await follower.RefreshAsync().WaitAsync(Hang);

        Assert.IsTrue(follower.GetHold(session.SessionId)?.JournalLength > first, "The journal that grew is projected again.");
        CollectionAssert.Contains(changes.Select(static change => change.Kind).ToArray(), ExternalSessionChangeKind.Updated);

        changes.Clear();
        Assert.IsTrue(await cli.RuntimeService.DetachRuntimeSessionAsync(session.SessionId));
        await follower.RefreshAsync().WaitAsync(Hang);

        Assert.IsNull(follower.GetHold(session.SessionId));
        Assert.AreEqual(0, follower.Held.Count);
        CollectionAssert.AreEqual(new[] { ExternalSessionChangeKind.Released }, changes.Select(static change => change.Kind).ToArray());
        Assert.IsNull(desktop.RuntimeService.GetSessionLeaseHolderElsewhere(session.SessionId));
    }

    [TestMethod]
    public async Task HostFollower_StartsWithTheHost_AndReadsTheMarkersWrittenBefore()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var cli = await root.CreateHostAsync(provider, readOnlyGlobalState: true, sessionLeaseOwner: SessionLeaseOwner.CliKind);
        var session = await RunInCliAsync(cli, provider, root, "before");
        Assert.IsTrue(await cli.RuntimeService.DetachRuntimeSessionAsync(session.SessionId));
        var markers = new SessionMarkerStore(cli.CatalogOptions);
        markers.Write(MarkerOf(cli, session.SessionId));

        var desktop = await root.CreateHostAsync(provider, sessionLeaseOwner: SessionLeaseOwner.DesktopKind, followExternalSessions: true);

        Assert.IsNotNull(desktop.ExternalSessions);
        using var deadline = new CancellationTokenSource(Hang);
        while (markers.List().Count > 0) await Task.Delay(10, deadline.Token);
        Assert.IsTrue(await ListsAsync(desktop, session.SessionId));
    }

    [TestMethod]
    public async Task RunningSession_IsListedWithinTheFollowInterval_NotTheRelistPeriod()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var (cli, desktop) = await CreateHostsAsync(root, provider);
        Assert.IsFalse(await ListsAsync(desktop, "none"));
        // The instance runs before the command line starts, as it does: what it sees is told by its watchers and its follow.
        await using var follower = new ExternalSessionFollower(desktop.CatalogOptions, desktop.SessionViewCatalog.JournalStore.CreateSessionStore(),
            desktop.AgentSessionCatalog, desktop.RuntimeService) { FollowInterval = TimeSpan.FromMilliseconds(200), RelistInterval = TimeSpan.FromHours(1) };
        var changes = Record(follower);
        follower.Start();

        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await cli.RuntimeService.CreateProjectSessionAsync(cli.CurrentProject, options, "From the command line");
        var markers = new SessionMarkerStore(desktop.CatalogOptions);
        markers.Write(MarkerOf(cli, session.SessionId));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Turns.Enqueue(ScriptedTurnProvider.WaitThenReply(gate.Task, "done.", started));
        var send = cli.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("run") });
        try
        {
            await started.Task.WaitAsync(Hang);
            // Well within the period of the listing: the watchers and the follow cadence tell it.
            var listed = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && !(listed = await ListsAsync(desktop, session.SessionId) && markers.List().Count == 0
                && follower.GetHold(session.SessionId) is not null))
                await Task.Delay(50);

            Assert.IsTrue(listed, $"The running session is listed while it runs (listed: {await ListsAsync(desktop, session.SessionId)}, "
                + $"markers left: {markers.List().Count}, followed: {follower.GetHold(session.SessionId) is not null}, "
                + $"changes: {string.Join(", ", changes.Select(static change => change.Kind))}).");
            Assert.AreEqual(SessionLeaseOwner.CliKind, follower.GetHold(session.SessionId)?.HolderKind, "The running session is followed.");
        }
        finally
        {
            gate.TrySetResult();
        }

        // The end of the turn grows the journal while the command line still holds the session: the change is told.
        var length = follower.GetHold(session.SessionId)!.JournalLength;
        changes.Clear();
        await send.WaitAsync(Hang);
        await cli.RuntimeService.WhenSettledAsync(session.SessionId).WaitAsync(Hang);
        await WaitUntilAsync(() => changes.Any(static change => change.Kind == ExternalSessionChangeKind.Updated)
            && follower.GetHold(session.SessionId)?.JournalLength > length, "The grown journal of a held session is told as an update.");
        Assert.IsTrue(await cli.RuntimeService.DetachRuntimeSessionAsync(session.SessionId));
        await WaitUntilAsync(() => follower.GetHold(session.SessionId) is null, "The released session is no longer followed.");
        CollectionAssert.Contains(changes.Select(static change => change.Kind).ToArray(), ExternalSessionChangeKind.Released);
    }

    [TestMethod]
    public async Task Marker_WhoseJournalIsNotASessionYet_IsProjectedAtTheFollowPace_WhileAnotherHandleAppends()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var (cli, desktop) = await CreateHostsAsync(root, provider);
        Assert.IsFalse(await ListsAsync(desktop, "none"));
        var session = await RunInCliAsync(cli, provider, root, "hello");
        Assert.IsTrue(await cli.RuntimeService.DetachRuntimeSessionAsync(session.SessionId));
        var marker = MarkerOf(cli, session.SessionId);
        var markers = new SessionMarkerStore(desktop.CatalogOptions);
        var journal = markers.GetJournalPath(marker);
        var records = await File.ReadAllBytesAsync(journal);
        // The journal is there, not a session yet: as the command line has it before it writes its first records.
        await File.WriteAllBytesAsync(journal, []);
        await using var follower = new ExternalSessionFollower(desktop.CatalogOptions, desktop.SessionViewCatalog.JournalStore.CreateSessionStore(),
            desktop.AgentSessionCatalog, desktop.RuntimeService) { FollowInterval = TimeSpan.FromMilliseconds(200), RelistInterval = TimeSpan.FromHours(1) };
        var changes = Record(follower);
        follower.Start();
        markers.Write(marker);
        await Task.Delay(500);
        Assert.AreEqual(1, markers.List().Count, "A marker whose journal is not a session yet is kept.");

        // Another process appends, as a journal writer does, and keeps its handle open on a record it has not finished:
        // no watcher sees the journal grow.
        await using var writer = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 0);
        await writer.WriteAsync(records);
        await writer.WriteAsync("{\"$type\":\"raw\",\"backendEventType\":\"local.sessionSummary\",\"raw\":{"u8.ToArray());
        await writer.FlushAsync();

        await WaitUntilAsync(() => markers.List().Count == 0, "The marker is projected at the follow pace, not the listing period.", TimeSpan.FromSeconds(3));
        Assert.IsTrue(await ListsAsync(desktop, session.SessionId), "The session the marker named is listed.");
        CollectionAssert.AreEqual(new[] { (session.SessionId, ExternalSessionChangeKind.Listed) },
            changes.Select(static change => (change.SessionId, change.Kind)).ToArray());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) Assert.Fail(message);
            await Task.Delay(50);
        }
    }

    private static async Task<(CodeAltaHost Cli, CodeAltaHost Desktop)> CreateHostsAsync(HeadlessTestRoot root, ScriptedTurnProvider provider)
    {
        // The command line on a private database, the instance on the database of the state root, both on one sessions folder.
        var cli = await root.CreateHostAsync(provider, readOnlyGlobalState: true, sessionLeaseOwner: SessionLeaseOwner.CliKind);
        var desktop = await root.CreateHostAsync(provider, sessionLeaseOwner: SessionLeaseOwner.DesktopKind);
        return (cli, desktop);
    }

    private static async Task<SessionViewDescriptor> RunInCliAsync(CodeAltaHost cli, ScriptedTurnProvider provider, HeadlessTestRoot root, string prompt)
    {
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await cli.RuntimeService.CreateProjectSessionAsync(cli.CurrentProject, options, "From the command line");
        await cli.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text(prompt) });
        await cli.RuntimeService.WhenSettledAsync(session.SessionId).WaitAsync(Hang);
        return session;
    }

    private static SessionMarker MarkerOf(CodeAltaHost host, string sessionId)
    {
        var sessions = Path.Combine(host.CatalogOptions.StateRoot, "sessions");
        var journal = Directory.EnumerateFiles(sessions, sessionId + ".jsonl", SearchOption.AllDirectories).Single();
        return new SessionMarker(sessionId, Path.GetRelativePath(sessions, journal).Replace('\\', '/'), DateTimeOffset.UtcNow,
            SessionLeaseOwner.CliKind, Environment.ProcessId);
    }

    private static ExternalSessionFollower CreateFollower(CodeAltaHost host)
        => new(host.CatalogOptions, host.SessionViewCatalog.JournalStore.CreateSessionStore(), host.AgentSessionCatalog, host.RuntimeService);

    private static ConcurrentQueue<ExternalSessionChange> Record(ExternalSessionFollower follower)
    {
        var changes = new ConcurrentQueue<ExternalSessionChange>();
        follower.Changed += (_, change) => changes.Enqueue(change);
        return changes;
    }

    private static async Task<bool> ListsAsync(CodeAltaHost host, string sessionId)
    {
        await foreach (var session in host.AgentSessionCatalog.ListSessionsAsync())
        {
            if (string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
