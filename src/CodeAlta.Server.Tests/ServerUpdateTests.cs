using System.IO.Compression;
using System.Net;
using System.Text;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Update;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Server.Tests;

[TestClass]
public sealed class ServerUpdateTests
{
    private static readonly string Runtime = UpdateProduct.CurrentRuntime()!;

    [TestMethod]
    public async Task TheServerReportsItsVersionsToItsDevices()
    {
        var status = new FleetUpdateStatus("99.0.1", "99.0.0", "99.0.1", "99.0.0", new FleetUpdateTrial("99.0.1", DateTimeOffset.UnixEpoch, 1), null, [], null);
        await using var server = await ServerFixture.StartAsync(updateStatus: () => status);
        using var owner = new TestDevice("Desktop");
        await server.SetUpAsync(owner);

        var answer = await server.SendAsync(owner, HttpMethod.Get, "/api/v1/server/update");
        Assert.AreEqual(HttpStatusCode.OK, answer.Status, answer.Body.ToString());
        var read = answer.Read<FleetUpdateStatus>();
        Assert.AreEqual("99.0.1", read.Running);
        Assert.AreEqual("99.0.0", read.Installed);
        Assert.AreEqual(1, read.Trial?.Starts);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await server.SendAsync(null, HttpMethod.Get, "/api/v1/server/update")).Status, "a device signs the request");

        await using var plain = await ServerFixture.StartAsync();
        using var other = new TestDevice("Desktop");
        await plain.SetUpAsync(other);
        Assert.AreEqual(ServerVersion.Current, (await plain.SendAsync(other, HttpMethod.Get, "/api/v1/server/update")).Read<FleetUpdateStatus>().Running);
    }

    [TestMethod]
    public async Task Update_StagesAVersion_AndRunHandsOverToIt()
    {
        using var root = new TempDirectory();
        var feed = Feed(root, "99.0.0");
        var paths = new ServerPaths(Path.Combine(root.Path, "data"));
        var store = new UpdateVersionStore(paths.VersionsRoot);
        var runner = new RecordingRunner(3, () => store.Confirm(UpdateVersion.Parse("99.0.0")));
        var app = Application(runner, out var output, out var error);

        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["update", "--check", "--source", feed, "--data-root", paths.DataRoot]), error.ToString());
        StringAssert.Contains(output.ToString(), "An update to 99.0.0 is available");
        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["update", "--source", feed, "--data-root", paths.DataRoot]), error.ToString());
        StringAssert.Contains(output.ToString(), "No server runs on");

        var exit = await app.RunAsync(["run", "--port", "45999", "--data-root", paths.DataRoot]);
        Assert.AreEqual(3, exit, "the exit code of the version is the one of run");
        var started = runner.Started.Single();
        StringAssert.StartsWith(started.Command.FileName, store.VersionPath(UpdateVersion.Parse("99.0.0")));
        CollectionAssert.AreEqual(new[] { "run", "--port", "45999", "--data-root", paths.DataRoot }, started.Arguments.ToArray());

        output.GetStringBuilder().Clear();
        Assert.AreEqual(ServerExitCodes.Failure, await app.RunAsync(["status", "--data-root", paths.DataRoot]));
        StringAssert.Contains(output.ToString(), $"Version: 99.0.0 (selected by altaserver update, installed {ServerVersion.Current}).");
    }

    [TestMethod]
    public async Task Run_CopiesItsDatabase_ThenStartsTheNewVersion()
    {
        using var root = new TempDirectory();
        var paths = new ServerPaths(Path.Combine(root.Path, "data"));
        var store = new UpdateVersionStore(paths.VersionsRoot);
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        var runner = new RecordingRunner(0, () => store.Confirm(target));
        var app = Application(runner, out var output, out var error, started: (_, _) => store.RequestSwitch(target, now: false, "local"));

        var exit = await app.RunAsync(["run", "--urls", "http://127.0.0.1:0", "--data-root", paths.DataRoot]);

        Assert.AreEqual(0, exit, error.ToString());
        StringAssert.Contains(output.ToString(), "Stopping CodeAlta Server to start its new version");
        var state = store.Read();
        Assert.AreEqual("99.0.0", state.Current);
        StringAssert.StartsWith(runner.Started.Single().Command.FileName, store.VersionPath(target));
        var backup = Directory.GetFiles(paths.BackupsRoot, "before-99.0.0-*.sqlite3").Single();
        Assert.IsTrue(new FileInfo(backup).Length > 0, "the database was copied before the switch");
    }

    [TestMethod]
    public async Task ARollbackRestoresTheDatabaseCopiedBeforeTheSwitch()
    {
        using var root = new TempDirectory();
        var paths = new ServerPaths(Path.Combine(root.Path, "data"));
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(paths.DatabasePath, "migrated by 99.0.0");
        File.WriteAllText(paths.DatabasePath + "-wal", "wal of 99.0.0");
        var backup = Path.Combine(root.Path, "before.sqlite3");
        File.WriteAllText(backup, "as 98 left it");
        var store = new UpdateVersionStore(paths.VersionsRoot);
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        store.Select(target, backup);
        // 99.0.0 crashes at its start; the installed entry point then runs and ends with 0.
        var runner = new RecordingRunner(0, crashFirst: true);
        var app = Application(runner, out _, out var error);

        Assert.AreEqual(0, await app.RunAsync(["run", "--data-root", paths.DataRoot]), error.ToString());

        Assert.AreEqual("as 98 left it", File.ReadAllText(paths.DatabasePath));
        Assert.IsFalse(File.Exists(paths.DatabasePath + "-wal"));
        Assert.IsTrue(store.Read().IsBad(target));
        Assert.IsNull(store.Read().Current);
        Assert.AreEqual(2, runner.Started.Count);
        StringAssert.Contains(error.ToString(), "was restored");
    }

    [TestMethod]
    public async Task Update_WhileStopped_CopiesTheDatabaseBeforeRunStartsTheNewVersion()
    {
        using var root = new TempDirectory();
        await (await ServerFixture.StartAsync(root)).DisposeAsync();
        var paths = new ServerPaths(root.Path);
        Assert.IsTrue(File.Exists(paths.DatabasePath), "the server ran once and left its database");
        var feed = Feed(root, "99.0.0");
        var store = new UpdateVersionStore(paths.VersionsRoot);
        var target = UpdateVersion.Parse("99.0.0");
        string? backupOfTrial = null;
        var runner = new RecordingRunner(0, () =>
        {
            backupOfTrial ??= store.Read().Trial?.Backup ?? "";
            store.Confirm(target);
        });
        var app = Application(runner, out var output, out var error);

        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["update", "--source", feed, "--data-root", paths.DataRoot]), error.ToString());
        StringAssert.Contains(output.ToString(), "No server runs on");
        Assert.AreEqual(0, await app.RunAsync(["run", "--port", "45998", "--data-root", paths.DataRoot]), error.ToString());

        Assert.IsFalse(string.IsNullOrEmpty(backupOfTrial), "the version on trial has a copy of the database a rollback restores: " + error);
        Assert.IsTrue(File.Exists(backupOfTrial) && new FileInfo(backupOfTrial).Length > 0, backupOfTrial);
        StringAssert.StartsWith(backupOfTrial, paths.BackupsRoot);
    }

    [TestMethod]
    public async Task ARollbackWhoseBackupCannotBeRestoredDoesNotStartThePreviousVersion()
    {
        using var root = new TempDirectory();
        var paths = new ServerPaths(Path.Combine(root.Path, "data"));
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(paths.DatabasePath, "migrated by 99.0.0");
        var store = new UpdateVersionStore(paths.VersionsRoot);
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        store.Select(target, Path.Combine(root.Path, "missing.sqlite3"));
        var runner = new RecordingRunner(0, crashFirst: true);
        var app = Application(runner, out _, out var error);

        Assert.AreNotEqual(0, await app.RunAsync(["run", "--data-root", paths.DataRoot]), error.ToString());

        Assert.AreEqual(1, runner.Started.Count, "the previous version is not started on a database it may not read");
        Assert.AreEqual("migrated by 99.0.0", File.ReadAllText(paths.DatabasePath));
        StringAssert.Contains(error.ToString(), "could not be restored");
        Assert.IsTrue(store.Read().IsBad(target));
        Assert.AreEqual(UpdateLauncher.RestoreFailedExitCode, await app.RunAsync(["run", "--data-root", paths.DataRoot]), "the next run restores first, and still cannot");
        Assert.AreEqual(1, runner.Started.Count, error.ToString());

        var output = new StringWriter();
        Assert.AreEqual(ServerExitCodes.Failure, await new ServerApplication(output, new StringWriter()).RunAsync(["status", "--data-root", paths.DataRoot]));
        StringAssert.Contains(output.ToString(), "is not restored yet");
    }

    [TestMethod]
    public async Task ASecondRunWhileALauncherRunsStartsNoOtherVersion()
    {
        using var root = new TempDirectory();
        var paths = new ServerPaths(Path.Combine(root.Path, "data"));
        var store = new UpdateVersionStore(paths.VersionsRoot);
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        store.Select(target);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingRunner(0, gate: release.Task);
        var first = Application(runner, out _, out var firstError).RunAsync(["run", "--data-root", paths.DataRoot]);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (runner.Started.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.AreEqual(1, runner.Started.Count, firstError.ToString());

        // The version on trial has not taken the lock of the data root yet: only the launcher's lease tells that one runs.
        var second = Application(runner, out _, out var secondError).RunAsync(["run", "--data-root", paths.DataRoot]);
        var ended = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10))) == second;
        var startedBySecond = runner.Started.Count - 1;
        store.Confirm(target);
        release.TrySetResult(0);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.IsTrue(ended, "the second run is refused at once");
        Assert.AreEqual(0, startedBySecond, "the second run starts no other version");
        Assert.AreEqual(ServerExitCodes.DataRootInUse, await second);
        StringAssert.Contains(secondError.ToString(), paths.DataRoot);
        Assert.AreEqual(0, store.Read().Bad.Count);
    }

    [TestMethod]
    public async Task AVersionRefusedBecauseAnotherServerHoldsTheDataRootIsNotRolledBack()
    {
        using var root = new TempDirectory();
        var paths = new ServerPaths(Path.Combine(root.Path, "data"));
        var store = new UpdateVersionStore(paths.VersionsRoot);
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        store.Select(target);
        // The version on trial is refused by the lock of the data root (a server started by hand meanwhile).
        var runner = new RecordingRunner(ServerExitCodes.DataRootInUse);
        var app = Application(runner, out _, out var error);

        Assert.AreEqual(ServerExitCodes.DataRootInUse, await app.RunAsync(["run", "--data-root", paths.DataRoot]), error.ToString());

        Assert.AreEqual(1, runner.Started.Count, error.ToString());
        var state = store.Read();
        Assert.AreEqual(0, state.Bad.Count, "the version on trial is not blamed");
        Assert.AreEqual("99.0.0", state.Trial?.Version);
        Assert.AreEqual(0, state.Trial?.Starts, "a start refused for the data root does not count");
    }

    [TestMethod]
    public async Task Run_DoesNotHandOverWhileAnotherServerHoldsTheDataRoot()
    {
        using var root = new TempDirectory();
        await using var running = await ServerFixture.StartAsync(root);
        var paths = new ServerPaths(root.Path);
        var store = new UpdateVersionStore(paths.VersionsRoot);
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        store.Select(target);
        var runner = new RecordingRunner(1);
        var app = Application(runner, out _, out var error);

        Assert.AreEqual(ServerExitCodes.DataRootInUse, await app.RunAsync(["run", "--urls", "http://127.0.0.1:0", "--data-root", paths.DataRoot]));
        StringAssert.Contains(error.ToString(), "Another CodeAlta Server uses the data root");
        Assert.AreEqual(0, runner.Started.Count);
        Assert.AreEqual(0, store.Read().Bad.Count, "the version on trial is not blamed for a server that was already running");
    }

    [TestMethod]
    public async Task AFailedBackupDropsTheSwitch()
    {
        using var root = new TempDirectory();
        var store = new UpdateVersionStore(Path.Combine(root.Path, "versions"));
        var target = UpdateVersion.Parse("99.0.0");
        Stage(store, target);
        var restarts = 0;
        var updates = new ServerUpdates(new ServerUpdatesOptions
        {
            Store = store, Running = UpdateVersion.Parse("98.0.0"), Healthy = static _ => Task.FromResult(true),
            Backup = static (_, _) => throw new IOException("disk full"), RequestRestart = () => restarts++,
        });
        store.RequestSwitch(target, now: false, "local");
        await updates.TickAsync(CancellationToken.None);
        Assert.AreEqual(0, restarts);
        Assert.IsNull(store.Read().Current);
        StringAssert.Contains(store.Read().LastOutcome?.Message, "disk full");
    }

    private static ServerApplication Application(RecordingRunner runner, out StringWriter output, out StringWriter error, Action<CoordinatorServer, Action>? started = null)
    {
        output = new StringWriter();
        error = new StringWriter();
        return new ServerApplication(TextWriter.Synchronized(output), TextWriter.Synchronized(error))
        {
            Started = started,
            ConfigureServer = static options => new CoordinatorServerOptions
            {
                DataRoot = options.DataRoot, Urls = options.Urls, UpdateStatus = options.UpdateStatus, ConfigureLogging = static _ => { },
            },
            ConfigureLauncher = options => options with { Runner = runner, HandleSignals = false, RestartDelay = TimeSpan.Zero, DotnetRoot = null, PollInterval = TimeSpan.FromMilliseconds(20) },
            ConfigureUpdates = static options => options with { Interval = TimeSpan.FromMilliseconds(50) },
        };
    }

    private static string Feed(TempDirectory root, string version)
    {
        var feed = Directory.CreateDirectory(Path.Combine(root.Path, "feed")).FullName;
        // The tool package and the runtime package of this machine, as dotnet pack makes them: a version needs both.
        using (var tool = ZipFile.Open(Path.Combine(feed, $"{UpdateProduct.Server.PackageId}.{version}.nupkg"), ZipArchiveMode.Create))
        using (var settings = tool.CreateEntry("tools/net10.0/any/DotnetToolSettings.xml").Open())
            settings.Write(Encoding.UTF8.GetBytes("<DotNetCliTool Version=\"2\" />"));
        var id = UpdateProduct.Server.RuntimePackageId(Runtime);
        using var archive = ZipFile.Open(Path.Combine(feed, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
        foreach (var name in new[] { UpdateProduct.Server.AssemblyFileName, UpdateProduct.Server.AppHostFileName(OperatingSystem.IsWindows()) })
        {
            using var stream = archive.CreateEntry($"tools/net10.0/{Runtime}/{name}").Open();
            stream.Write(Encoding.UTF8.GetBytes(version));
        }

        return feed;
    }

    private static void Stage(UpdateVersionStore store, UpdateVersion version)
    {
        Directory.CreateDirectory(store.VersionPath(version));
        File.WriteAllText(Path.Combine(store.VersionPath(version), UpdateProduct.Server.AssemblyFileName), version.ToString());
        File.WriteAllText(Path.Combine(store.VersionPath(version), UpdateProduct.Server.AppHostFileName(OperatingSystem.IsWindows())), version.ToString());
    }

    /// <summary>Records the versions a launcher starts; each does what the test says, then exits with the code given.</summary>
    private sealed class RecordingRunner(int exitCode, Action? onStart = null, bool crashFirst = false, Task? gate = null) : IUpdateProcessRunner
    {
        private readonly List<(UpdateCommand Command, IReadOnlyList<string> Arguments)> _started = [];

        public List<(UpdateCommand Command, IReadOnlyList<string> Arguments)> Started
        {
            get
            {
                lock (_started) return [.. _started];
            }
        }

        public IUpdateProcess Start(UpdateCommand command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment)
        {
            int count;
            lock (_started)
            {
                _started.Add((command, arguments));
                count = _started.Count;
            }

            return new Exited(crashFirst && count == 1 ? 134 : exitCode, crashFirst ? null : onStart, gate);
        }

        private sealed class Exited(int code, Action? onStart, Task? gate) : IUpdateProcess
        {
            public int Id => 1;

            public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
            {
                onStart?.Invoke();
                if (gate is not null) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
