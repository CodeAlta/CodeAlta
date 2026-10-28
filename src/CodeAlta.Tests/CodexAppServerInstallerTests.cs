using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.CodexAppServer;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexAppServerInstallerTests
{
    private const string Target = "test-target";

    [TestMethod]
    public async Task Install_DownloadsChecksAndUnpacksThePackage_Once()
    {
        using var temp = TestTempDirectory.Create();
        var archive = CreatePackage(CodexPinnedRelease.Version, Target);
        var handler = new PackageHandler(archive);
        using var installer = new CodexAppServerInstaller(Path.Combine(temp.Path, "cache"), handler, Package(archive));
        var reports = new List<CodexInstallProgress>();

        var installed = await installer.EnsureInstalledAsync(new SynchronousProgress(reports), CancellationToken.None);
        var again = await installer.EnsureInstalledAsync(null, CancellationToken.None);

        Assert.AreEqual(Path.Combine(temp.Path, "cache", CodexPinnedRelease.Version, Target, "bin", "codex-app-server"), installed.Executable);
        Assert.AreEqual(Path.Combine(temp.Path, "cache", CodexPinnedRelease.Version, Target, "codex-path"), installed.PathDirectory);
        Assert.AreEqual(CodexPinnedRelease.Version, installed.Version);
        Assert.AreEqual("#!/bin/sh\necho codex\n", File.ReadAllText(installed.Executable));
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsTrue(File.GetUnixFileMode(installed.Executable).HasFlag(UnixFileMode.UserExecute), "the executable bit of the package is kept");
        }

        Assert.AreEqual(installed, again);
        Assert.AreEqual(1, handler.Requests, "a package in the cache is not downloaded again");
        Assert.IsTrue(reports.Exists(static report => report.Received == report.Total && report.Total > 0));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(installed.Executable)!)!, Package(archive).FileName)), "the archive is removed once unpacked");
    }

    [TestMethod]
    public async Task Install_RefusesAPackageThatDoesNotMatchItsHash()
    {
        using var temp = TestTempDirectory.Create();
        var archive = CreatePackage(CodexPinnedRelease.Version, Target);
        using var installer = new CodexAppServerInstaller(Path.Combine(temp.Path, "cache"), new PackageHandler(archive),
            Package(archive) with { Sha256 = new string('0', 64) });

        var exception = await Assert.ThrowsExactlyAsync<CodexInstallException>(() => installer.EnsureInstalledAsync(null, CancellationToken.None));

        Assert.AreEqual("checksum", exception.Code);
        Assert.IsNull(installer.Installed);
    }

    [TestMethod]
    public async Task Install_RefusesAPackageOfAnotherVersion()
    {
        using var temp = TestTempDirectory.Create();
        var archive = CreatePackage("0.1.0", Target);
        using var installer = new CodexAppServerInstaller(Path.Combine(temp.Path, "cache"), new PackageHandler(archive), Package(archive));

        var exception = await Assert.ThrowsExactlyAsync<CodexInstallException>(() => installer.EnsureInstalledAsync(null, CancellationToken.None));

        Assert.AreEqual("package", exception.Code);
        Assert.IsNull(installer.Installed);
    }

    [TestMethod]
    public async Task Install_SaysWhenItIsOffline()
    {
        using var temp = TestTempDirectory.Create();
        var archive = CreatePackage(CodexPinnedRelease.Version, Target);
        using var installer = new CodexAppServerInstaller(Path.Combine(temp.Path, "cache"), new PackageHandler(archive) { Offline = true }, Package(archive));

        var exception = await Assert.ThrowsExactlyAsync<CodexInstallException>(() => installer.EnsureInstalledAsync(null, CancellationToken.None));

        Assert.AreEqual("network", exception.Code);
        StringAssert.Contains(exception.Message, "GitHub");
    }

    [TestMethod]
    public async Task TwoInstancesOfCodeAlta_DownloadThePackageOnce()
    {
        using var temp = TestTempDirectory.Create();
        var archive = CreatePackage(CodexPinnedRelease.Version, Target);
        var handler = new PackageHandler(archive) { Delay = TimeSpan.FromMilliseconds(700) };
        var root = Path.Combine(temp.Path, "cache");
        using var normal = new CodexAppServerInstaller(root, handler, Package(archive));
        using var developer = new CodexAppServerInstaller(root, handler, Package(archive));

        var installed = await Task.WhenAll(normal.EnsureInstalledAsync(null, CancellationToken.None), developer.EnsureInstalledAsync(null, CancellationToken.None));

        Assert.AreEqual(installed[0], installed[1]);
        Assert.AreEqual(1, handler.Requests, "the instance that found the other one downloading waited for it");
    }

    [TestMethod]
    public void PinnedRelease_HasAPackageForEachPlatformOfCodex()
    {
        CollectionAssert.AreEquivalent(
            new[] { "aarch64-apple-darwin", "x86_64-apple-darwin", "aarch64-unknown-linux-musl", "x86_64-unknown-linux-musl", "aarch64-pc-windows-msvc", "x86_64-pc-windows-msvc" },
            CodexPinnedRelease.Packages.Select(static package => package.Target).ToArray());
        Assert.IsTrue(CodexPinnedRelease.Packages.All(static package => package.Sha256.Length == 64 && package.Size > 0));
        Assert.IsNotNull(CodexPinnedRelease.FindPackage(CodexPinnedRelease.CurrentTarget()), "this computer has a package");
        Assert.AreEqual($"https://github.com/openai/codex/releases/download/rust-v{CodexPinnedRelease.Version}/codex-app-server-package-x86_64-pc-windows-msvc.tar.gz",
            CodexPinnedRelease.AddressOf(CodexPinnedRelease.FindPackage("x86_64-pc-windows-msvc")!).ToString());
    }

    private static CodexAppServerPackage Package(byte[] archive) => new(Target, archive.Length, Convert.ToHexStringLower(SHA256.HashData(archive)));

    // A package laid out as Codex publishes it: the entry point, a tool on the PATH, and the manifest.
    private static byte[] CreatePackage(string version, string target)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            void Add(string name, string content, UnixFileMode mode)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)), Mode = mode };
                tar.WriteEntry(entry);
            }

            const UnixFileMode executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            Add("bin/codex-app-server", "#!/bin/sh\necho codex\n", executable);
            Add("codex-path/rg", "#!/bin/sh\n", executable);
            Add("codex-package.json", $$"""{"layoutVersion":1,"version":"{{version}}","target":"{{target}}","variant":"codex-app-server","entrypoint":"bin/codex-app-server","resourcesDir":"codex-resources","pathDir":"codex-path"}""",
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return stream.ToArray();
    }

    private sealed class SynchronousProgress(List<CodexInstallProgress> reports) : IProgress<CodexInstallProgress>
    {
        public void Report(CodexInstallProgress value)
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
