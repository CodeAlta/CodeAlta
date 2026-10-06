using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// Makes the installed tool an application of the desktop it runs on, so it is started like any other:
/// a Start Menu shortcut on Windows, <c>CodeAlta.app</c> in the user's Applications folder on macOS, and a
/// desktop entry on Linux. On macOS and Linux each starts the tool's launcher (<c>alta</c> in the .NET tools
/// folder), so an update of the tool needs nothing here. On Windows that launcher is a script, which would
/// show a console window: the shortcut starts the executable of the installed version instead, and is
/// written again by the first start of each version. Everything is written in the user's own folders: no
/// elevation.
/// </summary>
/// <remarks>
/// Only a tool installed with <c>dotnet tool install -g</c> is integrated; a build output or a local tool
/// leaves the desktop alone. The files are rewritten when the version or the launcher's path changed.
/// </remarks>
internal static class DesktopIntegration
{
    /// <summary>The identity Windows groups the application's windows, its shortcut and its taskbar pin under.</summary>
    internal const string WindowsAppId = "CodeAlta.Desktop";

    /// <summary>The identity of the developer instance, which is not the installed application.</summary>
    internal const string WindowsDeveloperAppId = "CodeAlta.Desktop.Dev";

    internal const string MacBundleIdentifier = "org.codealta.desktop";

    /// <summary>
    /// The launcher of a globally installed tool: <c>alta</c> beside the <c>.store</c> folder this process
    /// runs from. Null for anything else (a build output, a local tool run through <c>dotnet</c>). For a tool
    /// packed per runtime the SDK writes a script on Windows (<c>alta.cmd</c>) and a link elsewhere.
    /// </summary>
    internal static string? InstalledLauncher(string baseDirectory, bool windows)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        var separator = windows ? '\\' : '/';
        var normalized = windows ? baseDirectory.Replace('/', '\\') : baseDirectory;
        var index = normalized.IndexOf(separator + ".store" + separator, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return index <= 0 ? null : normalized[..index] + separator + (windows ? "alta.cmd" : "alta");
    }

    /// <summary>
    /// What the desktop's entry starts: the launcher, except on Windows, where it is the executable beside the
    /// application (a shortcut to the launcher's script would show a console window while CodeAlta starts).
    /// </summary>
    internal static string EntryStart(string launcher, string baseDirectory, bool windows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcher);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        if (!windows) return launcher;
        var folder = baseDirectory.Replace('/', '\\');
        return (folder.EndsWith('\\') ? folder : folder + '\\') + "alta.exe";
    }

    /// <summary>Where Windows keeps the copy of the shortcut that a pin on the taskbar starts.</summary>
    internal static string WindowsTaskbarPin(string applicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationData);
        return applicationData.TrimEnd('\\') + @"\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\CodeAlta.lnk";
    }

    /// <summary>
    /// Gives this process the application's identity on Windows, before any window exists: the taskbar then
    /// groups the window with the Start Menu shortcut, and a pin keeps the application's name and icon.
    /// </summary>
    internal static void IdentifyProcess(bool developer)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _ = SetCurrentProcessExplicitAppUserModelID(developer ? WindowsDeveloperAppId : WindowsAppId); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { /* The window keeps the executable's identity. */ }
    }

    /// <summary>
    /// Writes or refreshes the desktop's entry for the installed tool. Never throws: an entry that cannot be
    /// written leaves the application as it is, started from a terminal.
    /// </summary>
    /// <param name="dataRoot">The application data root: the icons are copied below it, where they stay across updates.</param>
    /// <param name="version">The running version.</param>
    /// <returns>True when the entry did not exist and was added: the user is told where to find the application.</returns>
    internal static bool Ensure(string dataRoot, string version)
    {
        try
        {
            var launcher = InstalledLauncher(AppContext.BaseDirectory, OperatingSystem.IsWindows());
            if (launcher is null || !File.Exists(launcher)) return false;
            var start = EntryStart(launcher, AppContext.BaseDirectory, OperatingSystem.IsWindows());
            if (!File.Exists(start)) return false;
            var folder = Path.Combine(dataRoot, "integration");
            var stamp = Path.Combine(folder, "installed.txt");
            var current = version + "\n" + start;
            var target = EntryPath();
            if (target is null) return false;
            var existed = File.Exists(target) || Directory.Exists(target);
            if (existed && File.Exists(stamp) && File.ReadAllText(stamp) == current) return false;
            Directory.CreateDirectory(folder);
            if (OperatingSystem.IsWindows())
            {
                var icon = CopyIcon(folder, "alta.ico");
                WriteWindowsShortcut(target, start, icon);
                // The executable's path holds the version: a pin made from the previous one would start nothing.
                var pin = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } data ? WindowsTaskbarPin(data) : null;
                if (pin is not null && File.Exists(pin)) WriteWindowsShortcut(pin, start, icon);
            }
            else if (OperatingSystem.IsMacOS()) WriteMacBundle(target, launcher, version, Path.Combine(AppContext.BaseDirectory, "alta.icns"));
            else WriteLinuxEntry(target, launcher, CopyIcon(folder, "alta.png"));
            File.WriteAllText(stamp, current);
            LogManager.GetLogger("CodeAlta.Desktop").Info($"Desktop entry written: {target}");
            return !existed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or COMException
            or InvalidCastException or NotSupportedException or ArgumentException or PlatformNotSupportedException)
        {
            LogManager.GetLogger("CodeAlta.Desktop").Warn($"The desktop entry could not be written: {exception.Message}");
            return false;
        }
    }

    /// <summary>Where this platform keeps the user's own entry for the application; null when it has no such place.</summary>
    internal static string? EntryPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            return programs.Length == 0 ? null : Path.Combine(programs, "CodeAlta.lnk");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) return null;
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Applications", "CodeAlta.app");
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data) ? Path.Combine(home, ".local", "share") : data,
            "applications", "codealta.desktop");
    }

    /// <summary>The <c>Info.plist</c> of the macOS bundle.</summary>
    internal static string MacInfoPlist(string version)
    {
        var text = SecurityElement.Escape(version) ?? string.Empty;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleName</key><string>CodeAlta</string>
              <key>CFBundleDisplayName</key><string>CodeAlta</string>
              <key>CFBundleIdentifier</key><string>{MacBundleIdentifier}</string>
              <key>CFBundleExecutable</key><string>CodeAlta</string>
              <key>CFBundleIconFile</key><string>alta</string>
              <key>CFBundlePackageType</key><string>APPL</string>
              <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
              <key>CFBundleShortVersionString</key><string>{text}</string>
              <key>CFBundleVersion</key><string>{text}</string>
              <key>LSMinimumSystemVersion</key><string>11.0</string>
              <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
              <key>NSHighResolutionCapable</key><true/>
            </dict>
            </plist>

            """.ReplaceLineEndings("\n");
    }

    /// <summary>
    /// The executable of the macOS bundle: a script that becomes the installed tool. An application started
    /// from the Finder gets a bare environment, so the tool is started through the user's login shell, which
    /// gives it the PATH their terminal has (git, node, the .NET runtime); the process stays the one the
    /// Finder started, which keeps the bundle's icon in the Dock.
    /// </summary>
    internal static string MacLauncherScript(string launcher, string? dotnetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcher);
        var script = new StringBuilder();
        script.Append("#!/bin/sh\n");
        script.Append("# Written by CodeAlta: starts the tool installed with `dotnet tool install -g CodeAlta`.\n");
        script.Append("TOOL=").Append(ShellQuote(launcher)).Append('\n');
        script.Append("if [ ! -x \"$TOOL\" ]; then\n");
        script.Append("  /usr/bin/osascript -e 'display alert \"CodeAlta is not installed\" message \"Install it again with: dotnet tool install -g CodeAlta\"' >/dev/null 2>&1\n");
        script.Append("  exit 1\n");
        script.Append("fi\n");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
            script.Append("[ -n \"$DOTNET_ROOT\" ] || [ ! -d ").Append(ShellQuote(dotnetRoot)).Append(" ] || export DOTNET_ROOT=").Append(ShellQuote(dotnetRoot)).Append('\n');
        // The Finder starts applications in the root folder, which is nobody's project.
        script.Append("cd \"$HOME\" 2>/dev/null\n");
        // Only shells that read this syntax; any other starts the tool with the environment it was given.
        script.Append("case \"$SHELL\" in\n");
        script.Append("  */zsh|*/bash|*/sh|*/dash|*/ksh) [ -x \"$SHELL\" ] && exec \"$SHELL\" -l -c 'exec \"$0\" \"$@\"' \"$TOOL\" \"$@\" ;;\n");
        script.Append("esac\n");
        script.Append("exec \"$TOOL\" \"$@\"\n");
        return script.ToString();
    }

    /// <summary>The Linux desktop entry.</summary>
    internal static string LinuxDesktopEntry(string launcher, string? icon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcher);
        var entry = new StringBuilder();
        entry.Append("[Desktop Entry]\n");
        entry.Append("Type=Application\n");
        entry.Append("Name=CodeAlta\n");
        entry.Append("GenericName=Coding agent workspace\n");
        entry.Append("Comment=Work with coding agents across your projects\n");
        entry.Append("Exec=").Append(DesktopEntryArgument(launcher)).Append('\n');
        if (!string.IsNullOrWhiteSpace(icon)) entry.Append("Icon=").Append(icon).Append('\n');
        entry.Append("Terminal=false\n");
        entry.Append("Categories=Development;IDE;\n");
        entry.Append("StartupNotify=true\n");
        return entry.ToString();
    }

    // A single-quoted shell word: the only character to escape is the quote itself.
    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // An Exec argument of a desktop entry: quoted, with the characters the specification reserves escaped.
    internal static string DesktopEntryArgument(string value)
    {
        var escaped = new StringBuilder("\"");
        foreach (var character in value)
        {
            if (character is '"' or '`' or '$' or '\\') escaped.Append('\\');
            escaped.Append(character == '%' ? "%%" : character.ToString());
        }
        return escaped.Append('"').ToString();
    }

    // The .NET installation this process runs on: three folders above the runtime's own directory
    // (<root>/shared/Microsoft.NETCore.App/<version>).
    internal static string? DotnetRoot()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var root = runtime is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtime)));
        return root is not null && Directory.Exists(Path.Combine(root, "shared")) ? root : null;
    }

    private static string? CopyIcon(string folder, string name)
    {
        var source = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(source)) return null;
        var target = Path.Combine(folder, name);
        File.Copy(source, target, overwrite: true);
        return target;
    }

    private static void WriteMacBundle(string bundle, string launcher, string version, string icon)
    {
        var contents = Path.Combine(bundle, "Contents");
        var executable = Path.Combine(contents, "MacOS", "CodeAlta");
        Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
        Directory.CreateDirectory(Path.Combine(contents, "Resources"));
        File.WriteAllText(Path.Combine(contents, "Info.plist"), MacInfoPlist(version), new UTF8Encoding(false));
        File.WriteAllText(executable, MacLauncherScript(launcher, DotnetRoot()), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        if (File.Exists(icon)) File.Copy(icon, Path.Combine(contents, "Resources", "alta.icns"), overwrite: true);
        // The Finder and the Dock notice a changed bundle by its modification time.
        Directory.SetLastWriteTimeUtc(bundle, DateTime.UtcNow);
    }

    private static void WriteLinuxEntry(string path, string launcher, string? icon)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, LinuxDesktopEntry(launcher, icon), new UTF8Encoding(false));
    }

    [SupportedOSPlatform("windows")]
    internal static void WriteWindowsShortcut(string path, string launcher, string? icon)
    {
        Exception? failure = null;
        // The shell's link object lives in a single-threaded apartment.
        var thread = new Thread(() =>
        {
            try
            {
                var link = (IShellLinkW)new ShellLink();
                try
                {
                    link.SetPath(launcher);
                    link.SetWorkingDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    link.SetDescription("CodeAlta");
                    if (icon is not null) link.SetIconLocation(icon, 0);
                    var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5); // System.AppUserModel.ID
                    var value = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(WindowsAppId) };
                    try
                    {
                        var store = (IPropertyStore)link;
                        Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
                        Marshal.ThrowExceptionForHR(store.Commit());
                    }
                    finally { Marshal.FreeCoTaskMem(value.Pointer); }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    ((IPersistFile)link).Save(path, true);
                }
                finally { Marshal.FinalReleaseComObject(link); }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure is not null) throw new IOException(failure.Message, failure);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string id);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, nint data, uint flags);
        void GetIDList(out nint list);
        void SetIDList(nint list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public nint Pointer;
        public nint Extra;
    }
}
