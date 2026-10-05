using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodeAlta.Hosting;
using CodeAlta.Tui.Views;
using NuGet.Versioning;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaUpdateCheckerTests
{
    [TestMethod]
    public async Task CheckNuGetOrgAsync_ReadsGzippedRegistrationIndex()
    {
        var registry = new Registry { ["codealta.tui"] = ["0.8.0", "0.9.0"], ["codealta.tui.win-x64"] = ["0.8.0", "0.9.0"] };
        using var httpClient = new HttpClient(registry);

        var result = await CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync(
            CodeAltaUpdateChecker.PackageId, NuGetVersion.Parse("0.8.0"), includePrerelease: false, "win-x64", httpClient);

        Assert.IsTrue(result.PackageFound);
        Assert.IsTrue(result.HasNewerVersion);
        Assert.AreEqual("0.9.0", result.LatestVersionText);
        // The command still names the tool package; the lookup also read the package of the platform.
        Assert.AreEqual("CodeAlta.Tui", result.PackageId);
        CollectionAssert.AreEquivalent(new[] { "codealta.tui", "codealta.tui.win-x64" }, registry.Requested);
    }

    [TestMethod]
    public async Task CheckNuGetOrgAsync_WaitsForThePackageOfThePlatform()
    {
        // nuget.org lists the small tool package first: updating then would fail to find the platform's package.
        var registry = new Registry { ["codealta"] = ["1.0.3", "1.0.4", "1.0.5"], ["codealta.win-x64"] = ["1.0.3", "1.0.4"] };
        using var httpClient = new HttpClient(registry);

        var early = await CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync("CodeAlta", NuGetVersion.Parse("1.0.4"), false, "win-x64", httpClient);
        Assert.IsTrue(early.PackageFound);
        Assert.IsFalse(early.HasNewerVersion);
        Assert.AreEqual("1.0.4", early.LatestVersionText);

        // Another platform's package is already there: each platform is told when its own package is.
        registry["codealta.osx-arm64"] = ["1.0.3", "1.0.4", "1.0.5"];
        var other = await CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync("CodeAlta", NuGetVersion.Parse("1.0.4"), false, "osx-arm64", httpClient);
        Assert.IsTrue(other.HasNewerVersion);
        Assert.AreEqual("1.0.5", other.LatestVersionText);

        registry["codealta.win-x64"] = ["1.0.3", "1.0.4", "1.0.5"];
        var later = await CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync("CodeAlta", NuGetVersion.Parse("1.0.4"), false, "win-x64", httpClient);
        Assert.IsTrue(later.HasNewerVersion);
        Assert.AreEqual("1.0.5", later.LatestVersionText);
    }

    [TestMethod]
    public async Task CheckNuGetOrgAsync_NeedsTheToolPackageToo()
    {
        // The other order: the platform's package is listed and the tool package that names it is not yet.
        var registry = new Registry { ["codealta"] = ["1.0.4"], ["codealta.linux-x64"] = ["1.0.4", "1.0.5"] };
        using var httpClient = new HttpClient(registry);

        var result = await CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync("CodeAlta", NuGetVersion.Parse("1.0.4"), false, "linux-x64", httpClient);

        Assert.IsFalse(result.HasNewerVersion);
        Assert.AreEqual("1.0.4", result.LatestVersionText);
    }

    [TestMethod]
    public async Task CheckNuGetOrgAsync_KeepsPrereleasesForPrereleaseBuilds()
    {
        var registry = new Registry
        {
            ["codealta"] = ["1.0.4", "1.1.0-alpha.1", "1.1.0-alpha.2"],
            ["codealta.win-arm64"] = ["1.0.4", "1.1.0-alpha.1"],
        };
        using var httpClient = new HttpClient(registry);

        var stable = await CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync("CodeAlta", NuGetVersion.Parse("1.0.4"), includePrerelease: false, "win-arm64", httpClient);
        Assert.IsFalse(stable.HasNewerVersion);
        var prerelease = await CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync("CodeAlta", NuGetVersion.Parse("1.0.4"), includePrerelease: true, "win-arm64", httpClient);
        Assert.IsTrue(prerelease.HasNewerVersion);
        Assert.AreEqual("1.1.0-alpha.1", prerelease.LatestVersionText);
    }

    [TestMethod]
    public async Task CheckNuGetOrgAsync_WithoutAPackageForThePlatform_ReadsTheToolPackage()
    {
        // A tool that is not packed per runtime (1.0.0 was one package) has nothing else to wait for.
        var registry = new Registry { ["codealta"] = ["1.0.0", "1.0.1"] };
        using var httpClient = new HttpClient(registry);

        var result = await CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync("CodeAlta", NuGetVersion.Parse("1.0.0"), false, "freebsd-x64", httpClient);

        Assert.IsTrue(result.HasNewerVersion);
        Assert.AreEqual("1.0.1", result.LatestVersionText);
    }

    [TestMethod]
    public async Task CheckNuGetOrgAsync_UnpublishedTool_IsNotFound()
    {
        var registry = new Registry { ["codealta.win-x64"] = ["1.0.5"] };
        using var httpClient = new HttpClient(registry);

        var result = await CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync("CodeAlta", NuGetVersion.Parse("1.0.4"), false, "win-x64", httpClient);

        Assert.IsFalse(result.PackageFound);
        Assert.IsFalse(result.HasNewerVersion);
        Assert.IsNull(result.LatestVersion);
    }

    [TestMethod]
    public void RuntimePackageId_NamesThePackageOfAPlatform()
    {
        Assert.AreEqual("CodeAlta.win-x64", CodeAltaNuGetUpdateChecker.RuntimePackageId("CodeAlta", "win-x64"));
        Assert.AreEqual("CodeAlta.Tui.linux-musl-arm64", CodeAltaNuGetUpdateChecker.RuntimePackageId("CodeAlta.Tui", "linux-musl-arm64"));
        Assert.ThrowsExactly<ArgumentException>(() => CodeAltaNuGetUpdateChecker.RuntimePackageId("CodeAlta", " "));
    }

    // nuget.org's registration index for a few packages: gzipped, as the real one answers.
    private sealed class Registry : HttpMessageHandler
    {
        private readonly Dictionary<string, string[]> _packages = new(StringComparer.Ordinal);
        private readonly List<string> _requested = [];

        public string[] this[string package] { set => _packages[package] = value; }

        public string[] Requested { get { lock (_requested) return _requested.Distinct().ToArray(); } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // https://api.nuget.org/v3/registration5-gz-semver2/<package>/index.json
            var package = request.RequestUri!.Segments[^2].TrimEnd('/');
            lock (_requested) _requested.Add(package);
            if (!_packages.TryGetValue(package, out var versions)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var entries = string.Join(",", versions.Select(version => $$"""{ "catalogEntry": { "version": "{{version}}", "listed": true } }"""));
            return Task.FromResult(CreateGzipJsonResponse($$"""{ "items": [ { "items": [ {{entries}} ] } ] }"""));
        }
    }

    private static HttpResponseMessage CreateGzipJsonResponse(string json)
    {
        using var compressedStream = new MemoryStream();
        using (var gzipStream = new GZipStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gzipStream.Write(bytes, 0, bytes.Length);
        }

        var content = new ByteArrayContent(compressedStream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }
}
