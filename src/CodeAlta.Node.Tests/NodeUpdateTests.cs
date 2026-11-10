using System.IO.Compression;
using System.Text;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Update;

namespace CodeAlta.Node.Tests;

[TestClass]
public sealed class NodeUpdateTests
{
    private static readonly string Runtime = UpdateProduct.CurrentRuntime()!;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Update_StagesTheVersionOfAFolderFeed_AndTheNextRunHandsOverToIt()
    {
        using var root = new NodeTestRoot();
        var feed = Feed(root, "99.0.0", "99.0.1-beta.1");
        var store = NodeUpdatePaths.Store(root.Profile());
        // The version starts, confirms that it works, and ends with 7.
        var runner = new RecordingRunner(exitCode: 7, onStart: () => store.Confirm(UpdateVersion.Parse("99.0.0")));
        var app = Application(root, out var output, out var error, runner);

        var check = await app.RunAsync(["update", "--check", "--source", feed, "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, check, error.ToString());
        var latest = UpdateVersion.Parse(NodeApplication.Version).IsPrerelease ? "99.0.1-beta.1" : "99.0.0";
        StringAssert.Contains(output.ToString(), $"An update to {latest} is available", "a stable build follows releases; a prerelease follows prereleases");
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Profile().StateRoot, "fleet", "versions", latest)), "--check changes nothing");

        output.GetStringBuilder().Clear();
        Assert.AreEqual(NodeExitCodes.Success, await app.RunAsync(["update", "--check", "--prerelease", "--source", feed, "--data-root", root.DataRoot]));
        StringAssert.Contains(output.ToString(), "An update to 99.0.1-beta.1 is available", "--prerelease opts a stable build into prereleases too");
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Profile().StateRoot, "fleet", "versions", "99.0.1-beta.1")), "--check changes nothing");

        var code = await app.RunAsync(["update", "--version", "99.0.0", "--source", feed, "--data-root", root.DataRoot]);
        Assert.AreEqual(NodeExitCodes.Success, code, error.ToString());
        StringAssert.Contains(output.ToString(), "hash checked by the local folder");
        StringAssert.Contains(output.ToString(), "No node runs on this profile: the next altanode run (or its service) starts 99.0.0 on trial.");
        Assert.AreEqual(UpdatePhases.Requested, store.Read().Pending?.Phase);

        // run takes the switch that waited, and hands over to the version: its exit code is the one of run.
        var exit = await app.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0"]);
        Assert.AreEqual(7, exit, error.ToString());
        var started = runner.Started.Single();
        StringAssert.StartsWith(started.Command.FileName, store.VersionPath(UpdateVersion.Parse("99.0.0")));
        CollectionAssert.AreEqual(new[] { "run", "--data-root", root.DataRoot, "--port", "0" }, started.Arguments.ToArray());
        Assert.AreEqual(NodeApplication.Version, started.Environment[UpdateLauncher.InstalledVersionVariable]);
        Assert.AreEqual("99.0.0", store.Read().Current);
        Assert.AreEqual(UpdateOutcomes.Confirmed, store.Read().LastOutcome?.Outcome);

        output.GetStringBuilder().Clear();
        Assert.AreEqual(NodeExitCodes.Failure, await app.RunAsync(["status", "--data-root", root.DataRoot]));
        StringAssert.Contains(output.ToString(), "Version: 99.0.0 (selected by altanode update, installed " + NodeApplication.Version);
        StringAssert.Contains(output.ToString(), "Last update: 99.0.0, confirmed");
    }

    [TestMethod]
    public async Task Update_RefusesAFeedTheOwnerDidNotAllow_AndAHashThatDoesNotMatch()
    {
        using var root = new NodeTestRoot();
        var feed = Feed(root, "99.0.0");
        Directory.CreateDirectory(Path.Combine(root.Profile().StateRoot, "fleet"));
        var app = Application(root, out _, out var error, new RecordingRunner(0));

        Assert.AreEqual(NodeExitCodes.Failure, await app.RunAsync(["update", "--source", feed, "--sha512", Convert.ToBase64String(new byte[64]), "--data-root", root.DataRoot]));
        StringAssert.Contains(error.ToString(), "does not match the expected hash");
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Profile().StateRoot, "fleet", "versions", "99.0.0")));

        File.WriteAllText(NodeUpdatePaths.PolicyPath(root.Profile()), """{ "allowedSources": [ "https://mirror.example/v3/index.json" ] }""");
        Assert.AreEqual(NodeExitCodes.Failure, await app.RunAsync(["update", "--source", feed, "--data-root", root.DataRoot]));
        StringAssert.Contains(error.ToString(), "is not allowed on this machine");
    }

    [TestMethod]
    [DataRow("update", "now")]
    [DataRow("update", "--check", "--now")]
    public async Task Update_InvalidCommandLines_ExitWithTwo(params string[] args)
    {
        using var root = new NodeTestRoot();
        var app = Application(root, out _, out _, new RecordingRunner(0));
        Assert.AreEqual(NodeExitCodes.InvalidUsage, await app.RunAsync([.. args, "--data-root", root.DataRoot]));
    }

    [TestMethod]
    public async Task Run_DoesNotHandOverWhileAnotherInstanceHoldsTheProfile()
    {
        using var root = new NodeTestRoot();
        var store = NodeUpdatePaths.Store(root.Profile());
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        store.Select(target);
        await using var running = await NodeHost.StartAsync(root.Options(), TestContext.CancellationToken);
        var runner = new RecordingRunner(exitCode: NodeExitCodes.ProfileInUse);
        var app = Application(root, out _, out var error, runner);

        // A second run by mistake is refused as before; it never starts the version on trial, which would fail and be rolled back.
        Assert.AreEqual(NodeExitCodes.ProfileInUse, await app.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0"]));
        Assert.AreEqual(0, runner.Started.Count, error.ToString());
        Assert.AreEqual("99.0.0", store.Read().Trial?.Version);
        Assert.AreEqual(0, store.Read().Bad.Count);
    }

    [TestMethod]
    public async Task AVersionRefusedBecauseTheProfileIsHeldIsNotRolledBack()
    {
        using var root = new NodeTestRoot();
        var store = NodeUpdatePaths.Store(root.Profile());
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        store.Select(target);
        // The version on trial is refused by the lock of the profile (CodeAlta Desktop or a node started by hand meanwhile).
        var runner = new RecordingRunner(exitCode: NodeExitCodes.ProfileInUse);
        var app = Application(root, out _, out var error, runner);

        Assert.AreEqual(NodeExitCodes.ProfileInUse, await app.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0"]));

        Assert.AreEqual(1, runner.Started.Count, error.ToString());
        var state = store.Read();
        Assert.AreEqual(0, state.Bad.Count, "the version on trial is not blamed");
        Assert.AreEqual("99.0.0", state.Trial?.Version);
        Assert.AreEqual(0, state.Trial?.Starts, "a start refused for the profile does not count");
    }

    [TestMethod]
    public async Task ASecondRunWhileALauncherRunsStartsNoOtherVersion()
    {
        using var root = new NodeTestRoot();
        var store = NodeUpdatePaths.Store(root.Profile());
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        store.Select(target);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingRunner(exitCode: 0, gate: release.Task);
        var first = Application(root, out _, out var firstError, runner).RunAsync(["run", "--data-root", root.DataRoot, "--port", "0"]);
        var deadline = DateTime.UtcNow + TestTime.Seconds(10);
        while (runner.Started.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20, TestContext.CancellationToken);
        Assert.AreEqual(1, runner.Started.Count, firstError.ToString());

        // The version on trial has not taken the profile yet: only the launcher's lease tells that one runs.
        var second = Application(root, out _, out var secondError, runner).RunAsync(["run", "--data-root", root.DataRoot, "--port", "0"]);
        var ended = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10), TestContext.CancellationToken)) == second;
        var startedBySecond = runner.Started.Count - 1;
        store.Confirm(target);
        release.TrySetResult();
        await Task.WhenAll(first, second).WithinAsync(TestTime.Seconds(30), TestContext.CancellationToken);

        Assert.IsTrue(ended, "the second run is refused at once");
        Assert.AreEqual(0, startedBySecond, "the second run starts no other version");
        Assert.AreEqual(NodeExitCodes.ProfileInUse, await second, secondError.ToString());
        Assert.AreEqual(0, store.Read().Bad.Count);
    }

    [TestMethod]
    public async Task Run_SwitchesWhenIdle_ThenBecomesTheLauncherOfTheNewVersion()
    {
        using var root = new NodeTestRoot();
        var store = NodeUpdatePaths.Store(root.Profile());
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        var runner = new RecordingRunner(exitCode: 0, onStart: () => store.Confirm(target));
        NodeHost? node = null;
        var app = Application(root, out var output, out var error, runner, started: (host, _) =>
        {
            node = host;
            store.RequestSwitch(target, now: false, "local");
        });

        var exit = await app.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0"]);

        Assert.AreEqual(0, exit, error.ToString());
        StringAssert.Contains(output.ToString(), "Stopping CodeAlta Node to start its new version");
        Assert.IsNotNull(node);
        Assert.IsFalse(node.AcceptsWork);
        var state = store.Read();
        Assert.AreEqual("99.0.0", state.Current);
        Assert.IsNull(state.Previous, "a rollback goes back to the installed entry point");
        StringAssert.StartsWith(runner.Started.Single().Command.FileName, store.VersionPath(target), "this process started the new version");
    }

    [TestMethod]
    public async Task Run_ConfirmsAVersionOnTrialOnceItListens()
    {
        using var root = new NodeTestRoot();
        var store = NodeUpdatePaths.Store(root.Profile());
        var running = UpdateVersion.Parse(NodeApplication.Version);
        Stage(store, running);
        store.Select(running);
        var confirmed = false;
        var app = Application(root, out _, out var error, new RecordingRunner(0), directory: store.VersionPath(running), started: (_, stop) =>
        {
            var deadline = DateTime.UtcNow + TestTime.Seconds(30);
            while (store.Read().Trial is not null && DateTime.UtcNow < deadline) Thread.Sleep(20);
            confirmed = store.Read().Trial is null;
            stop();
        });

        Assert.AreEqual(NodeExitCodes.Success, await app.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0"]), error.ToString());
        Assert.IsTrue(confirmed, "the version on trial confirmed itself once it listened");
        Assert.AreEqual(UpdateOutcomes.Confirmed, store.Read().LastOutcome?.Outcome);
    }

    [TestMethod]
    public void TheDrainPausesAdmissionWhileSessionsWork_ThenSwitches()
    {
        using var root = new NodeTestRoot();
        var store = NodeUpdatePaths.Store(root.Profile());
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        var atWork = 2;
        var paused = new List<bool>();
        var restarts = 0;
        var updates = new NodeUpdates(new NodeUpdatesOptions
        {
            Store = store, Running = UpdateVersion.Parse("98.0.0"), SessionsAtWork = () => atWork, PauseAdmission = paused.Add, Healthy = static () => true,
            RequestRestart = () => restarts++, DrainBound = TimeSpan.FromMinutes(10),
        });
        var now = DateTimeOffset.UtcNow;
        updates.Tick(now);
        Assert.AreEqual(0, paused.Count, "nothing to do without a switch");

        store.RequestSwitch(target, now: false, "local");
        updates.Tick(now);
        updates.Tick(now.AddMinutes(1));
        Assert.IsTrue(paused[^1], "no new work while it drains");
        Assert.AreEqual(UpdatePhases.Draining, store.Read().Pending?.Phase);
        Assert.AreEqual(2, store.Read().Pending?.SessionsAtWork);
        Assert.AreEqual(UpdatePhases.Draining, updates.Status().Pending?.Phase);

        updates.Tick(now.AddMinutes(11));
        Assert.IsFalse(paused[^1], "past the bound, work is taken again");
        Assert.AreEqual(UpdatePhases.WaitingForIdle, store.Read().Pending?.Phase);

        atWork = 0;
        updates.Tick(now.AddMinutes(12));
        Assert.IsTrue(paused[^1]);
        Assert.AreEqual(0, restarts);
        updates.Tick(now.AddMinutes(12).AddSeconds(1));
        Assert.AreEqual(1, restarts);
        Assert.IsTrue(updates.RestartRequested);
        Assert.AreEqual("99.0.0", store.Read().Current);
        Assert.IsNull(store.Read().Pending);
    }

    [TestMethod]
    public void AWithdrawnSwitchResumesAdmission_AndOneNotStagedIsDropped()
    {
        using var root = new NodeTestRoot();
        var store = NodeUpdatePaths.Store(root.Profile());
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        var paused = new List<bool>();
        var updates = new NodeUpdates(new NodeUpdatesOptions
        {
            Store = store, Running = UpdateVersion.Parse("98.0.0"), SessionsAtWork = static () => 1, PauseAdmission = paused.Add, Healthy = static () => true,
            RequestRestart = static () => Assert.Fail("no restart"),
        });
        store.RequestSwitch(target, now: false, "local");
        updates.Tick(DateTimeOffset.UtcNow);
        Assert.IsTrue(paused[^1]);
        store.CancelPending(null);
        updates.Tick(DateTimeOffset.UtcNow);
        Assert.IsFalse(paused[^1]);

        store.RequestSwitch(UpdateVersion.Parse("99.0.5"), now: true, "local");
        updates.Tick(DateTimeOffset.UtcNow);
        Assert.IsNull(store.Read().Pending);
        Assert.AreEqual(UpdateOutcomes.Failed, store.Read().LastOutcome?.Outcome);
    }

    [TestMethod]
    public void ASwitchCancelledOrReplacedWhileTheNodeDecidesIsNotSelected()
    {
        using var root = new NodeTestRoot();
        var store = NodeUpdatePaths.Store(root.Profile());
        var target = UpdateVersion.Parse("99.0.0");
        var newer = UpdateVersion.Parse("99.0.5");
        Stage(store, target);
        Stage(store, newer);
        const string campaign = "fleet campaign c1";
        Action? meanwhile = null;
        var restarts = 0;
        // What happens between the read of the state and the selection: the node counts its sessions there.
        var updates = new NodeUpdates(new NodeUpdatesOptions
        {
            Store = store, Running = UpdateVersion.Parse("98.0.0"), PauseAdmission = static _ => { }, Healthy = static () => true, RequestRestart = () => restarts++,
            SessionsAtWork = () =>
            {
                meanwhile?.Invoke();
                meanwhile = null;
                return 0;
            },
        });

        // The campaign is cancelled: its switch is withdrawn just before the node takes it.
        store.RequestSwitch(target, now: true, campaign);
        meanwhile = () => store.CancelPending("Campaign c1 was cancelled.", campaign);
        updates.Tick(DateTimeOffset.UtcNow);
        Assert.AreEqual((null, null, 0), (store.Read().Current, store.Read().Pending, restarts), "the cancelled version is not selected");

        // The owner asks for a newer version just before the node takes the campaign's: the newer request stays.
        store.RequestSwitch(target, now: true, campaign);
        meanwhile = () => store.RequestSwitch(newer, now: true, "local");
        updates.Tick(DateTimeOffset.UtcNow);
        Assert.AreEqual((null, "99.0.5", 0), (store.Read().Current, store.Read().Pending?.Version, restarts), "the newer request is not cleared");

        updates.Tick(DateTimeOffset.UtcNow);
        Assert.AreEqual(("99.0.5", 1), (store.Read().Current, restarts), "the next step takes what waits");
    }

    [TestMethod]
    public async Task TheFleetsSwitchNeverReplacesNorWithdrawsTheOwnersSwitch()
    {
        using var root = new NodeTestRoot();
        var feed = Feed(root, "99.0.0");
        var store = NodeUpdatePaths.Store(root.Profile());
        var updates = new NodeUpdates(new NodeUpdatesOptions
        {
            Store = store, Running = UpdateVersion.Parse("98.0.0"), SessionsAtWork = static () => 0, PauseAdmission = static _ => { }, Healthy = static () => true,
            RequestRestart = static () => Assert.Fail("no restart"),
        });
        var request = new UpdateRequest { Version = "99.0.0", Source = feed, Origin = UpdateOrigin.Local };
        const string campaign = "fleet campaign c1";

        // The owner asked for a version while the campaign downloaded.
        store.RequestSwitch(UpdateVersion.Parse("99.0.5"), now: false, "local");
        var refused = await Assert.ThrowsExactlyAsync<UpdateException>(() => updates.ApplyAsync(request, campaign, TestContext.CancellationToken));
        StringAssert.Contains(refused.Message, "altanode update");
        Assert.IsFalse(updates.CancelPending("Campaign c1 was cancelled.", campaign));
        Assert.AreEqual(("99.0.5", "local"), (store.Read().Pending?.Version, store.Read().Pending?.RequestedBy));

        store.CancelPending(null);
        Assert.IsNotNull(await updates.ApplyAsync(request, campaign, TestContext.CancellationToken));
        Assert.AreEqual(campaign, store.Read().Pending?.RequestedBy);
        Assert.IsTrue(updates.CancelPending("Campaign c1 was cancelled.", campaign));
        Assert.IsNull(store.Read().Pending);
    }

    [TestMethod]
    public async Task ADrainingNodeRefusesNewWorkAsUpdating_AndReportsItsVersions()
    {
        using var root = new NodeTestRoot();
        var provider = new ScriptedNodeProvider();
        await using var node = await NodeHost.StartAsync(root.Options() with { ConfigureModelProviders = provider.Register }, TestContext.CancellationToken);
        var status = new FleetUpdateStatus("99.0.0", "98.0.0", "99.0.0", null, null, null, [], null);
        await using var fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "test-node", "99.0.0", null, () => status, null);
        using var nodeIdentity = Fleet.Identity.DeviceIdentity.LoadOrCreate(node.Host.CatalogOptions);
        var viewerFleet = Path.Combine(root.Root, "viewer", "fleet");
        using var viewer = Fleet.Identity.DeviceIdentity.LoadOrCreate(viewerFleet);
        var (offer, _) = fleet.Service.Pairing.CreateOffer("test-node", fleet.Addresses, TimeSpan.FromMinutes(1));
        var machine = await FleetNodeClient.PairAsync(offer, viewer, "viewer", new PairedMachineStore(viewerFleet), TestContext.CancellationToken);
        await using var client = await FleetNodeClient.ConnectAsync(machine, viewer, TestContext.CancellationToken);

        Assert.AreEqual("98.0.0", (await client.GetInfoAsync(TestContext.CancellationToken)).Update?.Installed);
        node.PauseAdmission(true);
        Assert.IsFalse(node.AcceptsWork);
        Assert.IsTrue(node.AdmissionPaused);
        var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => client.CreateSessionAsync(new FleetCreateSessionParams(Title: "x"), "create-1", TestContext.CancellationToken));
        Assert.AreEqual(FleetNodeErrorCodes.NodeUpdating, refused.Error.Code);
        Assert.AreEqual(FleetErrorHints.Retry, refused.Error.Hint);

        await using (var owner = await FleetNodeClient.ConnectAsOwnerAsync(fleet.Addresses, nodeIdentity, TestContext.CancellationToken))
        {
            var ownStatus = await owner.GetStatusAsync(TestContext.CancellationToken);
            Assert.IsTrue(ownStatus.Updating);
            Assert.AreEqual("99.0.0", ownStatus.Update?.Running);
            // The node says whether a service runs it, for the page of the machine: none runs the node of a test.
            Assert.IsNotNull(ownStatus.Service);
            Assert.IsFalse(ownStatus.Service.Installed);
            Assert.IsFalse(ownStatus.Service.StartsAtBoot);
            StringAssert.Contains(ownStatus.Service.Lines[0], "not installed");
        }

        node.PauseAdmission(false);
        Assert.IsTrue(node.AcceptsWork);
        Assert.IsNotNull(await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "x"), "create-2", TestContext.CancellationToken));
    }

    // A folder feed with the tool package and the runtime package of this machine of each version, as dotnet pack makes them.
    private static string Feed(NodeTestRoot root, params string[] versions)
    {
        var feed = Directory.CreateDirectory(Path.Combine(root.Root, "feed")).FullName;
        foreach (var version in versions)
        {
            using (var tool = ZipFile.Open(Path.Combine(feed, $"{UpdateProduct.Node.PackageId}.{version}.nupkg"), ZipArchiveMode.Create))
            using (var settings = tool.CreateEntry("tools/net10.0/any/DotnetToolSettings.xml").Open())
                settings.Write(Encoding.UTF8.GetBytes("<DotNetCliTool Version=\"2\" />"));
            var id = UpdateProduct.Node.RuntimePackageId(Runtime);
            using var archive = ZipFile.Open(Path.Combine(feed, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
            foreach (var name in new[] { UpdateProduct.Node.AssemblyFileName, UpdateProduct.Node.AppHostFileName(OperatingSystem.IsWindows()) })
            {
                using var stream = archive.CreateEntry($"tools/net10.0/{Runtime}/{name}").Open();
                stream.Write(Encoding.UTF8.GetBytes(version));
            }
        }

        return feed;
    }

    private static void Stage(UpdateVersionStore store, UpdateVersion version)
    {
        Directory.CreateDirectory(store.VersionPath(version));
        File.WriteAllText(Path.Combine(store.VersionPath(version), UpdateProduct.Node.AssemblyFileName), version.ToString());
        File.WriteAllText(Path.Combine(store.VersionPath(version), UpdateProduct.Node.AppHostFileName(OperatingSystem.IsWindows())), version.ToString());
    }

    private static NodeApplication Application(NodeTestRoot root, out StringWriter output, out StringWriter error, RecordingRunner runner,
        Action<NodeHost, Action>? started = null, string? directory = null)
    {
        output = new StringWriter();
        error = new StringWriter();
        return new NodeApplication(TextWriter.Synchronized(output), TextWriter.Synchronized(error))
        {
            InitializeLogging = false,
            ConfigureHost = root.Isolate,
            Started = started,
            ServiceEnvironment = static () => null,
            ApplicationDirectory = directory,
            ConfigureLauncher = options => options with { Runner = runner, HandleSignals = false, RestartDelay = TimeSpan.Zero, DotnetRoot = null, PollInterval = TimeSpan.FromMilliseconds(20) },
            ConfigureUpdates = static options => options with { Interval = TimeSpan.FromMilliseconds(50) },
        };
    }

    /// <summary>Records the versions a launcher starts; each does what the test says at its start, then exits with the code given.</summary>
    private sealed class RecordingRunner(int exitCode, Action? onStart = null, Task? gate = null) : IUpdateProcessRunner
    {
        private readonly List<(UpdateCommand Command, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?> Environment)> _started = [];

        public List<(UpdateCommand Command, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?> Environment)> Started
        {
            get
            {
                lock (_started) return [.. _started];
            }
        }

        public IUpdateProcess Start(UpdateCommand command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment)
        {
            lock (_started) _started.Add((command, arguments, environment));
            return new Exited(exitCode, onStart, gate);
        }

        private sealed class Exited(int code, Action? onStart, Task? gate) : IUpdateProcess
        {
            public int Id => 1;

            public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
            {
                onStart?.Invoke();
                if (gate is not null) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                // The launcher reads the state between polls: the version exits once it confirmed.
                await Task.Delay(TimeSpan.FromMilliseconds(onStart is null ? 0 : 300), cancellationToken).ConfigureAwait(false);
                return code;
            }

            public bool RequestStop() => true;

            public void Kill()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
