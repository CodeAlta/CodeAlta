using System.Text;
using System.Text.Json;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Dialogs;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopShellTests
{
    [TestMethod]
    // Closing the window keeps the application in the tray, when there is one and the user chose it.
    [DataRow(NeoWindowCloseReason.User, true, DesktopCloseBehavior.KeepRunning, true, true, DesktopCloseAction.Hide)]
    [DataRow(NeoWindowCloseReason.Programmatic, true, DesktopCloseBehavior.KeepRunning, true, true, DesktopCloseAction.Hide)]
    [DataRow(NeoWindowCloseReason.User, true, DesktopCloseBehavior.KeepRunning, true, false, DesktopCloseAction.Hide)]
    // Until the user has chosen, the page asks; without a page to ask, the window is hidden, as by default.
    [DataRow(NeoWindowCloseReason.User, true, DesktopCloseBehavior.Ask, true, true, DesktopCloseAction.Ask)]
    [DataRow(NeoWindowCloseReason.Programmatic, true, DesktopCloseBehavior.Ask, true, true, DesktopCloseAction.Ask)]
    [DataRow(NeoWindowCloseReason.User, true, DesktopCloseBehavior.Ask, true, false, DesktopCloseAction.Hide)]
    // Without a tray there is nothing to choose, and the user may have chosen to exit: the page handles the exit.
    [DataRow(NeoWindowCloseReason.User, true, DesktopCloseBehavior.KeepRunning, false, true, DesktopCloseAction.RequestExit)]
    [DataRow(NeoWindowCloseReason.User, true, DesktopCloseBehavior.Ask, false, true, DesktopCloseAction.RequestExit)]
    [DataRow(NeoWindowCloseReason.User, true, DesktopCloseBehavior.Exit, true, true, DesktopCloseAction.RequestExit)]
    [DataRow(NeoWindowCloseReason.User, true, DesktopCloseBehavior.Exit, true, false, DesktopCloseAction.Exit)]
    // Quitting the application (the macOS menu) is never a hide, nor a question about the window.
    [DataRow(NeoWindowCloseReason.ApplicationQuit, true, DesktopCloseBehavior.KeepRunning, true, true, DesktopCloseAction.RequestExit)]
    [DataRow(NeoWindowCloseReason.ApplicationQuit, true, DesktopCloseBehavior.Ask, true, true, DesktopCloseAction.RequestExit)]
    // What cannot wait for an answer exits at once.
    [DataRow(NeoWindowCloseReason.SessionEnd, true, DesktopCloseBehavior.Ask, true, true, DesktopCloseAction.Exit)]
    [DataRow(NeoWindowCloseReason.System, true, DesktopCloseBehavior.KeepRunning, true, true, DesktopCloseAction.Exit)]
    [DataRow(NeoWindowCloseReason.User, false, DesktopCloseBehavior.Ask, true, true, DesktopCloseAction.Exit)]
    public void ClosingTheWindow_AsksHidesItOrExits(NeoWindowCloseReason reason, bool canCancel, object onClose, bool canHide, bool hasPage, object expected)
        => Assert.AreEqual((DesktopCloseAction)expected, DesktopShell.Decide(reason, canCancel, (DesktopCloseBehavior)onClose, canHide, hasPage));

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
    public void Preferences_AskByDefault_AndRememberTheChoice()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-preferences-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "preferences.json");
        try
        {
            Assert.AreEqual(DesktopCloseBehavior.Ask, DesktopPreferences.Load(root).OnClose);
            foreach (var behavior in new[] { DesktopCloseBehavior.KeepRunning, DesktopCloseBehavior.Exit, DesktopCloseBehavior.Ask })
            {
                Assert.IsTrue(new DesktopPreferences(behavior).Save(root));
                Assert.AreEqual(behavior, DesktopPreferences.Load(root).OnClose);
            }
            Assert.AreEqual("{\"onClose\":\"ask\"}", File.ReadAllText(file));
            // The switch of the versions before the question was the user's choice: it is kept, and nothing is asked.
            File.WriteAllText(file, "{\"closeToTray\":false}");
            Assert.AreEqual(DesktopCloseBehavior.Exit, DesktopPreferences.Load(root).OnClose);
            File.WriteAllText(file, "{\"closeToTray\":true}");
            Assert.AreEqual(DesktopCloseBehavior.KeepRunning, DesktopPreferences.Load(root).OnClose);
            // A file that is not preferences gives the defaults; it is never an error at start-up.
            foreach (var other in new[] { "{\"closeToTray\":\"no\"}", "{\"onClose\":\"later\"}", "{\"onClose\":true}", "[\"exit\"]", "not json" })
            {
                File.WriteAllText(file, other);
                Assert.AreEqual(DesktopCloseBehavior.Ask, DesktopPreferences.Load(root).OnClose, other);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void CloseBehavior_HasOneNameForTheFileAndThePage()
    {
        foreach (var (behavior, name) in new[] { (DesktopCloseBehavior.Ask, "ask"), (DesktopCloseBehavior.KeepRunning, "keep"), (DesktopCloseBehavior.Exit, "exit") })
        {
            Assert.AreEqual(name, DesktopPreferences.Name(behavior));
            Assert.IsTrue(DesktopPreferences.TryParse(name, out var parsed));
            Assert.AreEqual(behavior, parsed);
        }
        foreach (var other in new[] { null, "", "Ask", "tray", "keep " }) Assert.IsFalse(DesktopPreferences.TryParse(other, out _), other);
    }

    [TestMethod]
    public void ShellService_WithoutAShell_ChangesNothing()
    {
        var service = new DesktopShellService();
        Assert.AreEqual("unavailable", service.Preferences(new()).Status);
        Assert.AreEqual("ask", service.SetOnClose(new("exit")).OnClose);
        Assert.AreEqual("unavailable", service.Hide(new()).Status);
        Assert.AreEqual("unavailable", service.Exit(new(Confirmed: true)).Status);
        // What the page reads: the names of the generated client.
        // Without a shell the width of the conversations is the whole space, and asking for another keeps it.
        Assert.AreEqual(("unavailable", 100), (service.SetSessionWidth(new(70, "session-1")).Status, service.SetSessionWidth(new(70)).SessionWidth));
        Assert.AreEqual("""{"status":"unavailable","onClose":"ask","canKeepRunning":false,"platform":"windows","entryAdded":false,"sessionWidth":100,"sessionWidths":null}""",
            JsonSerializer.Serialize(service.Preferences(new()) with { Platform = "windows" }, DesktopJsonContext.Default.DesktopShellPreferences));
        Assert.AreEqual("""{"kind":"session-width","runningSessions":0,"busyTerminals":0,"sessionWidth":60,"sessionId":"session-1"}""",
            JsonSerializer.Serialize(new DesktopShellEvent("session-width", 0, SessionWidth: 60, SessionId: "session-1"), DesktopJsonContext.Default.DesktopShellEvent));
    }

    [TestMethod]
    public async Task FolderPick_WithoutAShell_IsUnavailable_AndItsTitleIsOneBoundedLine()
    {
        var response = await new DesktopShellService().PickFolderAsync(new("Add a project folder", null), CancellationToken.None);
        Assert.AreEqual(new DesktopShellPickFolderResponse("unavailable", null), response);
        var wire = JsonSerializer.Serialize(response, DesktopJsonContext.Default.DesktopShellPickFolderResponse);
        Assert.AreEqual("""{"status":"unavailable","path":null}""", wire);

        Assert.AreEqual("Add a project folder", DesktopShellService.Title("  Add a project folder\r\n"));
        Assert.AreEqual("Select a folder", DesktopShellService.Title(null));
        Assert.AreEqual("Select a folder", DesktopShellService.Title(" \t "));
        Assert.AreEqual(120, DesktopShellService.Title(new string('x', 400)).Length);
    }

    [TestMethod]
    public async Task FolderPick_ReturnsOnlyAnExistingFolderByItsFullPath()
    {
        var folder = Directory.CreateTempSubdirectory("codealta-folder-pick-").FullName;
        try
        {
            var file = Path.Combine(folder, "file.txt");
            await File.WriteAllTextAsync(file, "x");
            Assert.AreEqual(folder, DesktopFolderPicker.ExistingDirectory($"  {folder}  "));
            Assert.IsNull(DesktopFolderPicker.ExistingDirectory(file), "A file is not a folder.");
            Assert.IsNull(DesktopFolderPicker.ExistingDirectory(Path.Combine(folder, "missing")));
            Assert.IsNull(DesktopFolderPicker.ExistingDirectory("relative"));
            Assert.IsNull(DesktopFolderPicker.ExistingDirectory(null));
            Assert.IsNull(DesktopFolderPicker.ExistingDirectory(folder + "\0"));

            // The answer of the system dialog, where NeoAstra shows it: a folder, a cancellation, or no dialog at all.
            var dialogs = new NeoFakeDialogs();
            dialogs.Enqueue(NeoDesktopResult<IReadOnlyList<string>>.Success([folder]));
            dialogs.Enqueue(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled));
            dialogs.Enqueue(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Unsupported));
            dialogs.Enqueue(NeoDesktopResult<IReadOnlyList<string>>.Success([file]));
            dialogs.Enqueue(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Denied, "path_scope"));
            Assert.AreEqual(new DesktopFolderPick(DesktopFolderPickStatus.Ok, folder),
                await DesktopFolderPicker.ShowNeoAstraDialogAsync(null, dialogs, "Add a project folder", folder, CancellationToken.None));
            foreach (var expected in new[] { DesktopFolderPickStatus.Canceled, DesktopFolderPickStatus.Unavailable, DesktopFolderPickStatus.Failed, DesktopFolderPickStatus.Failed })
            {
                Assert.AreEqual(new DesktopFolderPick(expected),
                    await DesktopFolderPicker.ShowNeoAstraDialogAsync(null, dialogs, "Add a project folder", null, CancellationToken.None));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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
        // The SDK installs a tool packed per runtime as a script on Windows and as a link elsewhere.
        Assert.AreEqual(@"C:\Users\me\.dotnet\tools\alta.cmd",
            DesktopIntegration.InstalledLauncher(@"C:\Users\me\.dotnet\tools\.store\codealta\1.2.3\codealta.win-x64\1.2.3\tools\net10.0\win-x64\", windows: true));
        Assert.AreEqual("/Users/me/.dotnet/tools/alta",
            DesktopIntegration.InstalledLauncher("/Users/me/.dotnet/tools/.store/codealta/1.2.3/codealta.osx-arm64/1.2.3/tools/net10.0/osx-arm64/", windows: false));
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
    public void InstallationTooDeepForWindows_IsSaidInPlainWords()
    {
        // A usual global tool installation is far from the limit.
        var usual = @"C:\Users\me\.dotnet\tools\.store\codealta\1.2.3\codealta\1.2.3\tools\net10.0\any\assets";
        var files = new[] { "index.html", "splash.js", "assets/index-hIHksSFA.js" };
        var longest = DesktopAssetPaths.Longest(usual, files);
        Assert.IsTrue(longest.EndsWith(Path.Combine("assets", "index-hIHksSFA.js"), StringComparison.Ordinal));
        Assert.IsNull(DesktopAssetPaths.Problem(usual, longest));

        // One character short of the limit still works; at the limit Windows refuses the path.
        var root = @"C:\" + new string('d', DesktopAssetPaths.WindowsLimit - 5 - "index.html".Length);
        Assert.AreEqual(DesktopAssetPaths.WindowsLimit - 1, DesktopAssetPaths.Longest(root, ["index.html"]).Length);
        Assert.IsNull(DesktopAssetPaths.Problem(root, DesktopAssetPaths.Longest(root, ["index.html"])));
        var tooLong = DesktopAssetPaths.Longest(root, ["splash.html"]);
        Assert.AreEqual(DesktopAssetPaths.WindowsLimit, tooLong.Length);
        var problem = DesktopAssetPaths.Problem(root, tooLong);
        Assert.IsNotNull(problem);
        StringAssert.Contains(problem, root);
        StringAssert.Contains(problem, "260 characters");
        StringAssert.Contains(problem, "shorter path");
        Assert.AreEqual(string.Empty, DesktopAssetPaths.Longest(root, []));
    }

    [TestMethod]
    public void WindowsEntry_StartsTheExecutableAndTheOthersTheLauncher()
    {
        // The launcher of Windows is a script: a shortcut to it would show a console window.
        Assert.AreEqual(@"C:\Users\me\.dotnet\tools\.store\codealta\1.2.3\codealta.win-x64\1.2.3\tools\net10.0\win-x64\alta.exe",
            DesktopIntegration.EntryStart(@"C:\Users\me\.dotnet\tools\alta.cmd",
                @"C:\Users\me\.dotnet\tools\.store\codealta\1.2.3\codealta.win-x64\1.2.3\tools\net10.0\win-x64\", windows: true));
        Assert.AreEqual("/Users/me/.dotnet/tools/alta",
            DesktopIntegration.EntryStart("/Users/me/.dotnet/tools/alta", "/Users/me/.dotnet/tools/.store/codealta/1.2.3/codealta.osx-arm64/1.2.3/tools/net10.0/osx-arm64/", windows: false));
        // A pin on the taskbar is a copy of the shortcut, which is refreshed with it.
        Assert.AreEqual(@"C:\Users\me\AppData\Roaming\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\CodeAlta.lnk",
            DesktopIntegration.WindowsTaskbarPin(@"C:\Users\me\AppData\Roaming"));
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
