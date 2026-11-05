using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeAlta.Desktop.Fleet;

/// <summary>
/// The full name of the person signed in to this computer, as the system knows it: what the setup of a server proposes as
/// the owner's name. The account settings of Windows (the local account's full name), <c>id -F</c> on macOS, the comment
/// of the account on Linux. Null when the system has none.
/// </summary>
internal static class PersonName
{
    private const int MaximumLength = 128;

    /// <summary>Reads the full name of the account this process runs as; null when the system gives none.</summary>
    /// <returns>The full name, trimmed and at most 128 characters; null for none.</returns>
    public static string? Read()
    {
        try
        {
            var name = OperatingSystem.IsWindows() ? ReadWindows(Environment.UserName)
                : OperatingSystem.IsMacOS() ? Run("/usr/bin/id", "-F")
                : FromPasswd(Run("getent", "passwd", Environment.UserName));
            return Clean(name, Environment.UserName);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException
            or EntryPointNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What reads the full name once, when it is first asked, then gives it again: the name of the account does not change
    /// while the application runs, and reading it may start a process.
    /// </summary>
    /// <returns>The reader.</returns>
    public static Func<string?> ReadOnce()
    {
        var name = new Lazy<string?>(Read, LazyThreadSafetyMode.ExecutionAndPublication);
        return () => name.Value;
    }

    /// <summary>The full name of a line of <c>/etc/passwd</c>: the first part of its comment (GECOS), before its first comma.</summary>
    /// <param name="line">The line, <c>name:x:uid:gid:comment:home:shell</c>; null for none.</param>
    /// <returns>The full name; null when the line has none.</returns>
    internal static string? FromPasswd(string? line)
    {
        var fields = line?.Trim().Split(':');
        if (fields is not { Length: >= 5 }) return null;
        var full = fields[4].Split(',')[0].Trim();
        return full.Length > 0 ? full : null;
    }

    /// <summary>A name the setup can propose: trimmed, bounded, without control characters, and not the bare account name.</summary>
    /// <param name="name">The name the system gave.</param>
    /// <param name="account">The name of the account, which is no full name.</param>
    /// <returns>The name; null for none.</returns>
    internal static string? Clean(string? name, string? account)
    {
        var value = new string((name ?? "").Where(static character => !char.IsControl(character)).ToArray()).Trim();
        if (value.Length == 0 || string.Equals(value, account, StringComparison.Ordinal)) return null;
        return value.Length <= MaximumLength ? value : value[..MaximumLength].TrimEnd();
    }

    private static string? Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(); }
            catch (InvalidOperationException) { }
            return null;
        }

        return process.ExitCode == 0 ? output.Trim() : null;
    }

    // USER_INFO_10 of the local account: usri10_name, usri10_comment, usri10_usr_comment, usri10_full_name. A domain account is not
    // found on this computer: the display name the system gives it, from GetUserNameEx.
    private static string? ReadWindows(string user)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (NetUserGetInfo(null, user, 10, out var buffer) == 0)
        {
            try
            {
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, 3 * IntPtr.Size)) is { Length: > 0 } full) return full;
            }
            finally
            {
                NetApiBufferFree(buffer);
            }
        }

        var size = 256u;
        var display = new StringBuilder((int)size);
        return GetUserNameEx(NameDisplay, display, ref size) ? display.ToString() : null;
    }

    private const int NameDisplay = 3;

    [DllImport("netapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserNameExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);
}
