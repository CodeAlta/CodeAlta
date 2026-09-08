using System.ComponentModel;
using System.Globalization;
using CodeAlta.Hosting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaSingleInstanceGuardTests
{
    [TestMethod]
    public void GetDefaultLockFilePath_UsesAltaHomeDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.IsFalse(string.IsNullOrWhiteSpace(userProfile));
        Assert.AreEqual(
            Path.Combine(userProfile, ".alta", "alta.lock"),
            CodeAltaSingleInstanceGuard.GetDefaultLockFilePath());
    }

    [TestMethod]
    public void Acquire_WritesCurrentProcessIdToLockFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var lockFilePath = Path.Combine(directory, "alta.lock");

            using var guard = CodeAltaSingleInstanceGuard.Acquire(lockFilePath);

            Assert.AreEqual(lockFilePath, guard.LockFilePath);
            Assert.AreEqual(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), ReadSharedLockFile(lockFilePath).Trim());
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public void Acquire_WhenAlreadyLocked_ThrowsWithRunningProcessId()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var lockFilePath = Path.Combine(directory, "alta.lock");
            using var guard = CodeAltaSingleInstanceGuard.Acquire(lockFilePath);

            var exception = Assert.ThrowsExactly<CodeAltaAlreadyRunningException>(
                () => CodeAltaSingleInstanceGuard.Acquire(lockFilePath));

            Assert.AreEqual(Environment.ProcessId, exception.ProcessId);
            StringAssert.Contains(exception.Message, $"PID {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");
            StringAssert.Contains(exception.Message, "only one application instance per machine");
            StringAssert.Contains(exception.Message, "same sessions and shared application state");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public void Acquire_AfterDispose_CanAcquireAgain()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var lockFilePath = Path.Combine(directory, "alta.lock");
            using (CodeAltaSingleInstanceGuard.Acquire(lockFilePath))
            {
            }

            using var guard = CodeAltaSingleInstanceGuard.Acquire(lockFilePath);

            Assert.AreEqual(lockFilePath, guard.LockFilePath);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    public async Task Dispose_CanRunOnDifferentSessionThanAcquire()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var lockFilePath = Path.Combine(directory, "alta.lock");
            var guard = CodeAltaSingleInstanceGuard.Acquire(lockFilePath);

            await Task.Run(guard.Dispose).ConfigureAwait(false);

            using var reacquired = CodeAltaSingleInstanceGuard.Acquire(lockFilePath);
            Assert.AreEqual(lockFilePath, reacquired.LockFilePath);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [TestMethod]
    [DataRow(1, 0)]
    [DataRow(2, 0)]
    [DataRow(3, 0)]
    [DataRow(1, 123)]
    [DataRow(2, 123)]
    [DataRow(3, 123)]
    public void IsProcessRunning_ValidatesCallbacksBeforeInspection(int nullMask, int processId)
    {
        var calls = 0;
        Func<int, Probe?>? lookup = (nullMask & 1) != 0 ? null : _ => { calls++; return null; };
        Func<Probe, bool>? inspect = (nullMask & 2) != 0 ? null : _ => { calls++; return false; };

        // Deliberately invalid callbacks must be rejected even for a nonpositive PID.
        var error = Assert.ThrowsExactly<ArgumentNullException>(() =>
            CodeAltaSingleInstanceGuard.IsProcessRunning<Probe>(processId, lookup!, inspect!));

        Assert.AreEqual((nullMask & 1) != 0 ? "getProcessById" : "hasExited", error.ParamName);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow("lookup", "access")]
    [DataRow("lookup", "unsupported")]
    [DataRow("lookup", "invalid")]
    [DataRow("lookup", "unexpected")]
    [DataRow("inspection", "argument")]
    [DataRow("inspection", "access")]
    [DataRow("inspection", "unsupported")]
    [DataRow("inspection", "invalid")]
    [DataRow("inspection", "unexpected")]
    [DataRow("release", "argument")]
    [DataRow("release", "access")]
    [DataRow("release", "unsupported")]
    [DataRow("release", "invalid")]
    [DataRow("release", "unexpected")]
    public void IsProcessRunning_UnknownInspectionFailsClosed(string stage, string failure)
    {
        var error = failure switch
        {
            "argument" => new ArgumentException("not a missing-PID lookup"),
            "access" => new Win32Exception(5, "access denied"),
            "unsupported" => new NotSupportedException("unsupported inspection"),
            "invalid" => new InvalidOperationException("invalid inspection state"),
            "unexpected" => new Exception("unexpected failure"),
            _ => throw new AssertFailedException("Unknown row."),
        };
        var calls = new List<string>();
        var probe = new Probe(() =>
        {
            calls.Add("release");
            if (stage == "release") throw error;
        });
        var result = CodeAltaSingleInstanceGuard.IsProcessRunning<Probe>(123,
            _ => { calls.Add("lookup"); return stage == "lookup" ? throw error : probe; },
            _ => { calls.Add("inspection"); return stage == "inspection" ? throw error : true; });

        Assert.IsTrue(result);
        Assert.AreEqual(stage == "lookup" ? 0 : 1, probe.Disposals);
        CollectionAssert.AreEqual(stage == "lookup"
            ? new[] { "lookup" }
            : new[] { "lookup", "inspection", "release" }, calls);
    }

    [TestMethod]
    [DataRow("missing", false)]
    [DataRow("exited", false)]
    [DataRow("live", true)]
    [DataRow("null", true)]
    public void IsProcessRunning_OnlyLookupAbsenceOrObservedExitReturnsFalse(string scenario, bool expected)
    {
        var calls = new List<string>();
        var probe = new Probe(() => calls.Add("release"));
        var observedId = 0;
        var result = CodeAltaSingleInstanceGuard.IsProcessRunning<Probe>(123, id =>
        {
            observedId = id;
            calls.Add("lookup");
            return scenario switch
            {
                "missing" => throw new ArgumentException("missing positive PID"),
                "null" => null,
                _ => probe,
            };
        }, _ => { calls.Add("inspection"); return scenario == "exited"; });

        Assert.AreEqual(expected, result);
        Assert.AreEqual(123, observedId);
        var acquired = scenario is "live" or "exited";
        Assert.AreEqual(acquired ? 1 : 0, probe.Disposals);
        CollectionAssert.AreEqual(acquired
            ? new[] { "lookup", "inspection", "release" }
            : new[] { "lookup" }, calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(int.MinValue)]
    public void IsProcessRunning_InvalidPidDoesNotAuthorizeReclamation(int processId)
    {
        var calls = 0;
        var probe = new Probe(() => calls++);
        Assert.IsTrue(CodeAltaSingleInstanceGuard.IsProcessRunning<Probe>(processId,
            _ => { calls++; return probe; }, _ => { calls++; return true; }));
        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, probe.Disposals);
    }

    [TestMethod]
    [DataRow("live", false)]
    [DataRow("exited", false)]
    [DataRow("inspection-failure", false)]
    [DataRow("live", true)]
    [DataRow("exited", true)]
    [DataRow("inspection-failure", true)]
    public void IsProcessRunning_ReleasesTheExactProbeResource(string scenario, bool releaseFails)
    {
        var calls = new List<string>();
        var probe = new Probe(() =>
        {
            calls.Add("release");
            if (releaseFails) throw new ArgumentException("release failure is not lookup absence");
        });
        Probe? inspected = null;
        var result = CodeAltaSingleInstanceGuard.IsProcessRunning<Probe>(123,
            _ => { calls.Add("lookup"); return probe; }, value =>
            {
                inspected = value;
                calls.Add("inspection");
                if (scenario == "inspection-failure") throw new InvalidOperationException("inspection failure");
                return scenario == "exited";
            });

        Assert.AreEqual(releaseFails || scenario != "exited", result);
        Assert.AreSame(probe, inspected);
        Assert.AreEqual(1, probe.Disposals);
        CollectionAssert.AreEqual(new[] { "lookup", "inspection", "release" }, calls);
    }

    private sealed class Probe(Action release) : IDisposable
    {
        public int Disposals { get; private set; }

        public void Dispose()
        {
            Disposals++;
            release();
        }
    }

    private static string ReadSharedLockFile(string lockFilePath)
    {
        using var stream = new FileStream(lockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodeAlta.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
