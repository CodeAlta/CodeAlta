using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using CodeAlta.Agent.Copilot;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CopilotCliInstallerTests
{
    [TestMethod]
    public void ResolvePackage_MapsWindowsX64ToNpmPlatform()
    {
        var package = CopilotCliInstaller.ResolvePackage(
            CopilotCliPlatformKind.Windows,
            Architecture.X64,
            "win-x64");

        Assert.AreEqual("win32-x64", package.PlatformName);
        Assert.AreEqual("copilot.exe", package.BinaryName);
    }

    [TestMethod]
    public void ResolvePackage_RejectsLinuxMuslRid()
    {
        var exception = Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            CopilotCliInstaller.ResolvePackage(
                CopilotCliPlatformKind.Linux,
                Architecture.X64,
                "linux-musl-x64"));

        StringAssert.Contains(exception.Message, "musl");
    }

    [TestMethod]
    public void BuildPackageUri_UsesNpmPackageTarballPath()
    {
        var uri = CopilotCliInstaller.BuildPackageUri(
            "https://registry.example.test/npm/",
            "darwin-arm64",
            "1.0.44-2");

        Assert.AreEqual(
            "https://registry.example.test/npm/@github/copilot-darwin-arm64/-/copilot-darwin-arm64-1.0.44-2.tgz",
            uri.AbsoluteUri);
    }

    [TestMethod]
    public void GetInstallDirectory_UsesVersionedPlatformCacheDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-cache-root");

        var directory = CopilotCliInstaller.GetInstallDirectory(root, "1.0.44-2", "linux-x64");

        Assert.AreEqual(
            Path.Combine(root, "bin", "copilot", "1.0.44-2", "linux-x64"),
            directory);
    }

    [TestMethod]
    public async Task ExtractPackageBinaryAsync_ExtractsPackageBinaryToInstallRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "CodeAltaTests", Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(tempRoot, "source");
        var archivePath = Path.Combine(tempRoot, "copilot.tgz");
        var destinationRoot = Path.Combine(tempRoot, "destination");

        try
        {
            Directory.CreateDirectory(Path.Combine(sourceRoot, "package"));
            await File.WriteAllTextAsync(
                    Path.Combine(sourceRoot, "package", "copilot.exe"),
                    "copilot binary",
                    Encoding.UTF8)
                .ConfigureAwait(false);

            await using (var archiveStream = File.Create(archivePath))
            await using (var gzipStream = new GZipStream(archiveStream, CompressionLevel.SmallestSize))
            {
                TarFile.CreateFromDirectory(sourceRoot, gzipStream, includeBaseDirectory: false);
            }

            await CopilotCliInstaller
                .ExtractPackageBinaryAsync(archivePath, destinationRoot, "copilot.exe", CancellationToken.None)
                .ConfigureAwait(false);

            var extractedPath = Path.Combine(destinationRoot, "copilot.exe");
            Assert.IsTrue(File.Exists(extractedPath));
            Assert.AreEqual("copilot binary", await File.ReadAllTextAsync(extractedPath).ConfigureAwait(false));
            Assert.IsFalse(Directory.Exists(Path.Combine(destinationRoot, "package")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
