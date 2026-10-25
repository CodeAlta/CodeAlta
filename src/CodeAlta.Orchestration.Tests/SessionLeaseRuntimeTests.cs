using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The leases of the sessions across processes, as the runtime takes them: with the attachment of a session, while a frontend
/// shows it, and while a change is written; a session another process holds is neither attached nor changed.
/// </summary>
[TestClass]
public sealed class SessionLeaseRuntimeTests
{
    private static readonly TimeSpan Hang = TestDeadlines.Hang;

    [TestMethod]
    public async Task Attachment_TakesTheLease_ADetachReleasesIt_AndASecondRuntimeIsRefused()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var desktop = await root.CreateHostAsync(provider, readOnlyGlobalState: true, sessionLeaseOwner: SessionLeaseOwner.DesktopKind);
        var cli = await root.CreateHostAsync(provider, readOnlyGlobalState: true, sessionLeaseOwner: SessionLeaseOwner.CliKind);
        var leases = new SessionLeaseStore(desktop.CatalogOptions);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);

        var session = await desktop.RuntimeService.CreateProjectSessionAsync(desktop.CurrentProject, options, "Leased");
        await desktop.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("hello") });
        await desktop.RuntimeService.WhenSettledAsync(session.SessionId).WaitAsync(Hang);

        var holder = leases.TryReadHolder(session.SessionId);
        Assert.AreEqual(SessionLeaseOwner.DesktopKind, holder?.OwnerKind, "The attached session holds its lease.");
        CollectionAssert.Contains(desktop.RuntimeService.GetHeldSessionLeases().ToArray(), session.SessionId);
        Assert.IsNull(desktop.RuntimeService.GetSessionLeaseHolderElsewhere(session.SessionId), "Its own lease is not held elsewhere.");
        Assert.AreEqual(SessionLeaseOwner.DesktopKind, cli.RuntimeService.GetSessionLeaseHolderElsewhere(session.SessionId)?.OwnerKind);

        var refused = Assert.ThrowsExactly<SessionInUseException>(() => cli.RuntimeService.HoldSession(session.SessionId));
        Assert.AreEqual(SessionLeaseOwner.DesktopKind, refused.HolderKind);
        var resumed = await cli.RuntimeService.TryGetResumableSessionAsync(session.SessionId);
        Assert.IsNotNull(resumed);
        var send = await Assert.ThrowsAsync<Exception>(() => cli.RuntimeService.SendAsync(resumed, options, new AgentSendOptions { Input = AgentInput.Text("taken") }));
        Assert.IsNotNull(SessionRuntimeService.FindSessionInUse(send), "A runtime that cannot take the lease does not attach the session.");
        Assert.AreEqual(1, provider.RequestsOf(session.SessionId).Length, "Nothing ran in the second runtime.");

        Assert.IsTrue(await desktop.RuntimeService.DetachRuntimeSessionAsync(session.SessionId));
        Assert.IsNull(leases.TryReadHolder(session.SessionId), "The lease goes with the attachment.");

        // Once released, the other side continues the session.
        await cli.RuntimeService.SendAsync(resumed, options, new AgentSendOptions { Input = AgentInput.Text("continued") });
        await cli.RuntimeService.WhenSettledAsync(session.SessionId).WaitAsync(Hang);
        Assert.AreEqual(SessionLeaseOwner.CliKind, leases.TryReadHolder(session.SessionId)?.OwnerKind);
        Assert.AreEqual(2, provider.RequestsOf(session.SessionId).Length);
    }

    [TestMethod]
    public async Task Changes_OfASessionHeldElsewhere_AreRefused_AndAForkIsNot()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, sessionLeaseOwner: SessionLeaseOwner.DesktopKind);
        var runtime = host.RuntimeService;
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await runtime.CreateProjectSessionAsync(host.CurrentProject, options, "Held elsewhere");
        var run = await runtime.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("first") });
        await runtime.WhenSettledAsync(session.SessionId).WaitAsync(Hang);
        Assert.IsTrue(await runtime.DetachRuntimeSessionAsync(session.SessionId));
        var journal = Directory.EnumerateFiles(Path.Combine(host.CatalogOptions.StateRoot, "sessions"), session.SessionId + ".jsonl", SearchOption.AllDirectories).Single();
        var leases = new SessionLeaseStore(host.CatalogOptions);
        using var elsewhere = leases.TryAcquire(session.SessionId, new SessionLeaseOwner(SessionLeaseOwner.CliKind, "other"));
        Assert.IsNotNull(elsewhere);
        var before = new FileInfo(journal).Length;

        await Assert.ThrowsExactlyAsync<SessionInUseException>(() => runtime.UpdateNotesAsync(session.SessionId, "notes", AgentNotesUpdateKind.Set, static _ => { }));
        await Assert.ThrowsExactlyAsync<SessionInUseException>(() => runtime.SetSessionTitleAsync(session.SessionId, "Renamed", changeMode: false, mode: null));
        await Assert.ThrowsExactlyAsync<SessionInUseException>(() => runtime.SetSessionTierAsync(session.SessionId, CodeAlta.Catalog.ModelTiers.ModelTier.Quick));
        await Assert.ThrowsExactlyAsync<SessionInUseException>(() => runtime.QueuePromptAsync(session, "later", "send", submittedBy: null));
        await Assert.ThrowsExactlyAsync<SessionInUseException>(() => runtime.SetReviewBeforeCommitAsync(session, true));
        await Assert.ThrowsExactlyAsync<SessionInUseException>(() => runtime.DeliverHostPromptAsync(session.SessionId, "a job ended", "job"));
        Assert.AreEqual("session_in_use", (await runtime.SetSessionArchivedAsync(session.SessionId, archived: true, withChildren: false)).Status);
        Assert.AreEqual("session_in_use", (await runtime.RewindSessionAsync(new SessionRewindRequest { SessionId = session.SessionId, BeforeRunId = run.Value })).Status);
        var send = await Assert.ThrowsAsync<Exception>(() => runtime.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("refused") }));
        Assert.IsNotNull(SessionRuntimeService.FindSessionInUse(send));
        Assert.AreEqual(before, new FileInfo(journal).Length, "Nothing was written in the journal another process holds.");

        // A fork reads the source and writes a session of its own.
        var fork = await runtime.ForkSessionAsync(new SessionForkRequest { SessionId = session.SessionId });
        Assert.AreEqual("ok", fork.Status);
        Assert.AreEqual(1, provider.RequestsOf(session.SessionId).Length);
    }

    [TestMethod]
    public async Task ALeaseThatCannotBeTaken_RefusesTheAttachmentAndTheChanges_WhileTheJournalStaysWritable()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, sessionLeaseOwner: SessionLeaseOwner.CliKind);
        var runtime = host.RuntimeService;
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await runtime.CreateProjectSessionAsync(host.CurrentProject, options, "Unusable leases");
        await runtime.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("first") });
        await runtime.WhenSettledAsync(session.SessionId).WaitAsync(Hang);
        Assert.IsTrue(await runtime.DetachRuntimeSessionAsync(session.SessionId));
        var journal = Directory.EnumerateFiles(Path.Combine(host.CatalogOptions.StateRoot, "sessions"), session.SessionId + ".jsonl", SearchOption.AllDirectories).Single();

        // The folder of the leases becomes a file: no lease can be taken there, and the journals stay writable.
        var leasesRoot = host.CatalogOptions.SessionLeasesRoot;
        if (Directory.Exists(leasesRoot)) Directory.Delete(leasesRoot, recursive: true);
        await File.WriteAllTextAsync(leasesRoot, "not a folder");
        using (new FileStream(journal, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
        var before = new FileInfo(journal).Length;

        var hold = Assert.ThrowsExactly<SessionLeaseUnavailableException>(() => runtime.HoldSession(session.SessionId));
        StringAssert.StartsWith(hold.Message, $"The lease of session '{session.SessionId}' cannot be taken: ");
        Assert.AreEqual(session.SessionId, hold.SessionId);
        await Assert.ThrowsExactlyAsync<SessionLeaseUnavailableException>(() => runtime.UpdateNotesAsync(session.SessionId, "notes", AgentNotesUpdateKind.Set, static _ => { }));
        await Assert.ThrowsExactlyAsync<SessionLeaseUnavailableException>(() => runtime.SetSessionTitleAsync(session.SessionId, "Renamed", changeMode: false, mode: null));
        await Assert.ThrowsExactlyAsync<SessionLeaseUnavailableException>(() => runtime.QueuePromptAsync(session, "later", "send", submittedBy: null));
        await Assert.ThrowsExactlyAsync<SessionLeaseUnavailableException>(() => runtime.DeliverHostPromptAsync(session.SessionId, "a job ended", "job"));
        var send = await Assert.ThrowsAsync<Exception>(() => runtime.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("refused") }));
        Assert.IsTrue(Contains<SessionLeaseUnavailableException>(send), $"The send fails on the lease: {send}");
        Assert.IsNull(SessionRuntimeService.FindSessionInUse(send), "Nobody is known to hold the session.");

        Assert.AreEqual(1, provider.RequestsOf(session.SessionId).Length, "Nothing ran without the lease.");
        Assert.AreEqual(before, new FileInfo(journal).Length, "Nothing was written without the lease.");
        Assert.IsEmpty(runtime.GetHeldSessionLeases());
    }

    [TestMethod]
    public async Task ShownSessions_HoldTheirLeases_AndAnIdleSessionNobodyShows_IsReleased()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, sessionLeaseOwner: SessionLeaseOwner.DesktopKind);
        var runtime = host.RuntimeService;
        runtime.IdleReleaseDelay = TimeSpan.Zero;
        runtime.IdleSweepInterval = TimeSpan.FromMilliseconds(20);
        var leases = new SessionLeaseStore(host.CatalogOptions);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var shown = await runtime.CreateProjectSessionAsync(host.CurrentProject, options, "Shown");
        var hidden = await runtime.CreateProjectSessionAsync(host.CurrentProject, options, "Hidden");
        var notAttached = Guid.CreateVersion7().ToString();

        runtime.SetShownSessions([shown.SessionId, notAttached]);

        Assert.AreEqual(SessionLeaseOwner.DesktopKind, leases.TryReadHolder(notAttached)?.OwnerKind, "A shown session holds its lease without an attachment.");
        await WaitUntilAsync(() => leases.TryReadHolder(hidden.SessionId) is null);
        Assert.IsNotNull(leases.TryReadHolder(shown.SessionId), "A shown session keeps its attachment and its lease.");

        // A tab that closes on an idle session lets another process continue it.
        runtime.SetShownSessions([]);
        Assert.IsNull(leases.TryReadHolder(notAttached));
        await WaitUntilAsync(() => leases.TryReadHolder(shown.SessionId) is null);

        // A shown session another process holds is taken once that process releases it.
        var other = leases.TryAcquire(shown.SessionId, new SessionLeaseOwner(SessionLeaseOwner.CliKind, "other"));
        Assert.IsNotNull(other);
        runtime.SetShownSessions([shown.SessionId]);
        Assert.AreEqual(SessionLeaseOwner.CliKind, runtime.GetSessionLeaseHolderElsewhere(shown.SessionId)?.OwnerKind);
        other.Dispose();
        runtime.RetryShownSessionLeases();
        Assert.AreEqual(SessionLeaseOwner.DesktopKind, leases.TryReadHolder(shown.SessionId)?.OwnerKind);
    }

    [TestMethod]
    public async Task ARuntimeWithoutLeases_TakesNone()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "No lease");

        Assert.IsFalse(host.RuntimeService.TakesSessionLeases);
        Assert.IsNull(new SessionLeaseStore(host.CatalogOptions).TryReadHolder(session.SessionId));
        using (host.RuntimeService.HoldSession(session.SessionId)) { }
    }

    private static bool Contains<T>(Exception? exception) where T : Exception
    {
        for (var depth = 0; exception is not null && depth < 8; depth++)
        {
            if (exception is T) return true;
            if (exception is AggregateException aggregate) return aggregate.InnerExceptions.Any(Contains<T>);
            exception = exception.InnerException;
        }

        return false;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(Hang);
        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }
}
