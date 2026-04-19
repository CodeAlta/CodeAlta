using System.Runtime.InteropServices;
using CodeAlta.CodexSdk;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexProcessTests
{
    [TestMethod]
    public void ResolveAsset_WindowsX64_UsesWindowsZip()
    {
        var asset = CodexReleaseInstaller.ResolveAsset(CodexPlatform.Windows, Architecture.X64, "win-x64");

        Assert.AreEqual("codex-x86_64-pc-windows-msvc.exe.zip", asset.AssetName);
        Assert.AreEqual("codex-x86_64-pc-windows-msvc.exe", asset.ExecutableName);
        Assert.IsFalse(asset.IsMusl);
    }

    [TestMethod]
    public void ResolveAsset_LinuxArm64Musl_UsesMuslTarball()
    {
        var asset = CodexReleaseInstaller.ResolveAsset(CodexPlatform.Linux, Architecture.Arm64, "linux-musl-arm64");

        Assert.AreEqual("codex-aarch64-unknown-linux-musl.tar.gz", asset.AssetName);
        Assert.AreEqual("codex-aarch64-unknown-linux-musl", asset.ExecutableName);
        Assert.IsTrue(asset.IsMusl);
    }

    [TestMethod]
    public void ResolveAsset_MacOsX64_UsesDarwinTarball()
    {
        var asset = CodexReleaseInstaller.ResolveAsset(CodexPlatform.MacOS, Architecture.X64, "osx-x64");

        Assert.AreEqual("codex-x86_64-apple-darwin.tar.gz", asset.AssetName);
        Assert.AreEqual("codex-x86_64-apple-darwin", asset.ExecutableName);
        Assert.IsFalse(asset.IsMusl);
    }

    [TestMethod]
    public void GetInstallDirectory_UsesLocalBinCodexTag()
    {
        var installDirectory = CodexReleaseInstaller.GetInstallDirectory(
            @"C:\Users\test\.codealta\cache",
            "rust-v0.118.0");

        Assert.AreEqual(
            Path.Combine(@"C:\Users\test\.codealta\cache", "bin", "codex", "rust-v0.118.0"),
            installDirectory);
    }

    [TestMethod]
    public void BuildAssetUri_UsesPinnedReleasePath()
    {
        var uri = CodexReleaseInstaller.BuildAssetUri(
            "rust-v0.118.0",
            "codex-x86_64-pc-windows-msvc.exe.zip");

        Assert.AreEqual(
            "https://github.com/openai/codex/releases/download/rust-v0.118.0/codex-x86_64-pc-windows-msvc.exe.zip",
            uri.ToString());
    }
}
