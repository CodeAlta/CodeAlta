using System.Diagnostics;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopNativeTests
{
    [TestMethod]
    [TestCategory("DesktopNative")]
    public async Task PackagedDesktopAndFixtures_RunOnAnExplicitGraphicalHost()
    {
        if (Environment.GetEnvironmentVariable("CODEALTA_DESKTOP_NATIVE_TESTS") != "1")
            Assert.Inconclusive("Real native tests are opt-in. Set CODEALTA_DESKTOP_NATIVE_TESTS=1 on a graphical qualification host; ordinary managed tests do not qualify native support.");
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            Assert.Inconclusive("This driver requires an interactive Windows desktop with WebView2. Other RIDs remain unqualified.");

        var script = Path.Combine(DesktopArchitectureTests.SourceRoot, "CodeAlta.Desktop.Tests", "Verify-DesktopPackage.ps1");
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail("Owned native qualification process exceeded twelve minutes.");
        }
        var output = await stdout;
        Console.WriteLine(output);
        Assert.AreEqual(0, process.ExitCode, output + Environment.NewLine + await stderr);
    }
}
