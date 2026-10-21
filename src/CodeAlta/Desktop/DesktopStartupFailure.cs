using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>
/// What the user reads when CodeAlta Desktop cannot start its window, wherever they started it from: the terminal
/// (the start that waits for the window prints it), the Start Menu, the Dock or the applications menu (a message box
/// on Windows, a notification or an alert elsewhere), and the log in any case.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>NeoAstra explains a native library that does not load: the paths it tried, the libraries that are missing
/// (GTK 4 and WebKitGTK 6.0 on Linux, the Visual C++ runtime on Windows), an architecture or a macOS that does not
/// match, and what fixes it, from its probes. Nothing of CodeAlta loads these libraries before NeoAstra.</item>
/// <item>A web view that cannot start (no WebView2 Runtime on Windows, no display on Linux) is said with what to do.</item>
/// <item>On Linux the sandbox of WebKitGTK aborts the process where unprivileged user namespaces are restricted,
/// which no code catches: it is said before, and by the start that waits once the process ended.</item>
/// </list>
/// </remarks>
internal static class DesktopStartupFailure
{
    /// <summary>The variable of WebKitGTK that turns its sandbox off for the process.</summary>
    internal const string DisableSandboxVariable = "WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS";

    /// <summary>The page of the documentation that explains a start that fails.</summary>
    internal const string TroubleshootingUrl = "https://codealta.github.io/docs/troubleshooting/#the-desktop-app-does-not-start";

    /// <summary>Where the WebView2 Runtime is installed from.</summary>
    internal const string WebView2Url = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>What the user reads for a failure of the start, or null when it is not one the user can act on.</summary>
    /// <param name="failure">What the start threw.</param>
    internal static string? Describe(Exception failure)
        => Describe(failure, OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));

    /// <summary>What the user reads for a failure of the start on a system, or null when it is not one the user can act on.</summary>
    /// <param name="failure">What the start threw.</param>
    /// <param name="system"><c>windows</c>, <c>macos</c> or <c>linux</c>.</param>
    /// <param name="display">On Linux, whether the session names a display (X11 or Wayland).</param>
    internal static string? Describe(Exception failure, string system, bool display) => failure switch
    {
        // NeoAstra's own message is for the developer of an application: the user reads what its probes found.
        NeoAstraNativeLibraryException native => NativeLibraryText(native),
        NeoAstraException { Code: NeoErrorCode.RuntimeUnavailable } runtime when system == "windows"
            => $"CodeAlta Desktop could not start the Microsoft Edge WebView2 Runtime, which draws its window: {runtime.Message}."
                + $"{Environment.NewLine}To fix it: install or repair the WebView2 Runtime (Evergreen) from {WebView2Url}, then start CodeAlta again. It is part of Windows 11 and of an up-to-date Windows 10.",
        NeoAstraException window when system == "linux" && !display
            => $"CodeAlta Desktop could not open its window: no display was found (neither DISPLAY nor WAYLAND_DISPLAY is set). {window.Message}."
                + $"{Environment.NewLine}The desktop app needs an X11 or Wayland session; over SSH or in a console, use the terminal app: dotnet tool install -g CodeAlta.Tui, then altatui.",
        NeoAstraException { Code: NeoErrorCode.RuntimeUnavailable or NeoErrorCode.BackendUnavailable } runtime when system == "linux"
            => $"CodeAlta Desktop could not start GTK or WebKitGTK, which draw its window: {runtime.Message}."
                + $"{Environment.NewLine}To fix it: check that GTK 4 and WebKitGTK 6.0 are installed and up to date (on Ubuntu 24.04 or later: sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4), and that the session can open windows. More help: {TroubleshootingUrl}",
        NeoAstraException window => $"CodeAlta Desktop could not open its window: {window.Message} ({window.Code}). More help: {TroubleshootingUrl}",
        _ => null,
    };

    /// <summary>
    /// Reports a failure of the start, before the process ends, where the user started it: to the start that
    /// waits in a terminal (it prints it); in a message box on Windows, where a windowed program has no console;
    /// elsewhere, without a terminal, in a notification (Linux) or an alert (macOS). A start in a terminal that
    /// keeps it (<c>alta --wait</c>) has the failure on its standard error already.
    /// </summary>
    /// <param name="options">The options of the start.</param>
    /// <param name="failure">What the start threw.</param>
    internal static void Report(DesktopLaunchOptions options, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Only a start of the user's profile shows a dialog: a start on explicit roots is a test or an automation.
        var user = options.Owned is { Home: null };
        Report(options, failure, text => { if (user && OperatingSystem.IsWindows()) ShowMessage(text); }, (text, logs) => { if (user) Notify(text, logs); },
            Console.IsErrorRedirected, Console.Error);
    }

    /// <summary>Reports a failure of the start through the given message box and notification.</summary>
    /// <param name="options">The options of the start.</param>
    /// <param name="failure">What the start threw.</param>
    /// <param name="showMessage">Shows a message box on Windows.</param>
    /// <param name="notify">Shows a notification or an alert elsewhere, with the text and the folder of the log.</param>
    /// <param name="noTerminal">Whether the standard error of this process reaches no terminal.</param>
    /// <param name="error">The standard error of this process: a start that keeps its terminal reads the failure there, after the log of it.</param>
    internal static void Report(DesktopLaunchOptions options, Exception failure, Action<string> showMessage, Action<string, string> notify, bool noTerminal, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(showMessage);
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(error);
        if (Describe(failure) is not { } text) return;
        var logs = Path.Combine(options.DataRoot, "logs");
        if (options.StartToken is { } token) Tell(token, text);
        if (OperatingSystem.IsWindows()) showMessage($"{text}{Environment.NewLine}{Environment.NewLine}The log is in {logs}");
        else if (options.StartToken is null && noTerminal) notify(text, logs);
        if (options.StartToken is null && !noTerminal) error.WriteLine(Environment.NewLine + text);
    }

    /// <summary>The file the application writes its failure in, for the start that waits for it.</summary>
    /// <param name="token">The token of that start.</param>
    internal static string ReportPath(string token) => DesktopTerminalStart.TokenPath(token) + ".failed";

    /// <summary>Writes the failure for the start that waits, while it still waits: once the window was shown, nobody reads it.</summary>
    internal static void Tell(string token, string text)
    {
        try
        {
            if (File.Exists(DesktopTerminalStart.TokenPath(token))) File.WriteAllText(ReportPath(token), text, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The start says the application ended, and where its log is.
        }
    }

    /// <summary>Reads and removes what the application wrote of its failure; null when it wrote nothing.</summary>
    internal static string? Collect(string token)
    {
        var path = ReportPath(token);
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* The temporary folder is cleaned in time. */ }
        }
    }

    /// <summary>One path NeoAstra tried for its native library: what became of it, and the libraries it found missing.</summary>
    internal readonly record struct NativeAttempt(NeoNativeLibraryProbeResult Result, IReadOnlyList<string> Missing);

    /// <summary>What the user reads when the native library of NeoAstra does not load, from the probes of NeoAstra.</summary>
    internal static string NativeLibraryText(NeoAstraNativeLibraryException native)
    {
        ArgumentNullException.ThrowIfNull(native);
        return NativeLibraryText([.. native.Probes.Select(static probe => new NativeAttempt(probe.Result, probe.MissingDependencies))], native.Guidance,
            native.RuntimeIdentifier, native.InnerException is EntryPointNotFoundException);
    }

    /// <summary>
    /// What the user reads when the native library of NeoAstra does not load: what is missing, and the fixes of NeoAstra
    /// that are the user's (the libraries of the system to install, a system too old). Those for the developer of an
    /// application (reference a package, publish for a runtime, set a variable) become one: reinstall CodeAlta, whose
    /// own files are then missing, damaged, or of another version or architecture. The log keeps NeoAstra's report.
    /// </summary>
    /// <param name="attempts">The paths NeoAstra tried, in order.</param>
    /// <param name="guidance">The fixes NeoAstra gives, most specific first.</param>
    /// <param name="runtimeIdentifier">The runtime identifier of the process.</param>
    /// <param name="otherVersion">Whether a library loaded but is of another version than NeoAstra's assemblies.</param>
    internal static string NativeLibraryText(IReadOnlyList<NativeAttempt> attempts, IReadOnlyList<string> guidance, string runtimeIdentifier, bool otherVersion)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(guidance);
        var missing = attempts.SelectMany(static attempt => attempt.Missing).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var text = new StringBuilder("CodeAlta Desktop could not load the native library that opens its window (")
            .Append(runtimeIdentifier).Append(").");
        if (missing.Count > 0) text.AppendLine().Append("Missing: ").Append(string.Join(", ", missing)).Append('.');
        var fixes = guidance.Where(static line => !ForDevelopers(line)).ToList();
        var damaged = otherVersion || attempts.Count == 0
            || attempts.All(static attempt => attempt.Result == NeoNativeLibraryProbeResult.NotFound)
            || attempts.Any(static attempt => attempt.Result is NeoNativeLibraryProbeResult.InvalidImage or NeoNativeLibraryProbeResult.ArchitectureMismatch);
        if (damaged)
            fixes.Add("Reinstall CodeAlta: its files are missing, damaged, or of another version or architecture. Run dotnet tool uninstall -g CodeAlta, then dotnet tool install -g CodeAlta.");
        fixes.Add("More help: " + TroubleshootingUrl);
        text.AppendLine().Append("To fix it:");
        foreach (var fix in fixes) text.AppendLine().Append("  - ").Append(fix);
        return text.AppendLine().Append("The log has the full report, with every path that was tried.").ToString();
    }

    // The fixes of NeoAstra that are for the developer of an application, not for its user.
    private static bool ForDevelopers(string line)
        => line.StartsWith("No usable ", StringComparison.Ordinal) || line.StartsWith("Set the NEOASTRA_NATIVE_LIBRARY", StringComparison.Ordinal)
            || line.StartsWith("Replace the loaded library", StringComparison.Ordinal) || line.StartsWith("A file named like the library", StringComparison.Ordinal)
            || line.StartsWith("A library built for", StringComparison.Ordinal);

    /// <summary>
    /// Whether unprivileged user namespaces are restricted for this process on Linux, which the sandbox of WebKitGTK
    /// needs: Ubuntu 24.04 restricts them through AppArmor for a program without a profile that allows them (as
    /// <c>~/.dotnet/tools/alta</c>), Debian may turn them off, and a limit of zero forbids them. False when the
    /// sandbox is turned off, or on another system.
    /// </summary>
    /// <param name="read">Reads a file of <c>/proc</c>; null when it cannot be read.</param>
    /// <param name="disableSandbox">The value of <see cref="DisableSandboxVariable"/>.</param>
    internal static bool UserNamespacesRestricted(Func<string, string?> read, string? disableSandbox)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!string.IsNullOrEmpty(disableSandbox) && disableSandbox != "0") return false;
        if (read("/proc/sys/user/max_user_namespaces")?.Trim() == "0") return true;
        if (read("/proc/sys/kernel/unprivileged_userns_clone")?.Trim() == "0") return true;
        if (read("/proc/sys/kernel/apparmor_restrict_unprivileged_userns")?.Trim() != "1") return false;
        // A profile of this program may allow them: only a process that no profile confines is restricted for sure.
        var confinement = read("/proc/self/attr/apparmor/current") ?? read("/proc/self/attr/current");
        return confinement is null || confinement.Trim().TrimEnd('\0').StartsWith("unconfined", StringComparison.Ordinal);
    }

    /// <summary>Whether the sandbox of WebKitGTK cannot start in this process: on Linux, with user namespaces restricted.</summary>
    internal static bool SandboxRestricted()
        => OperatingSystem.IsLinux() && UserNamespacesRestricted(ReadProc, Environment.GetEnvironmentVariable(DisableSandboxVariable));

    /// <summary>What the user can do when the sandbox of WebKitGTK cannot start.</summary>
    internal static string SandboxGuidance(string executable) =>
        "WebKitGTK, the web view of the desktop app on Linux, starts its pages in a sandbox (bubblewrap) that needs unprivileged user namespaces, "
        + "and this system restricts them for programs without an AppArmor profile that allows them (Ubuntu 24.04 and later): WebKitGTK then aborts the process."
        + Environment.NewLine + "To fix it:"
        + Environment.NewLine + $"  - Turn the sandbox off for CodeAlta only: {DisableSandboxVariable}=1 alta. The window loads only the local pages of CodeAlta, but it is a sandbox turned off."
        + Environment.NewLine + $"  - Or give CodeAlta an AppArmor profile that allows user namespaces, as Ubuntu does for its browsers: a file in /etc/apparmor.d/ with 'profile codealta {executable} flags=(unconfined) {{ userns, }}', then sudo systemctl reload apparmor."
        + Environment.NewLine + "  - Or use the terminal app, which has no web view: dotnet tool install -g CodeAlta.Tui, then altatui."
        + Environment.NewLine + "More help: " + TroubleshootingUrl;

    /// <summary>Whether an exit code says the process was ended by a signal (128 plus the signal), as an abort of WebKitGTK does.</summary>
    internal static bool EndedBySignal(int code) => code is > 128 and < 160;

    // Best effort: a desktop without a notification service, or a macOS that refuses the alert, leaves the log.
    private static void Notify(string text, string logs)
    {
        var first = text.ReplaceLineEndings("\n").Split('\n', 2)[0].Trim();
        var body = $"{first} The log is in {logs}.";
        var start = OperatingSystem.IsMacOS()
            ? new ProcessStartInfo("osascript") { ArgumentList = { "-e", "on run argv", "-e", "display alert (item 1 of argv) message (item 2 of argv) as critical", "-e", "end run", "CodeAlta could not start", body } }
            : new ProcessStartInfo("notify-send") { ArgumentList = { "--app-name=CodeAlta", "--urgency=critical", "CodeAlta could not start", body } };
        start.UseShellExecute = false;
        try
        {
            using var process = Process.Start(start);
            process?.WaitForExit(TimeSpan.FromSeconds(OperatingSystem.IsMacOS() ? 600 : 5));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Not installed: the log has it.
        }
    }

    private static string? ReadProc(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static void ShowMessage(string text)
    {
        try { _ = MessageBox(0, text, "CodeAlta", 0x10 /* MB_ICONERROR */ | 0x10000 /* MB_SETFOREGROUND */); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { /* The log has it. */ }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);
}
