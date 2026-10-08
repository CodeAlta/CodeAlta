using CodeAlta.Desktop.Rpc;
using CodeAlta.Hosting;
using NuGet.Versioning;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopAppUpdateTests
{
    [TestMethod]
    public async Task NewerVersion_ComesWithTheCommandAndTheReleaseNotes()
    {
        var checks = 0;
        var opened = new List<string>();
        using var service = new AppUpdateService("1.2.0+abc123", (package, current, prerelease, _) =>
        {
            checks++;
            Assert.AreEqual("CodeAlta", package);
            Assert.AreEqual("1.2.0", current.ToNormalizedString());
            Assert.IsFalse(prerelease, "a release looks for releases only");
            return Task.FromResult(new CodeAltaNuGetUpdateCheckResult(package, current, NuGetVersion.Parse("1.3.0"), true, true, prerelease));
        }, address => { opened.Add(address); return true; });

        service.Start();
        var update = await service.CheckAsync(new(), CancellationToken.None);
        Assert.AreEqual("available", update.Status);
        Assert.AreEqual("1.2.0", update.CurrentVersion);
        Assert.AreEqual("1.3.0", update.LatestVersion);
        Assert.AreEqual("dotnet tool update -g CodeAlta", update.Command);
        Assert.AreEqual("https://github.com/CodeAlta/CodeAlta/releases/tag/1.3.0", update.ReleaseNotes);

        // The page opens the notes through the host, which opens its own address and no other.
        Assert.AreEqual("ok", (await service.OpenReleaseNotesAsync(new(), CancellationToken.None)).Status);
        CollectionAssert.AreEqual(new[] { "https://github.com/CodeAlta/CodeAlta/releases/tag/1.3.0" }, opened);
        // One look at nuget.org for a while, however often the page asks.
        _ = await service.CheckAsync(new(), CancellationToken.None);
        _ = await service.CheckAsync(new(Refresh: true), CancellationToken.None);
        Assert.AreEqual(1, checks);
    }

    [TestMethod]
    public async Task ApplicationThatKeepsRunning_ChecksAgainAsTimePasses()
    {
        // 1.2.0 is published while a 1.1.1 started the evening before is still running.
        var time = new ManualTime();
        var published = "1.1.1";
        var checks = 0;
        using var service = new AppUpdateService("1.1.1", (package, current, prerelease, _) =>
        {
            checks++;
            var latest = NuGetVersion.Parse(published);
            return Task.FromResult(new CodeAltaNuGetUpdateCheckResult(package, current, latest, true, latest > current, prerelease));
        }, _ => true) { Time = time };

        Assert.AreEqual("latest", (await service.CheckAsync(new(), CancellationToken.None)).Status);
        published = "1.2.0";
        time.Advance(AppUpdateService.Period - TimeSpan.FromSeconds(1));
        Assert.AreEqual("latest", (await service.CheckAsync(new(), CancellationToken.None)).Status, "the page asks often: nuget.org is not asked each time");
        Assert.AreEqual(1, checks);
        time.Advance(TimeSpan.FromSeconds(1));
        var update = await service.CheckAsync(new(), CancellationToken.None);
        Assert.AreEqual(("available", "1.2.0", 2), (update.Status, update.LatestVersion, checks));

        // The About page asks for a check that is not hours old, and still not for one of a moment ago.
        published = "1.3.0";
        Assert.AreEqual("1.2.0", (await service.CheckAsync(new(Refresh: true), CancellationToken.None)).LatestVersion);
        time.Advance(AppUpdateService.MinimumAge);
        Assert.AreEqual("1.2.0", (await service.CheckAsync(new(), CancellationToken.None)).LatestVersion);
        Assert.AreEqual("1.3.0", (await service.CheckAsync(new(Refresh: true), CancellationToken.None)).LatestVersion);
        Assert.AreEqual(3, checks);

        // What the page was told is what is installed or opened: neither asks nuget.org again, however old the check.
        time.Advance(AppUpdateService.Period + AppUpdateService.Period);
        Assert.AreEqual("ok", (await service.OpenReleaseNotesAsync(new(), CancellationToken.None)).Status);
        Assert.AreEqual("unavailable", (await service.InstallAsync(new(), CancellationToken.None)).Status, "not an installed tool");
        Assert.AreEqual(3, checks);
    }

    [TestMethod]
    public async Task FailedCheck_IsMadeAgainSoon_AndABuildWithoutVersionNever()
    {
        var time = new ManualTime();
        var online = false;
        var checks = 0;
        using var service = new AppUpdateService("1.2.0", (package, current, prerelease, _) =>
        {
            checks++;
            return online ? Task.FromResult(new CodeAltaNuGetUpdateCheckResult(package, current, NuGetVersion.Parse("1.3.0"), true, true, prerelease))
                : throw new HttpRequestException("no network");
        }, _ => true) { Time = time };
        Assert.AreEqual("failed", (await service.CheckAsync(new(), CancellationToken.None)).Status);
        online = true;
        Assert.AreEqual("failed", (await service.CheckAsync(new(), CancellationToken.None)).Status);
        time.Advance(AppUpdateService.MinimumAge);
        Assert.AreEqual(("available", 2), ((await service.CheckAsync(new(), CancellationToken.None)).Status, checks));

        // A build of the checkout, and an instance on explicit roots, ask nothing however long they run.
        using var development = new AppUpdateService("development", (_, _, _, _) => throw new InvalidOperationException("not asked"), _ => true) { Time = time };
        using var absent = new AppUpdateService { Time = time };
        foreach (var quiet in new[] { development, absent })
        {
            Assert.AreEqual("unavailable", (await quiet.CheckAsync(new(), CancellationToken.None)).Status);
            time.Advance(AppUpdateService.Period + AppUpdateService.Period);
            Assert.AreEqual("unavailable", (await quiet.CheckAsync(new(Refresh: true), CancellationToken.None)).Status);
        }
    }

    // A clock the test moves: the age of a check is elapsed time, not the time of day.
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    [TestMethod]
    public async Task Prerelease_LooksForPrereleases_AndUpdatesToOne()
    {
        using var service = new AppUpdateService("1.3.0-alpha.2", (package, current, prerelease, _) =>
        {
            Assert.IsTrue(prerelease);
            return Task.FromResult(new CodeAltaNuGetUpdateCheckResult(package, current, NuGetVersion.Parse("1.3.0-alpha.5"), true, true, prerelease));
        }, _ => true);
        var update = await service.CheckAsync(new(), CancellationToken.None);
        Assert.AreEqual("available", update.Status);
        Assert.AreEqual("dotnet tool update -g CodeAlta --prerelease", update.Command);
    }

    [TestMethod]
    public async Task NothingNewer_UnpublishedOrUnreachable_OffersNoCommand()
    {
        var opened = 0;
        static Func<string, NuGetVersion, bool, CancellationToken, Task<CodeAltaNuGetUpdateCheckResult>> Returns(NuGetVersion? latest, bool found, bool newer)
            => (package, current, prerelease, _) => Task.FromResult(new CodeAltaNuGetUpdateCheckResult(package, current, latest, found, newer, prerelease));

        using var latest = new AppUpdateService("1.3.0", Returns(NuGetVersion.Parse("1.3.0"), found: true, newer: false), _ => { opened++; return true; });
        var same = await latest.CheckAsync(new(), CancellationToken.None);
        Assert.AreEqual("latest", same.Status);
        Assert.IsNull(same.Command);
        Assert.AreEqual("unavailable", (await latest.OpenReleaseNotesAsync(new(), CancellationToken.None)).Status);

        using var unpublished = new AppUpdateService("1.3.0", Returns(null, found: false, newer: false), _ => true);
        Assert.AreEqual("not_found", (await unpublished.CheckAsync(new(), CancellationToken.None)).Status);

        using var offline = new AppUpdateService("1.3.0", (_, _, _, _) => throw new HttpRequestException("no network"), _ => true);
        var failed = await offline.CheckAsync(new(), CancellationToken.None);
        Assert.AreEqual("failed", failed.Status);
        Assert.AreEqual("1.3.0", failed.CurrentVersion);

        // A build that is not a published version has nothing to compare, and does not ask nuget.org.
        using var development = new AppUpdateService("development", (_, _, _, _) => throw new InvalidOperationException("not asked"), _ => true);
        Assert.AreEqual("unavailable", (await development.CheckAsync(new(), CancellationToken.None)).Status);
        using var absent = new AppUpdateService();
        Assert.AreEqual("unavailable", (await absent.CheckAsync(new(), CancellationToken.None)).Status);
        Assert.AreEqual(0, opened);
    }

    [TestMethod]
    public async Task InstalledTool_UpdatesItselfAndRestarts_OnlyWhenThereIsSomethingNewer()
    {
        static Func<string, NuGetVersion, bool, CancellationToken, Task<CodeAltaNuGetUpdateCheckResult>> Latest(string version, bool newer)
            => (package, current, prerelease, _) => Task.FromResult(new CodeAltaNuGetUpdateCheckResult(package, current, NuGetVersion.Parse(version), true, newer, prerelease));

        var installs = new List<bool>();
        var canceled = 0;
        using var service = new AppUpdateService("1.2.0", Latest("1.3.0-beta.1", newer: true), _ => true)
        {
            Install = prerelease => { installs.Add(prerelease); return installs.Count == 1; }, CancelInstall = () => canceled++, Installed = "ok",
        };
        var update = await service.CheckAsync(new(), CancellationToken.None);
        Assert.IsTrue(update.CanInstall);
        Assert.AreEqual("ok", update.Installed, "how the previous run's update went");
        // The helper is started for the kind of version that is newer; a helper that does not start says so.
        Assert.AreEqual("started", (await service.InstallAsync(new(), CancellationToken.None)).Status);
        Assert.AreEqual("failed", (await service.InstallAsync(new(), CancellationToken.None)).Status);
        CollectionAssert.AreEqual(new[] { true, true }, installs);
        Assert.AreEqual("ok", service.CancelInstallation(new()).Status);
        Assert.AreEqual(1, canceled);

        // Nothing newer, or not an installed tool: there is nothing to install.
        using var current = new AppUpdateService("1.3.0", Latest("1.3.0", newer: false), _ => true) { Install = _ => throw new InvalidOperationException("not asked") };
        Assert.IsFalse((await current.CheckAsync(new(), CancellationToken.None)).CanInstall);
        Assert.AreEqual("unavailable", (await current.InstallAsync(new(), CancellationToken.None)).Status);
        using var build = new AppUpdateService("1.2.0", Latest("1.3.0", newer: true), _ => true);
        Assert.IsFalse((await build.CheckAsync(new(), CancellationToken.None)).CanInstall);
        Assert.AreEqual("unavailable", (await build.InstallAsync(new(), CancellationToken.None)).Status);
        Assert.AreEqual("unavailable", build.CancelInstallation(new()).Status);
    }

    [TestMethod]
    public void UpdateHelper_WaitsForTheApplication_Updates_AndStartsItAgain()
    {
        string[] arguments = ["tool", "update", "-g", "CodeAlta", "--prerelease"];
        var windows = CodeAlta.Desktop.DesktopUpdateInstaller.WindowsScript(4242, @"C:\Program Files\dotnet\dotnet.exe", arguments,
            @"C:\Users\a 100%\update.log", @"C:\data\result.txt", @"C:\data\cancel", @"C:\Users\me\.dotnet\tools\alta.cmd");
        var lines = windows.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // It waits while the process exists, stops when the update was called off, and gives up in the end.
        CollectionAssert.Contains(lines, "tasklist /FI \"PID eq 4242\" /NH 2>nul | find \" 4242 \" >nul");
        CollectionAssert.Contains(lines, "if exist \"C:\\data\\cancel\" exit /b 3");
        Assert.IsTrue(lines.Any(line => line.StartsWith("if %tries% geq ", StringComparison.Ordinal) && line.EndsWith(" exit /b 2", StringComparison.Ordinal)));
        // Then the SDK's own update, its outcome on record, and the application again. A percent sign in a path is kept.
        CollectionAssert.Contains(lines, "\"C:\\Program Files\\dotnet\\dotnet.exe\" tool update -g CodeAlta --prerelease > \"C:\\Users\\a 100%%\\update.log\" 2>&1");
        CollectionAssert.Contains(lines, "> \"C:\\data\\result.txt\" echo %errorlevel%");
        // The launcher is the SDK's script: the helper becomes it, in its own console that has no window.
        Assert.AreEqual("\"C:\\Users\\me\\.dotnet\\tools\\alta.cmd\"", lines[^1]);
        Assert.IsTrue(Array.IndexOf(lines, ":update") < Array.FindIndex(lines, line => line.Contains("tool update", StringComparison.Ordinal)));

        var unix = CodeAlta.Desktop.DesktopUpdateInstaller.UnixScript(4242, "/usr/local/share/dotnet/dotnet", arguments,
            "/Users/o'b/update.log", "/data/result.txt", "/data/cancel", "/Users/me/.dotnet/tools/alta", "/Users/me/Applications/CodeAlta.app");
        Assert.IsTrue(unix.StartsWith("#!/bin/sh\n", StringComparison.Ordinal));
        Assert.IsFalse(unix.Contains('\r'));
        StringAssert.Contains(unix, "while kill -0 4242 2>/dev/null; do\n");
        StringAssert.Contains(unix, "[ -e '/data/cancel' ] && exit 3\n");
        StringAssert.Contains(unix, "'/usr/local/share/dotnet/dotnet' 'tool' 'update' '-g' 'CodeAlta' '--prerelease' > '/Users/o'\\''b/update.log' 2>&1\n");
        StringAssert.Contains(unix, "echo $? > '/data/result.txt'\n");
        // macOS starts the bundle, which keeps its icon in the Dock; elsewhere the launcher.
        // A bundle that does not open leaves the launcher: an update never ends with nothing started.
        StringAssert.Contains(unix, "if [ -d '/Users/me/Applications/CodeAlta.app' ] && /usr/bin/open '/Users/me/Applications/CodeAlta.app'; then exit 0; fi\n");
        Assert.IsTrue(unix.EndsWith("nohup '/Users/me/.dotnet/tools/alta' >/dev/null 2>&1 &\n", StringComparison.Ordinal));
        Assert.IsFalse(CodeAlta.Desktop.DesktopUpdateInstaller.UnixScript(1, "/d", arguments, "/l", "/r", "/c", "/alta", null).Contains("/usr/bin/open", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UpdateOutcome_IsReadOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.IsNull(CodeAlta.Desktop.DesktopUpdateInstaller.ConsumeResult(root));
            Directory.CreateDirectory(Path.Combine(root, "update"));
            File.WriteAllText(Path.Combine(root, "update", "result.txt"), "0 \r\n");
            Assert.AreEqual("ok", CodeAlta.Desktop.DesktopUpdateInstaller.ConsumeResult(root));
            Assert.IsNull(CodeAlta.Desktop.DesktopUpdateInstaller.ConsumeResult(root), "said once");
            File.WriteAllText(Path.Combine(root, "update", "result.txt"), "1");
            Assert.AreEqual("failed", CodeAlta.Desktop.DesktopUpdateInstaller.ConsumeResult(root));
            // Calling an update off leaves the mark its helper looks for.
            CodeAlta.Desktop.DesktopUpdateInstaller.Cancel(root);
            Assert.IsTrue(File.Exists(Path.Combine(root, "update", "cancel")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void UpdateCommandAndReleaseNotes_AreTheSameForBothApplications()
    {
        Assert.AreEqual("dotnet tool update -g CodeAlta.Tui", CodeAltaNuGetUpdateChecker.UpdateCommand("CodeAlta.Tui", prerelease: false));
        Assert.AreEqual("dotnet tool update -g CodeAlta --prerelease", CodeAltaNuGetUpdateChecker.UpdateCommand("CodeAlta", prerelease: true));
        CollectionAssert.AreEqual(new[] { "tool", "update", "-g", "CodeAlta" }, CodeAltaNuGetUpdateChecker.UpdateArguments("CodeAlta", prerelease: false).ToArray());
        Assert.AreEqual("https://github.com/CodeAlta/CodeAlta/releases/tag/1.3.0-alpha.5", CodeAltaNuGetUpdateChecker.ReleaseNotesUri(" 1.3.0-alpha.5 "));
        Assert.IsNull(CodeAltaNuGetUpdateChecker.ReleaseNotesUri(null));
        Assert.IsNull(CodeAltaNuGetUpdateChecker.ReleaseNotesUri(" "));
    }
}
