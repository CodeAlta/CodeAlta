using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;

namespace CodeAlta.Hosting.Tests;

/// <summary>Opt-in qualification of linked guard source, not the shipped Hosting binary.</summary>
/// <remarks>
/// Requires an explicitly supplied, parent-audited prebuilt apphost and existing task-owned root.
/// No executable discovery, build, application startup, default root or HOME substitution occurs.
/// Local OS/runtime evidence only: no network filesystem, permission-denial, alias or deterministic
/// stale/delete/release-interleaving proof. The persistent mode is an experiment, not production policy.
/// The test assembly's existing writerless logging is separate from the BCL-only helper process.
/// Async waits are bounded; synchronous OS launch, metadata, kill and pipe-close calls cannot be
/// preempted by cancellation. A hung OS call requires an external test-runner supervisor.
/// </remarks>
[TestClass]
[DoNotParallelize]
[TestCategory("LockProcessQualification")]
public sealed class CodeAltaSingleInstanceGuardProcessTests
{
    /// <summary>Rejects malformed explicit arguments without admission or lock creation.</summary>
    [TestMethod]
    [DataRow("missing")]
    [DataRow("relative-root")]
    [DataRow("relative-lock")]
    [DataRow("outside")]
    [DataRow("noncanonical")]
    [DataRow("duplicate")]
    [DataRow("unknown")]
    [DataRow("invalid-mode")]
    [DataRow("invalid-deadline")]
    [DataRow("invalid-nonce")]
    [DataRow("missing-root")]
    public Task Probe_RejectsMissingRelativeOrOutsideTaskRootPaths(string invalid)
        => RunScenarioAsync(async scenario =>
        {
            var probe = scenario.Start("guard", invalid);
            await probe.ExpectExitAsync(2);
            Assert.AreEqual("", await probe.OutputAsync());
            StringAssert.StartsWith(await probe.ErrorAsync(), "ERROR arguments");
            Assert.IsFalse(File.Exists(Path.Combine(scenario.Root, "alta.lock")));
            Assert.IsFalse(Directory.Exists(scenario.Root + "-outside"), "Rejected sibling path was created: " + scenario.Root + "-outside");
        });

    /// <summary>A live guard owner rejects a separate helper.</summary>
    [TestMethod]
    public Task Guard_HeldOwnerRejectsIndependentContender()
        => RunScenarioAsync(async scenario =>
        {
            var owner = await scenario.AcquireAsync("guard");
            var contender = await scenario.ReadyAsync("guard");
            await contender.BeginAsync();
            Assert.IsFalse(await contender.AdmissionAsync());
            owner.AssertRunning();
            await contender.ReleaseAsync();
            await owner.ReleaseAsync();
        });

    /// <summary>A fully released guard permits a separate successor.</summary>
    [TestMethod]
    public Task Guard_ReleasedOwnerAllowsIndependentSuccessor()
        => RunScenarioAsync(async scenario =>
        {
            var owner = await scenario.AcquireAsync("guard");
            await owner.ReleaseAsync();
            var successor = await scenario.AcquireAsync("guard");
            await successor.ReleaseAsync();
        });

    /// <summary>A confirmed terminated guard owner permits stale-file recovery.</summary>
    [TestMethod]
    public Task Guard_ReapedOwnerAllowsStaleRecovery()
        => RunScenarioAsync(async scenario =>
        {
            var owner = await scenario.AcquireAsync("guard");
            await owner.TerminateAsync();
            var successor = await scenario.AcquireAsync("guard");
            await successor.ReleaseAsync();
        });

    /// <summary>Offers bounded opportunities to reproduce overlapping stale-file ownership reports.</summary>
    [TestMethod]
    public Task Guard_StaleContendersNeverReportOverlappingOwnership()
        => RunScenarioAsync(async scenario =>
        {
            // Four bounded reproduction opportunities, NOT deterministic read/delete interleavings.
            for (var round = 0; round < 4; round++)
            {
                var seed = await scenario.AcquireAsync("guard");
                await seed.TerminateAsync();
                var first = await scenario.ReadyAsync("guard");
                var second = await scenario.ReadyAsync("guard");
                await first.BeginAsync();
                await second.BeginAsync();
                var firstAdmission = first.AdmissionAsync();
                var secondAdmission = second.AdmissionAsync();
                var admissions = await Task.WhenAll(firstAdmission, secondAdmission);
                first.AssertRunning();
                second.AssertRunning();
                Assert.AreEqual(1, admissions.Count(static acquired => acquired), $"Round {round}: expected exactly one retained owner.");
                await first.ReleaseAsync();
                await second.ReleaseAsync();
            }
        });

    /// <summary>Probes the experimental no-unlink primitive without adopting it.</summary>
    [TestMethod]
    public Task PersistentProbe_RejectsContenderAndRecoversAfterOwnerTermination()
        => RunScenarioAsync(async scenario =>
        {
            var owner = await scenario.AcquireAsync("persistent-probe");
            var contender = await scenario.ReadyAsync("persistent-probe");
            await contender.BeginAsync();
            Assert.IsFalse(await contender.AdmissionAsync());
            owner.AssertRunning();
            await contender.ReleaseAsync();
            await owner.TerminateAsync();
            var successor = await scenario.AcquireAsync("persistent-probe");
            await successor.ReleaseAsync();
        });

    private static async Task RunScenarioAsync(Func<Scenario, Task> body)
    {
        var scenario = Scenario.Create();
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            var bodyTask = body(scenario);
            await bodyTask;
        }
        catch (Exception error)
        {
            primary = error;
        }
        finally
        {
            try { cleanup.AddRange(await scenario.CleanupAsync()); }
            catch (Exception error) { cleanup.Add(error); }
        }
        if (primary is not null)
        {
            if (cleanup.Count != 0) throw new AggregateException("Scenario failed; cleanup also failed.", [primary, .. cleanup]);
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanup.Count != 0) throw new AggregateException("Lock qualification cleanup failed.", cleanup);
    }

    private sealed class Scenario(string apphost, string root)
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));
        private readonly List<OwnedProbe> _probes = [];
        public string Root { get; } = root;

        public static Scenario Create()
        {
            var apphost = Environment.GetEnvironmentVariable("CODEALTA_LOCK_PROBE_APPHOST");
            var parentRoot = Environment.GetEnvironmentVariable("CODEALTA_LOCK_PROBE_ROOT");
            if (string.IsNullOrWhiteSpace(apphost) || string.IsNullOrWhiteSpace(parentRoot))
                Assert.Inconclusive("Lock process qualification is opt-in: supply CODEALTA_LOCK_PROBE_APPHOST (audited prebuilt absolute apphost) and CODEALTA_LOCK_PROBE_ROOT (existing task-owned absolute root). Nothing was qualified.");
            ValidateExistingPath(apphost!, directory: false);
            ValidateExistingPath(parentRoot!, directory: true);
            var expectedName = "CodeAlta.Tests.LockProbe" + (OperatingSystem.IsWindows() ? ".exe" : "");
            Assert.AreEqual(expectedName, Path.GetFileName(apphost), "Only the explicitly audited LockProbe apphost is permitted.");
            var root = Path.Combine(parentRoot!, "lock-probe-" + Guid.NewGuid().ToString("N"));
            Assert.IsFalse(Directory.Exists(root) || File.Exists(root), "Scenario path must be new.");
            var scenario = new Scenario(apphost!, root);
            try
            {
                Directory.CreateDirectory(root);
                return scenario;
            }
            catch
            {
                scenario._deadline.Dispose();
                throw;
            }
        }

        public OwnedProbe Start(string mode, string? invalid = null)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            var probe = new OwnedProbe(apphost, Root, mode, _deadline.Token, invalid);
            _probes.Add(probe); // Ownership precedes Process.Start and every subsequent fallible operation.
            probe.Start();
            _deadline.Token.ThrowIfCancellationRequested();
            return probe;
        }

        public async Task<OwnedProbe> ReadyAsync(string mode)
        {
            var probe = Start(mode);
            await probe.ReadyAsync();
            return probe;
        }

        public async Task<OwnedProbe> AcquireAsync(string mode)
        {
            var probe = await ReadyAsync(mode);
            await probe.BeginAsync();
            Assert.IsTrue(await probe.AdmissionAsync(), "Expected acquisition in the isolated scenario.");
            return probe;
        }

        public async Task<List<Exception>> CleanupAsync()
        {
            var errors = new List<Exception>();
            try { _deadline.Cancel(); }
            catch (Exception error) { errors.Add(error); }
            // Start independent cleanup bounds for every retained child before awaiting the group.
            var cleanups = _probes.Select(static probe => probe.CleanupAsync()).ToArray();
            var results = await Task.WhenAll(cleanups);
            foreach (var result in results) errors.AddRange(result);
            if (_probes.All(static probe => probe.Joined))
            {
                _deadline.Dispose();
                try
                {
                    ValidateExistingPath(Root, directory: true);
                    // No recursion/enumeration: retain unknown files and report any nonempty directory.
                    File.Delete(Path.Combine(Root, "alta.lock"));
                    Directory.Delete(Root, recursive: false);
                }
                catch (Exception error) { errors.Add(new IOException("Retained scenario root: " + Root, error)); }
            }
            else
            {
                errors.Add(new IOException("Termination or task completion unconfirmed; retain root " + Root));
            }
            return errors;
        }
    }

    private static void ValidateExistingPath(string value, bool directory)
    {
        Assert.IsTrue(Path.IsPathFullyQualified(value), "Explicit absolute path required.");
        Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), value, "Canonical path required.");
        Assert.IsFalse(value.StartsWith("\\\\", StringComparison.Ordinal), "UNC/device paths are not qualified.");
        foreach (var part in value[Path.GetPathRoot(value)!.Length..].Split(Path.DirectorySeparatorChar))
        {
            Assert.IsFalse(part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':'), "Ambiguous path component.");
            if (OperatingSystem.IsWindows())
            {
                var stem = part.Split('.')[0].ToUpperInvariant();
                Assert.IsFalse(stem is "CON" or "PRN" or "AUX" or "NUL" ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'), "Device path component.");
            }
        }
        FileSystemInfo current = directory ? new DirectoryInfo(value) : new FileInfo(value);
        Assert.IsTrue(current.Exists, "Explicit pre-existing path required: " + value);
        Assert.IsNotNull(directory ? ((DirectoryInfo)current).Parent : ((FileInfo)current).Directory, "Volume root is not a task root.");
        while (true)
        {
            Assert.IsFalse((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null, "Linked paths are not supported.");
            var parent = current is DirectoryInfo folder ? folder.Parent : ((FileInfo)current).Directory;
            if (parent is null) break;
            current = parent;
        }
        // Metadata checks cannot defend hard links or concurrent namespace substitution. Root ownership
        // and the apphost/runtime launch environment must be audited by the caller before opting in.
    }

    private sealed class OwnedProbe
    {
        private readonly Process _process;
        private readonly CancellationToken _scenarioToken;
        private readonly CancellationTokenSource _readerStop = new();
        private readonly List<Task> _operations = [];
        private readonly Channel<string> _lines = Channel.CreateBounded<string>(8);
        private readonly string _nonce = Guid.NewGuid().ToString("N");
        private readonly Stopwatch _startup = new();
        private readonly string _root;
        private Task? _exit;
        private Task<string>? _stdout;
        private Task<string>? _stderr;
        private Task? _inputOperation;
        private bool _started;
        private bool _startAttempted;
        private bool _inputClosed;
        private bool? _acquired;
        private int? _ownedPid;
        public bool Joined { get; private set; }

        public OwnedProbe(string apphost, string root, string mode, CancellationToken scenarioToken, string? invalid)
        {
            _root = root;
            _scenarioToken = scenarioToken;
            var start = new ProcessStartInfo(apphost)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Child-only environment edits; no HOME replacement or global mutation. Avoid ordinary
            // managed startup-hook/profiler injection; this is not a sandbox for an unaudited executable.
            foreach (var key in new[] { "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "CORECLR_ENABLE_PROFILING", "COR_ENABLE_PROFILING" })
                start.Environment.Remove(key);
            start.Environment["DOTNET_EnableDiagnostics"] = "0";
            var arguments = new List<string>
            {
                "--task-root", root, "--lock-path", Path.Combine(root, "alta.lock"),
                "--mode", mode, "--deadline-ms", "30000", "--nonce", _nonce,
            };
            switch (invalid)
            {
                case null: break;
                case "missing": arguments.RemoveRange(0, 2); break;
                case "relative-root": arguments[1] = "."; break;
                case "relative-lock": arguments[3] = "alta.lock"; break;
                case "outside": arguments[3] = Path.Combine(root + "-outside", "alta.lock"); break;
                case "noncanonical": arguments[3] = Path.Combine(root, ".", "alta.lock"); break;
                case "duplicate": arguments[8] = "--mode"; break;
                case "unknown": arguments[0] = "--unknown"; break;
                case "invalid-mode": arguments[5] = "application"; break;
                case "invalid-deadline": arguments[7] = "0"; break;
                case "invalid-nonce": arguments[9] = "bad\nnonce"; break;
                case "missing-root": arguments[1] = Path.Combine(root, "missing"); arguments[3] = Path.Combine(arguments[1], "alta.lock"); break;
                default: throw new AssertFailedException("Unknown invalid-argument row.");
            }
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            _process = new Process { StartInfo = start };
        }

        public void Start()
        {
            _scenarioToken.ThrowIfCancellationRequested();
            _startAttempted = true;
            _startup.Start();
            _started = _process.Start();
            if (!_started) throw new InvalidOperationException("Helper did not start.");
            _stdout = Track(ReadOutputAsync(_process.StandardOutput, protocol: true));
            _stderr = Track(ReadOutputAsync(_process.StandardError, protocol: false));
            _exit = Track(_process.WaitForExitAsync());
            _ownedPid = _process.Id; // Never obtain termination identity from a lock file or wire PID.
            // A synchronous OS launch cannot be preempted. Fail on a slow return rather than hide
            // an outstanding native launch behind a detached Task.Run continuation.
            if (_startup.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException("Helper startup exceeded five seconds.");
        }

        public async Task ReadyAsync()
        {
            var remaining = TimeSpan.FromSeconds(5) - _startup.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Helper READY exceeded the startup budget.");
            var line = Track(_lines.Reader.ReadAsync(_scenarioToken).AsTask());
            Assert.AreEqual("READY " + _nonce, await line.WaitAsync(remaining, _scenarioToken));
        }
        public Task BeginAsync() => SendAsync("BEGIN");

        public async Task<bool> AdmissionAsync()
        {
            var line = await LineAsync();
            if (line == "ACQUIRED " + _nonce)
            {
                _acquired = true;
                return true;
            }
            var prefix = "BUSY " + _nonce + " ";
            Assert.IsTrue(line.StartsWith(prefix, StringComparison.Ordinal), "Unexpected admission: " + line);
            var diagnostic = line[prefix.Length..];
            Assert.IsTrue(diagnostic == "unknown" || (int.TryParse(diagnostic, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0));
            _acquired = false;
            return false;
        }

        public void AssertRunning() => Assert.IsFalse(_process.HasExited, "Helper exited while expected to retain its scope.");

        public async Task ReleaseAsync()
        {
            var acquired = _acquired ?? throw new AssertFailedException("Admission must be observed before normal release.");
            await SendAsync("RELEASE");
            Assert.AreEqual("RELEASED " + _nonce, await LineAsync());
            await ExpectExitAsync(acquired ? 0 : 10);
        }

        public async Task TerminateAsync()
        {
            AssertRunning();
            _process.Kill(); // Exactly this retained helper; the helper owns no descendants.
            await ObserveAsync(_exit!);
            await ObserveAsync(Task.WhenAll(_stdout!, _stderr!));
        }

        public async Task ExpectExitAsync(int code)
        {
            await ObserveAsync(_exit!);
            await ObserveAsync(Task.WhenAll(_stdout!, _stderr!));
            Assert.AreEqual(code, _process.ExitCode, await ErrorAsync());
            if (code is 0 or 10)
            {
                Assert.AreEqual("", await ErrorAsync(), "Successful protocol must not conceal helper errors.");
                Assert.IsFalse(_lines.Reader.TryRead(out _), "Unexpected extra protocol output.");
            }
        }

        public Task<string> OutputAsync() => ObserveAsync(_stdout!);
        public Task<string> ErrorAsync() => ObserveAsync(_stderr!);

        private Task<string> LineAsync() => ObserveAsync(_lines.Reader.ReadAsync(_scenarioToken).AsTask());

        private async Task SendAsync(string command)
        {
            _scenarioToken.ThrowIfCancellationRequested();
            _inputOperation = Track(_process.StandardInput.WriteLineAsync(command));
            await ObserveAsync(_inputOperation);
            _inputOperation = Track(_process.StandardInput.FlushAsync());
            await ObserveAsync(_inputOperation);
        }

        private T Track<T>(T task) where T : Task
        {
            if (!_operations.Contains(task)) _operations.Add(task);
            return task;
        }

        private async Task ObserveAsync(Task task)
            => await Track(task).WaitAsync(TimeSpan.FromSeconds(5), _scenarioToken);

        private async Task<T> ObserveAsync<T>(Task<T> task)
            => await Track(task).WaitAsync(TimeSpan.FromSeconds(5), _scenarioToken);

        private async Task<string> ReadOutputAsync(StreamReader reader, bool protocol)
        {
            var output = new StringBuilder();
            var line = new StringBuilder();
            var buffer = new char[256];
            Exception? failure = null;
            try
            {
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), _readerStop.Token);
                    if (count == 0) break;
                    if (output.Length + count > 32768) throw new InvalidDataException("Helper output exceeded 32768 characters per stream.");
                    output.Append(buffer, 0, count);
                    for (var index = 0; index < count; index++)
                    {
                        var character = buffer[index];
                        if (character == '\n')
                        {
                            if (line.Length > 0 && line[^1] == '\r') line.Length--;
                            if (protocol && !_lines.Writer.TryWrite(line.ToString())) throw new InvalidDataException("Helper protocol queue exceeded eight lines.");
                            line.Clear();
                        }
                        else
                        {
                            if (line.Length == 4096) throw new InvalidDataException("Helper output line exceeded 4096 characters.");
                            line.Append(character);
                        }
                    }
                }
                if (protocol && line.Length != 0) throw new InvalidDataException("Truncated helper protocol line.");
                return output.ToString();
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                if (protocol) _lines.Writer.TryComplete(failure);
            }
        }

        public async Task<List<Exception>> CleanupAsync()
        {
            var errors = new List<Exception>();
            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception error) { errors.Add(error); }
            }

            if (!_started)
            {
                if (!_startAttempted)
                {
                    Attempt(_process.Dispose);
                    Attempt(_readerStop.Dispose);
                    Joined = true;
                    return errors;
                }
                // A throwing Start must not be silently treated as a proven absence of a child.
                Attempt(() => { _ownedPid = _process.Id; _started = true; });
                if (!_started)
                {
                    errors.Add(new IOException("Launch outcome unconfirmed (owned PID unavailable); retain " + _root));
                    return errors;
                }
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Attempt(() => _ownedPid ??= _process.Id);
            Attempt(() => _stdout ??= Track(ReadOutputAsync(_process.StandardOutput, protocol: true)));
            Attempt(() => _stderr ??= Track(ReadOutputAsync(_process.StandardError, protocol: false)));
            Attempt(() => _exit ??= Track(_process.WaitForExitAsync()));
            try
            {
                // Never race a second StreamWriter operation with an outstanding original write.
                if (!_process.HasExited && _inputOperation is not { IsCompleted: false })
                {
                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    grace.CancelAfter(TimeSpan.FromSeconds(1));
                    _inputOperation = Track(_process.StandardInput.WriteLineAsync("RELEASE"));
                    await _inputOperation.WaitAsync(grace.Token);
                    _inputOperation = Track(_process.StandardInput.FlushAsync());
                    await _inputOperation.WaitAsync(grace.Token);
                    CloseInput();
                    if (_exit is not null) await _exit.WaitAsync(grace.Token);
                }
            }
            catch (Exception error) { errors.Add(error); }

            // Each obligation is independent: a broken stdin/reader must not prevent the owned kill/join.
            Attempt(CloseInput);
            Attempt(() => { if (!_process.HasExited) _process.Kill(); });
            var joinedOperations = Task.WhenAll(_operations.ToArray());
            try { await joinedOperations.WaitAsync(TimeSpan.FromSeconds(2), deadline.Token); }
            catch (Exception error)
            {
                // Completion racing the catch must not erase an observed timeout/cancellation.
                if (error is TimeoutException or OperationCanceledException || !joinedOperations.IsCompleted) errors.Add(error);
            }
            if (!joinedOperations.IsCompleted)
            {
                Attempt(_readerStop.Cancel);
                Attempt(() => _process.StandardOutput.Dispose());
                Attempt(() => _process.StandardError.Dispose());
                try { await joinedOperations.WaitAsync(deadline.Token); }
                catch (Exception error)
                {
                    if (error is TimeoutException or OperationCanceledException || !joinedOperations.IsCompleted) errors.Add(error);
                }
            }
            Attempt(() => Joined = _process.HasExited && _exit is not null && _stdout is not null && _stderr is not null && joinedOperations.IsCompleted);

            // Observe faults on the original tasks, even if a WaitAsync wrapper timed out earlier.
            foreach (var task in _operations)
                if (task.Exception is { } error) errors.Add(error);
            _ = joinedOperations.Exception;
            if (Joined)
            {
                Attempt(_process.Dispose);
                Attempt(_readerStop.Dispose);
            }
            else
            {
                // Do not claim termination, dispose live-task cancellation ownership, or erase the root.
                errors.Add(new IOException($"Unconfirmed owned helper PID {_ownedPid?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; retain {_root}."));
            }
            return errors;
        }

        private void CloseInput()
        {
            if (_inputClosed) return;
            _inputClosed = true;
            // Close the pipe without a synchronous StreamWriter flush racing an outstanding write.
            _process.StandardInput.BaseStream.Dispose();
        }
    }
}
