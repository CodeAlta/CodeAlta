using System.Reflection;
using System.Text.Json;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopStartupTests
{
    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    [DataRow("--version")]
    public void EarlyFlags_DoNotEnterNativeStartup(string flag)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = DesktopCommandLine.Run([flag], output, error, _ => throw new AssertFailedException("Native startup was entered."));
        Assert.AreEqual(0, exit);
        StringAssert.Contains(output.ToString(), "alta");
        Assert.AreEqual("", error.ToString());
    }

    [TestMethod]
    public void NoArguments_UsesStableLocalDesktopDataAndAnInteractiveOwnedHost()
    {
        var calls = 0;

        var exit = DesktopCommandLine.Run([], TextWriter.Null, TextWriter.Null, actual =>
        {
            calls++;
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
                localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            Assert.AreEqual(Path.Combine(localData, "CodeAlta", "desktop"), actual.DataRoot);
            Assert.AreEqual(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta"),
                actual.CatalogRoot);
            Assert.IsNotNull(actual.Owned);
            Assert.AreEqual(Path.GetFullPath(Environment.CurrentDirectory), actual.Owned.Project);
            Assert.IsNull(actual.Owned.Home);
            Assert.IsNull(actual.Owned.Instructions);
            Assert.IsNull(actual.Owned.Builtin);
            StringAssert.EndsWith(actual.DataRoot, Path.Combine("CodeAlta", "desktop"));
            return 7;
        });

        Assert.AreEqual(1, calls);
        Assert.AreEqual(7, exit);
    }

    [TestMethod]
    public void ExplicitCatalogOptions_RemainReadOnly()
    {
        var root = OperatingSystem.IsWindows() ? @"Q:\catalog" : "/catalog";
        var args = new[] { "--data-root", root + "/browser", "--catalog-root", root + "/copy", "--allow-catalog-cache" };
        bool DirectoryExists(string path) => path.EndsWith("copy", StringComparison.Ordinal);

        Assert.IsTrue(DesktopCommandLine.TryParse(args, DirectoryExists, _ => false, out var options, out _));
        Assert.IsNotNull(options);
        Assert.IsNull(options.Owned);
    }

    [TestMethod]
    public void MalformedAndUnsafeExplicitRoots_AreRejectedBeforeNativeStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-desktop-test-" + Guid.NewGuid().ToString("N"));
        string[][] cases = [["--unknown"], ["--smoke"], ["--data-root", "relative"],
            ["--data-root", Path.GetTempPath()], ["--data-root", Path.Combine(root, ".alta", "child")],
            ["--data-root", Path.Combine(root, ".ALTA", "child")], ["--help", "--data-root", root]];
        foreach (var args in cases)
        {
            using var error = new StringWriter();
            var exit = DesktopCommandLine.Run(args, TextWriter.Null, error, _ => throw new AssertFailedException("Native startup was entered."));
            Assert.AreEqual(2, exit, string.Join(' ', args));
            Assert.IsFalse(string.IsNullOrWhiteSpace(error.ToString()));
        }
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public void ExplicitRoot_IsForwardedWithoutCreatingStorage_AndNativeExitIsPreserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-desktop-test-" + Guid.NewGuid().ToString("N"));
        var calls = 0;
        var exit = DesktopCommandLine.Run(["--data-root", root], TextWriter.Null, TextWriter.Null, actual =>
        {
            calls++;
            Assert.AreEqual(root, actual.DataRoot);
            Assert.IsNull(actual.CatalogRoot);
            Assert.IsFalse(Directory.Exists(root));
            return 7;
        });
        Assert.AreEqual(1, calls);
        Assert.AreEqual(7, exit);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public void NativeStartupFailure_IsReportedAsFailure()
    {
        using var error = new StringWriter();
        var root = Path.Combine(Path.GetTempPath(), "codealta-desktop-test-" + Guid.NewGuid().ToString("N"));
        Assert.AreEqual(1, DesktopCommandLine.Run(["--data-root", root], TextWriter.Null, error,
            _ => throw new InvalidOperationException("native unavailable")));
        StringAssert.Contains(error.ToString(), "native unavailable");
    }

    [TestMethod]
    public void ExistingFileRoot_IsRejectedWithoutChangingTheFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "codealta-desktop-test-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "owned test sentinel");
        try
        {
            Assert.AreEqual(2, DesktopCommandLine.Run(["--data-root", file], TextWriter.Null, TextWriter.Null,
                _ => throw new AssertFailedException("Native startup was entered.")));
            Assert.AreEqual("owned test sentinel", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public void BootRpc_ProjectsOnlyDevelopmentMetadata_WithGeneratedJson()
    {
        var status = new BootService().Status(new BootRequest());
        Assert.AreEqual("in-development", status.State);
        Assert.AreEqual("CodeAlta", status.ProductName);
        Assert.IsFalse(status.HostAvailable);
        var json = JsonSerializer.Serialize(status, DesktopJsonContext.Default.BootStatus);
        Assert.AreEqual(status, JsonSerializer.Deserialize(json, DesktopJsonContext.Default.BootStatus));
        StringAssert.Contains(json, "\"hostAvailable\":false");
    }

    [TestMethod]
    public void BootRpc_VersionMatchesTheRunningDesktopAssemblyInBothModes()
    {
        var runningVersion = typeof(DesktopCommandLine).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";
        Assert.AreEqual(runningVersion, new BootService().Status(new()).Version);
        Assert.AreEqual(runningVersion, new BootService("test-owned-epoch").Status(new()).Version);
    }

    [TestMethod]
    [DataRow("app://codealta/index.html", true)]
    [DataRow("app://codealta/index.html#workspace", true)]
    [DataRow("app://codealta/splash.html", true)]
    [DataRow("app://codealta/other.html", false)]
    [DataRow("app://user@codealta/index.html", false)]
    [DataRow("app://codealta:123/index.html", false)]
    [DataRow("app://codealta/index.html?dev=true", false)]
    [DataRow("https://codealta/index.html", false)]
    [DataRow("javascript:alert(1)", false)]
    [DataRow("file:///tmp/index.html", false)]
    [DataRow("/index.html", false)]
    public void Navigation_OnlyAllowsControlledApplicationDocument(string value, bool expected) =>
        Assert.AreEqual(expected, DesktopApplication.IsApplicationDocument(new Uri(value, UriKind.RelativeOrAbsolute)));
}
