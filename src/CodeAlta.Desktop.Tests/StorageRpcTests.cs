using System.Collections.Immutable;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Storage;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class StorageRpcTests
{
    [TestMethod]
    public async Task UnownedAndStaleRequests_HaveNoAccess()
    {
        var unavailable = new StorageService();
        Assert.AreEqual("unavailable", (await unavailable.ReadSettingsAsync(new(null), default)).Status);
        Assert.AreEqual("unavailable", (await ReadScanAsync(unavailable, new(null))).Status);
        Assert.AreEqual("unavailable", (await unavailable.SaveSettingsAsync(new(null, new(false, false, 1, "months")), default)).Status);
        var root = NewRoot();
        var owned = new StorageService(new CatalogOptions { GlobalRoot = root }, "host");
        Assert.AreEqual("stale_epoch", (await ReadScanAsync(owned, new("old"))).Status);
        Assert.AreEqual("stale_epoch", (await owned.SaveSettingsAsync(new("old", new(false, false, 1, "months")), default)).Status);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public async Task SettingsAndScan_AreIndependentAndNeverDelete()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "scratchpad"));
        var sentinel = Path.Combine(root, "scratchpad", "keep");
        await File.WriteAllTextAsync(sentinel, "do not delete during settings operations");
        try
        {
            var service = new StorageService(new CatalogOptions { GlobalRoot = root }, "host");
            var initial = await service.ReadSettingsAsync(new("host"), default);
            Assert.AreEqual(new StoragePreferences(true, true, 3, "months"), initial.Settings);
            Assert.IsFalse(File.Exists(Path.Combine(root, "config.toml")), "reading does not write defaults");
            var preferences = new StoragePreferences(false, false, 45, "days");
            Assert.AreEqual(preferences, (await service.SaveSettingsAsync(new("host", preferences), default)).Settings);
            Assert.AreEqual("invalid_request", (await service.SaveSettingsAsync(new("host", preferences with { RetentionAmount = 0 }), default)).Status);
            Assert.AreEqual("invalid_request", (await service.SaveSettingsAsync(new("host", preferences with { RetentionEnabled = true, RetentionAmount = int.MaxValue }), default)).Status);
            var scan = await ReadScanAsync(service, new("host"));
            Assert.AreEqual("ok", scan.Status);
            Assert.IsNotNull(scan.Snapshot);
            Assert.IsTrue(scan.Snapshot.TotalBytes > 0);
            Assert.IsTrue(File.Exists(sentinel));
            Assert.AreEqual(preferences, (await service.ReadSettingsAsync(new("host"), default)).Settings);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task InvalidConfiguration_ExposesNoValuesOrFileContents_ButStillMeasures()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "config.toml"), "[storage.session_retention]\nunit = 'do-not-echo-secret'\n");
            var service = new StorageService(new CatalogOptions { GlobalRoot = root }, "host");
            var response = await service.ReadSettingsAsync(new("host"), default);
            Assert.AreEqual("read_failed", response.Status);
            Assert.IsNull(response.Settings);
            Assert.DoesNotContain("do-not-echo-secret", response.Error!);
            Assert.AreEqual("ok", (await ReadScanAsync(service, new("host"))).Status);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "codealta-storage-rpc-" + Guid.NewGuid().ToString("N"));

    private static async Task<StorageScanResponse> ReadScanAsync(StorageService service, StorageRequest request)
    {
        StorageScanResponse? result = null;
        await foreach (var update in service.ScanAsync(request, default)) result = update;
        Assert.IsNotNull(result);
        return result;
    }

    [TestMethod]
    public async Task ScanStreamsProgressAndExactFinalCounts_WhileSettingsAndFileWritesRemainAvailable()
    {
        var root = NewRoot();
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        try
        {
            for (var i = 0; i < 256; i++) await File.WriteAllTextAsync(Path.Combine(sessions, $"{i}.jsonl"), "fixture");
            var service = new StorageService(new CatalogOptions { GlobalRoot = root }, "host");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var scan = service.ScanAsync(new("host"), timeout.Token).GetAsyncEnumerator();
            Assert.IsTrue(await scan.MoveNextAsync());
            Assert.AreEqual("running", scan.Current.Status);
            Assert.IsNull(scan.Current.Snapshot);
            // Pause consumption of the progress stream, not the producer or unrelated RPC/file work.
            Assert.IsTrue(await scan.MoveNextAsync());
            Assert.AreEqual("ok", (await service.ReadSettingsAsync(new("host"), timeout.Token)).Status);
            await using (var writer = new FileStream(Path.Combine(sessions, "0.jsonl"), FileMode.Append, FileAccess.Write, FileShare.None))
                await writer.WriteAsync(new byte[8], timeout.Token);
            var last = scan.Current;
            while (await scan.MoveNextAsync())
            {
                Assert.IsTrue(scan.Current.ProcessedFiles >= last.ProcessedFiles);
                Assert.IsTrue(scan.Current.ProcessedDirectories >= last.ProcessedDirectories);
                last = scan.Current;
            }
            Assert.AreEqual("ok", last.Status);
            Assert.AreEqual(256L, last.ProcessedFiles);
            Assert.AreEqual(1L, last.ProcessedDirectories);
            Assert.IsNotNull(last.Snapshot);
            Assert.AreEqual(256L, last.Snapshot.Categories.Sum(category => category.FileCount));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancelingScanStreamDoesNotCancelMaintenanceOrLaterScans()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 256; i++) await File.WriteAllTextAsync(Path.Combine(root, $"{i}"), "fixture");
            var cleanup = new Cleanup();
            var service = new StorageService(new CatalogOptions { GlobalRoot = root }, "host", cleanup);
            using var cancellation = new CancellationTokenSource();
            await using (var scan = service.ScanAsync(new("host"), cancellation.Token).GetAsyncEnumerator())
            {
                Assert.IsTrue(await scan.MoveNextAsync());
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await scan.MoveNextAsync());
            }
            Assert.AreEqual(ScratchpadCleanupState.Running, cleanup.Snapshot.State);
            Assert.AreEqual(0, cleanup.RunCalls);
            Assert.AreEqual(256L, (await ReadScanAsync(service, new("host"))).ProcessedFiles);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Maintenance_LateSubscriberReplaysAndDisposalDoesNotCancelWorker()
    {
        var cleanup = new Cleanup();
        var service = new StorageService(new CatalogOptions { GlobalRoot = NewRoot() }, "host", cleanup);
        await using (var reader = service.WatchMaintenanceAsync(new("host"), default).GetAsyncEnumerator())
        {
            Assert.IsTrue(await reader.MoveNextAsync());
            Assert.AreEqual("running", reader.Current.State);
            Assert.AreEqual(4L, reader.Current.ProcessedFiles);
            Assert.AreEqual(2L, reader.Current.ProcessedDirectories);
            cleanup.Publish(ScratchpadCleanupState.Partial);
            Assert.IsTrue(await reader.MoveNextAsync());
            Assert.AreEqual("partial", reader.Current.State);
            Assert.AreEqual(3L, reader.Current.RemovedFiles);
            Assert.AreEqual(1, cleanup.Subscribers);
        }
        Assert.AreEqual(0, cleanup.Subscribers);
        Assert.AreEqual(0, cleanup.RunCalls, "Status has no authority to start or cancel host maintenance.");
        await using var late = service.WatchMaintenanceAsync(new("host"), default).GetAsyncEnumerator();
        Assert.IsTrue(await late.MoveNextAsync());
        Assert.AreEqual("partial", late.Current.State);
        await using var stale = service.WatchMaintenanceAsync(new("old"), default).GetAsyncEnumerator();
        Assert.IsFalse(await stale.MoveNextAsync());
    }

    [TestMethod]
    [DataRow("disabled")]
    [DataRow("invalid_cutoff")]
    [DataRow("protection_unavailable")]
    public async Task Retention_ReplaysHostOutcomeWithoutStartingOrCancelingIt(string reason)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "config.toml"), "");
        try
        {
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = root, StateRoot = root, CurrentProjectPath = root,
                DiscoveryScope = new(root, root), BuiltInSkillRoot = Path.Combine(root, "empty-skills"),
                StartPlugins = false, PluginSafeMode = true, IsHeadless = true,
            });
            var retention = new SessionRetentionService(host.Commands, host.RuntimeService, new SessionRetentionProtection(), null);
            var service = new StorageService(new CatalogOptions { GlobalRoot = root }, "host", null, retention);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using (var reader = service.WatchRetentionAsync(new("host"), timeout.Token).GetAsyncEnumerator())
            {
                Assert.IsTrue(await reader.MoveNextAsync());
                Assert.AreEqual("not_started", reader.Current.State, "Subscribing must not start maintenance.");
                var now = DateTimeOffset.UtcNow;
                DateTimeOffset? cutoff = reason == "disabled" ? null : reason == "invalid_cutoff" ? now : now.AddMonths(-1);
                var outcome = await retention.RunOnceAsync(now, cutoff);
                Assert.IsTrue(await reader.MoveNextAsync());
                Assert.AreEqual("skipped", reader.Current.State);
                Assert.AreEqual(reason, reader.Current.Reason);
                Assert.AreEqual(outcome.Examined, reader.Current.Examined);
                Assert.AreEqual(0, reader.Current.Deleted);
                Assert.AreEqual(0, reader.Current.Skipped);
                timeout.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
            }
            Assert.AreEqual(reason, retention.Status.Reason, "Closing a presentation stream cannot cancel the original pass.");
            await using var late = service.WatchRetentionAsync(new("host"), default).GetAsyncEnumerator();
            Assert.IsTrue(await late.MoveNextAsync());
            Assert.AreEqual(reason, late.Current.Reason);
            await using var stale = service.WatchRetentionAsync(new("old"), default).GetAsyncEnumerator();
            Assert.IsFalse(await stale.MoveNextAsync());
            await using var unavailable = new StorageService().WatchRetentionAsync(new(null), default).GetAsyncEnumerator();
            Assert.IsFalse(await unavailable.MoveNextAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ByteCounts_RoundTripBeyondJavaScriptIntegerPrecision()
    {
        var source = new StorageScanResponse("ok", new("root", long.MaxValue, DateTimeOffset.UtcNow,
            [new("sessions", long.MaxValue, 1)], 0, 0, false)) { ProcessedFiles = long.MaxValue, ProcessedDirectories = long.MaxValue };
        var json = JsonSerializer.Serialize(source, DesktopJsonContext.Default.StorageScanResponse);
        StringAssert.Contains(json, "\"9223372036854775807\"");
        var copy = JsonSerializer.Deserialize(json, DesktopJsonContext.Default.StorageScanResponse);
        Assert.AreEqual(long.MaxValue, copy!.Snapshot!.TotalBytes);
        Assert.AreEqual(long.MaxValue, copy.Snapshot.Categories[0].Bytes);
        Assert.AreEqual(long.MaxValue, copy.ProcessedFiles);
        Assert.AreEqual(long.MaxValue, copy.ProcessedDirectories);
    }

    private sealed class Cleanup : IStartupScratchpadCleanup
    {
        private Action<ScratchpadCleanupSnapshot>? _observer;
        public ScratchpadCleanupSnapshot Snapshot { get; private set; } = new(ScratchpadCleanupState.Running,
            3, 2, 1, ImmutableArray.Create("entry_unavailable"), DateTimeOffset.UtcNow, null)
        { ProcessedFiles = 4, ProcessedDirectories = 2 };
        public string StateRoot => "unused";
        public string GlobalRoot => "unused";
        public int Subscribers { get; private set; }
        public int RunCalls { get; private set; }
        public IDisposable Subscribe(Action<ScratchpadCleanupSnapshot> observer)
        {
            Subscribers++;
            _observer += observer;
            observer(Snapshot);
            return new Subscription(() => { Subscribers--; _observer -= observer; });
        }
        public Task RunAsync(bool enabled, bool configurationValid, CancellationToken cancellationToken)
        {
            RunCalls++;
            return Task.CompletedTask;
        }
        internal void Publish(ScratchpadCleanupState state)
        {
            Snapshot = Snapshot with { State = state };
            _observer?.Invoke(Snapshot);
        }
        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
