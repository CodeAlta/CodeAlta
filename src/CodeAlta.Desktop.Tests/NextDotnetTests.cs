using CodeAlta.Desktop.Rpc;
using CodeAlta.Hosting;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class NextDotnetTests
{
    [TestMethod]
    public void ReleasedSdk11_IsWhatMakesTheInstallationReady()
    {
        using var root = new TemporaryRoot();
        Assert.IsFalse(NextDotnet.HasSdk(root.Path), "an installation without SDK");
        root.Sdk("10.0.404");
        root.Sdk("11.0.100-rc.2.25502.107");
        Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "sdk", "11.0.101"));
        Assert.IsFalse(NextDotnet.HasSdk(root.Path), "a .NET 10 SDK, a preview of 11 and a folder without its files do not install a released .NET 11 tool");
        root.Sdk("11.0.100");
        Assert.IsTrue(NextDotnet.HasSdk(root.Path));
    }

    [TestMethod]
    public void State_SaysWhereTheSdkIsInstalled_AndWhetherTheSystemAsks()
    {
        using var root = new TemporaryRoot();
        Assert.AreEqual(new NextDotnetState("unavailable", null), new NextDotnet(null, _ => true, Fails).State(), "a published app has no .NET installation of its own");
        Assert.AreEqual(new NextDotnetState("missing", "local"), new NextDotnet(root.Path, _ => true, Fails).State());
        // A system installation of Linux belongs to its package manager.
        Assert.AreEqual(new NextDotnetState(OperatingSystem.IsLinux() ? "manual" : "missing", "system"), new NextDotnet(root.Path, _ => false, Fails).State());
        root.Sdk("11.0.100");
        Assert.AreEqual(new NextDotnetState("installed", "local"), new NextDotnet(root.Path, _ => true, Fails).State());
        Assert.AreEqual(new NextDotnetState("installed", "system"), new NextDotnet(root.Path, _ => false, Fails).State());
    }

    [TestMethod]
    public async Task Install_RunsOnce_ForEveryoneWhoAsksWhileItRuns()
    {
        using var root = new TemporaryRoot();
        var runs = new List<NextDotnetInstall>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var next = new NextDotnet(root.Path, _ => true, async (install, _) =>
        {
            lock (runs) runs.Add(install);
            started.TrySetResult();
            await release.Task;
            root.Sdk("11.0.100");
            return new(NextDotnetResult.Completed, null);
        });

        var first = next.InstallAsync(CancellationToken.None);
        var second = next.InstallAsync(CancellationToken.None);
        await started.Task.WithinAsync(TestTime.Seconds(5));
        Assert.AreEqual("installing", next.State().Status);
        release.SetResult();
        Assert.AreEqual(new NextDotnetState("installed", "local"), await first.WithinAsync(TestTime.Seconds(5)));
        Assert.AreEqual(new NextDotnetState("installed", "local"), await second.WithinAsync(TestTime.Seconds(5)));
        Assert.AreEqual(1, runs.Count);
        Assert.AreEqual(root.Path, runs[0].Root);
        Assert.AreEqual(NextDotnetKind.Script, runs[0].Kind, "a folder the user writes in takes the script, without a question of the system");
        // Nothing more to install.
        Assert.AreEqual("installed", (await next.InstallAsync(CancellationToken.None)).Status);
        Assert.AreEqual(1, runs.Count);
    }

    [TestMethod]
    public async Task Install_ThatFailsOrIsDeclined_SaysSo_AndCanBeAskedAgain()
    {
        using var root = new TemporaryRoot();
        var outcomes = new Queue<NextDotnetOutcome>([
            new(NextDotnetResult.Failed, "powershell.exe ended with 1: no network"),
            new(NextDotnetResult.Canceled, null),
            // An installer that says it succeeded and left no SDK did not install it.
            new(NextDotnetResult.Completed, null),
        ]);
        var next = new NextDotnet(root.Path, _ => false, (_, _) => Task.FromResult(outcomes.Dequeue()));
        if (OperatingSystem.IsLinux())
        {
            Assert.AreEqual("manual", (await next.InstallAsync(CancellationToken.None)).Status, "Linux: nothing is installed in a system installation");
            Assert.AreEqual(3, outcomes.Count);
            return;
        }

        var failed = await next.InstallAsync(CancellationToken.None);
        Assert.AreEqual("failed", failed.Status);
        Assert.AreEqual("powershell.exe ended with 1: no network", failed.Detail);
        Assert.AreEqual(failed, next.State(), "the failure stays until it is asked again");
        Assert.AreEqual(new NextDotnetState("canceled", "system"), await next.InstallAsync(CancellationToken.None));
        Assert.AreEqual("failed", (await next.InstallAsync(CancellationToken.None)).Status);
    }

    [TestMethod]
    public void Plan_InstallsInTheFolderOfCodeAltasDotnet()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\Users\someone\AppData\Local\Microsoft\dotnet" : "/home/someone/.dotnet";
        var local = NextDotnet.Plan(root, "local");
        Assert.AreEqual(NextDotnetKind.Script, local.Kind);
        var (program, arguments) = NextDotnet.Command(local, "install-script");
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual("https://dot.net/v1/dotnet-install.ps1", local.Url);
            Assert.AreEqual("powershell.exe", program);
            CollectionAssert.IsSubsetOf(new[] { "-File", "install-script", "-Channel", "11.0", "-InstallDir", root, "-NoPath", "-SkipNonVersionedFiles" }, arguments.ToArray());
        }
        else
        {
            Assert.AreEqual("https://dot.net/v1/dotnet-install.sh", local.Url);
            Assert.AreEqual("/bin/bash", program);
            CollectionAssert.IsSubsetOf(new[] { "install-script", "--channel", "11.0", "--install-dir", root, "--no-path", "--skip-non-versioned-files" }, arguments.ToArray());
        }

        // The architecture of this process names the installer, as the installation this process runs on.
        Assert.IsTrue(local.Architecture is "x64" or "arm64" or "x86", local.Architecture);
        if (OperatingSystem.IsLinux()) return;
        var system = NextDotnet.Plan(root, "system");
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(NextDotnetKind.WindowsInstaller, system.Kind);
            Assert.AreEqual($"https://aka.ms/dotnet/11.0/dotnet-sdk-win-{system.Architecture}.exe", system.Url);
            Assert.AreEqual(("installer.exe", "/install /quiet /norestart"), (NextDotnet.Command(system, "installer.exe").Program, string.Join(' ', NextDotnet.Command(system, "installer.exe").Arguments)));
        }
        else
        {
            Assert.AreEqual(NextDotnetKind.MacInstaller, system.Kind);
            Assert.AreEqual($"https://aka.ms/dotnet/11.0/dotnet-sdk-osx-{system.Architecture}.pkg", system.Url);
            var (osascript, script) = NextDotnet.Command(system, "/tmp/a \"b\".pkg");
            Assert.AreEqual("/usr/bin/osascript", osascript);
            StringAssert.Contains(script[1], "quoted form of \"/tmp/a \\\"b\\\".pkg\"");
            StringAssert.Contains(script[1], "with administrator privileges");
        }
    }

    [TestMethod]
    public async Task AppUpdate_TellsThePageAboutTheNextRuntime_AndInstallsIt()
    {
        using var root = new TemporaryRoot();
        var opened = new List<string>();
        await using var service = new AppUpdateService("1.2.0", (package, current, prerelease, _) =>
            Task.FromResult(new CodeAltaNuGetUpdateCheckResult(package, current, current, true, false, prerelease)), address => { opened.Add(address); return true; })
        {
            NextRuntime = new NextDotnet(root.Path, _ => true, (_, _) => { root.Sdk("11.0.100"); return Task.FromResult(new NextDotnetOutcome(NextDotnetResult.Completed, null)); }),
        };
        Assert.AreEqual(new AppNextRuntimeResponse("missing", "local", "11", null), service.NextRuntimeStatus(new()));
        Assert.AreEqual(new AppNextRuntimeResponse("installed", "local", "11", null), await service.InstallNextRuntimeAsync(new(), CancellationToken.None));
        Assert.AreEqual("installed", service.NextRuntimeStatus(new()).Status);
        Assert.AreEqual("ok", service.OpenNextRuntimeDownload(new()).Status);
        CollectionAssert.AreEqual(new[] { "https://dotnet.microsoft.com/en-us/download/dotnet/11.0" }, opened);

        // Automation and builds without an installation of their own propose nothing.
        await using var absent = new AppUpdateService();
        Assert.AreEqual(new AppNextRuntimeResponse("unavailable", null, "11", null), absent.NextRuntimeStatus(new()));
        Assert.AreEqual("unavailable", (await absent.InstallNextRuntimeAsync(new(), CancellationToken.None)).Status);
    }

    private static Task<NextDotnetOutcome> Fails(NextDotnetInstall install, CancellationToken cancellationToken) => throw new InvalidOperationException("not installed");

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("codealta-next-dotnet-").FullName;

        public void Sdk(string version)
        {
            var folder = System.IO.Path.Combine(Path, "sdk", version);
            Directory.CreateDirectory(folder);
            File.WriteAllText(System.IO.Path.Combine(folder, "dotnet.dll"), "");
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
