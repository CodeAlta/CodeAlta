using System.Text;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using NeoAstra;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopShellTests
{
    [TestMethod]
    // Closing the window keeps the application in the tray, when there is one and the user wants it.
    [DataRow(NeoWindowCloseReason.User, true, true, true, true, DesktopCloseAction.Hide)]
    [DataRow(NeoWindowCloseReason.Programmatic, true, true, true, true, DesktopCloseAction.Hide)]
    [DataRow(NeoWindowCloseReason.User, true, true, true, false, DesktopCloseAction.Hide)]
    // Without a tray, or with the preference off, closing the window is an exit, which the page handles.
    [DataRow(NeoWindowCloseReason.User, true, true, false, true, DesktopCloseAction.RequestExit)]
    [DataRow(NeoWindowCloseReason.User, true, false, true, true, DesktopCloseAction.RequestExit)]
    [DataRow(NeoWindowCloseReason.User, true, false, true, false, DesktopCloseAction.Exit)]
    // Quitting the application (the macOS menu) is never a hide.
    [DataRow(NeoWindowCloseReason.ApplicationQuit, true, true, true, true, DesktopCloseAction.RequestExit)]
    // What cannot wait for an answer exits at once.
    [DataRow(NeoWindowCloseReason.SessionEnd, true, true, true, true, DesktopCloseAction.Exit)]
    [DataRow(NeoWindowCloseReason.System, true, true, true, true, DesktopCloseAction.Exit)]
    [DataRow(NeoWindowCloseReason.User, false, true, true, true, DesktopCloseAction.Exit)]
    public void ClosingTheWindow_HidesItOrExits(NeoWindowCloseReason reason, bool canCancel, bool closeToTray, bool canHide, bool hasPage, object expected)
        => Assert.AreEqual((DesktopCloseAction)expected, DesktopShell.Decide(reason, canCancel, closeToTray, canHide, hasPage));

    [TestMethod]
    public void Exit_AsksOnlyWhileSessionsRun()
    {
        Assert.AreEqual(DesktopCloseAction.ConfirmExit, DesktopShell.DecideExit(confirmed: false, runningSessions: 2, canAsk: true));
        Assert.AreEqual(DesktopCloseAction.Exit, DesktopShell.DecideExit(confirmed: true, runningSessions: 2, canAsk: true));
        Assert.AreEqual(DesktopCloseAction.Exit, DesktopShell.DecideExit(confirmed: false, runningSessions: 0, canAsk: true));
        // Nobody to ask: the request is not lost.
        Assert.AreEqual(DesktopCloseAction.Exit, DesktopShell.DecideExit(confirmed: false, runningSessions: 2, canAsk: false));
        Assert.AreEqual(DesktopCloseAction.RequestExit, DesktopShell.DecideUserExit(hasPage: true));
        Assert.AreEqual(DesktopCloseAction.Exit, DesktopShell.DecideUserExit(hasPage: false));
    }

    [TestMethod]
    public void Preferences_KeepTheApplicationRunningByDefault_AndRememberTheChoice()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.IsTrue(DesktopPreferences.Load(root).CloseToTray);
            Assert.IsTrue(new DesktopPreferences(CloseToTray: false).Save(root));
            Assert.IsFalse(DesktopPreferences.Load(root).CloseToTray);
            Assert.IsTrue(new DesktopPreferences(CloseToTray: true).Save(root));
            Assert.IsTrue(DesktopPreferences.Load(root).CloseToTray);
            // A file that is not preferences gives the defaults; it is never an error at start-up.
            File.WriteAllText(Path.Combine(root, "preferences.json"), "{\"closeToTray\":\"no\"}");
            Assert.IsTrue(DesktopPreferences.Load(root).CloseToTray);
            File.WriteAllText(Path.Combine(root, "preferences.json"), "not json");
            Assert.IsTrue(DesktopPreferences.Load(root).CloseToTray);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void ShellService_WithoutAShell_ChangesNothing()
    {
        var service = new DesktopShellService();
        Assert.AreEqual("unavailable", service.Preferences(new()).Status);
        Assert.IsFalse(service.SetCloseToTray(new(true)).CloseToTray);
        Assert.AreEqual("unavailable", service.Exit(new(Confirmed: true)).Status);
    }

    [TestMethod]
    public void ExitOption_AsksTheRunningInstance_AndStandsAloneOrWithDev()
    {
        static DesktopLaunchOptions? Parse(params string[] args)
            => DesktopCommandLine.TryParse(args, _ => false, _ => false, out var options, out _) ? options : null;

        Assert.IsTrue(Parse("--exit")!.ExitRunning);
        Assert.IsFalse(Parse("--exit")!.Developer);
        Assert.IsTrue(Parse("--dev", "--exit")!.ExitRunning);
        Assert.IsTrue(Parse("--dev", "--exit")!.Developer);
        Assert.IsTrue(Parse("--exit", "--dev")!.Developer);
        Assert.IsFalse(Parse()!.ExitRunning);
        Assert.IsNull(Parse("--exit", "--exit"));
        Assert.IsNull(Parse("--exit", "--data-root", "C:\\data"));
    }

    [TestMethod]
    public void RunningInstances_AreFoundPerProfile()
    {
        var normal = DesktopCommandLine.CreateDefaultOptions(developer: false);
        var developer = DesktopCommandLine.CreateDefaultOptions(developer: true);
        Assert.AreEqual(DesktopApplication.InstanceId(normal), DesktopApplication.InstanceId(DesktopCommandLine.CreateDefaultOptions(developer: false)));
        Assert.AreNotEqual(DesktopApplication.InstanceId(normal), DesktopApplication.InstanceId(developer));
        Assert.IsTrue(DesktopApplication.InstanceId(normal).StartsWith("org.codealta.desktop.", StringComparison.Ordinal));
        Assert.IsTrue(DesktopApplication.InstanceId(normal).Length <= 192);
    }

    [TestMethod]
    public void OnlyAGloballyInstalledTool_HasALauncher()
    {
        Assert.AreEqual(@"C:\Users\me\.dotnet\tools\alta.exe",
            DesktopIntegration.InstalledLauncher(@"C:\Users\me\.dotnet\tools\.store\codealta\1.2.3\codealta\1.2.3\tools\net10.0\any\", windows: true));
        Assert.AreEqual("/Users/me/.dotnet/tools/alta",
            DesktopIntegration.InstalledLauncher("/Users/me/.dotnet/tools/.store/codealta/1.2.3/codealta/1.2.3/tools/net10.0/any/", windows: false));
        // A build output, a local tool manifest's cache: nothing to add to the desktop.
        Assert.IsNull(DesktopIntegration.InstalledLauncher(@"C:\code\CodeAlta\src\CodeAlta\bin\Debug\net10.0\", windows: true));
        Assert.IsNull(DesktopIntegration.InstalledLauncher("/home/me/code/CodeAlta/src/CodeAlta/bin/Release/net10.0/", windows: false));
    }

    [TestMethod]
    public void MacBundle_IsAScriptThatBecomesTheInstalledTool()
    {
        var plist = DesktopIntegration.MacInfoPlist("1.2.3+build<&>");
        StringAssert.Contains(plist, "<key>CFBundleExecutable</key><string>CodeAlta</string>");
        StringAssert.Contains(plist, "<key>CFBundleIdentifier</key><string>org.codealta.desktop</string>");
        StringAssert.Contains(plist, "<key>CFBundleIconFile</key><string>alta</string>");
        StringAssert.Contains(plist, "<string>1.2.3+build&lt;&amp;&gt;</string>");
        Assert.IsTrue(plist.StartsWith("<?xml", StringComparison.Ordinal));
        Assert.IsFalse(plist.Contains('\r'));
        // A property list is XML: it must parse.
        _ = System.Xml.Linq.XDocument.Parse(plist);

        var script = DesktopIntegration.MacLauncherScript("/Users/o'brien/.dotnet/tools/alta", "/usr/local/share/dotnet");
        Assert.IsTrue(script.StartsWith("#!/bin/sh\n", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains('\r'));
        // The path is one shell word whatever it contains.
        StringAssert.Contains(script, "TOOL='/Users/o'\\''brien/.dotnet/tools/alta'\n");
        StringAssert.Contains(script, "export DOTNET_ROOT='/usr/local/share/dotnet'");
        // The tool replaces the script (exec), through the login shell for the user's PATH.
        StringAssert.Contains(script, "exec \"$SHELL\" -l -c 'exec \"$0\" \"$@\"' \"$TOOL\" \"$@\"");
        Assert.IsTrue(script.EndsWith("exec \"$TOOL\" \"$@\"\n", StringComparison.Ordinal));
        Assert.IsFalse(DesktopIntegration.MacLauncherScript("/Users/me/.dotnet/tools/alta", null).Contains("DOTNET_ROOT", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LinuxEntry_StartsTheInstalledTool()
    {
        var entry = DesktopIntegration.LinuxDesktopEntry("/home/me/.dotnet/tools/alta", "/home/me/.local/share/CodeAlta/desktop/integration/alta.png");
        Assert.IsTrue(entry.StartsWith("[Desktop Entry]\nType=Application\nName=CodeAlta\n", StringComparison.Ordinal));
        StringAssert.Contains(entry, "Exec=\"/home/me/.dotnet/tools/alta\"\n");
        StringAssert.Contains(entry, "Icon=/home/me/.local/share/CodeAlta/desktop/integration/alta.png\n");
        StringAssert.Contains(entry, "Terminal=false\n");
        Assert.IsFalse(DesktopIntegration.LinuxDesktopEntry("/home/me/.dotnet/tools/alta", null).Contains("Icon=", StringComparison.Ordinal));
        // Characters the entry's syntax reserves are escaped.
        Assert.AreEqual("\"/home/a b/50%%/\\$x/alta\"", DesktopIntegration.DesktopEntryArgument("/home/a b/50%/$x/alta"));
    }

    [TestMethod]
    public void WindowsShortcut_IsWrittenForTheLauncher()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The Start Menu shortcut is a Windows file.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "codealta-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var launcher = Path.Combine(root, "tools", "alta.exe");
            var shortcut = Path.Combine(root, "menu", "CodeAlta.lnk");
            DesktopIntegration.WriteWindowsShortcut(shortcut, launcher, icon: null);
            var bytes = File.ReadAllBytes(shortcut);
            Assert.IsTrue(bytes.Length > 100);
            // A shell link starts with its header size and class identifier, and names its target and identity.
            Assert.AreEqual(0x4C, bytes[0]);
            Assert.IsTrue(Holds(bytes, "alta.exe"), "the target");
            Assert.IsTrue(Holds(bytes, DesktopIntegration.WindowsAppId), "the application identity");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Text in a shell link is stored in either width, at any offset.
    private static bool Holds(byte[] bytes, string text)
        => bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0 || bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;
}
