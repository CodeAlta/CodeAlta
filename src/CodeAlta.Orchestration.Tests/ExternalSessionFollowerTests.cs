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
