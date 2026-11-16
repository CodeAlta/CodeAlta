namespace CodeAlta.Fleet.Update.Tests;

[TestClass]
public sealed class UpdateStagingTests
{
    private static readonly string PackageId = Packages.Product.RuntimePackageId(Packages.Runtime);

    private static SelfUpdater Updater(TempFolder temp, string running, HttpClient? http = null, UpdateSourceSettings? settings = null)
        => new(new SelfUpdaterOptions
        {
            Product = Packages.Product,
            Store = new UpdateVersionStore(temp.Combine("versions")),
            Running = UpdateVersion.Parse(running),
            Policy = UpdateSourcePolicy.From(settings ?? new UpdateSourceSettings()),
            Http = http ?? new HttpClient(),
            Runtime = Packages.Runtime,
        });

    [TestMethod]
    public async Task AFolderFeedListsFlatAndHierarchicalPackagesOfTheRuntimeOnly()
    {
        using var temp = new TempFolder();
        var feed = Directory.CreateDirectory(temp.Combine("feed")).FullName;
        Packages.Write(feed, "4.0.0-alpha.901");
        Packages.Write(feed, "4.0.0-alpha.902", runtime: "linux-x64");
        var hierarchical = Directory.CreateDirectory(Path.Combine(feed, PackageId.ToLowerInvariant(), "4.0.0-alpha.903")).FullName;
        var file = Packages.Write(hierarchical, "4.0.0-alpha.903");
        File.Move(file, Path.Combine(hierarchical, $"{PackageId.ToLowerInvariant()}.4.0.0-alpha.903.nupkg"));
        File.WriteAllText(Path.Combine(hierarchical, $"{PackageId.ToLowerInvariant()}.4.0.0-alpha.903.nupkg.sha512"),
            Packages.Sha512(Path.Combine(hierarchical, $"{PackageId.ToLowerInvariant()}.4.0.0-alpha.903.nupkg")));

        var versions = await UpdateFeeds.Open(feed, new HttpClient()).ListVersionsAsync(PackageId, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "4.0.0-alpha.901", "4.0.0-alpha.903" }, versions.Select(static v => v.Version.ToString()).ToArray());
        var tools = await UpdateFeeds.Open(feed, new HttpClient()).ListVersionsAsync(Packages.Product.PackageId, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "4.0.0-alpha.901", "4.0.0-alpha.902" }, tools.Select(static v => v.Version.ToString()).ToArray(),
            "the tool packages, not the runtime packages, whose names start the same");
    }

    [TestMethod]
    public async Task AFolderFeedOffersAVersionOnceItHasBothPackages()
    {
        using var temp = new TempFolder();
        var feed = Directory.CreateDirectory(temp.Combine("feed")).FullName;
        Packages.Write(feed, "4.0.1");
        Packages.Write(feed, "4.0.2", withTool: false);
        var updater = Updater(temp, "4.0.0");
        Assert.AreEqual("4.0.1", (await updater.CheckAsync(new UpdateRequest { Source = feed }, CancellationToken.None)).Target?.ToString(),
            "the runtime package of 4.0.2 came before its tool package");
        Packages.WriteTool(feed, "4.0.2");
        Assert.AreEqual("4.0.2", (await updater.CheckAsync(new UpdateRequest { Source = feed }, CancellationToken.None)).Target?.ToString());
    }

    [TestMethod]
    public async Task AStagedVersionHoldsTheApplicationOfItsRuntimeFolder()
    {
        using var temp = new TempFolder();
        var feed = Directory.CreateDirectory(temp.Combine("feed")).FullName;
        Packages.Write(feed, "4.0.0-alpha.901");
        Packages.Write(feed, "4.0.0-alpha.902", extra: [("tools/net10.0/linux-x64/altanode.dll", "other runtime"), ("readme.md", "outside")]);
        var updater = Updater(temp, "4.0.0-alpha.901");
        var request = new UpdateRequest { Source = feed };
        var check = await updater.CheckAsync(request, CancellationToken.None);
        Assert.AreEqual("4.0.0-alpha.902", check.Target?.ToString());
        Assert.AreEqual(PackageId, check.PackageId);

        var staged = await updater.StageAsync(check, request, CancellationToken.None);
        Assert.AreEqual("local folder", staged.Checked);
        Assert.IsFalse(staged.AlreadyStaged);
        Assert.AreEqual("assembly 4.0.0-alpha.902", File.ReadAllText(Path.Combine(staged.Path, "altanode.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(staged.Path, "content", "user-guide", "readme.md")));
        Assert.IsFalse(File.Exists(Path.Combine(staged.Path, "readme.md")), "only the folder of the runtime is unpacked");
        Assert.IsTrue(File.Exists(Path.Combine(staged.Path, ".package.json")));
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(temp.Combine("versions"), UpdateVersionStore.StagingFolderName)).Length, "the staging folder is cleaned");

        var again = await updater.StageAsync(check, request, CancellationToken.None);
        Assert.IsTrue(again.AlreadyStaged);

        // A folder of another package of the same version (an earlier, different build) is not taken: it is replaced.
        File.WriteAllText(Path.Combine(staged.Path, "altanode.dll"), "another build");
        File.WriteAllText(Path.Combine(staged.Path, ".package.json"), """{ "sha512": "another" }""");
        var replaced = await updater.StageAsync(check, request, CancellationToken.None);
        Assert.IsFalse(replaced.AlreadyStaged);
        Assert.AreEqual("assembly 4.0.0-alpha.902", File.ReadAllText(Path.Combine(replaced.Path, "altanode.dll")));
    }

    private static string RuntimeConfig(params string[] frameworks)
        => "{ \"runtimeOptions\": { \"tfm\": \"net10.0\", \"frameworks\": [ " + string.Join(", ", frameworks.Select(static name => $"{{ \"name\": \"{name}\", \"version\": \"10.0.0\" }}")) + " ] } }";

    // A fake installation: shared/<framework>/<version>/ for each, as the folder of .NET has them.
    private static string Installation(TempFolder temp, string name, params string[] frameworks)
    {
        var root = temp.Combine(name);
        Directory.CreateDirectory(root);
        foreach (var framework in frameworks) Directory.CreateDirectory(Path.Combine(root, "shared", framework.Split(' ')[0], framework.Split(' ')[1]));
        return root;
    }

    // The packages are framework-dependent: a version that needs ASP.NET Core on a machine that has the .NET runtime alone would be stopped
    // by the .NET host before it ran, then rolled back as bad. It is refused when staged, with what to install, and the running version stays.
    [TestMethod]
    public async Task AVersionThatNeedsAFrameworkTheMachineLacksIsNotStaged_AndSaysWhatToInstall()
    {
        using var temp = new TempFolder();
        var feed = Directory.CreateDirectory(temp.Combine("feed")).FullName;
        Packages.Write(feed, "4.5.0", extra: [("tools/net10.0/win-x64/altanode.runtimeconfig.json", RuntimeConfig("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"))]);
        var request = new UpdateRequest { Source = feed };

        var runtimeOnly = new SelfUpdater(new SelfUpdaterOptions
        {
            Product = Packages.Product, Store = new UpdateVersionStore(temp.Combine("versions")), Running = UpdateVersion.Parse("4.4.0"),
            Policy = UpdateSourcePolicy.From(new UpdateSourceSettings()), Http = new HttpClient(), Runtime = Packages.Runtime,
            DotnetRoot = Installation(temp, "runtime-only", "Microsoft.NETCore.App 10.0.14"),
        });
        var failure = await Assert.ThrowsExactlyAsync<UpdateException>(async () => await runtimeOnly.StageAsync(await runtimeOnly.CheckAsync(request, CancellationToken.None), request, CancellationToken.None));
        Assert.AreEqual(UpdateErrorCodes.RuntimeMissing, failure.Code);
        StringAssert.Contains(failure.Message, "Microsoft.AspNetCore.App 10.0.0");
        StringAssert.Contains(failure.Message, "aspnetcore-runtime-10.0");
        StringAssert.Contains(failure.Message, "4.4.0 keeps running");
        Assert.IsFalse(Directory.Exists(temp.Combine("versions", "4.5.0")), "nothing is left to switch to");

        // The same package on a machine that has the runtime: staged. A newer patch of the major satisfies the version it asks for.
        var complete = new SelfUpdater(new SelfUpdaterOptions
        {
            Product = Packages.Product, Store = new UpdateVersionStore(temp.Combine("versions")), Running = UpdateVersion.Parse("4.4.0"),
            Policy = UpdateSourcePolicy.From(new UpdateSourceSettings()), Http = new HttpClient(), Runtime = Packages.Runtime,
            DotnetRoot = Installation(temp, "complete", "Microsoft.NETCore.App 10.0.14", "Microsoft.AspNetCore.App 10.0.14"),
        });
        var staged = await complete.StageAsync(await complete.CheckAsync(request, CancellationToken.None), request, CancellationToken.None);
        Assert.IsFalse(staged.AlreadyStaged);

        // A folder staged earlier, by a version that did not check, is checked too: it is not taken as it is.
        var failureAgain = await Assert.ThrowsExactlyAsync<UpdateException>(async () => await runtimeOnly.StageAsync(await runtimeOnly.CheckAsync(request, CancellationToken.None), request, CancellationToken.None));
        Assert.AreEqual(UpdateErrorCodes.RuntimeMissing, failureAgain.Code);
    }

    [TestMethod]
    public void TheFrameworksADeclaredVersionNeeds_AreThoseOfTheSameMajorNotOlder()
    {
        using var temp = new TempFolder();
        var app = Directory.CreateDirectory(temp.Combine("app")).FullName;
        File.WriteAllText(Path.Combine(app, "altanode.runtimeconfig.json"), RuntimeConfig("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"));
        var count = 0;
        string? Missing(params string[] installed) => UpdateFrameworks.FirstMissing(app, Packages.Product, Installation(temp, "dotnet" + count++, installed));

        Assert.IsNull(Missing("Microsoft.NETCore.App 10.0.14", "Microsoft.AspNetCore.App 10.0.14"));
        Assert.IsNull(Missing("Microsoft.NETCore.App 10.0.0-rc.2.25502.107", "Microsoft.AspNetCore.App 10.1.2"), "a preview and a newer minor count");
        Assert.AreEqual("Microsoft.AspNetCore.App 10.0.0", Missing("Microsoft.NETCore.App 10.0.14"), "the .NET runtime alone");
        Assert.AreEqual("Microsoft.AspNetCore.App 10.0.0", Missing("Microsoft.NETCore.App 10.0.14", "Microsoft.AspNetCore.App 9.0.9", "Microsoft.AspNetCore.App 8.0.14"), "another major does not stand in");
        Assert.AreEqual("Microsoft.NETCore.App 10.0.0", Missing(), "no runtime at all");

        // Nothing to check where the process runs on no shared runtime, or the package has no runtimeconfig.json.
        Assert.IsNull(UpdateFrameworks.FirstMissing(app, Packages.Product, null));
        var empty = Installation(temp, "empty");
        Assert.IsNull(UpdateFrameworks.FirstMissing(temp.Combine("elsewhere"), Packages.Product, empty));
        File.WriteAllText(Path.Combine(app, "altanode.runtimeconfig.json"), "not json");
        Assert.IsNull(UpdateFrameworks.FirstMissing(app, Packages.Product, empty), "a file nothing can read is not a reason to refuse");
        File.WriteAllText(Path.Combine(app, "altanode.runtimeconfig.json"), """{ "runtimeOptions": { "framework": { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" } } }""");
        Assert.AreEqual("Microsoft.AspNetCore.App 10.0.0", UpdateFrameworks.FirstMissing(app, Packages.Product, empty), "the single \"framework\" form");
    }

    [TestMethod]
    public async Task AnExpectedHashThatDoesNotMatchInstallsNothing()
    {
        using var temp = new TempFolder();
        var feed = Directory.CreateDirectory(temp.Combine("feed")).FullName;
        var package = Packages.Write(feed, "4.0.1");
        var updater = Updater(temp, "4.0.0");
        var wrong = new UpdateRequest { Source = feed, ExpectedSha512 = Convert.ToBase64String(new byte[64]) };
        var failure = await Assert.ThrowsExactlyAsync<UpdateException>(async () => await updater.StageAsync(await updater.CheckAsync(wrong, CancellationToken.None), wrong, CancellationToken.None));
        Assert.AreEqual(UpdateErrorCodes.HashMismatch, failure.Code);
        Assert.IsFalse(Directory.Exists(temp.Combine("versions", "4.0.1")));

        var right = wrong with { ExpectedSha512 = Packages.Sha512(package) };
        var staged = await updater.StageAsync(await updater.CheckAsync(right, CancellationToken.None), right, CancellationToken.None);
        Assert.AreEqual("expected hash", staged.Checked);

        File.WriteAllText(package + ".sha512", Convert.ToBase64String(new byte[64]));
        Directory.Delete(staged.Path, recursive: true);
        var feedHash = await Assert.ThrowsExactlyAsync<UpdateException>(async () =>
            await updater.StageAsync(await updater.CheckAsync(new UpdateRequest { Source = feed }, CancellationToken.None), new UpdateRequest { Source = feed }, CancellationToken.None));
        Assert.AreEqual(UpdateErrorCodes.HashMismatch, feedHash.Code, "the hash beside the file must match too");
    }

    [TestMethod]
    public async Task ANuGetFeedGivesTheVersionsAndTheHashOfItsCatalog()
    {
        using var temp = new TempFolder();
        var source = Directory.CreateDirectory(temp.Combine("packages")).FullName;
        var handler = new FakeNuGetFeed { PagedRegistration = true };
        foreach (var version in new[] { "4.0.0", "4.0.1", "4.0.2", "4.0.3" })
        {
            handler.Add(PackageId, version, File.ReadAllBytes(Packages.Write(source, version)));
            // The tool package of 4.0.2 is unlisted (the release withdrawn), that of 4.0.3 not published yet.
            if (version != "4.0.3") handler.Add(Packages.Product.PackageId, version, File.ReadAllBytes(Packages.WriteTool(source, version)), listed: version != "4.0.2");
        }

        var updater = Updater(temp, "4.0.0", new HttpClient(handler));
        var request = new UpdateRequest { Source = FakeNuGetFeed.Index };
        var check = await updater.CheckAsync(request, CancellationToken.None);
        Assert.AreEqual("4.0.1", check.Target?.ToString(), "a withdrawn release and an incomplete one are not the latest");
        var staged = await updater.StageAsync(check, request, CancellationToken.None);
        Assert.AreEqual("feed", staged.Checked);
        Assert.AreEqual("assembly 4.0.1", File.ReadAllText(Path.Combine(staged.Path, "altanode.dll")));
        CollectionAssert.Contains(handler.Requests, $"/catalog/{PackageId.ToLowerInvariant()}/4.0.1.json");
    }

    [TestMethod]
    public async Task ARegistrationAnsweredInGzipIsDecoded()
    {
        // nuget.org's RegistrationsBaseUrl/3.6.0 answers gzip whatever the request asks; a plain HttpClient does not decode it.
        using var temp = new TempFolder();
        var source = Directory.CreateDirectory(temp.Combine("packages")).FullName;
        var handler = new FakeNuGetFeed { GzipRegistrations = true, PagedRegistration = true };
        foreach (var version in new[] { "4.0.0", "4.0.1" })
        {
            handler.Add(PackageId, version, File.ReadAllBytes(Packages.Write(source, version)));
            handler.Add(Packages.Product.PackageId, version, File.ReadAllBytes(Packages.WriteTool(source, version)));
        }

        var updater = Updater(temp, "4.0.0", new HttpClient(handler));
        var request = new UpdateRequest { Source = FakeNuGetFeed.Index };
        var check = await updater.CheckAsync(request, CancellationToken.None);
        Assert.AreEqual("4.0.1", check.Target?.ToString());
        Assert.AreEqual("feed", (await updater.StageAsync(check, request, CancellationToken.None)).Checked, "the hash of the gzip catalog entry checks the package");
    }

    [TestMethod]
    public async Task APackageThatDoesNotMatchTheFeedsHashIsRefused()
    {
        using var temp = new TempFolder();
        var source = Directory.CreateDirectory(temp.Combine("packages")).FullName;
        var handler = new FakeNuGetFeed();
        handler.Add(PackageId, "4.0.1", File.ReadAllBytes(Packages.Write(source, "4.0.1")), hash: Convert.ToBase64String(new byte[64]));
        handler.Add(Packages.Product.PackageId, "4.0.1", File.ReadAllBytes(Packages.WriteTool(source, "4.0.1")));
        var updater = Updater(temp, "4.0.0", new HttpClient(handler));
        var request = new UpdateRequest { Source = FakeNuGetFeed.Index };
        var failure = await Assert.ThrowsExactlyAsync<UpdateException>(async () => await updater.StageAsync(await updater.CheckAsync(request, CancellationToken.None), request, CancellationToken.None));
        Assert.AreEqual(UpdateErrorCodes.HashMismatch, failure.Code);
        Assert.IsFalse(Directory.Exists(temp.Combine("versions", "4.0.1")));
    }

    [TestMethod]
    public async Task AFeedWithoutAHashNeedsTheExpectedOne()
    {
        using var temp = new TempFolder();
        var source = Directory.CreateDirectory(temp.Combine("packages")).FullName;
        var package = File.ReadAllBytes(Packages.Write(source, "4.0.1"));
        var handler = new FakeNuGetFeed { Registrations = false };
        handler.Add(PackageId, "4.0.1", package);
        handler.Add(Packages.Product.PackageId, "4.0.1", File.ReadAllBytes(Packages.WriteTool(source, "4.0.1")));
        var updater = Updater(temp, "4.0.0", new HttpClient(handler));
        var request = new UpdateRequest { Source = FakeNuGetFeed.Index };
        var check = await updater.CheckAsync(request, CancellationToken.None);
        Assert.AreEqual("4.0.1", check.Target?.ToString(), "the flat container lists the versions");
        var failure = await Assert.ThrowsExactlyAsync<UpdateException>(() => updater.StageAsync(check, request, CancellationToken.None));
        Assert.AreEqual(UpdateErrorCodes.HashMismatch, failure.Code);
        StringAssert.Contains(failure.Message, "--sha512");

        var withHash = request with { ExpectedSha512 = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(package)) };
        Assert.AreEqual("expected hash", (await updater.StageAsync(check, withHash, CancellationToken.None)).Checked);
    }

    [TestMethod]
    public async Task APackageWithoutTheRuntimeFolderOrWithAnEscapingEntryIsRefused()
    {
        using var temp = new TempFolder();
        var feed = Directory.CreateDirectory(temp.Combine("feed")).FullName;
        Packages.Write(feed, "4.0.1", withAssembly: false);
        var updater = Updater(temp, "4.0.0");
        var request = new UpdateRequest { Source = feed };
        var missing = await Assert.ThrowsExactlyAsync<UpdateException>(async () => await updater.StageAsync(await updater.CheckAsync(request, CancellationToken.None), request, CancellationToken.None));
        Assert.AreEqual(UpdateErrorCodes.InvalidPackage, missing.Code);

        var escaping = Packages.Write(feed, "4.0.2", extra: [("tools/net10.0/win-x64/..%2F..%2Fescaped.txt", "x")]);
        var destination = temp.Combine("out");
        var failure = Assert.ThrowsExactly<UpdateException>(() => SelfUpdater.Extract(escaping, destination, Packages.Product, Packages.Runtime));
        Assert.AreEqual(UpdateErrorCodes.InvalidPackage, failure.Code);
        Assert.IsFalse(File.Exists(temp.Combine("escaped.txt")));

        var notZip = Path.Combine(feed, $"{PackageId}.4.0.3.nupkg");
        File.WriteAllText(notZip, "not a zip");
        Assert.AreEqual(UpdateErrorCodes.InvalidPackage, Assert.ThrowsExactly<UpdateException>(() => SelfUpdater.Extract(notZip, destination, Packages.Product, Packages.Runtime)).Code);
    }
}
