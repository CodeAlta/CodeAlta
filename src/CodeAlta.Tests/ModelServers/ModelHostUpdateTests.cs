using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.ModelServers;
using CodeAlta.ModelServers.Hardware;

namespace CodeAlta.Tests.ModelServers;

/// <summary>
/// The updates of llama.cpp by the model host, against real processes: the builds are archives of the fake llama-server
/// (CodeAlta.Tests.FakeLlamaServer) served by a fake GitHub, each with the fake-llama.json that says how it behaves, and a
/// router of two models of a Hugging Face cache made for the test runs on them.
/// </summary>
[TestClass]
public sealed class ModelHostUpdateTests
{
    [TestMethod]
    public async Task AnUpdateThatPasses_RestartsTheServerOnTheNewBuild()
    {
        await using var machine = FakeMachine.Create();
        machine.GitHub.Publish("b12000", new FakeBehaviour());
        machine.GitHub.Publish("b12100", new FakeBehaviour());
        await using var host = machine.CreateHost();
        var before = await machine.PrepareAsync(host, "b12000");
        var installed = host.Builds.Find(machine.Backend, "b12000");
        Assert.IsNotNull(installed);
        Assert.AreEqual("version: 12000 (fake12000)", installed.VersionLine, "the probe read --version");
        Assert.AreEqual("CPU0", installed.Devices.Single().Name, "the probe read --list-devices");
        Assert.IsGreaterThan(0, machine.GitHub.RangeRequests, "the archive was fetched by ranges");

        // The channel moves to b12100: an update without a build takes it.
        machine.GitHub.Stable = "b12100";
        var task = host.UpdateNow(null, false, "test");
        var ended = await machine.WaitTaskAsync(host, task.Id);
        Assert.AreEqual("done", ended.State, ended.Message);

        var record = LastRecord(host, ended);
        Assert.AreEqual("updated b12000 → b12100", Summary(record), record.Reason);
        var after = await machine.WaitReadyAsync(host, "b12100");
        Assert.AreNotEqual(before.ProcessId, after.ProcessId, "the server restarted");
        Assert.IsFalse(IsRunning(before.ProcessId!.Value), "the process of the previous build ended");
        Assert.AreEqual("b12100", host.Builds.Find(machine.Backend, null)?.Tag);
        Assert.AreEqual("b12000", host.Builds.Read().Previous[machine.Backend], "the build it left is the way back");
        Assert.IsTrue(File.Exists(Path.Combine(machine.SharedRoot, "updates.json")), "the history is kept");
    }

    [TestMethod]
    [DataRow("error", "500", DisplayName = "The test answers 500")]
    [DataRow("empty", "no token", DisplayName = "The test gives no token")]
    public async Task AnUpdateWhoseTestFails_GoesBackToThePreviousBuild(string chat, string reason)
    {
        await using var machine = FakeMachine.Create();
        machine.GitHub.Publish("b12000", new FakeBehaviour());
        machine.GitHub.Publish("b12050", new FakeBehaviour { Chat = chat });
        await using var host = machine.CreateHost();
        await machine.PrepareAsync(host, "b12000");

        var task = host.UpdateNow("b12050", false, "test");
        var ended = await machine.WaitTaskAsync(host, task.Id);
        Assert.AreEqual("failed", ended.State, ended.Message);

        var record = LastRecord(host, ended);
        Assert.AreEqual("rolledBack b12000 → b12050", Summary(record), record.Reason);
        StringAssert.Contains(record.Reason, reason);
        await machine.WaitReadyAsync(host, "b12000");
        AssertRolledBack(host, machine.Backend, "b12000", "b12050");
    }

    [TestMethod]
    public async Task AnUpdateToABuildTheServerCannotStartOn_GoesBackToThePreviousBuild()
    {
        // Regression: a server that did not even start on the new build (one too old for router mode) ended the update
        // with that build in use, instead of a failed test rolled back (fixed in 47bf33c53).
        await using var machine = FakeMachine.Create();
        machine.GitHub.Publish("b12000", new FakeBehaviour());
        machine.GitHub.Publish("b9000", new FakeBehaviour());
        await using var host = machine.CreateHost();
        await machine.PrepareAsync(host, "b12000");

        var task = host.UpdateNow("b9000", false, "test");
        var ended = await machine.WaitTaskAsync(host, task.Id);
        Assert.AreEqual("failed", ended.State, ended.Message);

        var record = LastRecord(host, ended);
        Assert.AreEqual("rolledBack b12000 → b9000", Summary(record), record.Reason);
        StringAssert.Contains(record.Reason, "too old");
        await machine.WaitReadyAsync(host, "b12000");
        AssertRolledBack(host, machine.Backend, "b12000", "b9000");
    }

    [TestMethod]
    public async Task APlannedUpdate_IsKeptAcrossANewModelHost_AndRunsAtItsTime()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
        await using var machine = FakeMachine.Create();
        machine.GitHub.Publish("b12000", new FakeBehaviour());
        machine.GitHub.Publish("b12100", new FakeBehaviour());
        PlannedUpdate planned;
        await using (var first = machine.CreateHost(time))
        {
            await machine.PrepareAsync(first, "b12000");
            planned = first.PlanUpdate(time.GetUtcNow().AddHours(1), "b12100", false, "test");
        }

        Assert.IsTrue(File.Exists(Path.Combine(machine.SharedRoot, "update.json")));
        var timers = time.Created;
        await using var second = machine.CreateHost(time);
        Assert.AreEqual(planned.Id, second.GetStatus().Planned?.Id, "the planned update is kept across a new model host");
        await second.StartAsync(CancellationToken.None);
        await machine.WaitReadyAsync(second, "b12000");

        // The loop of the host waits on the clock: before its time, the update does not run.
        await time.WaitForTimerAsync(timers);
        timers = time.Created;
        time.Advance(TimeSpan.FromMinutes(30));
        await time.WaitForTimerAsync(timers);
        Assert.IsEmpty(second.GetStatus().History, "it is not its time yet");
        Assert.AreEqual(planned.Id, second.GetStatus().Planned?.Id);

        time.Advance(TimeSpan.FromMinutes(31));
        var ended = await machine.WaitForAsync(second, () => second.GetStatus().Tasks.FirstOrDefault(static task => task.Kind == "update" && task.EndedAt is not null),
            "the planned update ran");
        Assert.AreEqual("done", ended.State, ended.Message);
        Assert.AreEqual("Planned switch to b12100", ended.Title);
        Assert.AreEqual("test", ended.RequestedBy);
        var record = LastRecord(second, ended);
        Assert.AreEqual("updated b12000 → b12100", Summary(record), record.Reason);
        Assert.IsNull(second.GetStatus().Planned, "a planned update runs once");
        Assert.IsFalse(File.Exists(Path.Combine(machine.SharedRoot, "update.json")));
        await machine.WaitReadyAsync(second, "b12100");
    }

    private static UpdateRecord LastRecord(ModelHost host, HostTask ended)
    {
        var record = host.GetStatus().History.FirstOrDefault();
        Assert.IsNotNull(record, $"The update recorded nothing: {ended.Message}");
        return record;
    }

    private static string Summary(UpdateRecord record) => $"{record.Result} {record.From} → {record.To}";

    private static void AssertRolledBack(ModelHost host, string backend, string previous, string failed)
    {
        Assert.AreEqual(previous, host.Builds.Find(backend, null)?.Tag, "back on the previous build");
        Assert.IsTrue(host.Builds.IsBad(backend, failed), "the build is marked bad");
        var state = host.Builds.Read();
        Assert.AreNotEqual(failed, state.Previous.GetValueOrDefault(backend), "the build that failed is not offered as the way back");
        Assert.IsTrue(host.Builds.List().Single(build => build.Tag == failed).Bad);
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>How a build of the fake behaves: its fake-llama.json.</summary>
    private sealed record FakeBehaviour
    {
        public string Chat { get; init; } = "ok";

        public string? ExitAtOnce { get; init; }

        public string ToJson(int build) => new JsonObject
        {
            ["build"] = build,
            ["commit"] = "fake" + build.ToString(CultureInfo.InvariantCulture),
            ["devices"] = new JsonArray(new JsonObject { ["name"] = "CPU0", ["description"] = "Fake processor", ["totalMiB"] = 16384, ["freeMiB"] = 12000 }),
            ["chat"] = Chat,
            ["exitAtOnce"] = ExitAtOnce,
            // A fake that a dead test left behind ends by itself.
            ["lifetimeSeconds"] = 180,
        }.ToJsonString();
    }

    /// <summary>
    /// A machine for the model host: its folders in a temporary folder, a fake GitHub, a Hugging Face cache with two
    /// models, no GPU. Its dispose ends any fake server left under its folder, and says so.
    /// </summary>
    private sealed class FakeMachine : IAsyncDisposable
    {
        private const string Alpha = "fake-org/Alpha-GGUF:Q4_K_M";
        private const string Beta = "fake-org/Beta-GGUF:Q8_0";
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
        private readonly TestTempDirectory _temp;
        private readonly int _firstPort = Random.Shared.Next(20000, 40000);

        private FakeMachine(TestTempDirectory temp)
        {
            _temp = temp;
            GitHub = new FakeGitHub(Backend);
            Directory.CreateDirectory(LinuxRoot);
            WriteModel("fake-org/Alpha-GGUF", "Alpha-Q4_K_M.gguf");
            WriteModel("fake-org/Beta-GGUF", "Beta-Q8_0.gguf");
        }

        public static FakeMachine Create()
        {
            // The apphost of the fake finds the runtime that runs these tests where it is not installed in a known place.
            if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("DOTNET_ROOT") is null or "")
                Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")));
            return new FakeMachine(TestTempDirectory.Create());
        }

        public FakeGitHub GitHub { get; }

        /// <summary>Gets the backend of the archives: cpu, or metal on macOS arm64, which has no archive of the processor alone.</summary>
        public string Backend { get; } = OperatingSystem.IsMacOS() && HardwareProbe.CurrentArchitecture == "arm64" ? "metal" : "cpu";

        public string SharedRoot => Path.Combine(_temp.Path, "shared");

        private string InstanceRoot => Path.Combine(_temp.Path, "instance");

        private string ModelsFolder => Path.Combine(_temp.Path, "hub");

        private string LinuxRoot => Path.Combine(_temp.Path, "machine");

        public ModelHost CreateHost(TimeProvider? time = null) => new(new ModelHostOptions
        {
            SharedRoot = SharedRoot,
            InstanceRoot = InstanceRoot,
            FirstPort = _firstPort,
            NetworkHandler = GitHub,
            // No GPU and no tool of a driver: the machine runs the processor's build.
            Probe = new HardwareProbe(static (_, _, _) => Task.FromResult<string?>(null), LinuxRoot),
            Time = time ?? TimeProvider.System,
        });

        /// <summary>Starts the host with its models, installs a build, and runs a router of the two models on it.</summary>
        public async Task<ServerStatus> PrepareAsync(ModelHost host, string build)
        {
            host.SetSettings(host.Settings with { ModelsFolder = ModelsFolder });
            await host.StartAsync(CancellationToken.None);
            Assert.HasCount(2, host.GetStatus().Models, "the models of the cache");
            var install = host.InstallBuilds([Backend], build, "test");
            var installed = await WaitTaskAsync(host, install.Id);
            Assert.AreEqual("done", installed.State, installed.Message);
            await host.SetServerAsync(new ServerDefinition
            {
                Id = "",
                Name = "Fake router",
                Backend = Backend,
                LoadedAtOnce = 2,
                Models = [new ServerModel { Model = Alpha, LoadOnStart = true }, new ServerModel { Model = Beta }],
            }, CancellationToken.None);
            return await WaitReadyAsync(host, build);
        }

        /// <summary>Waits until the router answers on a build, with its first model loaded.</summary>
        public Task<ServerStatus> WaitReadyAsync(ModelHost host, string build)
            => WaitForAsync(host, () => host.ListServers().Single().Status is { State: "ready" } status && status.BuildTag == build
                && status.Models.Any(static model => model.Model == Alpha && model.State == "loaded") && status.Models.Any(static model => model.Model == Beta) ? status : null,
                $"the router runs {build}");

        public Task<HostTask> WaitTaskAsync(ModelHost host, string id)
            => WaitForAsync(host, () => host.GetStatus().Tasks.Single(task => task.Id == id) is { EndedAt: not null } task ? task : null, $"the task {id} ended");

        public async Task<T> WaitForAsync<T>(ModelHost host, Func<T?> probe, string what)
            where T : class
        {
            var end = DateTime.UtcNow + Deadline;
            while (true)
            {
                if (probe() is { } value) return value;
                if (DateTime.UtcNow > end) Assert.Fail($"Not in time: {what}.\n{Describe(host)}");
                await Task.Delay(100);
            }
        }

        private static string Describe(ModelHost host)
        {
            var status = host.GetStatus();
            var text = new StringBuilder();
            foreach (var task in status.Tasks) text.AppendLine($"task {task.Title}: {task.State} {task.Message}");
            foreach (var record in status.History) text.AppendLine($"history {record.Backend} {record.From} → {record.To}: {record.Result} {record.Reason}");
            foreach (var server in status.Servers)
            {
                text.AppendLine($"server {server.Definition.Id}: {server.Status.State} on {server.Status.BuildTag} ({server.Status.Reason}) "
                    + string.Join(", ", server.Status.Models.Select(static model => $"{model.Model} {model.State}")));
                foreach (var line in host.ReadServerLog(server.Definition.Id, 20)) text.AppendLine("  " + line);
            }

            return text.ToString();
        }

        // A model in the layout of the Hugging Face cache: refs/main names a commit, its snapshot holds the file (a copy,
        // as where links cannot be made).
        private void WriteModel(string repo, string file)
        {
            var folder = Path.Combine(ModelsFolder, "models--" + repo.Replace("/", "--", StringComparison.Ordinal));
            var commit = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(repo)));
            Directory.CreateDirectory(Path.Combine(folder, "refs"));
            File.WriteAllText(Path.Combine(folder, "refs", "main"), commit);
            var snapshot = Directory.CreateDirectory(Path.Combine(folder, "snapshots", commit)).FullName;
            File.WriteAllBytes(Path.Combine(snapshot, file), [.. "GGUF"u8, .. new byte[60]]);
        }

        public async ValueTask DisposeAsync()
        {
            var left = new List<string>();
            foreach (var process in Process.GetProcessesByName("llama-server"))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is not { } path || !path.StartsWith(_temp.Path, StringComparison.OrdinalIgnoreCase)) continue;
                        left.Add($"{process.Id} {path}");
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                    {
                    }
                }
            }

            GitHub.Dispose();
            _temp.Dispose();
            if (left.Count > 0) Assert.Fail("A fake llama-server was left running: " + string.Join(", ", left));
        }
    }

    /// <summary>
    /// GitHub for the model host: the releases of llama.cpp (the versioned release that names the stable build in its
    /// nightly-tag.txt, and the builds), and their archives with their SHA-256, by ranges as RangeDownloader asks.
    /// </summary>
    private sealed class FakeGitHub(string backend) : HttpMessageHandler
    {
        private const string Downloads = "https://github.example/download/";
        private readonly Lock _gate = new();
        private readonly Dictionary<string, JsonObject> _releases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private int _rangeRequests;

        /// <summary>Gets or sets the build the versioned release names.</summary>
        public string Stable { get; set; } = "b12000";

        public int RangeRequests => Volatile.Read(ref _rangeRequests);

        public void Publish(string tag, FakeBehaviour behaviour)
        {
            var build = int.Parse(tag.AsSpan(1), CultureInfo.InvariantCulture);
            var name = AssetName(tag);
            var archive = Pack(name, "llama-" + tag, behaviour.ToJson(build));
            lock (_gate)
            {
                _files[tag + "/" + name] = archive;
                _releases[tag] = new JsonObject
                {
                    ["tag_name"] = tag,
                    ["published_at"] = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(build).ToString("O", CultureInfo.InvariantCulture),
                    ["assets"] = new JsonArray(new JsonObject
                    {
                        ["name"] = name,
                        ["browser_download_url"] = Downloads + tag + "/" + name,
                        ["size"] = archive.Length,
                        ["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(archive)),
                        ["state"] = "uploaded",
                    }),
                };
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (_gate)
            {
                const string api = "/repos/ggml-org/llama.cpp/releases";
                if (uri.Host == "api.github.com" && uri.AbsolutePath == api + "/latest")
                    return Answer("""{ "tag_name": "v0.9.0", "assets": [ { "name": "nightly-tag.txt", "browser_download_url": "https://github.example/nightly-tag.txt", "size": 7 } ] }""");
                if (uri.Host == "api.github.com" && uri.AbsolutePath.StartsWith(api + "/tags/", StringComparison.Ordinal))
                    return _releases.TryGetValue(uri.AbsolutePath[(api.Length + 6)..], out var release) ? Answer(release.ToJsonString()) : NotFound();
                if (uri.Host == "api.github.com" && uri.AbsolutePath == api)
                    return Answer(new JsonArray([.. _releases.Values.Select(static release => release.DeepClone())]).ToJsonString());
                if (uri.AbsoluteUri == "https://github.example/nightly-tag.txt") return Answer(Stable + "\n");
                if (uri.AbsoluteUri.StartsWith(Downloads, StringComparison.Ordinal) && _files.TryGetValue(uri.AbsoluteUri[Downloads.Length..], out var bytes))
                    return Task.FromResult(Range(request, bytes));
                return NotFound();
            }
        }

        private HttpResponseMessage Range(HttpRequestMessage request, byte[] bytes)
        {
            if (request.Headers.Range?.Ranges.FirstOrDefault() is not { } range) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            Interlocked.Increment(ref _rangeRequests);
            var from = range.From ?? 0;
            var to = Math.Min(range.To ?? bytes.Length - 1, bytes.Length - 1);
            var content = new ByteArrayContent(bytes, (int)from, (int)(to - from + 1));
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, bytes.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        }

        private static Task<HttpResponseMessage> Answer(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        private static Task<HttpResponseMessage> NotFound() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        // The name of the archive of the backend on this machine, as llama.cpp names it.
        private string AssetName(string tag)
        {
            var arch = HardwareProbe.CurrentArchitecture;
            if (OperatingSystem.IsWindows()) return $"llama-{tag}-bin-win-{backend}-{arch}.zip";
            return OperatingSystem.IsMacOS() ? $"llama-{tag}-bin-macos-{arch}.tar.gz" : $"llama-{tag}-bin-ubuntu-{arch}.tar.gz";
        }

        // An archive of the fake as a build: its program renamed llama-server beside its assembly, and its fake-llama.json,
        // in one folder as the archives of llama.cpp have them.
        private static byte[] Pack(string name, string folder, string settings)
        {
            const string assembly = "CodeAlta.Tests.FakeLlamaServer";
            var source = AppContext.BaseDirectory;
            var program = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
            var files = new List<(string Name, byte[] Bytes, bool Executable)>
            {
                (program, File.ReadAllBytes(Path.Combine(source, OperatingSystem.IsWindows() ? assembly + ".exe" : assembly)), true),
                ("fake-llama.json", Encoding.UTF8.GetBytes(settings), false),
            };
            foreach (var extension in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
            {
                var path = Path.Combine(source, assembly + extension);
                if (File.Exists(path)) files.Add((assembly + extension, File.ReadAllBytes(path), false));
            }

            using var memory = new MemoryStream();
            if (name.EndsWith(".zip", StringComparison.Ordinal))
            {
                using var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var file in files)
                {
                    using var entry = zip.CreateEntry(folder + "/" + file.Name, CompressionLevel.Fastest).Open();
                    entry.Write(file.Bytes);
                }
            }
            else
            {
                using var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true);
                using var tar = new TarWriter(gzip);
                foreach (var file in files)
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, folder + "/" + file.Name)
                    {
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                            | (file.Executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : UnixFileMode.None),
                        DataStream = new MemoryStream(file.Bytes),
                    });
                }
            }

            return memory.ToArray();
        }
    }

    /// <summary>A clock that moves when the test says; its timers (those of Task.Delay) fire then, on the thread pool.</summary>
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = start;
        private int _created;

        /// <summary>Gets how many timers were made so far.</summary>
        public int Created => Volatile.Read(ref _created);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            Interlocked.Increment(ref _created);
            return timer;
        }

        /// <summary>Waits until a timer was made after the count given: the loop of the host waits on the clock again.</summary>
        public async Task WaitForTimerAsync(int after)
        {
            var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (Created <= after)
            {
                if (DateTime.UtcNow > end) Assert.Fail("The model host did not wait on its clock.");
                await Task.Delay(20);
            }
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(timer => timer.DueAt <= _now).ToList();
                foreach (var timer in due) _timers.Remove(timer);
            }

            foreach (var timer in due) ThreadPool.QueueUserWorkItem(static timer => timer.Fire(), timer, preferLocal: false);
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan _period = Timeout.InfiniteTimeSpan;

            public DateTimeOffset DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                    _period = period;
                    if (dueTime == Timeout.InfiniteTimeSpan) return true;
                    DueAt = owner._now + dueTime;
                    owner._timers.Add(this);
                }

                return true;
            }

            public void Fire()
            {
                callback(state);
                if (_period != Timeout.InfiniteTimeSpan && _period > TimeSpan.Zero) Change(_period, _period);
            }

            public void Dispose()
            {
                lock (owner._gate) owner._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
