using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace CodeAlta.Browser.Launch;

/// <summary>
/// Spares a Windows account the failed logon of a Chromium browser's first start on a profile. That start tries to log on
/// to the account with an empty password, to learn whether its password is blank, and keeps the answer in the
/// <c>Local State</c> file of the profile until the password changes. For a local account with a password, each try
/// counts towards the lockout of the account, and every temporary profile is a first start: a few sessions or a test run
/// can lock the user out. Writing the answer first leaves nothing to try.
/// </summary>
/// <remarks>
/// <c>--guest</c> also prevents the logon, but the browser's connection then ends at <c>Target.createBrowserContext</c>,
/// which every session needs. The names of the file are Chromium's own and undocumented: when they change, or the value
/// is not accepted, the browser tries its logon as it would without this.
/// </remarks>
internal static class ChromiumPasswordCheck
{
    private const string LocalStateFile = "Local State";

    /// <summary>Writes the answer in a new profile of the current account; a profile that has a state keeps it.</summary>
    public static void Seed(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(profileDirectory);
        if (!OperatingSystem.IsWindows() || PasswordAgeInSeconds(Environment.UserName) is not { } age) return;
        Seed(profileDirectory, DateTime.UtcNow, age);
    }

    /// <summary>Writes the answer in a new profile for a password of an age; a profile that has a state keeps it.</summary>
    internal static void Seed(string profileDirectory, DateTime utcNow, uint passwordAgeInSeconds)
    {
        var path = Path.Combine(profileDirectory, LocalStateFile);
        var state = new JsonObject
        {
            ["password_manager"] = new JsonObject
            {
                ["os_password_blank"] = false,
                ["os_password_last_changed"] = LastChanged(utcNow, passwordAgeInSeconds).ToString(CultureInfo.InvariantCulture),
            },
        };
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file);
            writer.Write(state.ToJsonString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The profile has a state of its own, or the browser pays its logon.
        }
    }

    /// <summary>
    /// When the password changed for the browser, in microseconds since 1601. The browser compares it with its own reading
    /// to about the second, and its reading (measured on Chrome and Edge 156) falls in the second after the one the age
    /// gives: the middle of that second stays within it.
    /// </summary>
    internal static long LastChanged(DateTime utcNow, uint passwordAgeInSeconds)
    {
        var sinceEpoch = (utcNow - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / TimeSpan.TicksPerSecond;
        return (sinceEpoch - passwordAgeInSeconds + 1) * 1_000_000 + 500_000;
    }

    private static uint? PasswordAgeInSeconds(string user)
    {
        // A domain account is not found on this computer: nothing is written for it, and its browser does as it would.
        if (NetUserGetInfo(null, user, 1, out var buffer) != 0) return null;
        try
        {
            // USER_INFO_1: usri1_name, usri1_password, usri1_password_age.
            return unchecked((uint)Marshal.ReadInt32(buffer, 2 * IntPtr.Size));
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
    }

    [DllImport("netapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
