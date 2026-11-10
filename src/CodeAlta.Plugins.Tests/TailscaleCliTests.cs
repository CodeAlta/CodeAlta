using CodeAlta.Plugins.PrivateNetworks;

namespace CodeAlta.Plugins.Tests;

/// <summary>The command line can be installed after the provider was created, without restarting CodeAlta.</summary>
[TestClass]
public sealed class TailscaleCliTests
{
    [TestMethod]
    public async Task Discovery_SeesAnInstallationAfterTheCliWasCreated()
    {
        string? path = null;
        var cli = new TailscaleCli(() => path);
        Assert.IsFalse(cli.IsInstalled);
        Assert.AreEqual(-1, (await cli.RunAsync([], default)).ExitCode);

        // Only a harmless shell command is run: no real Tailscale installation or profile is touched.
        path = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
            : "/bin/sh";
        Assert.IsTrue(cli.IsInstalled, "the same instance sees a newly installed command line");
        var result = await cli.RunAsync(OperatingSystem.IsWindows()
            ? ["/c", "echo", "tailscale-test"]
            : ["-c", "printf tailscale-test"], default);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("tailscale-test", result.Output.Trim());

        path = null;
        Assert.IsFalse(cli.IsInstalled, "a removed executable is not kept either");
        Assert.AreEqual(-1, (await cli.RunAsync([], default)).ExitCode);
    }

    [TestMethod]
    public async Task Run_RediscoversWithoutAnInstalledCheck()
    {
        string? path = null;
        var cli = new TailscaleCli(() => path);
        Assert.AreEqual(-1, (await cli.RunAsync([], default)).ExitCode);

        path = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
            : "/bin/sh";
        var result = await cli.RunAsync(OperatingSystem.IsWindows()
            ? ["/c", "echo", "tailscale-test"]
            : ["-c", "printf tailscale-test"], default);
        Assert.AreEqual(0, result.ExitCode, "RunAsync discovers installations even without reading IsInstalled");
        Assert.AreEqual("tailscale-test", result.Output.Trim());
    }
}
