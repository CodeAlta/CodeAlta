using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// The executable of the macOS bundle, <c>CodeAlta.app</c>. The system knows a process by its executable:
/// the tool's own (<c>alta</c> in the .NET tools store, which the SDK signs as <c>apphost</c>) is in no bundle,
/// so the system listed it as "alta" in Privacy &amp; Security, once for each version (the folder of a version
/// holds its number) and with nothing to show in the Finder once that version was replaced. The bundle's
/// executable is therefore the .NET host itself: a copy of the tool's, which starts the application through
/// <see cref="AppLink"/>, a link to the folder of the installed version, and is signed as the bundle. The
/// process is then CodeAlta for the system, with its icon, and the bundle does not change when the tool is
/// updated, so neither does what the system has allowed it.
/// </summary>
/// <remarks>
/// A bundle started by the system has the bare environment of the Finder: <see cref="EnterLoginShell"/> starts
/// the same executable again through the user's login shell, which gives it the PATH of a terminal. When
/// the host cannot be made (no host beside the application, no <c>codesign</c>), the bundle's executable is
/// the script of <see cref="DesktopIntegration.MacLauncherScript"/>, which becomes the tool.
/// </remarks>
internal static class DesktopMacBundle
{
    /// <summary>The executable of the bundle, as its <c>Info.plist</c> names it.</summary>
    internal const string Executable = "CodeAlta";

    /// <summary>Set for the process started again through the login shell, and inherited by what it starts.</summary>
    internal const string LoginVariable = "CODEALTA_LOGIN_ENVIRONMENT";

    // The managed application the host starts: alta.dll.
    private static readonly string AppFile = typeof(DesktopMacBundle).Assembly.GetName().Name + ".dll";

    // The SDK's host keeps the path of the application in a buffer of 1025 bytes, zero after the path.
    private const int HostPathCapacity = 1024;

    /// <summary>The link to the folder of the installed version that the bundle starts.</summary>
    internal static string AppLink(string dataRoot) => Path.Combine(dataRoot, "integration", "app");

    /// <summary>
    /// The host of the bundle: <paramref name="apphost"/>, the SDK's host of the installed version, with the
    /// path of the application it starts replaced by <paramref name="relativePath"/>, relative to the folder
    /// of the executable as the host requires. Null when that path is not found once, or does not fit.
    /// </summary>
    internal static byte[]? Host(ReadOnlySpan<byte> apphost, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        var current = Encoding.UTF8.GetBytes(AppFile + "\0");
        var at = apphost.IndexOf(current);
        if (at < 0 || apphost[(at + 1)..].IndexOf(current) >= 0) return null;
        var path = Encoding.UTF8.GetBytes(relativePath);
        if (path.Length >= HostPathCapacity || at + HostPathCapacity > apphost.Length) return null;
        // What follows the current path in the buffer is zero: nothing else is overwritten.
        if (apphost.Slice(at + current.Length, HostPathCapacity - current.Length).ContainsAnyExcept((byte)0)) return null;
        var host = apphost.ToArray();
        path.CopyTo(host, at);
        host.AsSpan(at + path.Length, HostPathCapacity - path.Length).Clear();
        return host;
    }

    /// <summary>
    /// The path of the application through <paramref name="appLink"/>, relative to the folder of the bundle's
    /// executable; null when there is none (another volume).
    /// </summary>
    internal static string? HostPath(string bundle, string appLink)
    {
        var relative = Path.GetRelativePath(Path.Combine(bundle, "Contents", "MacOS"), Path.Combine(appLink, AppFile));
        return Path.IsPathRooted(relative) ? null : relative;
    }

    /// <summary>
    /// Points <paramref name="link"/> at <paramref name="folder"/>, the folder of the running version, at once
    /// for a bundle that starts at that moment.
    /// </summary>
    /// <returns>Whether the link changed.</returns>
    internal static bool RefreshAppLink(string link, string folder)
    {
        var target = Path.TrimEndingDirectorySeparator(folder);
        if (new FileInfo(link).LinkTarget is { } existing && Path.TrimEndingDirectorySeparator(existing) == target) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var next = link + ".new";
        File.Delete(next);
        File.CreateSymbolicLink(next, target);
        if (rename(next, link) != 0) throw new IOException($"The link {link} could not be replaced (errno {Marshal.GetLastPInvokeError()}).");
        return true;
    }

    /// <summary>What the bundle's content depends on: the stamp of the bundle, without the version of the tool.</summary>
    internal static string Fingerprint(string infoPlist, byte[]? host, string script, string iconFile)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(infoPlist));
        hash.AppendData(host ?? Encoding.UTF8.GetBytes(script));
        hash.AppendData(Encoding.UTF8.GetBytes(iconFile));
        return (host is null ? "script " : "host ") + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Writes the bundle: its contents are made and signed beside it, then take the place of the previous
    /// ones. The executable of a running application is never written over, and the bundle's folder stays the
    /// one the Dock knows.
    /// </summary>
    /// <returns>Whether the bundle's executable is the host; false for the script.</returns>
    [SupportedOSPlatform("macos")]
    internal static bool Write(string bundle, string infoPlist, byte[]? host, string script, string? icon)
    {
        var staging = Path.Combine(Path.GetDirectoryName(bundle)!, ".CodeAlta-staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        var staged = Path.Combine(staging, Path.GetFileName(bundle));
        var contents = Path.Combine(staged, "Contents");
        var executable = Path.Combine(contents, "MacOS", Executable);
        Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
        Directory.CreateDirectory(Path.Combine(contents, "Resources"));
        File.WriteAllText(Path.Combine(contents, "Info.plist"), infoPlist, new UTF8Encoding(false));
        if (icon is not null && File.Exists(icon)) File.Copy(icon, Path.Combine(contents, "Resources", "alta.icns"));
        var hosted = false;
        if (host is not null)
        {
            File.WriteAllBytes(executable, host);
            SetExecutable(executable);
            hosted = Sign(staged);
        }
        if (!hosted)
        {
            // What a failed signature left: a script needs none, and a bundle that does not match its own does not open.
            if (Directory.Exists(Path.Combine(contents, "_CodeSignature"))) Directory.Delete(Path.Combine(contents, "_CodeSignature"), recursive: true);
            File.Delete(executable);
            File.WriteAllText(executable, script, new UTF8Encoding(false));
            SetExecutable(executable);
        }
        Directory.CreateDirectory(bundle);
        var current = Path.Combine(bundle, "Contents");
        var previous = Path.Combine(staging, "previous");
        if (Directory.Exists(current)) Directory.Move(current, previous);
        try { Directory.Move(contents, current); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The bundle keeps what it had rather than nothing.
            if (Directory.Exists(previous) && !Directory.Exists(current)) Directory.Move(previous, current);
            throw;
        }
        try { Directory.Delete(staging, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Removed by the next write. */ }
        return hosted;
    }

    /// <summary>
    /// In the bundle started by the system: starts this executable again through the user's login shell
    /// (<c>exec</c>, so the process stays the one the system started, with its place in the Dock), as a
    /// terminal would, so that the application has the PATH of the user (git, node, the .NET runtime).
    /// Returns when this process is not that bundle, was started that way already, or the shell does not start.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static void EnterLoginShell(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var executable = Environment.ProcessPath;
        if (!IsBundleExecutable(executable) || Environment.GetEnvironmentVariable(LoginVariable) is not null) return;
        // The Finder starts applications in the root folder, which is nobody's project.
        if (Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home && Directory.Exists(home)) Directory.SetCurrentDirectory(home);
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!IsLoginShell(shell) || !File.Exists(shell)) return;
        var environment = new List<string?>();
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            if (variable.Key is string name && name != LoginVariable) environment.Add(name + "=" + variable.Value);
        environment.Add(LoginVariable + "=1");
        environment.Add(null);
        // Returns only when the shell did not start: the application runs with the environment it has. Nothing
        // is logged yet at this point of the start.
        execve(shell!, LoginShellArguments(shell!, executable!, args), [.. environment]);
    }

    /// <summary>The arguments of the login shell that becomes <paramref name="executable"/> again, ended by null.</summary>
    internal static string?[] LoginShellArguments(string shell, string executable, IReadOnlyList<string> args)
        => [shell, "-l", "-c", "exec \"$0\" \"$@\"", executable, .. args, null];

    /// <summary>Whether <paramref name="path"/> is the executable of a bundle written by <see cref="Write"/>.</summary>
    internal static bool IsBundleExecutable(string? path)
        => path is not null && path.EndsWith(".app/Contents/MacOS/" + Executable, StringComparison.Ordinal);

    /// <summary>The shells whose syntax <see cref="LoginShellArguments"/> is written in; any other starts nothing.</summary>
    internal static bool IsLoginShell(string? shell)
        => shell is not null && shell.StartsWith('/') && shell[(shell.LastIndexOf('/') + 1)..] is "zsh" or "bash" or "sh" or "dash" or "ksh";

    // Signs the bundle for this computer: the system then knows the process as the bundle, CodeAlta.
    private static bool Sign(string bundle)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/codesign") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "--force", "--sign", "-", bundle }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return false;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60))) { process.Kill(); return false; }
            if (process.ExitCode == 0) return true;
            LogManager.GetLogger("CodeAlta.Desktop").Warn($"The bundle could not be signed: {error.Result.Trim()}");
            return false;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogManager.GetLogger("CodeAlta.Desktop").Warn($"The bundle could not be signed: {exception.Message}");
            return false;
        }
    }

    private static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int rename([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);

    // An array of strings takes LPStr only, which is UTF-8 outside Windows.
    [DllImport("libc", SetLastError = true)]
    private static extern int execve([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] envp);
}
