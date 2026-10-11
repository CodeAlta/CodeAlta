using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using System.Text.Json;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionRetentionRuntimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CorruptUnprojectableChildMakesFamilyGraphUnknown(bool validHeader)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync("parent");
        if (validHeader) await fixture.SeedAsync("child");
        await File.AppendAllTextAsync(fixture.Journal("child"), "{broken metadata that may have referred to parent\n");
        var archive = new FakeArchive();
        var status = await fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(0, status.Deleted);
        Assert.AreEqual(0, archive.Prepared);
        Assert.IsTrue(File.Exists(fixture.Journal("parent")));
    }

    [TestMethod]
    [DataRow("codealta.sessionHeader", "parent_session_id")]
    [DataRow("codealta.sessionState", "parent_session_id")]
    [DataRow("codealta.sessionHeader", "created_by")]
    [DataRow("codealta.sessionState", "created_by")]
    public async Task DurableHeaderAndStateLineageProtectsBothParentAndChild(string eventType, string property)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync("parent");
        await fixture.SeedAsync("child"); // The local summary has no lineage.
        using var payload = JsonDocument.Parse(property == "parent_session_id"
            ? "{\"session_id\":\"child\",\"parent_session_id\":\"parent\"}"
            : "{\"session_id\":\"child\",\"created_by\":{\"Kind\":\"agent\",\"SourceSessionId\":\"parent\"}}");
        await File.AppendAllTextAsync(fixture.Journal("child"), new AgentRawEvent(new("retention-test"), "child",
            Now.AddMonths(-2), eventType, payload.RootElement).ToJson() + "\n");
        var archive = new FakeArchive();
        var result = await fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(0, result.Deleted);
        Assert.AreEqual(0, archive.Prepared);
        Assert.IsTrue(File.Exists(fixture.Journal("parent")));
        Assert.IsTrue(File.Exists(fixture.Journal("child")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DuplicateLineagePropertiesCannotHideFamilyEdges(bool outerDuplicate)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync("parent");
        await fixture.SeedAsync("child");
        var summary = new AgentSessionSummary
        {
            SessionId = "child", ProviderId = new("retention-test"), ProviderKey = "retention-test", WorkingDirectory = fixture.Root,
            CreatedAt = Now.AddYears(-1), UpdatedAt = Now, ParentSessionId = "parent",
        };
        var payload = JsonSerializer.Serialize(summary, AgentJsonSerializerContext.Default.AgentSessionSummary);
        var noParent = payload.Replace("\"parentSessionId\":\"parent\"", "\"parentSessionId\":null", StringComparison.Ordinal);
        var duplicatePayload = payload.Replace("\"parentSessionId\":\"parent\"", "\"parentSessionId\":\"parent\",\"parentSessionId\":null", StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(outerDuplicate ? noParent : duplicatePayload);
        var line = new AgentRawEvent(new("retention-test"), "child", Now, "local.sessionSummary", parsed.RootElement).ToJson();
        if (outerDuplicate) line = line.Replace("\"raw\":", "\"raw\":" + payload + ",\"raw\":", StringComparison.Ordinal);
        await File.AppendAllTextAsync(fixture.Journal("child"), line + "\n");
        var archive = new FakeArchive();
        var result = await fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(0, result.Deleted);
        Assert.AreEqual(0, archive.Prepared);
        Assert.AreEqual("family_graph_unknown", result.Reason);
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
    [DataRow("missing")]
    [DataRow("wrong_source")]
    [DataRow("unverified")]
    [DataRow("throw")]
    public async Task ArchiveFailureNeverDeletesJournal(string mode)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync("old");
        var before = await File.ReadAllBytesAsync(fixture.Journal("old"));
        var archive = new FakeArchive { Mode = mode };
        await fixture.Service(archive).RunOnceAsync(Now, Now.AddMonths(-1));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Journal("old")));
        Assert.AreEqual(0, archive.Reconciled);
    }

    [TestMethod]
    public async Task VerifiedReceiptPrecedesDeletion_AndPassIsIdempotent()
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
        Assert.AreEqual(1, archive.Verified);
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
        await fixture.SeedAsync("child", parent: "parent");
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
    public async Task ExactCutoffIsRetained_InspectionDoesNotRefreshOlderWork()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync("boundary", activity: Now.AddMonths(-1));
        var result = await fixture.Service(new FakeArchive()).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(0, result.Deleted);
        Assert.IsTrue(File.Exists(fixture.Journal("boundary")));
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

    private sealed class FakeArchive : ISessionStatisticsRetirementArchive
    {
        public Func<string, string> ResolvePath { get; set; } = _ => throw new AssertFailedException("No fixture source resolver.");
        public string Mode { get; init; } = "ok";
        public Action? DuringPrepare { get; init; }
        public Func<Task>? WaitDuringPrepare { get; init; }
        public int Prepared { get; private set; }
        public int Verified { get; private set; }
        public int Reconciled { get; private set; }
        public async ValueTask<SessionStatisticsRetirementResult> PrepareAsync(string sessionId, CancellationToken cancellationToken)
        {
            Prepared++;
            var path = ResolvePath(sessionId);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var digest = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken));
            var source = new SessionStatisticsSource(sessionId, path, stream.Length, File.GetLastWriteTimeUtc(path), digest);
            Assert.IsTrue(File.Exists(source.Path));
            Assert.IsTrue(source.Length > 0);
            Assert.AreEqual(64, source.Sha256.Length);
            DuringPrepare?.Invoke();
            if (WaitDuringPrepare is not null) await WaitDuringPrepare();
            if (Mode == "throw") throw new IOException("archive disk full");
            return new(Mode == "missing" ? null : new SessionStatisticsRetirementReceipt(
                Mode == "wrong_source" ? source with { Sha256 = new string('0', 64) } : source,
                "durable-fixture", new string('a', 64)), Mode == "missing" ? "history_incomplete" : null);
        }
        public ValueTask<bool> ValidateAsync(SessionStatisticsRetirementReceipt receipt, CancellationToken cancellationToken)
        {
            Verified++;
            Assert.IsTrue(File.Exists(receipt.Source.Path));
            return ValueTask.FromResult(Mode != "unverified");
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
        public async Task SeedAsync(string id, string? parent = null, DateTimeOffset? activity = null)
        {
            var created = Now.AddYears(-1);
            await Catalog.JournalStore.EnsureHeaderAsync(new SessionViewDescriptor
            {
                SessionId = id, ProviderId = "retention-test", ProviderKey = "retention-test", ParentSessionId = parent,
                Kind = SessionViewKind.GlobalSession, WorkingDirectory = Root, CreatedAt = created, Title = "Retention fixture",
            });
            await File.AppendAllTextAsync(Journal(id), new AgentContentCompletedEvent(new("retention-test"), id,
                activity ?? Now.AddMonths(-2), null, AgentContentKind.User, "prompt", null, "fixture").ToJson() + "\n");
            await Catalog.JournalStore.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = id, ProviderId = new("retention-test"), ProviderKey = "retention-test", ParentSessionId = parent,
                ProtocolFamily = "test-protocol", WorkingDirectory = Root, CreatedAt = created, UpdatedAt = Now, Title = "Retention fixture",
            });
        }
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
