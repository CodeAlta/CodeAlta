using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Node.Tests;

[TestClass]
public sealed class NodeHostTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task StartAndStop_HoldsTheLockWhileItRuns_AndReleasesIt()
    {
        using var root = new NodeTestRoot();
        var profile = root.Profile();
        Assert.IsFalse(NodeProfileStatus.Read(profile).IsHeld);

        var node = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);
        try
        {
            Assert.IsTrue(node.AcceptsWork);
            Assert.AreEqual(profile.LockFilePath, node.LockFilePath);
            var held = NodeProfileStatus.Read(profile);
            Assert.IsTrue(held.IsHeld, "The node holds its profile.");
            Assert.AreEqual(Environment.ProcessId, held.ProcessId);
        }
        finally
        {
            await node.StopAsync(TestContext.CancellationToken);
        }

        Assert.IsFalse(node.AcceptsWork, "A node that stops accepts no more work.");
        Assert.IsFalse(File.Exists(profile.LockFilePath), "The lock is released.");
        Assert.IsFalse(NodeProfileStatus.Read(profile).IsHeld);

        // Nothing leaked: the profile starts again.
        var again = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);
        await again.DisposeAsync();
        Assert.IsFalse(File.Exists(profile.LockFilePath));
    }

    [TestMethod]
    public async Task SecondStart_OnTheSameProfile_IsRefused_AndLeavesTheFirstRunning()
    {
        using var root = new NodeTestRoot();
        await using var first = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);

        var admitted = false;
        await Assert.ThrowsExactlyAsync<CodeAltaAlreadyRunningException>(() => NodeHost.StartAsync(root.Options() with { Admitted = () => admitted = true },
            TestContext.CancellationToken));

        Assert.IsFalse(admitted, "A refused start does not open the log of the profile.");
        Assert.IsTrue(NodeProfileStatus.Read(root.Profile()).IsHeld, "The refused start leaves the lock of the first node.");
        Assert.IsTrue(first.AcceptsWork);
    }

    [TestMethod]
    public async Task DeveloperProfile_TakesItsOwnLock_BesideTheNormalOne()
    {
        using var root = new NodeTestRoot();
        await using var normal = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);
        await using var developer = await NodeHost.StartAsync(root.Options(developer: true), TestContext.CancellationToken);

        Assert.AreEqual(Path.Combine(root.DataRoot, "dev", "alta.lock"), developer.LockFilePath);
        Assert.AreEqual(Path.Combine(root.DataRoot, "dev"), developer.Host.CatalogOptions.StateRoot);
        Assert.IsTrue(NodeProfileStatus.Read(root.Profile(developer: true)).IsHeld);
    }

    [TestMethod]
    public async Task Composition_NeverTrustsFully_AndFollowsThePermissionModeOfEachSession()
    {
        using var root = new NodeTestRoot();
        await using var toolEndpoints = new LoopbackHarnessToolEndpoints(LogManager.GetLogger("CodeAlta.Node.Tests"));
        await using var modelsDev = new ModelsDevCatalogService(new ModelsDevCatalogServiceOptions { CacheFilePath = Path.Combine(root.Root, "models.json") });
        var options = NodeHost.CreateHostOptions(root.Options(), null, modelsDev, new PluginModelProviderTypes(toolEndpoints));

        Assert.IsFalse(options.FullTrust, "A node never uses the full trust of headless runs: the mode of a session decides.");
        Assert.IsTrue(options.SessionPermissionModes, "Each session runs in its permission mode, as in CodeAlta Desktop.");
        Assert.IsNotNull(options.ReviewOwnedPermissionsPolicy);
        Assert.IsFalse(options.ReviewOwnedPermissionsPolicy(), "A machine installed to run agents bypasses permissions until its owner chooses.");
        Assert.IsTrue(options.InheritPermissionModePolicy?.Invoke(), "A session another session creates asks what its creator asks.");
        Assert.IsTrue(options.IsHeadless);
        Assert.IsFalse(options.HasInteractiveUi);
        Assert.IsFalse(options.ReadOnlyGlobalState, "A node owns its profile.");
        Assert.AreEqual(SessionLeaseOwner.NodeKind, options.SessionLeaseOwner);
        Assert.IsTrue(options.FollowExternalSessions);
        Assert.AreEqual(PluginFrontends.Cli, options.PluginFrontend, "Only the plugins made for hosts without a window start.");
        Assert.IsTrue(options.PluginBuiltIns.All(static plugin => plugin.Frontends.HasFlag(PluginFrontends.Cli)));
        Assert.IsNotNull(options.StartupScratchpadCleanup);
    }

    [TestMethod]
    public async Task PermissionRequests_FollowTheModeOfTheMachine_AndWhatAlwaysAsksIsNeverGranted()
    {
        using var root = new NodeTestRoot();
        await using var node = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);
        var permissions = node.Host.RuntimeService.Permissions;
        var provider = new ModelProviderId("scripted");
        var command = new AgentCommandPermissionRequest(provider, "session-1", DateTimeOffset.UtcNow, null, "i-1", null,
            "rm -rf build", root.ProjectRoot, null, null, null, null, null);
        var fileChange = new AgentFileChangePermissionRequest(provider, "session-1", DateTimeOffset.UtcNow, null, "i-2", root.ProjectRoot, null);
        var skill = new AgentSkillPermissionRequest(provider, "session-1", DateTimeOffset.UtcNow, null, "i-3", "server", "skill",
            "skill://server/skill/SKILL.md", 1, 10, Dynamic: false);

        // By default the sessions of a machine bypass permissions: a command and a file change run; a skill of a server always asks.
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await permissions.OwnedDefaultPermissionHandler(command, TestContext.CancellationToken)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await permissions.OwnedDefaultPermissionHandler(fileChange, TestContext.CancellationToken)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await permissions.RequestForSessionAsync("session-1", skill, TestContext.CancellationToken)).Kind);

        // Once the owner makes them ask first, a request nobody can be asked about is refused, never granted.
        FleetSessionDefaults.Write(node.Host.CatalogOptions.FleetRoot, new FleetSessionDefaultsInfo(SessionPermissionModes.Ask));
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await permissions.OwnedDefaultPermissionHandler(command, TestContext.CancellationToken)).Kind);
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await permissions.OwnedDefaultPermissionHandler(fileChange, TestContext.CancellationToken)).Kind);
    }
}
