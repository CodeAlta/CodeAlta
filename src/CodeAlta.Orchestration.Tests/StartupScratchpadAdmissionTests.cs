using CodeAlta.Catalog;
using CodeAlta.Catalog.Storage;
using CodeAlta.Hosting;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class StartupScratchpadAdmissionTests
{
    [TestMethod]
    public async Task HostConstructionAndAllItsExecutionServicesWaitForCleanupTermination()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows deletion qualification.");
        using var fixture = new Fixture();
        using var ownership = CodeAltaSingleInstanceGuard.Acquire(fixture.Profile.LockFilePath);
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, ownership);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var observer = cleanup.Subscribe(snapshot =>
        {
            if (snapshot.State == ScratchpadCleanupState.Running)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        var configuredProviders = false;
        var creation = CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = fixture.Profile.GlobalRoot,
            StateRoot = fixture.Profile.StateRoot,
            CurrentProjectPath = fixture.Project,
            DiscoveryScope = new SessionDiscoveryScope(fixture.Home, fixture.Project),
            BuiltInSkillRoot = fixture.Builtins,
            StartPlugins = false,
            OwnsLogging = false,
            StartupScratchpadCleanup = cleanup,
            ConfigureModelProviders = _ => configuredProviders = true,
        }, cancellation.Token);
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            Assert.IsFalse(creation.IsCompleted, "A canceled admission waiter must still join filesystem work.");
            Assert.IsFalse(configuredProviders, "Not even provider construction may run before the barrier.");
            Assert.AreEqual(ScratchpadCleanupState.Running, cleanup.Snapshot.State);
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await creation);
        Assert.IsFalse(configuredProviders);
        Assert.AreEqual(ScratchpadCleanupState.Canceled, cleanup.Snapshot.State);
    }

    [TestMethod]
    public async Task HostRetainsCompletedSnapshotAndDefaultPreferenceIsOn()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows deletion qualification.");
        using var fixture = new Fixture();
        using var ownership = CodeAltaSingleInstanceGuard.Acquire(fixture.Profile.LockFilePath);
        var scratchpad = Path.Combine(fixture.Profile.StateRoot, "scratchpad");
        Directory.CreateDirectory(scratchpad);
        File.WriteAllText(Path.Combine(scratchpad, "old.md"), "remove");
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, ownership);
        await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = fixture.Profile.GlobalRoot,
            StateRoot = fixture.Profile.StateRoot,
            CurrentProjectPath = fixture.Project,
            DiscoveryScope = new SessionDiscoveryScope(fixture.Home, fixture.Project),
            BuiltInSkillRoot = fixture.Builtins,
            StartPlugins = false,
            OwnsLogging = false,
            StartupScratchpadCleanup = cleanup,
        });
        Assert.AreSame(cleanup, host.StartupScratchpadCleanup);
        Assert.IsNotNull(host.StartupScratchpadCleanup);
        Assert.AreEqual(ScratchpadCleanupState.Completed, host.StartupScratchpadCleanup.Snapshot.State);
        Assert.AreEqual(1L, host.StartupScratchpadCleanup.Snapshot.RemovedFiles);
        Assert.IsNotNull(host.AgentHub);
        Assert.IsNotNull(host.Commands);
    }

    [TestMethod]
    [DataRow("# User formatting and comments stay byte-identical.\r\n", true, null)]
    [DataRow("[storage]\nclear_scratchpad_on_startup = false\n", false, "disabled")]
    [DataRow("[storage]\nclear_scratchpad_on_startup = 'invalid'\n", false, "invalid-configuration")]
    [DataRow("[storage.session_retention]\namount = -1\n", false, "invalid-configuration")]
    [DataRow("[storage.session_retention]\nunit = 'fortnights'\n", false, "invalid-configuration")]
    [DataRow("[storage\n", false, "invalid-configuration")]
    public async Task SharedHostResolvesStoragePolicyAndNeverRewritesDeveloperConfiguration(string content, bool removed, string? reason)
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows deletion qualification.");
        using var fixture = new Fixture();
        using var ownership = CodeAltaSingleInstanceGuard.Acquire(fixture.Profile.LockFilePath);
        var configPath = Path.Combine(fixture.Profile.GlobalRoot, "config.toml");
        var config = System.Text.Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(configPath, config);
        var scratchpad = Directory.CreateDirectory(Path.Combine(fixture.Profile.StateRoot, "scratchpad")).FullName;
        File.WriteAllText(Path.Combine(scratchpad, "note.md"), "scratchpad");
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, ownership);
        await using (var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = fixture.Profile.GlobalRoot,
            StateRoot = fixture.Profile.StateRoot,
            CurrentProjectPath = fixture.Project,
            DiscoveryScope = new SessionDiscoveryScope(fixture.Home, fixture.Project),
            BuiltInSkillRoot = fixture.Builtins,
            StartPlugins = false,
            OwnsLogging = false,
            StartupScratchpadCleanup = cleanup,
        }))
        {
            Assert.AreEqual(removed ? ScratchpadCleanupState.Completed : ScratchpadCleanupState.Skipped, cleanup.Snapshot.State);
            Assert.AreEqual(removed, !File.Exists(Path.Combine(scratchpad, "note.md")));
            if (reason is not null) CollectionAssert.Contains(cleanup.Snapshot.Reasons.ToArray(), reason);
            CollectionAssert.AreEqual(config, File.ReadAllBytes(configPath));
            Assert.IsNotNull(host.AgentHub);
        }
        CollectionAssert.AreEqual(config, File.ReadAllBytes(configPath));
    }

    [TestMethod]
    public async Task UnexpectedCleanupFailureStillAdmitsHostAndPreservesSharedDeveloperConfiguration()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows deletion qualification.");
        using var fixture = new Fixture();
        using var ownership = CodeAltaSingleInstanceGuard.Acquire(fixture.Profile.LockFilePath);
        var configPath = Path.Combine(fixture.Profile.GlobalRoot, "config.toml");
        var config = System.Text.Encoding.UTF8.GetBytes("# Shared user configuration must not be rewritten by developer startup.\r\n");
        File.WriteAllBytes(configPath, config);
        var scratchpad = Directory.CreateDirectory(Path.Combine(fixture.Profile.StateRoot, "scratchpad")).FullName;
        File.WriteAllText(Path.Combine(scratchpad, "keep.md"), "keep");
        var faultInjected = false;
        var cleanup = new ScratchpadStartupCleanup(fixture.Profile, ownership)
        {
            BeforeEntryDelete = _ => { faultInjected = true; throw new InvalidOperationException("Injected cleanup failure"); },
        };
        await using (var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
        {
            GlobalRoot = fixture.Profile.GlobalRoot,
            StateRoot = fixture.Profile.StateRoot,
            CurrentProjectPath = fixture.Project,
            DiscoveryScope = new SessionDiscoveryScope(fixture.Home, fixture.Project),
            BuiltInSkillRoot = fixture.Builtins,
            StartPlugins = false,
            OwnsLogging = false,
            StartupScratchpadCleanup = cleanup,
        }))
        {
            Assert.IsTrue(faultInjected);
            Assert.AreEqual(ScratchpadCleanupState.Skipped, cleanup.Snapshot.State);
            CollectionAssert.Contains(cleanup.Snapshot.Reasons.ToArray(), "unexpected-cleanup-failure");
            Assert.AreSame(cleanup, host.StartupScratchpadCleanup);
            Assert.IsNotNull(host.AgentHub);
            Assert.IsNotNull(host.RuntimeService);
            Assert.IsNotNull(host.Commands);
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(scratchpad, "keep.md")));
            CollectionAssert.AreEqual(config, File.ReadAllBytes(configPath));
        }
        CollectionAssert.AreEqual(config, File.ReadAllBytes(configPath));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void SharedDesktopAndTuiPromptProviderUsesInstanceStateButSharedResources(bool developer, bool coordinator)
    {
        using var fixture = new Fixture();
        var profile = CodeAltaInstanceProfile.Create(fixture.Profile.GlobalRoot, developer);
        var promptDirectory = Path.Combine(profile.GlobalRoot, "prompts", "agents");
        Directory.CreateDirectory(promptDirectory);
        File.WriteAllText(Path.Combine(promptDirectory, "scratchpad-test.prompt.md"), """
            ---
            name: scratchpad-test
            description: fixture
            system: default
            ---
            Shared resource marker.
            """);
        var provider = new AgentInstructionTemplateProvider(null,
            new CatalogOptions { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot },
            null, null, new SessionDiscoveryScope(fixture.Home, fixture.Project));
        var session = new SessionViewDescriptor
        {
            SessionId = "resumed-old-developer-session",
            ProviderId = "openai", ProviderKey = "openai", WorkingDirectory = fixture.Project,
        };
        // A resumed session keeps its identity/hash; no transcript or historical scratchpad is moved.
        var instructions = coordinator
            ? provider.BuildCoordinatorInstructions(session, null, selectedPromptName: "scratchpad-test")
            : provider.BuildGeneralInstructions(session, null, selectedPromptName: "scratchpad-test");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(session.SessionId)), 0, 3);
        StringAssert.Contains(instructions.SessionInstructions!, Path.Combine(profile.StateRoot, "scratchpad", $"<yyyy-mm-dd>-<short-name>-{hash}.md"));
        StringAssert.Contains(instructions.DeveloperInstructions!, "Shared resource marker.");
        Assert.IsFalse(Directory.Exists(Path.Combine(profile.StateRoot, "scratchpad")), "Prompt composition must not migrate or create scratchpads.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-startup-tests-" + Guid.NewGuid().ToString("N"));
        internal Fixture()
        {
            Home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
            Project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
            Builtins = Directory.CreateDirectory(Path.Combine(_root, "builtin-skills")).FullName;
            Profile = CodeAltaInstanceProfile.Create(Path.Combine(Home, ".alta"), true);
        }
        internal string Home { get; }
        internal string Project { get; }
        internal string Builtins { get; }
        internal CodeAltaInstanceProfile Profile { get; }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
