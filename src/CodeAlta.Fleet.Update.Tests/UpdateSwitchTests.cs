using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Update.Tests;

[TestClass]
public sealed class UpdateSwitchTests
{
    private static readonly UpdateVersion V1 = UpdateVersion.Parse("4.0.1");
    private static readonly UpdateVersion V2 = UpdateVersion.Parse("4.0.2");
    private static readonly UpdateVersion V3 = UpdateVersion.Parse("4.0.3");

    private static UpdateVersionStore StoreWith(TempFolder temp, params UpdateVersion[] staged)
    {
        var store = new UpdateVersionStore(temp.Combine("versions"));
        foreach (var version in staged)
        {
            Directory.CreateDirectory(store.VersionPath(version));
            File.WriteAllText(Path.Combine(store.VersionPath(version), "altanode.dll"), version.ToString());
        }

        return store;
    }

    [TestMethod]
    public void ASelectedVersionIsOnTrialUntilItConfirms()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1, V2);
        Assert.IsNull(store.Read().Current, "nothing selected: the installed entry point runs");

        store.RequestSwitch(V1, now: false, "local");
        Assert.AreEqual(UpdatePhases.Requested, store.Read().Pending?.Phase);
        store.SetPhase(V1, UpdatePhases.Draining, 2);
        Assert.AreEqual(2, store.Read().Pending?.SessionsAtWork);
        store.SetPhase(V2, UpdatePhases.Switching);
        Assert.AreEqual(UpdatePhases.Draining, store.Read().Pending?.Phase, "a phase of another version changes nothing");

        var selected = store.Select(V1);
        Assert.AreEqual("4.0.1", selected.Current);
        Assert.IsNull(selected.Previous, "a rollback goes back to the installed entry point");
        Assert.AreEqual("4.0.1", selected.Trial?.Version);
        Assert.IsNull(selected.Pending, "the switch is done");
        Assert.IsFalse(store.Confirm(V2), "another version cannot confirm the trial");
        Assert.IsTrue(store.Confirm(V1));
        Assert.IsNull(store.Read().Trial);
        Assert.AreEqual(UpdateOutcomes.Confirmed, store.Read().LastOutcome?.Outcome);

        store.Select(V2, backup: "backup.sqlite3");
        Assert.AreEqual("4.0.1", store.Read().Previous);
        store.CountTrialStart();
        store.CountTrialStart();
        Assert.AreEqual(2, store.Read().Trial?.Starts);
    }

    [TestMethod]
    public void ARollbackReturnsToThePreviousVersionAndMarksTheTrialBad()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1, V2);
        store.Select(V1);
        store.Confirm(V1);
        store.Select(V2, backup: "b.sqlite3");
        var trial = store.RollBack("it crashed");
        Assert.AreEqual("4.0.2", trial?.Version);
        Assert.AreEqual("b.sqlite3", trial?.Backup);
        var state = store.Read();
        Assert.AreEqual("4.0.1", state.Current);
        Assert.IsNull(state.Trial);
        Assert.IsTrue(state.IsBad(V2));
        Assert.AreEqual(UpdateOutcomes.RolledBack, state.LastOutcome?.Outcome);
        Assert.IsNull(store.RollBack("again"), "no trial, nothing to roll back");

        var status = UpdateStatusReport.Describe(state, "4.0.1", "4.0.0");
        Assert.AreEqual("4.0.1", status.Selected);
        Assert.AreEqual("4.0.2", status.Bad.Single().Version);
        var lines = UpdateStatusReport.Lines(status, "altanode");
        Assert.IsTrue(lines.Any(static line => line.Contains("4.0.2 was rolled back", StringComparison.Ordinal)), string.Join("\n", lines));
    }

    [TestMethod]
    public void PruningKeepsTheSelectedThePreviousAndThePendingVersions()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1, V2, V3, UpdateVersion.Parse("4.0.0"));
        store.Select(V1);
        store.Confirm(V1);
        store.Select(V2);
        store.Confirm(V2);
        store.RequestSwitch(V3, now: false, "local");
        Directory.CreateDirectory(Path.Combine(store.Root, "not-a-version"));
        CollectionAssert.AreEquivalent(new[] { "4.0.0" }, store.Prune().ToArray());
        Assert.IsTrue(Directory.Exists(store.VersionPath(V1)));
        Assert.IsTrue(Directory.Exists(Path.Combine(store.Root, "not-a-version")), "only folders of versions are pruned");
        store.CancelPending("the backup failed");
        CollectionAssert.AreEquivalent(new[] { "4.0.3" }, store.Prune().ToArray());
        Assert.AreEqual(UpdateOutcomes.Failed, store.Read().LastOutcome?.Outcome);
    }

    [TestMethod]
    public void AStateThatCannotBeReadRunsTheInstalledEntryPoint()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp);
        Directory.CreateDirectory(store.Root);
        File.WriteAllText(Path.Combine(store.Root, "state.json"), "{ broken");
        Assert.IsNull(store.Read().Current);
        Assert.IsFalse(UpdateLauncher.ShouldHandOver(store, AppContext.BaseDirectory));
        Assert.IsTrue(store.Contains(Path.Combine(store.VersionPath(V1), "altanode.dll")));
        Assert.IsFalse(store.Contains(store.Root + "-other"));
    }

    [TestMethod]
    public void TheCommandOfAVersionIsItsAppHostOrDotnetExec()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1);
        Assert.IsNull(UpdateLauncher.CommandFor(store, UpdateProduct.Node, V2, null), "not staged");
        var exec = UpdateLauncher.CommandFor(store, UpdateProduct.Node, V1, null)!;
        Assert.AreEqual("dotnet", exec.FileName);
        CollectionAssert.AreEqual(new[] { "exec", Path.Combine(store.VersionPath(V1), "altanode.dll") }, exec.PrefixArguments.ToArray());
        var host = Path.Combine(store.VersionPath(V1), UpdateProduct.Node.AppHostFileName(OperatingSystem.IsWindows()));
        File.WriteAllText(host, "");
        Assert.AreEqual(host, UpdateLauncher.CommandFor(store, UpdateProduct.Node, V1, null)!.FileName);
        Assert.IsNotNull(UpdateLauncher.FindDotnetRoot(), "the tests run on a shared runtime");
    }

    private static UpdateLauncher Launcher(UpdateVersionStore store, FakeRunner runner, TimeSpan? trialLimit = null, Func<UpdateTrial, CancellationToken, Task>? rolledBack = null,
        List<string>? log = null, CancellationToken stop = default)
        => new(new UpdateLauncherOptions
        {
            Store = store, Product = UpdateProduct.Node, Arguments = ["run", "--port", "0"], Installed = new UpdateCommand("installed-altanode", []),
            InstalledVersion = "4.0.0", Runner = runner, TrialLimit = trialLimit ?? TimeSpan.FromSeconds(30), PollInterval = TimeSpan.FromMilliseconds(10),
            StopGrace = TimeSpan.FromSeconds(5), RestartDelay = TimeSpan.Zero, DotnetRoot = null, HandleSignals = false, RolledBack = rolledBack,
            Log = log is null ? null : message => { lock (log) log.Add(message); }, Stop = stop,
        });

    private static string VersionOf(FakeProcess process) => process.Command.FileName == "installed-altanode" ? "installed" : process.Command.PrefixArguments[^1].Split(Path.DirectorySeparatorChar)[^2];

    [TestMethod]
    public async Task TheLauncherRunsTheSelectedVersionAndPassesItsExitCode()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1);
        store.Select(V1);
        store.Confirm(V1);
        var runner = new FakeRunner((process, _) => { process.Exit(3); return Task.CompletedTask; });
        Assert.AreEqual(3, await Launcher(store, runner).RunAsync());
        Assert.AreEqual("4.0.1", VersionOf(runner.Started.Single()));
        Assert.AreEqual("4.0.0", runner.Environments[0][UpdateLauncher.InstalledVersionVariable]);
        Assert.IsNotNull(runner.Environments[0][UpdateLauncher.SupervisorVariable]);
    }

    [TestMethod]
    public async Task AVersionThatConfirmsStaysAndARestartStartsTheNextOne()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1, V2);
        store.Select(V1);
        var runner = new FakeRunner(async (process, count) =>
        {
            await Task.Delay(30);
            if (count == 1)
            {
                // 4.0.1 confirms, then selects 4.0.2 and asks for a restart, as a node does after its drain.
                Assert.IsTrue(store.Confirm(V1));
                await Task.Delay(30);
                store.Select(V2);
                process.Exit(UpdateLauncher.RestartExitCode);
                return;
            }

            Assert.IsTrue(store.Confirm(V2));
            await Task.Delay(30);
            process.Exit(0);
        });
        Assert.AreEqual(0, await Launcher(store, runner).RunAsync());
        CollectionAssert.AreEqual(new[] { "4.0.1", "4.0.2" }, runner.Started.Select(VersionOf).ToArray());
        var state = store.Read();
        Assert.AreEqual("4.0.2", state.Current);
        Assert.AreEqual("4.0.1", state.Previous);
        Assert.AreEqual(0, state.Bad.Count);
    }

    [TestMethod]
    public async Task AVersionThatCrashesOnStartIsRolledBack()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1, V2);
        store.Select(V1);
        store.Confirm(V1);
        store.Select(V2, backup: "pre-4.0.2.sqlite3");
        UpdateTrial? restored = null;
        var log = new List<string>();
        var runner = new FakeRunner((process, count) =>
        {
            process.Exit(count == 1 ? 134 : 0);
            return Task.CompletedTask;
        });
        var exitCode = await Launcher(store, runner, rolledBack: (trial, _) => { restored = trial; return Task.CompletedTask; }, log: log).RunAsync();
        Assert.AreEqual(0, exitCode, "the previous version runs and ends as it ends");
        CollectionAssert.AreEqual(new[] { "4.0.2", "4.0.1" }, runner.Started.Select(VersionOf).ToArray());
        Assert.AreEqual("pre-4.0.2.sqlite3", restored?.Backup);
        var state = store.Read();
        Assert.AreEqual("4.0.1", state.Current);
        Assert.IsTrue(state.IsBad(V2));
        StringAssert.Contains(state.LastOutcome?.Message, "exited with code 134");
        Assert.IsTrue(log.Any(static line => line.Contains("rolled back", StringComparison.Ordinal)), string.Join("\n", log));
    }

    [TestMethod]
    public async Task AVersionThatCannotBeStartedIsRolledBack()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1, V2);
        store.Select(V1);
        store.Confirm(V1);
        store.Select(V2, backup: "pre-4.0.2.sqlite3");
        UpdateTrial? restored = null;
        var inner = new FakeRunner((process, _) => { process.Exit(0); return Task.CompletedTask; });
        // The app host of 4.0.2 is corrupt or of another architecture: the operating system refuses to start it.
        var runner = new FailingStartRunner(inner, count => count == 1 ? new System.ComponentModel.Win32Exception(193, "not a valid application") : null);
        var launcher = new UpdateLauncher(new UpdateLauncherOptions
        {
            Store = store, Product = UpdateProduct.Node, Arguments = ["run"], Installed = new UpdateCommand("installed-altanode", []), Runner = runner,
            PollInterval = TimeSpan.FromMilliseconds(10), RestartDelay = TimeSpan.Zero, DotnetRoot = null, HandleSignals = false,
            RolledBack = (trial, _) => { restored = trial; return Task.CompletedTask; },
        });

        Assert.AreEqual(0, await launcher.RunAsync(), "the previous version runs and ends as it ends");
        Assert.AreEqual("4.0.1", VersionOf(inner.Started.Single()));
        Assert.AreEqual("pre-4.0.2.sqlite3", restored?.Backup);
        var state = store.Read();
        Assert.AreEqual("4.0.1", state.Current);
        Assert.IsTrue(state.IsBad(V2));
        StringAssert.Contains(state.LastOutcome?.Message, "not a valid application");
    }

    [TestMethod]
    public async Task ARestoreThatFailsStartsNothing_AndTheNextStartRestoresFirst()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1, V2);
        store.Select(V1);
        store.Confirm(V1);
        store.Select(V2, backup: "pre-4.0.2.sqlite3");
        var log = new List<string>();
        var runner = new FakeRunner((process, _) => { process.Exit(134); return Task.CompletedTask; });
        var exitCode = await Launcher(store, runner, rolledBack: static (_, _) => throw new IOException("the disk is full"), log: log).RunAsync();

        // 4.0.1 never runs on the database 4.0.2 migrated: the launcher stops and says why.
        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual("4.0.2", VersionOf(runner.Started.Single()));
        Assert.IsTrue(store.Read().IsBad(V2));
        Assert.IsTrue(log.Any(static line => line.Contains("the disk is full", StringComparison.Ordinal)), string.Join("\n", log));
        Assert.AreEqual("pre-4.0.2.sqlite3", store.Read().RollingBack?.Trial.Backup, "the rollback is recorded until its backup is restored");
        Assert.AreEqual("the disk is full", store.Read().RollingBack?.Error);
        Assert.IsNull(store.TakePending(UpdateProduct.Node), "nothing is selected before the restore");

        UpdateTrial? restored = null;
        var next = new FakeRunner((process, _) => { process.Exit(0); return Task.CompletedTask; });
        Assert.AreEqual(0, await Launcher(store, next, rolledBack: (trial, _) => { restored = trial; return Task.CompletedTask; }).RunAsync());
        Assert.AreEqual("pre-4.0.2.sqlite3", restored?.Backup, "the backup reference is kept until the restore succeeds");
        Assert.AreEqual("4.0.1", VersionOf(next.Started.Single()));
        Assert.IsNull(store.Read().RollingBack);
    }

    [TestMethod]
    public async Task AVersionThatDoesNotConfirmInTimeIsStoppedAndRolledBackToTheInstalledEntryPoint()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1);
        store.Select(V1);
        var runner = new FakeRunner((process, count) =>
        {
            if (count == 1) process.HonorsStop = false; // hangs: killed after the grace
            else process.Exit(0);
            return Task.CompletedTask;
        });
        var launcher = new UpdateLauncher(new UpdateLauncherOptions
        {
            Store = store, Product = UpdateProduct.Node, Arguments = ["run"], Installed = new UpdateCommand("installed-altanode", []), Runner = runner,
            TrialLimit = TimeSpan.FromMilliseconds(150), PollInterval = TimeSpan.FromMilliseconds(10), StopGrace = TimeSpan.FromMilliseconds(50),
            RestartDelay = TimeSpan.Zero, DotnetRoot = null, HandleSignals = false,
        });
        Assert.AreEqual(0, await launcher.RunAsync());
        Assert.AreEqual(1, runner.Started[0].StopRequests);
        Assert.IsTrue(runner.Started[0].Killed);
        Assert.AreEqual("installed", VersionOf(runner.Started[1]));
        Assert.IsNull(store.Read().Current);
        Assert.IsTrue(store.Read().IsBad(V1));
    }

    [TestMethod]
    public async Task AVersionStartedTooOftenWithoutConfirmingIsRolledBackBeforeItStarts()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1);
        store.Select(V1);
        store.CountTrialStart();
        store.CountTrialStart();
        var runner = new FakeRunner((process, _) => { process.Exit(0); return Task.CompletedTask; });
        await Launcher(store, runner).RunAsync();
        Assert.AreEqual("installed", VersionOf(runner.Started.Single()));
        Assert.IsTrue(store.Read().IsBad(V1));
    }

    [TestMethod]
    public async Task AStopDuringTheTrialIsNotAFailureOfTheVersion()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1);
        store.Select(V1);
        using var stop = new CancellationTokenSource();
        var runner = new FakeRunner(async (process, _) =>
        {
            await Task.Delay(30);
            await stop.CancelAsync();
        });
        Assert.AreEqual(0, await Launcher(store, runner, stop: stop.Token).RunAsync());
        Assert.AreEqual(1, runner.Started.Single().StopRequests, "the stop is passed to the version");
        var state = store.Read();
        Assert.AreEqual("4.0.1", state.Current);
        Assert.AreEqual(1, state.Trial?.Starts, "still on trial, tried again at the next start");
        Assert.AreEqual(0, state.Bad.Count);
    }

    [TestMethod]
    public async Task AMissingConfirmedVersionFallsBackToTheInstalledEntryPoint()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1);
        store.Select(V1);
        store.Confirm(V1);
        Directory.Delete(store.VersionPath(V1), recursive: true);
        var runner = new FakeRunner((process, _) => { process.Exit(0); return Task.CompletedTask; });
        await Launcher(store, runner).RunAsync();
        Assert.AreEqual("installed", VersionOf(runner.Started.Single()));
        Assert.IsNull(store.Read().Current);
    }

    [TestMethod]
    public async Task ASwitchThatWaitsIsTakenWhenNothingRuns()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1, V2);
        store.RequestSwitch(V3, now: false, "local");
        Assert.IsNull(store.TakePending(UpdateProduct.Node), "4.0.3 is not staged");
        Assert.AreEqual(UpdateOutcomes.Failed, store.Read().LastOutcome?.Outcome);
        Assert.IsNull(store.Read().Pending);

        store.RequestSwitch(V2, now: false, "local");
        var runner = new FakeRunner(async (process, _) =>
        {
            await Task.Delay(20);
            store.Confirm(V2);
            await Task.Delay(20);
            process.Exit(0);
        });
        Assert.AreEqual(0, await Launcher(store, runner).RunAsync());
        Assert.AreEqual("4.0.2", VersionOf(runner.Started.Single()), "the launcher took the switch before it started a version");
        Assert.AreEqual("4.0.2", store.Read().Current);
        Assert.IsNull(store.Read().Trial);

        store.RequestSwitch(V2, now: false, "local");
        Assert.IsNull(store.TakePending(UpdateProduct.Node), "it is selected already");
        Assert.IsNull(store.Read().Pending);
    }

    [TestMethod]
    public async Task ASwitchTakenBetweenTwoVersionsIsCopiedFirst_AndDroppedWhenTheCopyFails()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1, V2);
        store.Select(V1);
        store.Confirm(V1);
        string? backupOfTrial = null;
        var runner = new FakeRunner(async (process, count) =>
        {
            await Task.Delay(20);
            if (count == 1)
            {
                // 4.0.1 runs; a switch is asked for meanwhile and 4.0.1 stops for it, without selecting it itself.
                store.RequestSwitch(V2, now: true, "local");
                process.Exit(UpdateLauncher.RestartExitCode);
                return;
            }

            backupOfTrial = store.Read().Trial?.Backup;
            store.Confirm(V2);
            await Task.Delay(20);
            process.Exit(0);
        });
        var launcher = new UpdateLauncher(new UpdateLauncherOptions
        {
            Store = store, Product = UpdateProduct.Node, Arguments = ["run"], Installed = new UpdateCommand("installed-altanode", []), Runner = runner,
            PollInterval = TimeSpan.FromMilliseconds(10), RestartDelay = TimeSpan.Zero, DotnetRoot = null, HandleSignals = false,
            Backup = version => $"before-{version}.sqlite3",
        });
        Assert.AreEqual(0, await launcher.RunAsync());
        CollectionAssert.AreEqual(new[] { "4.0.1", "4.0.2" }, runner.Started.Select(VersionOf).ToArray());
        Assert.AreEqual("before-4.0.2.sqlite3", backupOfTrial);

        store.RequestSwitch(V1, now: false, "local");
        Assert.IsNull(store.TakePending(UpdateProduct.Node, static _ => throw new IOException("disk full")), "no switch without its copy");
        var state = store.Read();
        Assert.AreEqual("4.0.2", state.Current);
        Assert.IsNull(state.Pending);
        StringAssert.Contains(state.LastOutcome?.Message, "disk full");
    }

    [TestMethod]
    public async Task OneLauncherAtATimeRunsTheVersions_AndARefusedVersionIsNotBlamed()
    {
        using var temp = new TempFolder();
        var store = StoreWith(temp, V1);
        store.Select(V1);
        var runner = new FakeRunner((process, _) => { process.Exit(3); return Task.CompletedTask; });
        UpdateLauncher Create(UpdateLauncherLease? lease) => new(new UpdateLauncherOptions
        {
            Store = store, Product = UpdateProduct.Node, Arguments = ["run"], Installed = new UpdateCommand("installed-altanode", []), Runner = runner,
            PollInterval = TimeSpan.FromMilliseconds(10), RestartDelay = TimeSpan.Zero, DotnetRoot = null, HandleSignals = false, Lease = lease, HeldExitCode = 3,
        });

        using (var lease = store.TryAcquireLauncherLease())
        {
            Assert.IsNotNull(lease);
            Assert.IsNull(new UpdateVersionStore(store.Root).TryAcquireLauncherLease(), "another launcher of the same folder is refused");
            var busy = await Assert.ThrowsExactlyAsync<UpdateException>(() => Create(null).RunAsync());
            Assert.AreEqual(UpdateErrorCodes.LauncherBusy, busy.Code);
            Assert.AreEqual(0, runner.Started.Count);

            // The holder of the lease runs the version; refused by the holder of its profile, it is not blamed.
            Assert.AreEqual(3, await Create(lease).RunAsync());
        }

        var state = store.Read();
        Assert.AreEqual("4.0.1", state.Trial?.Version);
        Assert.AreEqual(0, state.Trial?.Starts);
        Assert.AreEqual(0, state.Bad.Count);
        using var again = store.TryAcquireLauncherLease();
        Assert.IsNotNull(again, "the lease is free once its holder released it");
    }

    [TestMethod]
    public void ANodeDrainsThenWaitsForTheNextIdleMoment()
    {
        var start = DateTimeOffset.UnixEpoch;
        var drain = new UpdateDrain(now: false, TimeSpan.FromMinutes(10));
        Assert.AreEqual(new UpdateDrainStep(UpdatePhases.Draining, true, false), drain.Step(0, start), "new work is refused for one step first");
        Assert.AreEqual(new UpdateDrainStep(UpdatePhases.Switching, true, true), drain.Step(0, start.AddSeconds(1)));

        drain = new UpdateDrain(now: false, TimeSpan.FromMinutes(10));
        drain.Step(2, start);
        Assert.AreEqual(new UpdateDrainStep(UpdatePhases.Draining, true, false), drain.Step(1, start.AddMinutes(5)));
        Assert.AreEqual(new UpdateDrainStep(UpdatePhases.WaitingForIdle, false, false), drain.Step(1, start.AddMinutes(11)), "past the bound, work is taken again");
        Assert.AreEqual(new UpdateDrainStep(UpdatePhases.WaitingForIdle, false, false), drain.Step(3, start.AddMinutes(20)));
        Assert.AreEqual(new UpdateDrainStep(UpdatePhases.Draining, true, false), drain.Step(0, start.AddMinutes(30)), "idle: new work is refused once more");
        Assert.AreEqual(new UpdateDrainStep(UpdatePhases.Draining, true, false), drain.Step(1, start.AddMinutes(30).AddSeconds(1)), "a turn admitted just then is waited for");
        Assert.AreEqual(new UpdateDrainStep(UpdatePhases.Switching, true, true), drain.Step(0, start.AddMinutes(31)));

        var now = new UpdateDrain(now: true, TimeSpan.FromMinutes(10));
        Assert.IsTrue(now.Step(5, start).Switch, "--now does not wait");
    }

    [TestMethod]
    public void TheStatusReportsTheTrialAndThePendingSwitch()
    {
        var state = new UpdateState
        {
            Current = "4.0.2", Previous = "4.0.1", Trial = new UpdateTrial("4.0.2", DateTimeOffset.UnixEpoch, 1),
            Pending = new UpdatePending("4.0.3", DateTimeOffset.UnixEpoch, false, UpdatePhases.Draining, DateTimeOffset.UnixEpoch, 2, "local"),
        };
        FleetUpdateStatus status = UpdateStatusReport.Describe(state, "4.0.2", "4.0.0");
        Assert.AreEqual("4.0.0", status.Installed);
        Assert.AreEqual(1, status.Trial?.Starts);
        Assert.AreEqual(UpdatePhases.Draining, status.Pending?.Phase);
        var text = string.Join("\n", UpdateStatusReport.Lines(status, "altanode"));
        StringAssert.Contains(text, "Version: 4.0.2 (selected by altanode update, installed 4.0.0, previous 4.0.1).");
        StringAssert.Contains(text, "on trial");
        StringAssert.Contains(text, "waiting for 2 session(s)");
    }

    /// <summary>A runner whose starts fail as a test says, for an app host that cannot be started (corrupt, another architecture).</summary>
    private sealed class FailingStartRunner(IUpdateProcessRunner inner, Func<int, Exception?> failure) : IUpdateProcessRunner
    {
        private int _count;

        public IUpdateProcess Start(UpdateCommand command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment)
            => failure(++_count) is { } exception ? throw exception : inner.Start(command, arguments, environment);
    }
}
