using System.Diagnostics;
using System.Text;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// Updates the installed tool on the user's behalf. A tool cannot be replaced while it runs, so the work is
/// handed to a small script that outlives the application: it waits for this process to end, runs
/// <c>dotnet tool update</c>, records how that went, and starts CodeAlta again. The update itself is the
/// SDK's: it either completes or leaves the installed version in place.
/// </summary>
internal static class DesktopUpdateInstaller
{
    // How long the script waits for the application to end before giving up, in seconds.
    private const int WaitSeconds = 900;

    /// <summary>The <c>dotnet</c> of the installation this process runs on; null when it cannot be found.</summary>
    internal static string? DotnetPath()
    {
        var root = DesktopIntegration.DotnetRoot();
        if (root is null) return null;
        var path = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The Windows script. It stops without updating when the application is still running after the wait,
    /// or when <paramref name="cancel"/> appears (the user decided not to exit after all).
    /// </summary>
    internal static string WindowsScript(int processId, string dotnet, IReadOnlyList<string> arguments, string log, string result, string cancel, string launcher)
    {
        var script = new StringBuilder();
        script.Append("@echo off\r\n");
        // The script is UTF-8: paths keep their characters whatever the console's code page.
        script.Append("chcp 65001 >nul\r\n");
        script.Append("setlocal enableextensions\r\n");
        script.Append("set /a tries=0\r\n");
        script.Append(":wait\r\n");
        script.Append("if exist ").Append(BatchQuote(cancel)).Append(" exit /b 3\r\n");
        script.Append("tasklist /FI \"PID eq ").Append(processId).Append("\" /NH 2>nul | find \" ").Append(processId).Append(" \" >nul\r\n");
        script.Append("if errorlevel 1 goto update\r\n");
        script.Append("set /a tries+=1\r\n");
        script.Append("if %tries% geq ").Append(WaitSeconds).Append(" exit /b 2\r\n");
        // A wait of about a second that needs no console.
        script.Append("ping -n 2 127.0.0.1 >nul\r\n");
        script.Append("goto wait\r\n");
        script.Append(":update\r\n");
        script.Append(BatchQuote(dotnet));
        foreach (var argument in arguments) script.Append(' ').Append(argument);
        script.Append(" > ").Append(BatchQuote(log)).Append(" 2>&1\r\n");
        script.Append("> ").Append(BatchQuote(result)).Append(" echo %errorlevel%\r\n");
        // The launcher is the SDK's script for the installed version. The helper becomes it (no call, so
        // nothing returns here): it runs in this console, which has no window, until the application's window
        // is shown (see DesktopTerminalStart).
        script.Append(BatchQuote(launcher)).Append("\r\n");
        return script.ToString();
    }

    /// <summary>
    /// The macOS and Linux script; on macOS the application is started again through its bundle. On macOS the
    /// script is the executable of <see cref="MacHelperInfoPlist"/>'s bundle, a job of its own for launchd: what
    /// it leaves behind ends with it, so it becomes the launcher instead of starting it in the background.
    /// </summary>
    internal static string UnixScript(int processId, string dotnet, IReadOnlyList<string> arguments, string log, string result, string cancel, string launcher, string? bundle, bool macOS)
    {
        var script = new StringBuilder();
        script.Append("#!/bin/sh\n");
        script.Append("tries=0\n");
        script.Append("while kill -0 ").Append(processId).Append(" 2>/dev/null; do\n");
        script.Append("  [ -e ").Append(DesktopIntegration.ShellQuote(cancel)).Append(" ] && exit 3\n");
        script.Append("  tries=$((tries + 1))\n");
        script.Append("  [ \"$tries\" -ge ").Append(WaitSeconds).Append(" ] && exit 2\n");
        script.Append("  sleep 1\n");
        script.Append("done\n");
        script.Append("[ -e ").Append(DesktopIntegration.ShellQuote(cancel)).Append(" ] && exit 3\n");
        script.Append(DesktopIntegration.ShellQuote(dotnet));
        foreach (var argument in arguments) script.Append(' ').Append(DesktopIntegration.ShellQuote(argument));
        script.Append(" > ").Append(DesktopIntegration.ShellQuote(log)).Append(" 2>&1\n");
        script.Append("echo $? > ").Append(DesktopIntegration.ShellQuote(result)).Append('\n');
        if (bundle is not null)
            // The bundle keeps the icon and the place of the application in the Dock; the launcher when it does not open.
            script.Append("if [ -d ").Append(DesktopIntegration.ShellQuote(bundle)).Append(" ] && /usr/bin/open ").Append(DesktopIntegration.ShellQuote(bundle)).Append("; then exit 0; fi\n");
        if (macOS) script.Append("exec ").Append(DesktopIntegration.ShellQuote(launcher)).Append(" >/dev/null 2>&1\n");
        else script.Append("nohup ").Append(DesktopIntegration.ShellQuote(launcher)).Append(" >/dev/null 2>&1 &\n");
        return script.ToString();
    }

    /// <summary>
    /// The <c>Info.plist</c> of the bundle the macOS script runs as. launchd ends every process an application
    /// started once that application exits, a detached one too: the script, started by CodeAlta, died with it
    /// in the middle of the update. Opened as an application of its own, it outlives CodeAlta. It runs in the
    /// background: no icon in the Dock, no menu bar.
    /// </summary>
    internal static string MacHelperInfoPlist() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>CFBundleName</key><string>CodeAlta Update</string>
          <key>CFBundleIdentifier</key><string>{DesktopIntegration.MacBundleIdentifier}.update</string>
          <key>CFBundleExecutable</key><string>{MacHelperExecutable}</string>
          <key>CFBundlePackageType</key><string>APPL</string>
          <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
          <key>LSMinimumSystemVersion</key><string>11.0</string>
          <key>LSBackgroundOnly</key><true/>
        </dict>
        </plist>

        """.ReplaceLineEndings("\n");

    // The executable of the macOS helper bundle: the script itself.
    private const string MacHelperExecutable = "update";

    // How long the start of the macOS helper may take before the update is reported as not started.
    private static readonly TimeSpan OpenPatience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Starts the script for this process. The update happens once the application has exited; until then
    /// <see cref="Cancel"/> calls it off.
    /// </summary>
    /// <returns>True when the script is running.</returns>
    internal static bool Start(string dataRoot, string launcher, string dotnet, IReadOnlyList<string> arguments)
    {
        try
        {
            var folder = Folder(dataRoot);
            Directory.CreateDirectory(folder);
            var (log, result, cancel) = (Path.Combine(folder, "update.log"), Path.Combine(folder, "result.txt"), Path.Combine(folder, "cancel"));
            foreach (var stale in new[] { result, cancel }) File.Delete(stale);
            var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
            if (OperatingSystem.IsWindows())
            {
                var script = Path.Combine(folder, "update.cmd");
                File.WriteAllText(script, WindowsScript(Environment.ProcessId, dotnet, arguments, log, result, cancel, launcher), new UTF8Encoding(false));
                start.FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } shell ? shell : "cmd.exe";
                start.ArgumentList.Add("/d");
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add(script);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var helper = Path.Combine(folder, "CodeAlta Update.app");
                var contents = Path.Combine(helper, "Contents");
                var script = Path.Combine(contents, "MacOS", MacHelperExecutable);
                Directory.CreateDirectory(Path.GetDirectoryName(script)!);
                File.WriteAllText(Path.Combine(contents, "Info.plist"), MacHelperInfoPlist(), new UTF8Encoding(false));
                File.WriteAllText(script, UnixScript(Environment.ProcessId, dotnet, arguments, log, result, cancel, launcher,
                    DesktopIntegration.EntryPath(), macOS: true), new UTF8Encoding(false));
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                start.FileName = "/usr/bin/open";
                // A new instance, in the background: the window keeps the focus until it closes.
                start.ArgumentList.Add("-n");
                start.ArgumentList.Add("-g");
                start.ArgumentList.Add(helper);
                using var opening = Process.Start(start);
                if (opening is null) return false;
                if (opening.WaitForExit(OpenPatience) && opening.ExitCode == 0) return true;
                LogManager.GetLogger("CodeAlta.Desktop").Warn($"The update could not be started: {start.FileName} did not open {helper}.");
                // A helper that started all the same must not update once the application exits later.
                Cancel(dataRoot);
                return false;
            }
            else
            {
                var script = Path.Combine(folder, "update.sh");
                File.WriteAllText(script, UnixScript(Environment.ProcessId, dotnet, arguments, log, result, cancel, launcher, bundle: null, macOS: false), new UTF8Encoding(false));
                start.FileName = "/bin/sh";
                start.ArgumentList.Add("-c");
                // Detached from this process, which is about to end.
                start.ArgumentList.Add("nohup /bin/sh " + DesktopIntegration.ShellQuote(script) + " >/dev/null 2>&1 &");
            }
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            LogManager.GetLogger("CodeAlta.Desktop").Warn($"The update could not be started: {exception.Message}");
            return false;
        }
    }

    /// <summary>Calls a started update off: the script ends without touching the installed tool.</summary>
    internal static void Cancel(string dataRoot)
    {
        try
        {
            var folder = Folder(dataRoot);
            if (Directory.Exists(folder)) File.WriteAllText(Path.Combine(folder, "cancel"), string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* The script gives up after its wait. */ }
    }

    /// <summary>
    /// How the update of the previous run went, read once: <c>ok</c>, <c>failed</c>, or null when no update
    /// ran. The record is removed, so the next start says nothing.
    /// </summary>
    internal static string? ConsumeResult(string dataRoot)
    {
        try
        {
            var path = Path.Combine(Folder(dataRoot), "result.txt");
            if (!File.Exists(path)) return null;
            var code = File.ReadAllText(path).Trim();
            File.Delete(path);
            return code == "0" ? "ok" : "failed";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Folder(string dataRoot) => Path.Combine(dataRoot, "update");

    // A quoted batch word; a percent sign would otherwise start a variable.
    internal static string BatchQuote(string value) => "\"" + value.Replace("%", "%%", StringComparison.Ordinal) + "\"";
}
