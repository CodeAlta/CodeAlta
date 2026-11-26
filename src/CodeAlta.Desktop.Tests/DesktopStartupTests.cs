using System.Reflection;
using System.Text.Json;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using NeoAstra;

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
    public void Help_DescribesTheDesktopAndItsOptions()
    {
        using var output = new StringWriter();

        var exit = DesktopCommandLine.Run(["--help"], output, TextWriter.Null, _ => throw new AssertFailedException("Native startup was entered."));

        Assert.AreEqual(0, exit);
        var help = output.ToString();
        StringAssert.StartsWith(help, "CodeAlta Desktop");
        foreach (var option in new[] { "--dev", "--wait", "--exit", "--automated", "CODEALTA_AUTOMATED", "--version", "--help", "--data-root", "--catalog-root", "--allow-owned-host" })
            StringAssert.Contains(help, option);
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
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitRoots_RequireOwnedHostConsent(bool withCatalog)
    {
        var root = OperatingSystem.IsWindows() ? @"Q:\desktop-fixture" : "/desktop-fixture";
        var data = Path.Combine(root, "browser");
        var catalog = Path.Combine(root, "copy");
        string[] args = withCatalog
            ? ["--data-root", data, "--catalog-root", catalog, "--allow-catalog-cache"]
            : ["--data-root", data];

        Assert.IsFalse(DesktopCommandLine.TryParse(args, path => path == catalog, _ => false, out var options, out var error));
        Assert.IsNull(options);
        StringAssert.Contains(error, "--allow-owned-host");
    }

    [TestMethod]
    public void MalformedArguments_AreRejectedBeforeNativeStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-desktop-test-" + Guid.NewGuid().ToString("N"));
        string[][] cases = [["--unknown"], ["--smoke"], ["--data-root", "relative"], ["--help", "--data-root", root]];
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
    [DataRow("existing", true)]
    [DataRow(".alta/child", false)]
    [DataRow(".ALTA/child", false)]
    public void UnsafeExplicitDataRoots_AreRejectedBeforeNativeStartup(string relativePath, bool existing)
    {
        var fixture = Directory.CreateTempSubdirectory("codealta-desktop-test-");
        try
        {
            var data = Path.Combine(fixture.FullName, relativePath);
            if (existing) Directory.CreateDirectory(data);
            using var error = new StringWriter();
            var exit = DesktopCommandLine.Run(OwnedArguments(fixture.FullName, data), TextWriter.Null, error,
                _ => throw new AssertFailedException("Native startup was entered."));
            Assert.AreEqual(2, exit, error.ToString());
        }
        finally { fixture.Delete(recursive: true); }
    }

    [TestMethod]
    public void ExplicitRoot_IsForwardedWithoutCreatingStorage_AndNativeExitIsPreserved()
    {
        var fixture = Directory.CreateTempSubdirectory("codealta-desktop-test-");
        try
        {
            var data = Path.Combine(fixture.FullName, "browser");
            var calls = 0;
            var exit = DesktopCommandLine.Run(OwnedArguments(fixture.FullName, data), TextWriter.Null, TextWriter.Null, actual =>
            {
                calls++;
                Assert.AreEqual(data, actual.DataRoot);
                Assert.AreEqual(Path.Combine(fixture.FullName, "copy"), actual.CatalogRoot);
                Assert.IsNotNull(actual.Owned);
                Assert.AreEqual(Path.Combine(fixture.FullName, "project"), actual.Owned.Project);
                Assert.IsFalse(Directory.Exists(data));
                return 7;
            });
            Assert.AreEqual(1, calls);
            Assert.AreEqual(7, exit);
            Assert.IsFalse(Directory.Exists(data));
        }
        finally { fixture.Delete(recursive: true); }
    }

    [TestMethod]
    public void NativeStartupFailure_IsReportedAsFailure()
    {
        using var error = new StringWriter();
        Assert.AreEqual(1, DesktopCommandLine.Run([], TextWriter.Null, error,
            _ => throw new InvalidOperationException("native unavailable")));
        StringAssert.Contains(error.ToString(), "native unavailable");
    }

    [TestMethod]
    public void ExistingFileRoot_IsRejectedWithoutChangingTheFile()
    {
        var fixture = Directory.CreateTempSubdirectory("codealta-desktop-test-");
        try
        {
            var file = Path.Combine(fixture.FullName, "browser");
            File.WriteAllText(file, "owned test sentinel");
            Assert.AreEqual(2, DesktopCommandLine.Run(OwnedArguments(fixture.FullName, file), TextWriter.Null, TextWriter.Null,
                _ => throw new AssertFailedException("Native startup was entered.")));
            Assert.AreEqual("owned test sentinel", File.ReadAllText(file));
        }
        finally { fixture.Delete(recursive: true); }
    }

    private static string[] OwnedArguments(string root, string data)
    {
        var catalog = Directory.CreateDirectory(Path.Combine(root, "copy")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        return ["--data-root", data, "--catalog-root", catalog, "--allow-catalog-cache", "--allow-owned-host",
            "--project-root", project, "--discovery-home", project, "--instruction-root", root, "--builtin-skill-root", project];
    }

    [TestMethod]
    public void BootRpc_ProjectsOwnedHostMetadata_WithGeneratedJson()
    {
        var status = new BootService("epoch").Status(new BootRequest());
        Assert.AreEqual("owned-text-only", status.State);
        Assert.AreEqual("CodeAlta", status.ProductName);
        Assert.AreEqual("epoch", status.HostEpoch);
        Assert.IsTrue(status.HostAvailable);
        var json = JsonSerializer.Serialize(status, DesktopJsonContext.Default.BootStatus);
        Assert.AreEqual(status, JsonSerializer.Deserialize(json, DesktopJsonContext.Default.BootStatus));
        StringAssert.Contains(json, "\"hostAvailable\":true");
    }

    [TestMethod]
    public void BootRpc_VersionMatchesTheRunningDesktopAssemblyInBothModes()
    {
        var runningVersion = typeof(DesktopCommandLine).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";
        Assert.AreEqual(runningVersion, new BootService(developer: false).Status(new()).Version);
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

    [TestMethod]
    public void Navigation_LoadsTheDocumentsTheHostShows()
    {
        // A start shows the start-up screen, then the application; configuration recovery shows both twice.
        Assert.AreEqual(NeoDecisionAction.Allow, Decide(DesktopApplication.StartupDocument));
        Assert.AreEqual(NeoDecisionAction.Allow, Decide(DesktopApplication.ApplicationDocument));
        // The page loads itself again, whatever the view says of the request.
        Assert.AreEqual(NeoDecisionAction.Allow, Decide(DesktopApplication.ApplicationDocument, kind: NeoNavigationKind.Reload));
        Assert.AreEqual(NeoDecisionAction.Allow, Decide(DesktopApplication.ApplicationDocument, userInitiated: true));
        Assert.AreEqual(NeoDecisionAction.Allow, Decide(new Uri("app://codealta/index.html#workspace")));
    }

    [TestMethod]
    [DataRow("app://codealta/other.html")]
    [DataRow("app://codealta/index.html?dev=true")]
    [DataRow("https://codealta/index.html")]
    [DataRow("javascript:alert(1)")]
    [DataRow("/index.html")]
    public void Navigation_NeverLeavesTheApplicationDocuments(string value)
    {
        var uri = new Uri(value, UriKind.RelativeOrAbsolute);
        Assert.AreEqual(NeoDecisionAction.Cancel, Decide(uri));
        Assert.AreEqual(NeoDecisionAction.Cancel, Decide(uri, userInitiated: true));
        Assert.AreEqual(NeoDecisionAction.Cancel, Decide(uri, mainFrame: false));
        Assert.AreEqual(NeoDecisionAction.Cancel, Decide(uri, kind: NeoNavigationKind.Reload));
    }

    // A start that fails once the window shows says why on its start-up screen, which otherwise waited for nothing (#429).
    [TestMethod]
    public void StartFailure_SaysTheReasonAndWhereTheLogIs()
    {
        var logs = Path.Combine(Path.GetTempPath(), "logs");
        var text = DesktopApplication.StartFailure(new AggregateException(new InvalidOperationException("The folder\ncannot be read.")), logs);

        Assert.AreEqual($"CodeAlta could not start: The folder cannot be read. The log is in {logs}.", text);
    }

    private static NeoDecisionAction Decide(Uri uri, bool mainFrame = true, bool userInitiated = false, NeoNavigationKind kind = NeoNavigationKind.NewDocument) =>
        DesktopApplication.DecideNavigation(new NeoNavigationRequest(uri, mainFrame, userInitiated) { Kind = kind }).Action;
}
