using System.IO;
using System.Reflection;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ProgramThreadGuardTests
{
    [TestMethod]
    public void ThrowIfCurrentThreadIsMainThread_DoesNotThrow()
    {
        InvokeGuard(Environment.CurrentManagedThreadId);
    }

    [TestMethod]
    public void ThrowIfCurrentThreadIsWorkerThread_ThrowsInvalidOperationException()
    {
        var mainThreadId = Environment.CurrentManagedThreadId;
        var workerThreadId = 0;
        Exception? capturedException = null;

        var worker = new Thread(() =>
        {
            workerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                InvokeGuard(mainThreadId);
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        });
        worker.Start();
        Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)), "Timed out waiting for the worker thread to complete.");

        Assert.AreNotEqual(mainThreadId, workerThreadId);
        var exception = Assert.IsInstanceOfType<InvalidOperationException>(capturedException);
        StringAssert.Contains(exception.Message, "Program.RunAsync must start on the process main thread.");
        StringAssert.Contains(exception.Message, mainThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void CanStartPluginRuntimeBeforeConfigRecovery_MalformedGlobalConfig_ReturnsFalse()
    {
        var homeRoot = Path.Combine(Path.GetTempPath(), "CodeAlta.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(homeRoot);
        try
        {
            File.WriteAllText(Path.Combine(homeRoot, "config.toml"), "@");

            Assert.IsFalse(InvokeCanStartPluginRuntimeBeforeConfigRecovery(homeRoot));
        }
        finally
        {
            if (Directory.Exists(homeRoot))
            {
                Directory.Delete(homeRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CanStartPluginRuntimeBeforeConfigRecovery_MissingGlobalConfig_ReturnsTrue()
    {
        var homeRoot = Path.Combine(Path.GetTempPath(), "CodeAlta.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(homeRoot);
        try
        {
            Assert.IsTrue(InvokeCanStartPluginRuntimeBeforeConfigRecovery(homeRoot));
        }
        finally
        {
            if (Directory.Exists(homeRoot))
            {
                Directory.Delete(homeRoot, recursive: true);
            }
        }
    }

    private static void InvokeGuard(int mainThreadId)
    {
        var programType = typeof(CodeAltaCliOptions).Assembly.GetType("Program");
        Assert.IsNotNull(programType);
        var guardMethod = programType.GetMethod(
            "ThrowIfCurrentThreadIsNotMainThread",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(guardMethod);

        try
        {
            guardMethod.Invoke(null, [mainThreadId]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static bool InvokeCanStartPluginRuntimeBeforeConfigRecovery(string homeRoot)
    {
        var programType = typeof(CodeAltaCliOptions).Assembly.GetType("Program");
        Assert.IsNotNull(programType);
        var method = programType.GetMethod(
            "CanStartPluginRuntimeBeforeConfigRecovery",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        try
        {
            return (bool)method.Invoke(null, [homeRoot])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
