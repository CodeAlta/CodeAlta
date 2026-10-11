using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopSessionRetentionStartupTests
{
    [TestMethod]
    public async Task StartupUsesOriginalHostRegistryAndWaitsForCompleteRestoration()
    {
        using var fixture = new Fixture();
        var protection = DesktopSessionRetention.CreateProtection();
        await using var host = await fixture.CreateHostAsync(protection);
        Assert.AreSame(protection, host.RetentionProtection);
        await using var retention = new DesktopSessionRetention(host.Commands, host.RuntimeService,
            new CodeAltaConfigStore(host.CatalogOptions), new NoCandidatesArchive(), "epoch", host.RetentionProtection);
        var completed = new TaskCompletionSource<SessionRetentionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = retention.Service.Subscribe(status =>
        {
            if (status.State is "completed" or "skipped" or "failed" or "partial") completed.TrySetResult(status);
        });
        Assert.AreEqual("not_started", retention.Service.Status.State);
        Assert.IsTrue(protection.Publish("external-work", [], true));
        Assert.IsFalse(completed.Task.IsCompleted, "External registration alone cannot certify browser restoration.");
        Assert.IsTrue(protection.Publish("desktop-open-drafts", ["protected"], true));
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("completed", result.State);
        Assert.AreEqual(0, result.Examined);
        Assert.IsNull(protection.TryReserveRetirement("protected"));
        protection.MarkUnknown("desktop-open-drafts");
        Assert.AreSame(result, retention.Service.Status, "A later disconnect must not reset or retry the completed pass.");
    }

    [TestMethod]
    public async Task ExternalRegistrationRequiresOriginalRegistryAndDoesNotQualifyWindow()
    {
        using var fixture = new Fixture();
        var protection = DesktopSessionRetention.CreateProtection();
        await using var host = await fixture.CreateHostAsync(protection, startPlugins: true);
        Assert.IsFalse(DesktopSessionRetention.CompleteExternalRegistration(host, new SessionRetentionProtection()));
        Assert.IsNull(protection.TryReserveRetirement("old-session"));
        Assert.IsTrue(DesktopSessionRetention.CompleteExternalRegistration(host, protection));
        Assert.IsNull(protection.TryReserveRetirement("old-session"), "External registration cannot certify the native window.");
        Assert.IsTrue(protection.Publish("desktop-open-drafts", [], true));
        using var retirement = protection.TryReserveRetirement("old-session");
        Assert.IsNotNull(retirement);
    }

    [TestMethod]
    public async Task StartedBorrowedRuntimeWithDifferentRegistryRemainsUnknown()
    {
        using var fixture = new Fixture();
        await using var original = await fixture.CreateHostAsync(DesktopSessionRetention.CreateProtection(), startPlugins: true);
        var protection = DesktopSessionRetention.CreateProtection();
        await using var borrowed = await fixture.CreateHostAsync(protection, original.PluginRuntime);
        Assert.IsTrue(borrowed.PluginRuntime.IsStarted);
        Assert.IsFalse(DesktopSessionRetention.CompleteExternalRegistration(borrowed, protection));
        Assert.IsTrue(protection.Publish("external-work", [], true));
        Assert.IsTrue(protection.Publish("desktop-open-drafts", [], true));
        Assert.IsNull(protection.TryReserveRetirement("old-session"), "Publishing external work cannot remove the mismatched-plugin participant.");
    }

    [TestMethod]
    public async Task UnstartedBorrowedPluginRuntimeCannotQualifyAnOtherwiseReadyHost()
    {
        using var fixture = new Fixture();
        var protection = DesktopSessionRetention.CreateProtection();
        await using var plugins = new PluginRuntimeManager();
        await using var host = await fixture.CreateHostAsync(protection, plugins);
        Assert.IsTrue(protection.Publish("external-work", [], true));
        Assert.IsTrue(protection.Publish("desktop-open-drafts", [], true));
        Assert.IsNull(protection.TryReserveRetirement("old-session"));
    }

    [TestMethod]
    public async Task ShutdownJoinsStartupReadinessWithoutStartingDestructiveWork()
    {
        using var fixture = new Fixture();
        var protection = DesktopSessionRetention.CreateProtection();
        await using var host = await fixture.CreateHostAsync(protection);
        var retention = new DesktopSessionRetention(host.Commands, host.RuntimeService,
            new CodeAltaConfigStore(host.CatalogOptions), new NoCandidatesArchive(), "epoch", protection);
        await retention.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("canceled", retention.Service.Status.Reason);
        Assert.AreEqual(0, retention.Service.Status.Examined);
        await retention.DisposeAsync();
    }

    [TestMethod]
    public async Task InitialApplicationDocumentCanRestoreButReloadCannotRequalify()
    {
        using var fixture = new Fixture();
        var protection = DesktopSessionRetention.CreateProtection();
        await using var host = await fixture.CreateHostAsync(protection);
        await using var retention = new DesktopSessionRetention(host, "epoch");
        Assert.IsTrue(protection.Publish("external-work", [], true));
        retention.BeforeMainFrameNavigation(DesktopApplication.ApplicationDocument);
        using var lifetime = new CancellationTokenSource();
        await using var watch = retention.ProtectionRpc.WatchLifetime(new("epoch", "page"), "document", lifetime.Token).GetAsyncEnumerator();
        Assert.IsTrue(await watch.MoveNextAsync());
        Assert.IsTrue(watch.Current.Accepted);
        Assert.IsTrue((await retention.ProtectionRpc.PublishForDocumentAsync(new("epoch", "page", 1, ["open"], true), "document", default)).Accepted);
        using (var candidate = protection.TryReserveRetirement("unopened")) Assert.IsNotNull(candidate);
        retention.BeforeMainFrameNavigation(DesktopApplication.ApplicationDocument);
        Assert.IsNull(protection.TryReserveRetirement("unopened"));
        await using var replacement = retention.ProtectionRpc.WatchLifetime(new("epoch", "replacement"), "replacement-document", lifetime.Token).GetAsyncEnumerator();
        Assert.IsTrue(await replacement.MoveNextAsync());
        Assert.IsTrue(replacement.Current.Accepted);
        await retention.ProtectionRpc.PublishForDocumentAsync(new("epoch", "replacement", 1, [], true), "replacement-document", default);
        Assert.IsNull(protection.TryReserveRetirement("unopened"), "A replacement document cannot clear the unknown latch.");
        Assert.IsNull(protection.TryReserveRetirement("open"));
    }

    [TestMethod]
    public async Task UnrestoredWindowTimesOutOnceWithoutEnablingDeletion()
    {
        using var fixture = new Fixture();
        var protection = DesktopSessionRetention.CreateProtection();
        await using var host = await fixture.CreateHostAsync(protection);
        await using var retention = new DesktopSessionRetention(host.Commands, host.RuntimeService,
            new CodeAltaConfigStore(host.CatalogOptions), new NoCandidatesArchive(), "epoch", protection,
            TimeSpan.FromMilliseconds(20));
        var completed = new TaskCompletionSource<SessionRetentionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = retention.Service.Subscribe(status =>
        {
            if (status.State == "skipped") completed.TrySetResult(status);
        });
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, result.Examined);
        Assert.IsNull(protection.TryReserveRetirement("unprotected"));
        Assert.IsTrue(protection.Publish("external-work", [], true));
        Assert.IsTrue(protection.Publish("desktop-open-drafts", [], true));
        Assert.AreSame(result, retention.Service.Status, "Late readiness must not start a second pass.");
    }

    [TestMethod]
    public async Task MissingArchiveStillRunsThePassAfterRestoration()
    {
        using var fixture = new Fixture();
        var protection = DesktopSessionRetention.CreateProtection();
        await using var host = await fixture.CreateHostAsync(protection);
        await using var retention = new DesktopSessionRetention(host.Commands, host.RuntimeService,
            new CodeAltaConfigStore(host.CatalogOptions), null, "epoch", host.RetentionProtection);
        var completed = new TaskCompletionSource<SessionRetentionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = retention.Service.Subscribe(status =>
        {
            if (status.State is "completed" or "skipped" or "failed" or "partial") completed.TrySetResult(status);
        });
        // Saved Statistics are best effort: without them, expired sessions are still deleted.
        Assert.IsTrue(protection.Publish("external-work", [], true));
        Assert.IsTrue(protection.Publish("desktop-open-drafts", [], true));
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("completed", result.State, result.Reason);
    }

    private sealed class NoCandidatesArchive : ISessionStatisticsRetirementArchive
    {
        public ValueTask<SessionStatisticsRetirementResult> PrepareAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("An empty synthetic host has no retirement candidate.");
        public ValueTask<bool> ValidateAsync(SessionStatisticsRetirementReceipt receipt, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("No receipt was issued.");
        public ValueTask<bool> ReconcileDeletedAsync(SessionStatisticsRetirementReceipt receipt, bool artifactsRemaining, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("No deletion was authorized.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAltaRetentionStartup-" + Guid.NewGuid().ToString("N"));
        internal Task<CodeAltaHost> CreateHostAsync(SessionRetentionProtection protection, PluginRuntimeManager? plugins = null,
            bool startPlugins = false)
        {
            string DirectoryAt(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
            var project = DirectoryAt("project");
            var global = DirectoryAt("global");
            File.WriteAllText(Path.Combine(global, "config.toml"), "# Synthetic isolated profile\n");
            return CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global,
                StateRoot = DirectoryAt("state"),
                CurrentProjectPath = project,
                DiscoveryScope = new SessionDiscoveryScope(DirectoryAt("home"), project),
                BuiltInSkillRoot = DirectoryAt("builtins"),
                StartPlugins = startPlugins,
                PluginBuiltIns = [],
                PrestartedPluginRuntime = plugins,
                OwnsLogging = false,
                RetentionProtection = protection,
            });
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
