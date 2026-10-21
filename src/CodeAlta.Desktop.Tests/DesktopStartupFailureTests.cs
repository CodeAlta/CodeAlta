using CodeAlta.Desktop;
using NeoAstra;

namespace CodeAlta.Desktop.Tests;

/// <summary>What the user reads when the desktop app cannot start its window, wherever it was started from.</summary>
[TestClass]
public sealed class DesktopStartupFailureTests
{
    private static Func<string, string?> Proc(params (string Path, string Value)[] files)
    {
        var known = files.ToDictionary(static file => file.Path, static file => file.Value, StringComparer.Ordinal);
        return path => known.GetValueOrDefault(path);
    }

    [TestMethod]
    public void UserNamespaces_AreRestricted_ForAProcessThatNoAppArmorProfileConfines()
    {
        const string restrict = "/proc/sys/kernel/apparmor_restrict_unprivileged_userns";
        const string current = "/proc/self/attr/apparmor/current";
        // Ubuntu 24.04: the restriction is on, and alta from ~/.dotnet/tools has no profile.
        Assert.IsTrue(DesktopStartupFailure.UserNamespacesRestricted(Proc((restrict, "1\n"), (current, "unconfined\n")), null));
        Assert.IsTrue(DesktopStartupFailure.UserNamespacesRestricted(Proc((restrict, "1\n")), null), "a confinement that cannot be read is none");
        Assert.IsTrue(DesktopStartupFailure.UserNamespacesRestricted(Proc((restrict, "1"), ("/proc/self/attr/current", "unconfined\0")), null), "the older attribute file");
        // A profile of the user's own (the fix) may allow them; the restriction off, or another system, has none.
        Assert.IsFalse(DesktopStartupFailure.UserNamespacesRestricted(Proc((restrict, "1"), (current, "codealta (unconfined)")), null));
        Assert.IsFalse(DesktopStartupFailure.UserNamespacesRestricted(Proc((restrict, "0"), (current, "unconfined")), null));
        Assert.IsFalse(DesktopStartupFailure.UserNamespacesRestricted(Proc(), null), "WSL and systems without AppArmor");
        // Debian's switch, and a limit of zero, forbid them to every program.
        Assert.IsTrue(DesktopStartupFailure.UserNamespacesRestricted(Proc(("/proc/sys/kernel/unprivileged_userns_clone", "0")), null));
        Assert.IsTrue(DesktopStartupFailure.UserNamespacesRestricted(Proc(("/proc/sys/user/max_user_namespaces", "0")), null));
        // The sandbox turned off needs none.
        Assert.IsFalse(DesktopStartupFailure.UserNamespacesRestricted(Proc((restrict, "1")), "1"));
        Assert.IsTrue(DesktopStartupFailure.UserNamespacesRestricted(Proc((restrict, "1")), "0"), "0 does not turn it off");
        if (!OperatingSystem.IsLinux()) Assert.IsFalse(DesktopStartupFailure.SandboxRestricted());
    }

    [TestMethod]
    public void AStartInATerminal_SaysWhatTheApplicationReported_ElseWhatTheSandboxNeeds()
    {
        var logs = Path.Combine("data", "logs");
        var reported = DesktopTerminalStart.Explain(1, "Could not load the 'neoastra_native' native library.\nTo fix it:\n  - Install GTK 4 and WebKitGTK 6.0", false, logs, false, "/x/alta");
        StringAssert.Contains(reported, "exit code 1");
        StringAssert.Contains(reported, "Install GTK 4 and WebKitGTK 6.0");
        StringAssert.Contains(reported, logs);
        Assert.DoesNotContain("--wait", reported, "the report is the answer: nothing more to run");

        // An abort by the sandbox of WebKitGTK is explained with its fixes; the executable is the one a profile names.
        var aborted = DesktopTerminalStart.Explain(134, null, true, logs, false, "/home/me/.dotnet/tools/.store/codealta/1.2.3/alta");
        StringAssert.Contains(aborted, "exit code 134");
        StringAssert.Contains(aborted, DesktopStartupFailure.DisableSandboxVariable + "=1 alta");
        StringAssert.Contains(aborted, "profile codealta /home/me/.dotnet/tools/.store/codealta/1.2.3/alta flags=(unconfined) { userns, }");
        StringAssert.Contains(aborted, "altatui");
        StringAssert.Contains(aborted, DesktopStartupFailure.TroubleshootingUrl);

        Assert.AreEqual(DesktopTerminalStart.Failure(2, logs, false), DesktopTerminalStart.Explain(2, null, false, logs, false, "/x/alta"));
        Assert.IsTrue(DesktopStartupFailure.EndedBySignal(134));
        Assert.IsTrue(DesktopStartupFailure.EndedBySignal(139));
        Assert.IsFalse(DesktopStartupFailure.EndedBySignal(1));
        Assert.IsFalse(DesktopStartupFailure.EndedBySignal(128));
    }

    [TestMethod]
    public void TheFailure_ReachesTheStartThatWaits_WhileItWaits_AndNoFileIsLeft()
    {
        var token = Guid.NewGuid().ToString("N");
        var waiting = DesktopTerminalStart.TokenPath(token);
        try
        {
            // Nobody waits: nothing is written.
            DesktopStartupFailure.Tell(token, "late");
            Assert.IsFalse(File.Exists(DesktopStartupFailure.ReportPath(token)));

            File.WriteAllText(waiting, string.Empty);
            var data = Path.Combine(Path.GetTempPath(), "codealta-startup-" + token);
            var options = new DesktopLaunchOptions(data, null) { StartToken = token };
            var shown = new List<string>();
            var notified = new List<string>();
            var failure = new NeoAstraException(NeoErrorCode.BackendUnavailable, "Could not open display");
            DesktopStartupFailure.Report(options, failure, shown.Add, (_, logs) => notified.Add(logs), noTerminal: true, TextWriter.Null);
            Assert.AreEqual(DesktopStartupFailure.Describe(failure), DesktopStartupFailure.Collect(token));
            Assert.IsFalse(File.Exists(DesktopStartupFailure.ReportPath(token)), "read once, then removed");
            Assert.IsNull(DesktopStartupFailure.Collect(token));
            // A windowed program of Windows has no console: a message box says it. Elsewhere the terminal that waits does.
            Assert.HasCount(OperatingSystem.IsWindows() ? 1 : 0, shown);
            if (OperatingSystem.IsWindows()) StringAssert.EndsWith(shown[0], "The log is in " + Path.Combine(data, "logs"));
            Assert.IsEmpty(notified);

            // Without a terminal (the applications menu, the Dock), a notification says it outside Windows.
            DesktopStartupFailure.Report(options with { StartToken = null }, failure, _ => { }, (_, logs) => notified.Add(logs), noTerminal: true, TextWriter.Null);
            Assert.HasCount(OperatingSystem.IsWindows() ? 0 : 1, notified);
            using var terminal = new StringWriter();
            DesktopStartupFailure.Report(options with { StartToken = null }, failure, _ => { }, (_, logs) => notified.Add(logs), noTerminal: false, terminal);
            Assert.HasCount(OperatingSystem.IsWindows() ? 0 : 1, notified, "alta --wait has it on its terminal");
            StringAssert.Contains(terminal.ToString(), DesktopStartupFailure.Describe(failure)!);

            // A failure the user cannot act on is in the log only.
            shown.Clear();
            DesktopStartupFailure.Report(options, new InvalidOperationException("internal"), shown.Add, (_, _) => notified.Add("x"), noTerminal: true, TextWriter.Null);
            Assert.IsNull(DesktopStartupFailure.Collect(token));
            Assert.IsEmpty(shown);
        }
        finally
        {
            File.Delete(waiting);
            File.Delete(DesktopStartupFailure.ReportPath(token));
        }
    }

    [TestMethod]
    public void ANativeLibraryThatDoesNotLoad_IsExplainedToTheUser_FromTheProbesOfNeoAstra()
    {
        // NeoAstra's guidance, as it writes it: the fixes of the system first, then those for developers.
        const string install = "Install GTK 4 and WebKitGTK 6.0, which libneoastra_native.so depends on (libwebkitgtk-6.0.so.4). On Debian and Ubuntu 24.04 or later: sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4.";
        const string variable = "Set the NEOASTRA_NATIVE_LIBRARY environment variable to the full path of a working libneoastra_native.so to load it from there first.";
        const string package = "No usable neoastra_native.dll was found. Reference the NeoAstra.Core package (NeoAstra references it) so that the build copies it.";
        DesktopStartupFailure.NativeAttempt Attempt(NeoNativeLibraryProbeResult result, params string[] missing) => new(result, missing);

        // WebKitGTK is not installed: what is missing, and how the system gets it; nothing about the variable of developers.
        var linux = DesktopStartupFailure.NativeLibraryText(
            [Attempt(NeoNativeLibraryProbeResult.NotFound), Attempt(NeoNativeLibraryProbeResult.MissingDependency, "libwebkitgtk-6.0.so.4", "libgtk-4.so.1")],
            [install, variable], "linux-x64", otherVersion: false);
        StringAssert.StartsWith(linux, "CodeAlta Desktop could not load the native library that opens its window (linux-x64).");
        StringAssert.Contains(linux, "Missing: libwebkitgtk-6.0.so.4, libgtk-4.so.1.");
        StringAssert.Contains(linux, "  - " + install);
        Assert.DoesNotContain("NEOASTRA_NATIVE_LIBRARY", linux);
        Assert.DoesNotContain("Reinstall CodeAlta", linux, "the library of CodeAlta is there: the system lacks what it needs");
        StringAssert.Contains(linux, DesktopStartupFailure.TroubleshootingUrl);

        // Its own library missing, damaged, of another architecture or version: CodeAlta is reinstalled.
        foreach (var (attempts, otherVersion) in new (DesktopStartupFailure.NativeAttempt[], bool)[]
        {
            ([Attempt(NeoNativeLibraryProbeResult.NotFound), Attempt(NeoNativeLibraryProbeResult.NotFound)], false),
            ([Attempt(NeoNativeLibraryProbeResult.InvalidImage)], false),
            ([Attempt(NeoNativeLibraryProbeResult.ArchitectureMismatch)], false),
            ([Attempt(NeoNativeLibraryProbeResult.Loaded)], true),
        })
        {
            var text = DesktopStartupFailure.NativeLibraryText(attempts, [package, variable], "win-x64", otherVersion);
            StringAssert.Contains(text, "dotnet tool uninstall -g CodeAlta, then dotnet tool install -g CodeAlta");
            Assert.DoesNotContain("Reference the NeoAstra.Core package", text);
        }

        // The Visual C++ runtime of Windows is the user's to install.
        var windows = DesktopStartupFailure.NativeLibraryText([Attempt(NeoNativeLibraryProbeResult.MissingDependency, "VCRUNTIME140.dll")],
            ["neoastra_native.dll needs the Microsoft Visual C++ runtime (MSVCP140.dll, VCRUNTIME140.dll): install the latest Microsoft Visual C++ Redistributable for x64.", variable], "win-x64", false);
        StringAssert.Contains(windows, "Missing: VCRUNTIME140.dll.");
        StringAssert.Contains(windows, "Microsoft Visual C++ Redistributable");

        // An exception without probes says what it can; a failure that is not one of the start says nothing.
        StringAssert.Contains(DesktopStartupFailure.Describe(new NeoAstraNativeLibraryException("Could not load.")), "Reinstall CodeAlta");
        Assert.IsNull(DesktopStartupFailure.Describe(new IOException("disk")));
    }

    [TestMethod]
    public void AWebViewThatCannotStart_IsSaidWithWhatToDo()
    {
        var runtime = new NeoAstraException(NeoErrorCode.RuntimeUnavailable, "WebView2 environment creation failed", "create environment", "webview2", unchecked((int)0x80070002));
        var windows = DesktopStartupFailure.Describe(runtime, "windows", display: true)!;
        StringAssert.Contains(windows, "WebView2 Runtime");
        StringAssert.Contains(windows, DesktopStartupFailure.WebView2Url);

        var noDisplay = DesktopStartupFailure.Describe(new NeoAstraException(NeoErrorCode.BackendUnavailable, "Could not open display"), "linux", display: false)!;
        StringAssert.Contains(noDisplay, "neither DISPLAY nor WAYLAND_DISPLAY");
        StringAssert.Contains(noDisplay, "altatui");

        var webkit = DesktopStartupFailure.Describe(runtime, "linux", display: true)!;
        StringAssert.Contains(webkit, "sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4");

        var other = DesktopStartupFailure.Describe(new NeoAstraException(NeoErrorCode.NativeFailure, "Something broke"), "macos", display: false)!;
        StringAssert.Contains(other, "Something broke (NativeFailure)");
        StringAssert.Contains(other, DesktopStartupFailure.TroubleshootingUrl);
        Assert.IsNull(DesktopStartupFailure.Describe(new TimeoutException(), "linux", display: true));
    }
}
