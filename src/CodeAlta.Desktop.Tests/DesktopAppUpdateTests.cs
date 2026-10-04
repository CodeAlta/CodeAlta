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
        // One look at nuget.org per run, however often the page asks.
        _ = await service.CheckAsync(new(), CancellationToken.None);
        Assert.AreEqual(1, checks);
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
    public void UpdateCommandAndReleaseNotes_AreTheSameForBothApplications()
    {
        Assert.AreEqual("dotnet tool update -g CodeAlta.Tui", CodeAltaNuGetUpdateChecker.UpdateCommand("CodeAlta.Tui", prerelease: false));
        Assert.AreEqual("dotnet tool update -g CodeAlta --prerelease", CodeAltaNuGetUpdateChecker.UpdateCommand("CodeAlta", prerelease: true));
        Assert.AreEqual("https://github.com/CodeAlta/CodeAlta/releases/tag/1.3.0-alpha.5", CodeAltaNuGetUpdateChecker.ReleaseNotesUri(" 1.3.0-alpha.5 "));
        Assert.IsNull(CodeAltaNuGetUpdateChecker.ReleaseNotesUri(null));
        Assert.IsNull(CodeAltaNuGetUpdateChecker.ReleaseNotesUri(" "));
    }
}
