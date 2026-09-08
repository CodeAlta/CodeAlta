using System.ComponentModel;
using System.Diagnostics;
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
    public void IsProcessRunning_WhenHasExitedThrows_ReturnsFalse()
    {
        var isRunning = CodeAltaSingleInstanceGuard.IsProcessRunning(
            123,
            _ => new Process(),
            _ => throw new Win32Exception(5));

        Assert.IsFalse(isRunning);
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
