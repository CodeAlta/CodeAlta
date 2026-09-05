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
    public void ProgramSource_VerifiesRunAsyncStartsOnMainThread()
    {
        var programSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Program.cs"));

        Assert.IsTrue(programSource.Contains("var mainThreadId = Environment.CurrentManagedThreadId;", StringComparison.Ordinal));
        Assert.IsTrue(programSource.Contains("Program.ThrowIfCurrentThreadIsNotMainThread(mainThreadId);", StringComparison.Ordinal));
        Assert.IsTrue(programSource.Contains("Program.StartPluginRuntimeForCommandLine(args, CancellationToken.None);", StringComparison.Ordinal));
        Assert.IsTrue(programSource.Contains("return command.RunAsync(args).AsTask().GetAwaiter().GetResult();", StringComparison.Ordinal));
        Assert.IsTrue(programSource.Contains("ReportCommandLinePluginStartup(result, stopwatch.Elapsed, pluginBootstrapOptions);", StringComparison.Ordinal));
        Assert.IsTrue(programSource.Contains("LogManager.GetLogger(\"CodeAlta.Program\").Error(ex, \"Top-level exception\");", StringComparison.Ordinal));
        Assert.IsFalse(programSource.Contains("await Program.StartPluginRuntimeForCommandLineAsync", StringComparison.Ordinal));
        Assert.IsFalse(programSource.Contains("return await command.RunAsync(args)", StringComparison.Ordinal));
        Assert.IsTrue(programSource.Contains("internal partial class Program", StringComparison.Ordinal));
        Assert.IsTrue(programSource.Contains("internal static void ThrowIfCurrentThreadIsNotMainThread(int mainThreadId)", StringComparison.Ordinal));
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

    private static string GetCodeAltaSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidates = new[]
            {
                directory.FullName,
                Path.Combine(directory.FullName, "CodeAlta.Tui"),
                Path.Combine(directory.FullName, "src", "CodeAlta.Tui"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, "CodeAlta.Tui.csproj")) &&
                    Directory.Exists(Path.Combine(candidate, "App")) &&
                    Directory.Exists(Path.Combine(candidate, "Views")))
                {
                    return candidate;
                }
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate the CodeAlta.Tui source directory from the test output path.");
        return null!;
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
