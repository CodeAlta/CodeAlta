using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Agent.Copilot;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CopilotRuntimeInstallerTests
{
    private const string Platform = "test-platform";

    [TestMethod]
    public async Task Install_DownloadsChecksAndUnpacksTheRuntime_Once()
    {
        using var temp = TestTempDirectory.Create();
        var archive = CreatePackage();
        var handler = new PackageHandler(archive);
        using var installer = new CopilotRuntimeInstaller(Path.Combine(temp.Path, "cache"), handler, Package(archive));
        var reports = new List<CopilotRuntimeInstallProgress>();

        var installed = await installer.EnsureInstalledAsync(new SynchronousProgress(reports), CancellationToken.None);
        var again = await installer.EnsureInstalledAsync(null, CancellationToken.None);

        var folder = Path.Combine(temp.Path, "cache", CopilotPinnedRuntime.Version, Platform);
        Assert.AreEqual(Path.Combine(folder, CopilotPinnedRuntime.ExecutableName), installed.Executable);
        Assert.AreEqual(CopilotPinnedRuntime.Version, installed.Version);
        Assert.AreEqual("runtime", File.ReadAllText(Path.Combine(folder, "runtime.node")), "the files of the platform lie beside the other files of the root");
        Assert.IsTrue(File.Exists(Path.Combine(folder, "schemas", "api.schema.json")));
        Assert.IsTrue(File.Exists(Path.Combine(folder, "restart-supervisor.js")));
        Assert.IsFalse(File.Exists(Path.Combine(folder, "cli-main.js")), "the terminal CLI is not kept");
        Assert.IsFalse(File.Exists(Path.Combine(folder, "cli-native.node")));
        Assert.IsFalse(Directory.Exists(Path.Combine(folder, "webview")));
        Assert.IsFalse(Directory.Exists(Path.Combine(folder, "prebuilds")));
        Assert.IsFalse(File.Exists(Path.Combine(folder, Package(archive).FileName)), "the archive is removed once unpacked");
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsTrue(File.GetUnixFileMode(installed.Executable).HasFlag(UnixFileMode.UserExecute), "the executable bit of the package is kept");
        }

        Assert.AreEqual(installed, again);
        Assert.AreEqual(1, handler.Requests, "a runtime in the cache is not downloaded again");
        Assert.IsTrue(reports.Exists(static report => report.Received == report.Total && report.Total > 0));
    }

    [TestMethod]
    public async Task Install_RefusesAPackageThatDoesNotMatchItsHash()
    {
        using var temp = TestTempDirectory.Create();
        var archive = CreatePackage();
        using var installer = new CopilotRuntimeInstaller(Path.Combine(temp.Path, "cache"), new PackageHandler(archive),
            Package(archive) with { Sha256 = new string('0', 64) });

        var exception = await Assert.ThrowsExactlyAsync<CopilotRuntimeInstallException>(() => installer.EnsureInstalledAsync(null, CancellationToken.None));

        Assert.AreEqual("checksum", exception.Code);
        Assert.IsNull(installer.Installed);
    }

    [TestMethod]
    public async Task Install_RefusesAPackageWithoutTheRuntimeOfItsPlatform()
    {
        using var temp = TestTempDirectory.Create();
        var archive = CreatePackage(platform: "another-platform");
        using var installer = new CopilotRuntimeInstaller(Path.Combine(temp.Path, "cache"), new PackageHandler(archive), Package(archive));

        var exception = await Assert.ThrowsExactlyAsync<CopilotRuntimeInstallException>(() => installer.EnsureInstalledAsync(null, CancellationToken.None));

        Assert.AreEqual("package", exception.Code);
        Assert.IsNull(installer.Installed);
    }

    [TestMethod]
    public async Task Install_SaysWhenItIsOffline()
    {
        using var temp = TestTempDirectory.Create();
        var archive = CreatePackage();
        using var installer = new CopilotRuntimeInstaller(Path.Combine(temp.Path, "cache"), new PackageHandler(archive) { Offline = true }, Package(archive));

        var exception = await Assert.ThrowsExactlyAsync<CopilotRuntimeInstallException>(() => installer.EnsureInstalledAsync(null, CancellationToken.None));

        Assert.AreEqual("network", exception.Code);
        StringAssert.Contains(exception.Message, "GitHub");
    }

    [TestMethod]
    public async Task Install_InstallsAgain_WhenTheRuntimeWasRemoved()
    {
        using var temp = TestTempDirectory.Create();
        var archive = CreatePackage();
        var handler = new PackageHandler(archive);
        using var installer = new CopilotRuntimeInstaller(Path.Combine(temp.Path, "cache"), handler, Package(archive));
        var installed = await installer.EnsureInstalledAsync(null, CancellationToken.None);

        // An antivirus quarantines the library of the runtime: the folder keeps its marker.
        File.Delete(Path.Combine(Path.GetDirectoryName(installed.Executable)!, "runtime.node"));
        Assert.IsNull(installer.Installed);

        var again = await installer.EnsureInstalledAsync(null, CancellationToken.None).WithinAsync(TestTime.Seconds(30));

        Assert.AreEqual(installed, again);
        Assert.AreEqual(2, handler.Requests, "the package is downloaded again");
    }

    [TestMethod]
    public async Task Install_FailsForAPlatformWithoutAPackage()
    {
        using var temp = TestTempDirectory.Create();
        using var installer = new CopilotRuntimeInstaller(Path.Combine(temp.Path, "cache"), new PackageHandler([]), null);

        var exception = await Assert.ThrowsExactlyAsync<CopilotRuntimeInstallException>(() => installer.EnsureInstalledAsync(null, CancellationToken.None));

        Assert.AreEqual("unsupported_platform", exception.Code);
    }

    [TestMethod]
    public async Task TwoInstancesOfCodeAlta_DownloadTheRuntimeOnce()
    {
        using var temp = TestTempDirectory.Create();
        var archive = CreatePackage();
        var handler = new PackageHandler(archive) { Delay = TimeSpan.FromMilliseconds(700) };
        var root = Path.Combine(temp.Path, "cache");
        using var normal = new CopilotRuntimeInstaller(root, handler, Package(archive));
        using var developer = new CopilotRuntimeInstaller(root, handler, Package(archive));

        var installed = await Task.WhenAll(normal.EnsureInstalledAsync(null, CancellationToken.None), developer.EnsureInstalledAsync(null, CancellationToken.None));

        Assert.AreEqual(installed[0], installed[1]);
        Assert.AreEqual(1, handler.Requests, "the instance that found the other one downloading waited for it");
    }

    [TestMethod]
    public void Destination_KeepsWhatTheSdkKeeps()
    {
        Assert.AreEqual("copilot-runtime.exe", CopilotRuntimeInstaller.Destination("package/prebuilds/win32-x64/copilot-runtime.exe", "win32-x64"));
        Assert.AreEqual("runtime.node", CopilotRuntimeInstaller.Destination("package/prebuilds/win32-x64/runtime.node", "win32-x64"));
        Assert.AreEqual("definitions/task.agent.yaml", CopilotRuntimeInstaller.Destination("package/definitions/task.agent.yaml", "win32-x64"));
        Assert.AreEqual("ripgrep/bin/win32-x64/rg.exe", CopilotRuntimeInstaller.Destination("package/ripgrep/bin/win32-x64/rg.exe", "win32-x64"));
        Assert.IsNull(CopilotRuntimeInstaller.Destination("package/prebuilds/win32-x64/cli-native.node", "win32-x64"));
        Assert.IsNull(CopilotRuntimeInstaller.Destination("package/prebuilds/win32-x64/copilot-runtime-bin.exe", "win32-x64"));
        Assert.IsNull(CopilotRuntimeInstaller.Destination("package/prebuilds/darwin-arm64/runtime.node", "win32-x64"));
        Assert.IsNull(CopilotRuntimeInstaller.Destination("package/tree-sitter-c_sharp.wasm", "win32-x64"));
        Assert.IsNull(CopilotRuntimeInstaller.Destination("package/voice-server.js", "win32-x64"));
        Assert.IsNull(CopilotRuntimeInstaller.Destination("package/webview/index.html", "win32-x64"));
        Assert.IsNull(CopilotRuntimeInstaller.Destination("package/../evil.js", "win32-x64"));
        Assert.IsNull(CopilotRuntimeInstaller.Destination("runtime.node", "win32-x64"), "a file outside the top folder is not kept");
    }

    [TestMethod]
    public void PinnedRuntime_HasAPackageForEachPlatformOfTheRelease()
    {
        CollectionAssert.AreEquivalent(
            new[] { "darwin-arm64", "darwin-x64", "linux-arm64", "linux-x64", "linuxmusl-arm64", "linuxmusl-x64", "win32-arm64", "win32-x64" },
            CopilotPinnedRuntime.Packages.Select(static package => package.Platform).ToArray());
        Assert.IsTrue(CopilotPinnedRuntime.Packages.All(static package => package.Sha256.Length == 64 && package.Size > 0));
        Assert.IsNotNull(CopilotPinnedRuntime.FindPackage(CopilotPinnedRuntime.CurrentPlatform()), "this computer has a package");
        Assert.AreEqual($"https://github.com/github/copilot-cli/releases/download/v{CopilotPinnedRuntime.Version}/github-copilot-{CopilotPinnedRuntime.Version}-win32-x64.tgz",
            CopilotPinnedRuntime.AddressOf(CopilotPinnedRuntime.FindPackage("win32-x64")!).ToString());
    }

    private static CopilotRuntimePackage Package(byte[] archive) => new(Platform, archive.Length, Convert.ToHexStringLower(SHA256.HashData(archive)));

    // A package laid out as the Copilot CLI release publishes it, under its top folder.
    private static byte[] CreatePackage(string platform = Platform)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            void Add(string name, string content, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "package/" + name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)), Mode = mode };
                tar.WriteEntry(entry);
            }

            const UnixFileMode executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            Add($"prebuilds/{platform}/{CopilotPinnedRuntime.ExecutableName}", "#!/bin/sh\necho copilot\n", executable);
            Add($"prebuilds/{platform}/runtime.node", "runtime");
            Add($"prebuilds/{platform}/cli-native.node", "cli");
            Add("schemas/api.schema.json", "{}");
            Add("restart-supervisor.js", "//");
            Add("cli-main.js", "//");
            Add("webview/index.html", "<html></html>");
        }

        return stream.ToArray();
    }

    private sealed class SynchronousProgress(List<CopilotRuntimeInstallProgress> reports) : IProgress<CopilotRuntimeInstallProgress>
    {
        public void Report(CopilotRuntimeInstallProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }

    private sealed class PackageHandler(byte[] archive) : HttpMessageHandler
    {
        private int _requests;

        public bool Offline { get; init; }

        public TimeSpan Delay { get; init; }

        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (Offline)
            {
                throw new HttpRequestException("No route to host.");
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        }
    }
}
