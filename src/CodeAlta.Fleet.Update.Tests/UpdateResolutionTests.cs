namespace CodeAlta.Fleet.Update.Tests;

[TestClass]
public sealed class UpdateResolutionTests
{
    [TestMethod]
    public void VersionsOrderAsSemVer()
    {
        string[] ordered = ["3.9.9", "4.0.0-alpha.2", "4.0.0-alpha.10", "4.0.0-alpha.10.1", "4.0.0-beta", "4.0.0-rc.1", "4.0.0", "4.0.1", "4.1.0"];
        var parsed = ordered.Select(UpdateVersion.Parse).ToArray();
        CollectionAssert.AreEqual(ordered, parsed.OrderBy(static v => v).Select(static v => v.ToString()).ToArray());
        Assert.AreEqual(UpdateVersion.Parse("4.0.0+abc"), UpdateVersion.Parse("4.0.0"));
        Assert.AreEqual("4.0.0-alpha.901", UpdateVersion.Parse("4.0.0-Alpha.901+sha.1").ToString());
        Assert.AreEqual("1.2.3", UpdateVersion.Parse("1.2.3.0").ToString());
        Assert.IsTrue(UpdateVersion.Parse("4.0.0-alpha.1").IsPrerelease);
        foreach (var invalid in new[] { "", "4", "4.0", "4.0.0-", "4.0.0-a..b", "x.y.z", "4.0.0-a b", "../4.0.0" })
            Assert.IsFalse(UpdateVersion.TryParse(invalid, out _), invalid);
    }

    [TestMethod]
    public void HashesParseInBase64HexadecimalOrPrefixed()
    {
        var bytes = System.Security.Cryptography.SHA512.HashData("package"u8);
        var base64 = Convert.ToBase64String(bytes);
        var hex = Convert.ToHexString(bytes);
        Assert.AreEqual(PackageHash.Parse(base64), PackageHash.Parse(hex.ToLowerInvariant()));
        Assert.AreEqual(PackageHash.Parse(base64), PackageHash.Parse("sha512:" + base64));
        Assert.AreEqual("sha512:" + base64, PackageHash.Parse(hex).ToString());
        Assert.IsFalse(PackageHash.TryParse(Convert.ToBase64String(new byte[32]), out _), "a SHA-256 is not taken for a SHA-512");
        Assert.IsFalse(PackageHash.TryParse("not a hash", out _));
        Assert.AreNotEqual(PackageHash.Parse(base64), PackageHash.Parse(Convert.ToBase64String(new byte[64])));
    }

    [TestMethod]
    public void ALatestVersionFollowsReleasesUnlessThePrereleaseRunsOrIsAsked()
    {
        FeedVersion[] feed = [new(UpdateVersion.Parse("4.0.0"), true), new(UpdateVersion.Parse("4.0.1"), true), new(UpdateVersion.Parse("4.1.0-beta.1"), true),
            new(UpdateVersion.Parse("4.0.2"), Listed: false)];
        var stable = SelfUpdater.Choose("feed", "p", feed, "p.rid", feed, new UpdateRequest(), UpdateVersion.Parse("4.0.0"), new UpdateState());
        Assert.AreEqual("4.0.1", stable.Target?.ToString(), "the latest listed release");

        var asked = SelfUpdater.Choose("feed", "p", feed, "p.rid", feed, new UpdateRequest { Prerelease = true }, UpdateVersion.Parse("4.0.0"), new UpdateState());
        Assert.AreEqual("4.1.0-beta.1", asked.Target?.ToString());

        var onPrerelease = SelfUpdater.Choose("feed", "p", feed, "p.rid", feed, new UpdateRequest(), UpdateVersion.Parse("4.0.0-alpha.3"), new UpdateState());
        Assert.AreEqual("4.1.0-beta.1", onPrerelease.Target?.ToString(), "a machine on a prerelease follows the prereleases");

        var upToDate = SelfUpdater.Choose("feed", "p", feed, "p.rid", feed, new UpdateRequest(), UpdateVersion.Parse("4.0.1"), new UpdateState());
        Assert.IsNull(upToDate.Target);
        Assert.AreEqual("4.0.1", upToDate.Latest?.ToString());
        StringAssert.Contains(upToDate.Note, "up to date");

        var none = SelfUpdater.Choose("feed", "p", [], "p.rid", [], new UpdateRequest(), UpdateVersion.Parse("4.0.1"), new UpdateState());
        Assert.IsNull(none.Target);
        Assert.IsNull(none.Latest);
    }

    [TestMethod]
    public void AVersionIsAvailableOnceBothPackagesListIt()
    {
        var running = UpdateVersion.Parse("4.0.0");
        FeedVersion[] tool = [new(UpdateVersion.Parse("4.0.0"), true), new(UpdateVersion.Parse("4.0.1"), true), new(UpdateVersion.Parse("4.0.2"), Listed: false)];
        // The runtime package of 4.0.3 is published before its tool package: the release is not complete yet.
        FeedVersion[] runtime = [new(UpdateVersion.Parse("4.0.0"), true), new(UpdateVersion.Parse("4.0.1"), true), new(UpdateVersion.Parse("4.0.2"), true),
            new(UpdateVersion.Parse("4.0.3"), true)];
        var check = SelfUpdater.Choose("feed", "CodeAlta.Node", tool, "CodeAlta.Node.win-x64", runtime, new UpdateRequest(), running, new UpdateState());
        Assert.AreEqual("4.0.1", check.Target?.ToString(), "4.0.3 has no tool package yet, and the tool package of 4.0.2 is unlisted: the release is withdrawn");
        Assert.AreEqual("CodeAlta.Node.win-x64", check.PackageId, "the runtime package is the one downloaded");

        FeedVersion[] toolLater = [.. tool, new(UpdateVersion.Parse("4.0.3"), true)];
        Assert.AreEqual("4.0.3", SelfUpdater.Choose("feed", "CodeAlta.Node", toolLater, "CodeAlta.Node.win-x64", runtime, new UpdateRequest(), running, new UpdateState())
            .Target?.ToString(), "both packages list it");

        // A tool package published before the runtime package of this machine is not offered either.
        FeedVersion[] toolFirst = [.. toolLater, new(UpdateVersion.Parse("4.0.4"), true)];
        Assert.AreEqual("4.0.3", SelfUpdater.Choose("feed", "CodeAlta.Node", toolFirst, "CodeAlta.Node.win-x64", runtime, new UpdateRequest(), running, new UpdateState())
            .Target?.ToString());
        var none = SelfUpdater.Choose("feed", "CodeAlta.Node", [], "CodeAlta.Node.win-x64", runtime, new UpdateRequest(), running, new UpdateState());
        Assert.IsNull(none.Target);
        StringAssert.Contains(none.Note, "CodeAlta.Node with its package CodeAlta.Node.win-x64");
    }

    [TestMethod]
    public void AnExactVersionMustBeInBothPackages()
    {
        var running = UpdateVersion.Parse("4.0.0");
        FeedVersion[] tool = [new(UpdateVersion.Parse("4.0.1"), true), new(UpdateVersion.Parse("4.0.2"), Listed: false)];
        FeedVersion[] runtime = [new(UpdateVersion.Parse("4.0.2"), true), new(UpdateVersion.Parse("4.0.3"), true)];
        var noRuntime = Assert.ThrowsExactly<UpdateException>(() =>
            SelfUpdater.Choose("feed", "CodeAlta.Node", tool, "CodeAlta.Node.win-x64", runtime, new UpdateRequest { Version = "4.0.1" }, running, new UpdateState()));
        StringAssert.Contains(noRuntime.Message, "not CodeAlta.Node.win-x64 4.0.1, the package of this machine");
        var noTool = Assert.ThrowsExactly<UpdateException>(() =>
            SelfUpdater.Choose("feed", "CodeAlta.Node", tool, "CodeAlta.Node.win-x64", runtime, new UpdateRequest { Version = "4.0.3" }, running, new UpdateState()));
        StringAssert.Contains(noTool.Message, "but not its tool package CodeAlta.Node 4.0.3");
        Assert.AreEqual(UpdateErrorCodes.NotFound, noTool.Code);

        var unlisted = SelfUpdater.Choose("feed", "CodeAlta.Node", tool, "CodeAlta.Node.win-x64", runtime, new UpdateRequest { Version = "4.0.2" }, running, new UpdateState());
        Assert.AreEqual("4.0.2", unlisted.Target?.ToString(), "a withdrawn release is installed when named, as NuGet does");
        StringAssert.Contains(unlisted.Note, "unlisted");
    }

    [TestMethod]
    public void AnExactVersionOfTheAutomaticPolicyMustBeTheLatestStableBothPackagesList()
    {
        var running = UpdateVersion.Parse("4.0.0");
        FeedVersion[] tool = [new(UpdateVersion.Parse("4.0.1"), true), new(UpdateVersion.Parse("4.0.2"), Listed: false), new(UpdateVersion.Parse("4.0.3"), true),
            new(UpdateVersion.Parse("4.1.0-beta.1"), true)];
        FeedVersion[] runtime = [new(UpdateVersion.Parse("4.0.1"), true), new(UpdateVersion.Parse("4.0.2"), true), new(UpdateVersion.Parse("4.0.3"), true),
            new(UpdateVersion.Parse("4.1.0-beta.1"), true)];
        UpdateCheck Choose(string version, FeedVersion[] runtimes)
            => SelfUpdater.Choose("feed", "CodeAlta.Node", tool, "CodeAlta.Node.win-x64", runtimes, new UpdateRequest { Version = version, StableOnly = true }, running,
                new UpdateState());

        Assert.AreEqual("4.0.3", Choose("4.0.3", runtime).Target?.ToString());
        StringAssert.Contains(Assert.ThrowsExactly<UpdateException>(() => Choose("4.0.2", runtime)).Message, "unlisted", "a withdrawn release");
        StringAssert.Contains(Assert.ThrowsExactly<UpdateException>(() => Choose("4.0.1", runtime)).Message, "4.0.3", "a release that is no longer the latest");
        StringAssert.Contains(Assert.ThrowsExactly<UpdateException>(() => Choose("4.1.0-beta.1", runtime)).Message, "stable", "a prerelease");
        FeedVersion[] runtimeWithdrawn = [.. runtime.Where(static found => !found.Version.Equals(UpdateVersion.Parse("4.0.3"))), new(UpdateVersion.Parse("4.0.3"), Listed: false)];
        StringAssert.Contains(Assert.ThrowsExactly<UpdateException>(() => Choose("4.0.3", runtimeWithdrawn)).Message, "unlisted", "the package of this machine withdrawn");
    }

    [TestMethod]
    public void AVersionRolledBackIsSkippedUnlessNamed()
    {
        FeedVersion[] feed = [new(UpdateVersion.Parse("4.0.0"), true), new(UpdateVersion.Parse("4.0.1"), true), new(UpdateVersion.Parse("4.0.2"), true)];
        var state = new UpdateState { Bad = [new UpdateBadVersion("4.0.2", DateTimeOffset.UnixEpoch, "crashed")] };
        var latest = SelfUpdater.Choose("feed", "p", feed, "p.rid", feed, new UpdateRequest(), UpdateVersion.Parse("4.0.0"), state);
        Assert.AreEqual("4.0.1", latest.Target?.ToString());
        StringAssert.Contains(latest.Note, "4.0.2 was rolled back");

        var named = SelfUpdater.Choose("feed", "p", feed, "p.rid", feed, new UpdateRequest { Version = "4.0.2" }, UpdateVersion.Parse("4.0.0"), state);
        Assert.AreEqual("4.0.2", named.Target?.ToString());
        StringAssert.Contains(named.Note, "tried again");
    }

    [TestMethod]
    public void AnExactVersionMayGoDownButMustBeInTheFeed()
    {
        FeedVersion[] feed = [new(UpdateVersion.Parse("4.0.0"), true), new(UpdateVersion.Parse("4.0.1"), Listed: false)];
        Assert.AreEqual("4.0.0", SelfUpdater.Choose("feed", "p", feed, "p.rid", feed, new UpdateRequest { Version = "4.0.0" }, UpdateVersion.Parse("4.0.5"), new UpdateState()).Target?.ToString());
        Assert.AreEqual("4.0.1", SelfUpdater.Choose("feed", "p", feed, "p.rid", feed, new UpdateRequest { Version = "4.0.1" }, UpdateVersion.Parse("4.0.0"), new UpdateState()).Target?.ToString(),
            "an unlisted version is installed when named");
        Assert.IsNull(SelfUpdater.Choose("feed", "p", feed, "p.rid", feed, new UpdateRequest { Version = "4.0.0" }, UpdateVersion.Parse("4.0.0"), new UpdateState()).Target, "it already runs");
        var missing = Assert.ThrowsExactly<UpdateException>(() => SelfUpdater.Choose("feed", "p", feed, "p.rid", feed, new UpdateRequest { Version = "9.9.9" }, UpdateVersion.Parse("4.0.0"), new UpdateState()));
        Assert.AreEqual(UpdateErrorCodes.NotFound, missing.Code);
    }

    [TestMethod]
    public void TheOwnerRestrictsTheFeeds()
    {
        using var temp = new TempFolder();
        var open = UpdateSourcePolicy.Load(temp.Combine("update.json"));
        Assert.AreEqual(UpdateFeeds.NuGetOrg, open.Resolve(null, UpdateOrigin.Local));
        Assert.AreEqual(temp.Path, open.Resolve(temp.Path + Path.DirectorySeparatorChar, UpdateOrigin.Local), "the owner on the machine names any feed");
        Assert.AreEqual(UpdateErrorCodes.SourceNotAllowed, Assert.ThrowsExactly<UpdateException>(() => open.Resolve("https://evil.test/v3/index.json", UpdateOrigin.Remote)).Code,
            "the fleet reads only the configured feed or nuget.org");
        Assert.AreEqual(UpdateFeeds.NuGetOrg, open.Resolve("https://api.nuget.org/v3/index.json/", UpdateOrigin.Remote));

        File.WriteAllText(temp.Combine("update.json"), """{ "source": "https://mirror.test/v3/index.json", "allowedSources": [ "https://mirror.test/v3/index.json", "https://other.test/index.json" ] }""");
        var restricted = UpdateSourcePolicy.Load(temp.Combine("update.json"));
        Assert.AreEqual("https://mirror.test/v3/index.json", restricted.Resolve(null, UpdateOrigin.Remote));
        Assert.AreEqual("https://other.test/index.json", restricted.Resolve("https://OTHER.test/index.json", UpdateOrigin.Remote));
        Assert.AreEqual(UpdateErrorCodes.SourceNotAllowed, Assert.ThrowsExactly<UpdateException>(() => restricted.Resolve(UpdateFeeds.NuGetOrg, UpdateOrigin.Local)).Code,
            "a list binds the owner on the machine too");

        File.WriteAllText(temp.Combine("update.json"), "{ not json");
        Assert.AreEqual(UpdateErrorCodes.SourceNotAllowed, Assert.ThrowsExactly<UpdateException>(() => UpdateSourcePolicy.Load(temp.Combine("update.json"))).Code);
        Assert.AreEqual(UpdateErrorCodes.InvalidSource, Assert.ThrowsExactly<UpdateException>(() => open.Resolve("relative/folder", UpdateOrigin.Local)).Code);
    }

    [TestMethod]
    public void AFeedOfAnotherMachineNeedsTls()
    {
        using var http = new HttpClient();
        Assert.AreEqual(UpdateErrorCodes.InvalidSource, Assert.ThrowsExactly<UpdateException>(() => UpdateFeeds.Open("http://mirror.test/v3/index.json", http)).Code);
        Assert.IsFalse(UpdateFeeds.Open("http://127.0.0.1:5000/v3/index.json", http).IsLocal, "the loopback is the machine itself");
        Assert.AreEqual(UpdateErrorCodes.InvalidSource, Assert.ThrowsExactly<UpdateException>(() => UpdateFeeds.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), http)).Code);
    }

    [TestMethod]
    public void TheRuntimeOfThisProcessHasAPackage()
    {
        var runtime = UpdateProduct.CurrentRuntime();
        Assert.IsNotNull(runtime);
        CollectionAssert.Contains(UpdateProduct.PublishedRuntimes.ToArray(), runtime);
        Assert.AreEqual("CodeAlta.Server.linux-musl-arm64", UpdateProduct.Server.RuntimePackageId("linux-musl-arm64"));
    }
}
