using System.Diagnostics;
using System.Text;
using CodeAlta.Desktop;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopMacBundleTests
{
    // The buffer of the SDK's host: the path of the application, then zeros up to 1025 bytes.
    private static byte[] Apphost(string path = "alta.dll", int zeros = 1025)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("MACHO-HEADER"));
        var name = Encoding.UTF8.GetBytes(path);
        bytes.AddRange(name);
        bytes.AddRange(new byte[zeros - name.Length]);
        bytes.AddRange(Encoding.ASCII.GetBytes("TRAILER"));
        return [.. bytes];
    }

    [TestMethod]
    public void Host_StartsTheApplicationThroughTheLink_AndChangesNothingElse()
    {
        var apphost = Apphost();
        var host = DesktopMacBundle.Host(apphost, "../../../data/app/alta.dll");
        Assert.IsNotNull(host);
        Assert.AreEqual(apphost.Length, host.Length);
        var at = "MACHO-HEADER".Length;
        Assert.AreEqual("../../../data/app/alta.dll", Encoding.UTF8.GetString(host, at, "../../../data/app/alta.dll".Length));
        Assert.IsTrue(host.AsSpan(at + "../../../data/app/alta.dll".Length, 1024 - "../../../data/app/alta.dll".Length).IndexOfAnyExcept((byte)0) < 0);
        CollectionAssert.AreEqual(apphost[..at], host[..at]);
        CollectionAssert.AreEqual(apphost[(at + 1024)..], host[(at + 1024)..]);
        // The input is left as it was.
        Assert.AreEqual((byte)'a', apphost[at]);
    }

    [TestMethod]
    public void Host_IsNull_WhenThePathCannotBeReplacedSafely()
    {
        Assert.IsNull(DesktopMacBundle.Host(Apphost("other.dll"), "x/alta.dll"), "no path of the application");
        Assert.IsNull(DesktopMacBundle.Host([.. Apphost(), .. Apphost()], "x/alta.dll"), "two of them");
        Assert.IsNull(DesktopMacBundle.Host(Apphost(), new string('a', 1024)), "too long for the buffer");
        var dirty = Apphost();
        dirty["MACHO-HEADER".Length + 900] = 1;
        Assert.IsNull(DesktopMacBundle.Host(dirty, "x/alta.dll"), "something else follows the path");
        Assert.IsNull(DesktopMacBundle.Host(Apphost(zeros: 64), "x/alta.dll"), "the buffer is cut");
    }

    [TestMethod]
    public void HostPath_IsRelativeToTheBundlesExecutable()
    {
        Assert.AreEqual("../../../../Library/Application Support/CodeAlta/desktop/integration/app/alta.dll".Replace('/', Path.DirectorySeparatorChar),
            DesktopMacBundle.HostPath(Path.Combine("/Users", "me", "Applications", "CodeAlta.app"),
                Path.Combine("/Users", "me", "Library", "Application Support", "CodeAlta", "desktop", "integration", "app")));
    }

    [TestMethod]
    public void Fingerprint_DependsOnTheContent()
    {
        var host = new byte[] { 1, 2, 3 };
        var hosted = DesktopMacBundle.Fingerprint("plist", host, "script", "alta.icns");
        Assert.AreEqual(hosted, DesktopMacBundle.Fingerprint("plist", [1, 2, 3], "other script", "alta.icns"), "a host leaves the script out");
        Assert.IsTrue(hosted.StartsWith("host ", StringComparison.Ordinal));
        Assert.IsTrue(DesktopMacBundle.Fingerprint("plist", null, "script", "alta.icns").StartsWith("script ", StringComparison.Ordinal));
        Assert.AreNotEqual(hosted, DesktopMacBundle.Fingerprint("plist", [1, 2, 4], "script", "alta.icns"));
        Assert.AreNotEqual(hosted, DesktopMacBundle.Fingerprint("plist2", host, "script", "alta.icns"));
        Assert.AreNotEqual(hosted, DesktopMacBundle.Fingerprint("plist", host, "script", "alta-full.icns"));
    }

    [TestMethod]
    public void LoginShell_BecomesTheBundleAgain()
    {
        CollectionAssert.AreEqual(new string?[] { "/bin/zsh", "-l", "-c", "exec \"$0\" \"$@\"", "/Users/me/Applications/CodeAlta.app/Contents/MacOS/CodeAlta", "a b", "--dev", null },
            DesktopMacBundle.LoginShellArguments("/bin/zsh", "/Users/me/Applications/CodeAlta.app/Contents/MacOS/CodeAlta", ["a b", "--dev"]));
        Assert.IsTrue(DesktopMacBundle.IsBundleExecutable("/Users/me/Applications/CodeAlta.app/Contents/MacOS/CodeAlta"));
        Assert.IsFalse(DesktopMacBundle.IsBundleExecutable("/Users/me/.dotnet/tools/.store/codealta/1.9.0/codealta.osx-arm64/1.9.0/tools/net10.0/osx-arm64/alta"));
        Assert.IsFalse(DesktopMacBundle.IsBundleExecutable(null));
        foreach (var shell in new[] { "/bin/zsh", "/opt/homebrew/bin/bash", "/bin/sh", "/bin/dash", "/bin/ksh" }) Assert.IsTrue(DesktopMacBundle.IsLoginShell(shell), shell);
        foreach (var shell in new[] { "/opt/homebrew/bin/fish", "/usr/bin/nu", "zsh", "", null }) Assert.IsFalse(DesktopMacBundle.IsLoginShell(shell), shell ?? "null");
    }

    [TestMethod]
    public void AppLink_NamesTheRunningVersion()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("The link of the bundle is a macOS file.");
        var root = Path.Combine(Path.GetTempPath(), "codealta-applink-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (first, second) = (Path.Combine(root, "store", "1.0.0"), Path.Combine(root, "store", "1.1.0"));
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            var link = DesktopMacBundle.AppLink(Path.Combine(root, "data"));
            Assert.IsTrue(DesktopMacBundle.RefreshAppLink(link, first + "/"));
            Assert.AreEqual(first, new DirectoryInfo(link).LinkTarget);
            Assert.IsFalse(DesktopMacBundle.RefreshAppLink(link, first), "already there");
            Assert.IsTrue(DesktopMacBundle.RefreshAppLink(link, second));
            Assert.AreEqual(second, new DirectoryInfo(link).LinkTarget);
            Directory.Delete(second);
            Assert.IsTrue(DesktopMacBundle.RefreshAppLink(link, first), "a link to a removed version is replaced");
            Assert.AreEqual(first, new DirectoryInfo(link).LinkTarget);
            Assert.IsFalse(File.Exists(link + ".new"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void Bundle_IsSignedAsCodeAlta_AndStartsTheApplicationOfItsLink()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("A bundle is a macOS application.");
            return;
        }
        var apphost = Path.Combine(AppContext.BaseDirectory, "alta");
        if (!File.Exists(apphost)) Assert.Inconclusive("The host of the application is not beside the tests.");
        var root = Path.Combine(Path.GetTempPath(), "codealta-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bundle = Path.Combine(root, "Applications", "CodeAlta.app");
            var link = DesktopMacBundle.AppLink(Path.Combine(root, "data"));
            DesktopMacBundle.RefreshAppLink(link, AppContext.BaseDirectory);
            var host = DesktopMacBundle.Host(File.ReadAllBytes(apphost), DesktopMacBundle.HostPath(bundle, link)!);
            Assert.IsNotNull(host, "the SDK's host has the path of the application where it is replaced");
            Directory.CreateDirectory(bundle);
            File.WriteAllText(Path.Combine(bundle, "kept"), string.Empty);
            Assert.IsTrue(DesktopMacBundle.Write(bundle, DesktopIntegration.MacInfoPlist(null), host, "#!/bin/sh\n", icon: null));

            // The system knows the process as the bundle: its identifier, its Info.plist.
            var signature = Run("/usr/bin/codesign", "-dvv", bundle);
            StringAssert.Contains(signature, "Identifier=org.codealta.desktop");
            StringAssert.Contains(signature, "Format=app bundle with Mach-O");
            // Its folder is the one the Dock knows: only its contents were replaced.
            Assert.IsTrue(File.Exists(Path.Combine(bundle, "kept")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "Applications", ".CodeAlta-staging")));
            File.Delete(Path.Combine(bundle, "kept"));
            Assert.AreEqual(string.Empty, Run("/usr/bin/codesign", "--verify", "--strict", bundle).Trim());
            // The bundle's own executable starts the application, through the link.
            StringAssert.StartsWith(Run(Path.Combine(bundle, "Contents", "MacOS", "CodeAlta"), "--version"), "alta ");

            // Without a host, the script; a signature of the previous contents does not stay behind.
            Assert.IsFalse(DesktopMacBundle.Write(bundle, DesktopIntegration.MacInfoPlist(null), null, "#!/bin/sh\necho script\n", icon: null));
            Assert.AreEqual("#!/bin/sh\necho script\n", File.ReadAllText(Path.Combine(bundle, "Contents", "MacOS", "CodeAlta")));
            Assert.IsFalse(Directory.Exists(Path.Combine(bundle, "Contents", "_CodeSignature")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static string Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(60)), program);
        return output + error.Result;
    }
}
