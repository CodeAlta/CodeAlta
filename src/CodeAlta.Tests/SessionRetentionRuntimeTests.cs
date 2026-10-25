using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionRetentionRuntimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    [TestMethod]
    [DataRow("unreadable_tail")]
    [DataRow("unknown_records")]
    [DataRow("no_codealta_header")]
    public async Task JournalContentNeverKeepsAnExpiredSession(string content)
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows exact-file retirement.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old", header: content != "no_codealta_header");
        if (content == "unreadable_tail") await File.AppendAllTextAsync(fixture.Journal("old"), "{broken record\n");
        if (content == "unknown_records")
            await File.AppendAllTextAsync(fixture.Journal("old"), "{\"$type\":\"raw\",\"backendEventType\":\"local.userMessage\",\"raw\":{},\"sessionId\":\"old\"}\n{\"$type\":\"future-event\"}\n");
        fixture.Age("old", Now.AddMonths(-2));
        var result = await fixture.Service(new FakeArchive()).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(1, result.Deleted, result.Reason);
        Assert.AreEqual("completed", result.State, result.Reason);
        Assert.IsFalse(File.Exists(fixture.Journal("old")));
    }

    [TestMethod]
    public async Task RecentChildKeepsItsExpiredFamily()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync("parent");
        await fixture.SeedAsync("child", parent: "parent", activity: Now.AddDays(-1));
        var archive = new FakeArchive();
        var result = await fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual("completed", result.State, result.Reason);
        Assert.AreEqual(2, result.Examined);
        Assert.AreEqual(0, result.Deleted);
        Assert.AreEqual(2, result.Skipped);
        Assert.AreEqual(0, archive.Prepared);
        Assert.IsTrue(File.Exists(fixture.Journal("parent")));
        Assert.IsTrue(File.Exists(fixture.Journal("child")));
    }

    [TestMethod]
    public async Task ExpiredFamilyIsRemovedLeavesFirstInOnePass()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows exact-file retirement.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("parent");
        await fixture.SeedAsync("child", parent: "parent");
        await fixture.SeedAsync("grandchild", parent: "child");
        var archive = new FakeArchive();
        var result = await fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(3, result.Deleted, result.Reason);
        CollectionAssert.AreEqual(new[] { "grandchild", "child", "parent" }, archive.PreparedIds);
        Assert.IsFalse(File.Exists(fixture.Journal("parent")));
    }

    [TestMethod]
    public async Task ChildThatCannotBeRemovedKeepsItsParent()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows exact-file retirement.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("parent");
        await fixture.SeedAsync("child", parent: "parent");
        Assert.IsTrue(fixture.Protection.Publish("window", ["child"], true));
        var result = await fixture.Service(new FakeArchive()).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(0, result.Deleted);
        Assert.AreEqual(2, result.Examined);
        Assert.AreEqual("completed", result.State, "An open session is kept as the policy wants: the pass is not partial.");
        Assert.IsNull(result.Reason);
        Assert.IsTrue(File.Exists(fixture.Journal("parent")));
    }

    [TestMethod]
    public async Task CancellationKeepsOriginalRetirementClaimsUntilArchiveSettles()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows exact-file retirement.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archive = new FakeArchive { WaitDuringPrepare = async () => { entered.SetResult(); await release.Task; } };
        using var cancellation = new CancellationTokenSource();
        var retirement = fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<TimeoutException>(async () => await retirement.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.IsFalse(fixture.Protection.Publish("window", ["old"], true), "Original work still owns the claim.");
            Assert.IsNull(fixture.Runtime.Jobs.TryReserveRetirement("old"));
        }
        finally { release.TrySetResult(); }
        var result = await retirement;
        Assert.AreEqual(0, result.Deleted);
        Assert.IsTrue(File.Exists(fixture.Journal("old")));
    }

    [TestMethod]
    public async Task QueueSubmittedDuringArchiveIsRejectedAndNeverRecreatesJournal()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows exact-file retirement.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archive = new FakeArchive { WaitDuringPrepare = async () => { entered.SetResult(); await release.Task; } };
        var retirement = fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var descriptor = new SessionViewDescriptor { SessionId = "old", CreatedAt = Now.AddYears(-1), WorkingDirectory = fixture.Root };
        var queue = fixture.Runtime.QueuePromptAsync(descriptor, "must not recreate", "send", null);
        var options = new SessionExecutionOptions
        {
            ProviderId = new("retention-test"), WorkingDirectory = fixture.Root,
            OnPermissionRequest = static (_, _) => throw new AssertFailedException("No provider admission during retirement."),
        };
        var child = fixture.Runtime.CreateGlobalSessionAsync(options, "late child", "old", null);
        var resume = fixture.Runtime.EnsureCoordinatorSessionAsync(descriptor, options);
        var send = fixture.Runtime.SendAsync(descriptor, options, new AgentSendOptions { Input = AgentInput.Text("stale") });
        var append = fixture.Runtime.AppendSessionEventAsync(descriptor, new AgentNotesEvent(new("retention-test"), "old", Now, null, AgentNotesUpdateKind.Set, "stale"));
        var notes = fixture.Runtime.UpdateNotesAsync("old", "stale", AgentNotesUpdateKind.Set, _ => Assert.Fail("No notes committed."));
        release.SetResult();
        var result = await retirement;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await queue);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await child);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await resume);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await send);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await append);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await notes);
        Assert.AreEqual(1, result.Deleted, result.Reason);
        Assert.IsFalse(File.Exists(fixture.Journal("old")));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Runtime.QueuePromptAsync(descriptor, "stale", "send", null));
        Assert.IsFalse(File.Exists(fixture.Journal("old")));
    }

    [TestMethod]
    public async Task StatusSubscriptionReplaysAndCanBeDisposed()
    {
        await using var fixture = new Fixture();
        var service = fixture.Service(null);
        var statuses = new List<SessionRetentionStatus>();
        using var subscription = service.Subscribe(status => statuses.Add(status));
        Assert.AreEqual("not_started", statuses.Single().State);
        using var failingPresentation = service.Subscribe(_ => throw new InvalidOperationException("Presentation failed."));
        var stopped = new List<SessionRetentionStatus>();
        var removed = service.Subscribe(status => stopped.Add(status));
        removed.Dispose();
        await service.RunOnceAsync(Now, null);
        Assert.AreEqual("disabled", statuses[^1].Reason);
        Assert.AreEqual(1, stopped.Count, "Disposed observers must not receive the terminal update.");
        var replay = new List<SessionRetentionStatus>();
        var late = service.Subscribe(status => replay.Add(status));
        late.Dispose();
        late.Dispose();
        Assert.AreEqual(service.Status, replay.Single());
    }

    [TestMethod]
    public async Task RetirementHoldsBackCommandsOfItsSessionOnly()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows exact-file retirement.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archive = new FakeArchive { WaitDuringPrepare = async () => { entered.SetResult(); await release.Task; } };
        var retirement = fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        OwnedSessionCommandAdmission other;
        try
        {
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Busy, fixture.Commands.AdmitSend(new("retired-send", "old", "stale")).Kind);
            other = fixture.Commands.AdmitSend(new("other-send", "other", "hello"));
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, other.Kind, "A cleanup must not hold back prompts to other sessions.");
        }
        finally { release.TrySetResult(); }
        await other.Receipt!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, (await retirement).Deleted);
    }

    [TestMethod]
    public async Task RecentSessionsAreKeptWithoutMakingThePassPartial_AndProgressCountsFreedBytes()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact-handle deletion currently fails closed on other platforms.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old");
        await fixture.SeedAsync("recent", activity: Now.AddDays(-1));
        var length = new FileInfo(fixture.Journal("old")).Length;
        var archive = new FakeArchive();
        var service = fixture.Service(archive);
        var statuses = new List<SessionRetentionStatus>();
        using var subscription = service.Subscribe(statuses.Add);
        var result = await service.RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual("completed", result.State, result.Reason);
        Assert.AreEqual(2, result.Total);
        Assert.AreEqual(2, result.Examined);
        Assert.AreEqual(1, result.Deleted);
        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(length, result.FreedBytes);
        Assert.AreEqual(1, archive.Prepared, "A recent session never reaches the archive.");
        Assert.IsTrue(File.Exists(fixture.Journal("recent")));
        Assert.IsTrue(statuses.Any(status => status.State == "running" && status.Total == 2 && status.Examined == 0),
            "The total is published before the first session is checked.");
    }

    [TestMethod]
    [DataRow("none")]
    [DataRow("missing")]
    [DataRow("wrong_source")]
    [DataRow("throw")]
    public async Task StatisticsFailureNeverKeepsAnExpiredSession(string mode)
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact-handle deletion currently fails closed on other platforms.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old");
        var archive = new FakeArchive { Mode = mode };
        var result = await fixture.Service(mode == "none" ? null : archive).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(1, result.Deleted, result.Reason);
        Assert.AreEqual("completed", result.State, result.Reason);
        Assert.IsFalse(File.Exists(fixture.Journal("old")));
        Assert.AreEqual(0, archive.Reconciled, "Only a matching receipt is reconciled.");
    }

    [TestMethod]
    public async Task SavedStatisticsPrecedeDeletion_AndPassIsIdempotent()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact-handle deletion currently fails closed on other platforms.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old");
        var archive = new FakeArchive();
        var service = fixture.Service(archive);
        var first = service.RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreSame(first, service.RunOnceAsync(Now, Now.AddYears(-1)));
        var result = await first;
        Assert.AreEqual(1, result.Deleted, result.Reason);
        Assert.AreEqual(1, archive.Prepared);
        Assert.AreEqual(1, archive.Reconciled);
        Assert.IsFalse(File.Exists(fixture.Journal("old")));
        Assert.AreEqual("protected", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "auth", "sentinel")));
    }

    [TestMethod]
    public async Task ReadinessLossDuringArchivePreventsDeletion()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old");
        var archive = new FakeArchive { DuringPrepare = () => fixture.Protection.MarkUnknown("window") };
        var result = await fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(0, result.Deleted);
        Assert.IsTrue(File.Exists(fixture.Journal("old")));
    }

    [TestMethod]
    public async Task FamiliesOpenSessionsAndUnreadyHostAreRetained()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync("parent");
        await fixture.SeedAsync("child", parent: "parent", activity: Now.AddDays(-1));
        await fixture.SeedAsync("open");
        Assert.IsTrue(fixture.Protection.Publish("window", ["open"], true));
        var archive = new FakeArchive();
        var result = await fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(0, result.Deleted);
        Assert.AreEqual(0, archive.Prepared);
        fixture.Protection.MarkUnknown("external-work");
        Assert.AreEqual("protection_unavailable", (await fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1))).Reason);
    }

    [TestMethod]
    public async Task ExactCutoffIsRetained_ReadingDoesNotChangeTheAge()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync("boundary", activity: Now.AddMonths(-1));
        var result = await fixture.Service(new FakeArchive()).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(0, result.Deleted);
        Assert.IsTrue(File.Exists(fixture.Journal("boundary")));
        Assert.AreEqual(Now.AddMonths(-1).UtcDateTime, File.GetLastWriteTimeUtc(fixture.Journal("boundary")));
    }

    [TestMethod]
    public async Task ArtifactCleanupDoesNotFollowNestedOrExternalPaths()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact-handle deletion currently fails closed on other platforms.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old");
        var attachments = Path.ChangeExtension(fixture.Journal("old"), ".attachments");
        Directory.CreateDirectory(Path.Combine(attachments, "nested"));
        await File.WriteAllTextAsync(Path.Combine(attachments, "image.png"), "owned");
        await File.WriteAllTextAsync(Path.Combine(attachments, "nested", "unknown"), "leave");
        var trace = new AgentRuntimePathLayout(fixture.Root).GetSessionTraceFilePath("old");
        Directory.CreateDirectory(Path.GetDirectoryName(trace)!);
        await File.WriteAllTextAsync(trace, "owned trace");
        var result = await fixture.Service(new FakeArchive()).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(1, result.Deleted, result.Reason);
        Assert.AreEqual("deleted_partial_artifacts", result.Reason);
        Assert.IsFalse(File.Exists(Path.Combine(attachments, "image.png")));
        Assert.IsFalse(File.Exists(trace));
        Assert.AreEqual("leave", await File.ReadAllTextAsync(Path.Combine(attachments, "nested", "unknown")));
        await File.WriteAllTextAsync(Path.Combine(attachments, "image.png"), "new unrelated generation");
        await fixture.Service(new FakeArchive()).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual("new unrelated generation", await File.ReadAllTextAsync(Path.Combine(attachments, "image.png")));
    }

    [TestMethod]
    public async Task WorkProtectingEverySessionIsWaitedFor_ThenTheSessionIsRemoved()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact-handle deletion currently fails closed on other platforms.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old");
        // Like a provider prepared at startup: it protects every session while it runs, then ends.
        var claim = fixture.Protection.TryProtectAll()!;
        var pass = fixture.Service(new FakeArchive()).RunOnceAsync(Now, Now.AddMonths(-1));
        await Task.Delay(200);
        Assert.IsFalse(pass.IsCompleted, "The pass waits for the work to end instead of keeping every session.");
        claim.Dispose();
        var result = await pass.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual("completed", result.State, result.Reason);
        Assert.AreEqual(1, result.Deleted);
        Assert.IsFalse(File.Exists(fixture.Journal("old")));
    }

    [TestMethod]
    public async Task WorkProtectingEverySessionThatDoesNotEndMakesThePassPartial()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old");
        await fixture.SeedAsync("older");
        using var claim = fixture.Protection.TryProtectAll();
        var service = new SessionRetentionService(fixture.Commands, fixture.Runtime, fixture.Protection, null) { ProtectionWait = TimeSpan.FromMilliseconds(200) };
        var result = await service.RunOnceAsync(Now, Now.AddMonths(-1)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual("partial", result.State);
        Assert.AreEqual("all_sessions_protected", result.Reason);
        Assert.AreEqual(0, result.Deleted);
        Assert.IsTrue(File.Exists(fixture.Journal("old")));
        Assert.IsTrue(File.Exists(fixture.Journal("older")));
    }

    [TestMethod]
    public async Task EveryOldFileOfTheSessionsFolderIsRemoved_ButThoseOfKeptOrProtectedSessions()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact-handle deletion currently fails closed on other platforms.");
        await using var fixture = new Fixture();
        await fixture.SeedAsync("recent", activity: Now.AddDays(-1));
        var sessions = new AgentRuntimePathLayout(fixture.Root).SessionsRootPath;
        var old = Now.AddMonths(-4);
        string Write(string relative, DateTimeOffset lastWrite, string content = "x")
        {
            var path = Path.Combine(sessions, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
            return path;
        }
        var keptAttachment = Write(Path.Combine(Path.GetRelativePath(sessions, Path.ChangeExtension(fixture.Journal("recent"), ".attachments")), "old.png"), old);
        var stray = Write("stray.txt", old);
        var unreadable = Write(Path.Combine("2024", "01", "02", "unreadable.jsonl"), old, "not a journal");
        var orphanTrace = Write(Path.Combine("traces", "gone.trace"), old);
        var orphanAttachment = Write(Path.Combine("2023", "03", "04", "gone.attachments", "nested", "image.png"), old);
        var recentStray = Write("recent.txt", Now.AddDays(-2));
        var protectedJournal = Write(Path.Combine("2024", "05", "06", "open.jsonl"), old, "not a journal");
        var lease = Write(Path.Combine(AgentRuntimePathLayout.SessionLeasesFolderName, "gone.lease"), old);
        var marker = Write(Path.Combine(AgentRuntimePathLayout.SessionMarkersFolderName, "gone.json"), old);
        Assert.IsTrue(fixture.Protection.Publish("window", ["open"], true));

        var result = await fixture.Service(new FakeArchive()).RunOnceAsync(Now, Now.AddMonths(-3));

        Assert.AreEqual(4, result.FilesDeleted, result.Reason);
        Assert.AreEqual(1, result.Deleted, "The unreadable journal is a session removed, counted with the others.");
        Assert.AreEqual("completed", result.State, result.Reason);
        Assert.IsFalse(File.Exists(stray));
        Assert.IsFalse(File.Exists(unreadable));
        Assert.IsFalse(File.Exists(orphanTrace));
        Assert.IsFalse(File.Exists(orphanAttachment));
        Assert.IsFalse(Directory.Exists(Path.Combine(sessions, "2024", "01")), "An emptied date folder goes.");
        Assert.IsFalse(Directory.Exists(Path.Combine(sessions, "2023")), "An emptied attachments folder and its date folders go.");
        Assert.IsTrue(Directory.Exists(Path.Combine(sessions, "traces")));
        Assert.IsTrue(File.Exists(keptAttachment), "The files of a session that stays stay.");
        Assert.IsTrue(File.Exists(recentStray));
        Assert.IsTrue(File.Exists(protectedJournal), "The files of a session in use stay.");
        Assert.IsTrue(File.Exists(lease), "The leases belong to the processes that hold them.");
        Assert.IsTrue(File.Exists(marker), "The markers belong to the instance that lists their sessions.");
        Assert.IsTrue(File.Exists(fixture.Journal("recent")));
        Assert.AreEqual("protected", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "auth", "sentinel")));
    }

    private sealed class FakeArchive : ISessionStatisticsRetirementArchive
    {
        public Func<string, string> ResolvePath { get; set; } = _ => throw new AssertFailedException("No fixture source resolver.");
        public string Mode { get; init; } = "ok";
        public Action? DuringPrepare { get; init; }
        public Func<Task>? WaitDuringPrepare { get; init; }
        public int Prepared => PreparedIds.Count;
        public List<string> PreparedIds { get; } = [];
        public int Reconciled { get; private set; }
        public async ValueTask<SessionStatisticsRetirementResult> PrepareAsync(string sessionId, CancellationToken cancellationToken)
        {
            PreparedIds.Add(sessionId);
            // Like Statistics, the fake describes the journal without reading it.
            var file = new FileInfo(ResolvePath(sessionId));
            var source = new SessionStatisticsSource(sessionId, file.FullName, file.Length, file.LastWriteTimeUtc, Sha256: null);
            Assert.IsTrue(File.Exists(source.Path));
            Assert.IsTrue(source.Length > 0);
            DuringPrepare?.Invoke();
            if (WaitDuringPrepare is not null) await WaitDuringPrepare();
            if (Mode == "throw") throw new IOException("archive disk full");
            return new(Mode == "missing" ? null : new SessionStatisticsRetirementReceipt(
                Mode == "wrong_source" ? source with { Length = source.Length + 1 } : source,
                "durable-fixture", new string('a', 64)), Mode == "missing" ? "history_incomplete" : null);
        }
        public ValueTask<bool> ReconcileDeletedAsync(SessionStatisticsRetirementReceipt receipt, bool artifactsRemaining, CancellationToken cancellationToken)
        {
            Assert.IsFalse(File.Exists(receipt.Source.Path));
            Reconciled++;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CodeAlta-retention-" + Guid.NewGuid().ToString("N"));
        public SessionViewCatalog Catalog { get; }
        public AgentHub Hub { get; }
        public SessionRuntimeService Runtime { get; }
        public OwnedSessionCommandService Commands { get; }
        public SessionRetentionProtection Protection { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "auth"));
            File.WriteAllText(Path.Combine(Root, "auth", "sentinel"), "protected");
            var options = new CatalogOptions { GlobalRoot = Root };
            Catalog = new SessionViewCatalog(options);
            var registry = new ModelProviderRegistry();
            registry.RegisterOrReplace(new ModelProviderDescriptor(new("retention-test"), "Forbidden"),
                () => throw new AssertFailedException("Retention must never start providers."));
            Hub = new AgentHub(registry, Root);
            var discovery = new ForbiddenDiscovery();
            var skills = new SkillCatalog([discovery]);
            var projects = new ProjectCatalog(options);
            Runtime = new SessionRuntimeService(Hub, new AgentSessionCatalog(Catalog.JournalStore.CreateSessionStore()), projects,
                Catalog, new AgentInstructionTemplateProvider(skills, options, discovery), options, skills);
            Commands = new OwnedSessionCommandService(Runtime, projects, options, 8, () => true);
            Protection.SealParticipants(["window", "external-work"]);
            Protection.Publish("window", [], true);
            Protection.Publish("external-work", [], true);
        }
        public SessionRetentionService Service(ISessionStatisticsRetirementArchive? archive)
        {
            if (archive is FakeArchive fake) fake.ResolvePath = Journal;
            return new(Commands, Runtime, Protection, archive);
        }
        public string Journal(string id) => new AgentRuntimePathLayout(Root).GetSessionFilePath(id, Now.AddYears(-1));
        // The age of a journal is its last write time: a seeded journal is as old as its last activity.
        public async Task SeedAsync(string id, string? parent = null, DateTimeOffset? activity = null, bool header = true)
        {
            var created = Now.AddYears(-1);
            var store = Catalog.JournalStore.CreateSessionStore();
            var summary = new AgentSessionSummary
            {
                SessionId = id, ProviderId = new("retention-test"), ProviderKey = "retention-test", ParentSessionId = parent,
                ProtocolFamily = "test-protocol", WorkingDirectory = Root, CreatedAt = created, UpdatedAt = Now, Title = "Retention fixture",
            };
            if (header)
                await Catalog.JournalStore.EnsureHeaderAsync(new SessionViewDescriptor
                {
                    SessionId = id, ProviderId = "retention-test", ProviderKey = "retention-test", ParentSessionId = parent,
                    Kind = SessionViewKind.GlobalSession, WorkingDirectory = Root, CreatedAt = created, Title = "Retention fixture",
                });
            else await store.UpsertSessionAsync(summary); // The format of the first versions: no CodeAlta header.
            await File.AppendAllTextAsync(Journal(id), new AgentContentCompletedEvent(new("retention-test"), id,
                activity ?? Now.AddMonths(-2), null, AgentContentKind.User, "prompt", null, "fixture").ToJson() + "\n");
            await store.UpsertSessionAsync(summary);
            Age(id, activity ?? Now.AddMonths(-2));
        }
        public void Age(string id, DateTimeOffset lastWrite) => File.SetLastWriteTimeUtc(Journal(id), lastWrite.UtcDateTime);
        public async ValueTask DisposeAsync()
        {
            await Commands.DisposeAsync();
            await Runtime.DisposeAsync();
            await Hub.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }

    private sealed class ForbiddenDiscovery : ISystemPromptContentLocator, ISkillRootProvider
    {
        public SystemPromptContentRoots GetRoots(SystemPromptDiscoveryContext context) => throw new AssertFailedException("No profile prompt discovery.");
        public string ResolveBuiltInPromptPath(string relativePromptPath) => throw new AssertFailedException("No prompt discovery.");
        public string ResolveBuiltInDocPath(string fileName) => throw new AssertFailedException("No documentation discovery.");
        public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(SkillDiscoveryContext context, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("No skill discovery.");
    }
}
