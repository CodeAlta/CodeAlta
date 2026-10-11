using System.Security.Cryptography;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugin.Statistics.History;

namespace CodeAlta.Plugin.Statistics.Tests;

[TestClass]
public sealed class SessionRetentionArchiveIntegrationTests
{
    private const string SessionId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset Now = JournalBuilder.Time(0).AddMonths(2);

    [TestMethod]
    public async Task RealArchivePrecedesNativeDeletion_AndResetReopenPreservesFactsExactlyOnce()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact-handle retirement currently fails closed outside Windows.");
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        await using var fixture = await RetentionFixture.CreateAsync(harness);
        await fixture.SeedAsync();
        var bytes = await File.ReadAllBytesAsync(fixture.JournalPath);
        var source = new SessionStatisticsSource(SessionId, fixture.JournalPath, bytes.Length,
            File.GetLastWriteTimeUtc(fixture.JournalPath), Convert.ToHexString(SHA256.HashData(bytes)));
        StatisticsArchiveEntry? committedBeforeDeletion = null;
        var productionAdmissionsRefused = false;
        string? factsBeforeDeletion = null;
        string factsAfterDeletion;

        // The service receives the real engine directly, not a fabricated receipt or archive substitute.
        await using (var engine = fixture.CreateEngine(harness))
        {
            await engine.InitializeAsync();
            await engine.ChooseHistoryAsync(HistoryChoice.All);
            Assert.IsEmpty(await new StatisticsArchiveStore(harness.Root).ReadAsync());
            engine.DataChanged += _ =>
            {
                if (!File.Exists(fixture.JournalPath)) return; // Reconciliation publishes again after deletion.
                Assert.IsNull(committedBeforeDeletion);
                // This synchronous observation must finish before PrepareAsync returns to native retirement.
                // A separately opened store proves committed bytes, not the engine's in-memory projection.
                committedBeforeDeletion = new StatisticsArchiveStore(harness.Root).ReadAsync().AsTask().GetAwaiter().GetResult().Single();
                Assert.AreEqual(source, committedBeforeDeletion.Source);
                Assert.AreEqual(Convert.ToHexString(SHA256.HashData(committedBeforeDeletion.Payload)), committedBeforeDeletion.PayloadSha256);
                factsBeforeDeletion = harness.DumpAsync("archive", "session").GetAwaiter().GetResult();
                Assert.Throws<IOException>(() =>
                {
                    using var writer = new FileStream(fixture.JournalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                }, "The source must still be held against writers while its real archive commits.");
                using var competingHost = fixture.Protection.TryReserveRetirement(SessionId);
                Assert.IsNull(competingHost, "The original host claim must cover archive preparation.");
                Assert.ThrowsExactly<InvalidOperationException>(() => fixture.ResumeAsync().GetAwaiter().GetResult());
                Assert.ThrowsExactly<InvalidOperationException>(() => fixture.ModelsAsync().GetAwaiter().GetResult());
                productionAdmissionsRefused = true;
            };

            var status = await fixture.Service(engine).RunOnceAsync(Now, Now.AddMonths(-1));
            Assert.AreEqual("completed", status.State, status.Reason);
            Assert.AreEqual(1, status.Examined);
            Assert.AreEqual(1, status.Deleted, status.Reason);
            Assert.AreEqual(0, status.Skipped);
            Assert.IsNotNull(committedBeforeDeletion);
            Assert.IsNotNull(factsBeforeDeletion);
            Assert.IsTrue(productionAdmissionsRefused, "The real host's Hub and model service must share the archive/deletion registry.");
            Assert.IsFalse(File.Exists(fixture.JournalPath));
            Assert.IsFalse(File.Exists(fixture.AttachmentPath));
            Assert.IsFalse(File.Exists(fixture.TracePath));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.ResumeAsync());
            Assert.IsTrue((await harness.Store.GetJournalAsync(SessionId))!.Deleted);
            Assert.AreEqual(1234L, await harness.SumAsync("usage_q", "input_tokens"));
            Assert.AreEqual(56L, await harness.SumAsync("usage_q", "output_tokens"));
            Assert.AreEqual(1L, await harness.CountAsync("session"));
            Assert.AreEqual(1L, await harness.CountAsync("run"));
            Assert.AreEqual(factsBeforeDeletion, await harness.DumpAsync("archive", "session"));
            factsAfterDeletion = await harness.DumpAsync("archive"); // Includes session metadata's now-deleted flag.

            for (var reset = 0; reset < 2; reset++)
            {
                var resetStatus = await engine.ResetAsync();
                Assert.AreEqual(1, resetStatus.SavedSessions);
                Assert.AreEqual(factsAfterDeletion, await harness.DumpAsync("archive"), "Reset must import exactly one saved projection.");
                await harness.VerifyRollupsAsync();
            }
        }

        // Dispose the old engine/host before reopening the application DB: never two active engines or owners.
        await fixture.DisposeAsync();
        await harness.ReopenAsync();
        await using var restartedFixture = await RetentionFixture.CreateAsync(harness);
        await using var reopened = restartedFixture.CreateEngine(harness);
        await reopened.InitializeAsync();
        Assert.AreEqual(1, reopened.Status.SavedSessions);
        Assert.AreEqual(factsAfterDeletion, await harness.DumpAsync("archive"));
        await reopened.ResetAsync();
        await reopened.ChooseHistoryAsync(HistoryChoice.All);
        await reopened.DrainAsync();
        Assert.AreEqual(factsAfterDeletion, await harness.DumpAsync("archive"));
        await harness.VerifyRollupsAsync();
        var saved = (await new StatisticsArchiveStore(harness.Root).ReadAsync()).Single();
        Assert.AreEqual(committedBeforeDeletion.Generation, saved.Generation);
        Assert.AreEqual(source, saved.Source);
        CollectionAssert.AreEqual(committedBeforeDeletion.Payload, saved.Payload);
        var repeated = await restartedFixture.Service(reopened).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(0, repeated.Examined);
        Assert.AreEqual(0, repeated.Deleted);
        Assert.AreEqual(factsAfterDeletion, await harness.DumpAsync("archive"));
    }

    [TestMethod]
    public async Task OriginalHostClaimPreventsRealArchiveAndNativeDeletion()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact-handle retirement currently fails closed outside Windows.");
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        await using var fixture = await RetentionFixture.CreateAsync(harness);
        await fixture.SeedAsync();
        var before = await File.ReadAllBytesAsync(fixture.JournalPath);
        await using var engine = fixture.CreateEngine(harness);
        await engine.InitializeAsync();
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        using (var original = fixture.Protection.TryProtectSession(SessionId))
        {
            Assert.IsNotNull(original);
            var protectedResult = await fixture.Service(engine).RunOnceAsync(Now, Now.AddMonths(-1));
            Assert.AreEqual(0, protectedResult.Deleted);
            Assert.AreEqual("protection_unavailable", protectedResult.Reason);
            Assert.IsEmpty(await new StatisticsArchiveStore(harness.Root).ReadAsync());
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.JournalPath));
        }
        // A distinct synthetic pass proves exclusion was the lease, not an unavailable engine/source.
        var released = await fixture.Service(engine).RunOnceAsync(Now, Now.AddMonths(-1));
        Assert.AreEqual(1, released.Deleted, released.Reason);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisabledOrUnwritableRealArchiveStillRetiresExpiredSession(bool disabled)
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact-handle retirement currently fails closed outside Windows.");
        await using var harness = await StoreHarness.CreateAsync(archives: true);
        await using var fixture = await RetentionFixture.CreateAsync(harness);
        await fixture.SeedAsync();
        await using var engine = fixture.CreateEngine(harness);
        await engine.InitializeAsync();
        await engine.ChooseHistoryAsync(HistoryChoice.All);
        var archive = new StatisticsArchiveStore(harness.Root);
        Assert.IsEmpty(await archive.ReadAsync());
        if (disabled) await engine.DisposeAsync();
        await using (var archiveLock = disabled ? null : new FileStream(archive.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var status = await fixture.Service(engine).RunOnceAsync(Now, Now.AddMonths(-1));
            Assert.AreEqual(1, status.Examined);
            Assert.AreEqual(1, status.Deleted, "Saved Statistics are best effort: they never keep an expired session.");
            Assert.AreEqual("completed", status.State, status.Reason);
        }
        Assert.IsFalse(File.Exists(fixture.JournalPath));
        Assert.IsFalse(File.Exists(fixture.AttachmentPath));
        Assert.IsFalse(File.Exists(fixture.TracePath));
        Assert.IsEmpty(await archive.ReadAsync());
    }

    private sealed class RetentionFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly CodeAltaHost _host;
        public SessionRetentionProtection Protection => _host.RetentionProtection;
        public string JournalPath { get; }
        public string AttachmentPath { get; }
        public string TracePath { get; }

        private RetentionFixture(string root, CodeAltaHost host)
        {
            _root = root;
            _host = host;
            // This fixture owns every participant: no renderer, plugin worker, reminder or direct provider admission exists.
            // These ready publications qualify only the test host, never Desktop's unknown production participants.
            Protection.SealParticipants(["fixture-window", "fixture-external-work"]);
            Assert.IsTrue(Protection.Publish("fixture-window", [], true));
            Assert.IsTrue(Protection.Publish("fixture-external-work", [], true));
            var layout = new AgentRuntimePathLayout(root);
            JournalPath = layout.GetSessionFilePath(SessionId, JournalBuilder.Time(0));
            AttachmentPath = Path.Combine(Path.ChangeExtension(JournalPath, ".attachments"), "synthetic.png");
            TracePath = layout.GetSessionTraceFilePath(SessionId);
        }

        public static async Task<RetentionFixture> CreateAsync(StoreHarness harness)
        {
            var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = harness.Root, StateRoot = harness.Root, CurrentProjectPath = harness.Root,
                ApplicationDatabase = harness.Application, DiscoveryScope = new(harness.Root, harness.Root),
                BuiltInSkillRoot = Path.Combine(harness.Root, "empty-skills"),
                StartPlugins = false, PluginSafeMode = true, IsHeadless = true,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(new ModelProviderDescriptor(new("retention-test"), "Forbidden"),
                    () => throw new AssertFailedException("Retention must not construct a provider.")),
            });
            return new RetentionFixture(harness.Root, host);
        }

        public StatisticsEngine CreateEngine(StoreHarness harness) => new(harness.Store, _host.SessionJournals,
            new StatisticsEngineOptions { StartDelay = TimeSpan.Zero, FlowDebounce = TimeSpan.Zero });

        public SessionRetentionService Service(StatisticsEngine engine) => new(_host.Commands, _host.RuntimeService, Protection, engine);

        public Task<AgentSessionHandle> ResumeAsync() => _host.AgentHub.ResumeSessionAsync(SessionId, new()
        {
            ProviderKey = "retention-test", WorkingDirectory = _root,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        });

        public Task<IReadOnlyList<AgentModelInfo>> ModelsAsync()
            => _host.ModelProviderInitializationService.GetModelsAsync(new("retention-test"));

        public async Task SeedAsync()
        {
            await _host.SessionViewCatalog.JournalStore.EnsureHeaderAsync(new SessionViewDescriptor
            {
                SessionId = SessionId, ProviderId = "retention-test", ProviderKey = "retention-test",
                Kind = SessionViewKind.GlobalSession, WorkingDirectory = _root,
                CreatedAt = JournalBuilder.Time(0), Title = "Synthetic retention integration",
            });
            var events = new JournalBuilder(SessionId, "retention-test")
                .User(JournalBuilder.Time(1), "r1", "Synthetic prompt")
                .Usage(JournalBuilder.Time(2), "r1", input: 1234, output: 56, cost: 1.25, costUnit: "usd")
                .Assistant(JournalBuilder.Time(3), "r1", "Synthetic answer")
                .Idle(JournalBuilder.Time(4), "r1");
            await File.AppendAllTextAsync(JournalPath, string.Join('\n', events.Lines) + "\n");
            await _host.SessionViewCatalog.JournalStore.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = SessionId, ProviderId = new("retention-test"), ProviderKey = "retention-test",
                ProtocolFamily = "test-protocol", WorkingDirectory = _root,
                CreatedAt = JournalBuilder.Time(0), UpdatedAt = Now, Title = "Synthetic retention integration",
            });
            Directory.CreateDirectory(Path.GetDirectoryName(AttachmentPath)!);
            await File.WriteAllTextAsync(AttachmentPath, "synthetic attachment");
            Directory.CreateDirectory(Path.GetDirectoryName(TracePath)!);
            await File.WriteAllTextAsync(TracePath, "synthetic trace");
            // The age of a session is the last write time of its journal.
            File.SetLastWriteTimeUtc(JournalPath, JournalBuilder.Time(4).UtcDateTime);
        }

        public ValueTask DisposeAsync() => _host.DisposeAsync();
    }
}
