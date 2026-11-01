using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CodeAlta.Browser.Discovery;

/// <summary>The operating system, for the places browsers are installed in.</summary>
internal enum BrowserPlatform
{
    /// <summary>Windows.</summary>
    Windows,

    /// <summary>macOS.</summary>
    MacOS,

    /// <summary>Linux.</summary>
    Linux,
}

/// <summary>
/// What finding browsers reads of the computer: the system, the home folder, variables, the registry, the default
/// browser, the programs that run and the clock. Tests give fixture folders and fakes; <see cref="Current"/> is the computer.
/// </summary>
internal sealed class BrowserEnvironment
{
    /// <summary>Gets the system.</summary>
    public required BrowserPlatform Platform { get; init; }

    /// <summary>Gets the home folder of the user.</summary>
    public required string Home { get; init; }

    /// <summary>Gets an environment variable, or null.</summary>
    public required Func<string, string?> Variable { get; init; }

    /// <summary>Gets the path Windows registers for a program name (<c>App Paths</c>), or null.</summary>
    public Func<string, string?> AppPath { get; init; } = static _ => null;

    /// <summary>
    /// Gets what the system says of the default web browser: a program path on Windows, an application bundle on macOS,
    /// a desktop file id on Linux; or null.
    /// </summary>
    public Func<string?> DefaultBrowser { get; init; } = static () => null;

    /// <summary>Gets whether a program of this name runs.</summary>
    public Func<string, bool> IsRunning { get; init; } = static _ => false;

    /// <summary>Gets the clock.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Gets this computer.</summary>
    public static BrowserEnvironment Current()
    {
        var platform = OperatingSystem.IsWindows() ? BrowserPlatform.Windows : OperatingSystem.IsMacOS() ? BrowserPlatform.MacOS : BrowserPlatform.Linux;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new BrowserEnvironment
        {
            Platform = platform,
            Home = home,
            Variable = Environment.GetEnvironmentVariable,
            AppPath = OperatingSystem.IsWindows() ? WindowsAppPath : static _ => null,
            DefaultBrowser = platform switch
            {
                BrowserPlatform.Windows when OperatingSystem.IsWindows() => WindowsDefaultBrowser,
                BrowserPlatform.MacOS => MacDefaultBrowser,
                _ => () => LinuxDefaultBrowser(home, Environment.GetEnvironmentVariable),
            },
            IsRunning = static name =>
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var process in processes) process.Dispose();
                return processes.Length != 0;
            },
        };
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsAppPath(string program)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{program}");
            if (key?.GetValue(null) is string path && path.Length != 0) return path.Trim('"');
        }

        return null;
    }

    // Windows keeps the choice of the user in UserChoiceLatest, and in UserChoice on older builds. Its protection driver
    // blocks writes to these keys, not reads.
    [SupportedOSPlatform("windows")]
    private static string? WindowsDefaultBrowser()
    {
        const string associations = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\";
        foreach (var name in new[] { "UserChoiceLatest", "UserChoice" })
        {
            using var choice = Registry.CurrentUser.OpenSubKey(associations + name);
            if (choice?.GetValue("ProgId") is not string progId || progId.Length == 0) continue;
            using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (command?.GetValue(null) is string line) return FirstToken(line);
        }

        return null;
    }

    /// <summary>The program of a command line: its first token, quoted or not.</summary>
    internal static string FirstToken(string line)
    {
        line = line.Trim();
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            return end > 1 ? line[1..end] : line.Trim('"');
        }

        var space = line.IndexOf(" -", StringComparison.Ordinal);
        if (space < 0) space = line.IndexOf(" %", StringComparison.Ordinal);
        return space > 0 ? line[..space] : line;
    }

    /// <summary>The first desktop file of <c>x-scheme-handler/https</c> in the <c>mimeapps.list</c> files that <c>xdg-mime</c> reads, in their order.</summary>
    internal static string? LinuxDefaultBrowser(string home, Func<string, string?> variable)
    {
        var config = variable("XDG_CONFIG_HOME") is { Length: > 0 } xdgConfig ? xdgConfig : Path.Combine(home, ".config");
        var desktops = (variable("XDG_CURRENT_DESKTOP") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries).Select(static desktop => desktop.ToLowerInvariant());
        var data = variable("XDG_DATA_HOME") is { Length: > 0 } xdgData ? xdgData : Path.Combine(home, ".local", "share");
        var files = desktops.Select(desktop => Path.Combine(config, $"{desktop}-mimeapps.list"))
            .Append(Path.Combine(config, "mimeapps.list"))
            .Concat((variable("XDG_CONFIG_DIRS") is { Length: > 0 } dirs ? dirs : "/etc/xdg").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(static dir => Path.Combine(dir, "mimeapps.list")))
            .Append(Path.Combine(data, "applications", "mimeapps.list"))
            .Append("/usr/local/share/applications/mimeapps.list")
            .Append("/usr/share/applications/mimeapps.list");
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            var inDefaults = false;
            foreach (var raw in SafeLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) inDefaults = line == "[Default Applications]";
                else if (inDefaults && line.StartsWith("x-scheme-handler/https=", StringComparison.Ordinal))
                {
                    var first = line["x-scheme-handler/https=".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (first is { Length: > 0 }) return first;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SafeLines(string file)
    {
        try { return File.ReadAllLines(file); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    // Launch Services gives the application that opens https: links. Calls of CoreServices and CoreFoundation with
    // pointers only, so that nothing has to be marshalled.
    private static string? MacDefaultBrowser()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var bytes = "https://example.com"u8.ToArray();
            var url = MacNative.CFURLCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, 0x08000100 /* UTF-8 */, IntPtr.Zero);
            if (url == IntPtr.Zero) return null;
            try
            {
                var application = MacNative.LSCopyDefaultApplicationURLForURL(url, 0xFFFFFFFF /* all roles */, IntPtr.Zero);
                if (application == IntPtr.Zero) return null;
                try
                {
                    var buffer = new byte[4096];
                    return MacNative.CFURLGetFileSystemRepresentation(application, true, buffer, buffer.Length)
                        ? System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is var end and >= 0 ? end : buffer.Length)
                        : null;
                }
                finally
                {
                    MacNative.CFRelease(application);
                }
            }
            finally
            {
                MacNative.CFRelease(url);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static class MacNative
    {
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";

        [DllImport(CoreFoundation)]
        public static extern IntPtr CFURLCreateWithBytes(IntPtr allocator, byte[] bytes, nint length, uint encoding, IntPtr baseUrl);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool CFURLGetFileSystemRepresentation(IntPtr url, [MarshalAs(UnmanagedType.U1)] bool resolveAgainstBase, byte[] buffer, nint maxLength);

        [DllImport(CoreFoundation)]
        public static extern void CFRelease(IntPtr value);

        [DllImport(CoreServices)]
        public static extern IntPtr LSCopyDefaultApplicationURLForURL(IntPtr url, uint role, IntPtr error);
    }
}
