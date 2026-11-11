using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CodeAlta.Testing;

/// <summary>
/// Whether this process can create symbolic links, for the tests that make them: such a test calls
/// <see cref="RequireSymbolicLinks"/> first, and is inconclusive, not failed, where links cannot be made.
/// </summary>
/// <remarks>
/// On Windows, creating a symbolic link takes <c>SeCreateSymbolicLinkPrivilege</c>, which only an elevated administrator
/// holds by default, or Developer Mode, with which a process without that privilege creates links too (.NET passes
/// <c>SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE</c>). Without either, <see cref="File.CreateSymbolicLink"/> throws an
/// <see cref="IOException"/>: "A required privilege is not held by the client". Other systems need neither. This file is
/// linked into every test project by <c>Directory.Build.props</c>.
/// </remarks>
internal static class TestLinks
{
    /// <summary>What a test that needs links says when this process cannot create them.</summary>
    public const string Unavailable =
        "This test creates symbolic links, which Windows allows only with Developer Mode (Settings > System > For developers) " +
        "or in an elevated process: this process does not hold SeCreateSymbolicLinkPrivilege and Developer Mode is off.";

    private const string CreateSymbolicLinkPrivilege = "SeCreateSymbolicLinkPrivilege";
    private const int TokenPrivileges = 3;
    private const int PrivilegeRemoved = 0x4;
    private const int PrivilegesHeader = 4; // TOKEN_PRIVILEGES: the count, then LUID_AND_ATTRIBUTES entries.
    private const int PrivilegeEntry = 12; // LUID_AND_ATTRIBUTES: LowPart, HighPart, Attributes.

    /// <summary>Gets whether this process can create symbolic links.</summary>
    public static bool CanCreateSymbolicLinks
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return true;
            return IsDeveloperModeOn() || HoldsPrivilege(CreateSymbolicLinkPrivilege);
        }
    }

    /// <summary>Makes the calling test inconclusive when this process cannot create symbolic links.</summary>
    /// <exception cref="Microsoft.VisualStudio.TestTools.UnitTesting.AssertInconclusiveException">This process cannot create symbolic links.</exception>
    public static void RequireSymbolicLinks()
    {
        if (!CanCreateSymbolicLinks) Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Inconclusive(Unavailable);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsDeveloperModeOn()
        => Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock", "AllowDevelopmentWithoutDevLicense", 0) is int value && value != 0;

    /// <summary>Gets whether the token of this process holds a privilege, enabled or not: CreateSymbolicLink enables its own for the call.</summary>
    /// <param name="privilege">The name of the privilege, such as <c>SeCreateSymbolicLinkPrivilege</c>.</param>
    /// <returns>Whether the token holds it; false for a name Windows does not know.</returns>
    [SupportedOSPlatform("windows")]
    internal static bool HoldsPrivilege(string privilege)
    {
        if (!LookupPrivilegeValue(null, privilege, out var luid)) return false;
        using var identity = WindowsIdentity.GetCurrent();
        var token = identity.AccessToken;
        GetTokenInformation(token, TokenPrivileges, IntPtr.Zero, 0, out var length);
        if (length <= 0) return false;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenPrivileges, buffer, length, out _)) return false;
            var count = Marshal.ReadInt32(buffer);
            for (var index = 0; index < count; index++)
            {
                var entry = PrivilegesHeader + index * PrivilegeEntry;
                if ((uint)Marshal.ReadInt32(buffer, entry) == luid.LowPart && Marshal.ReadInt32(buffer, entry + 4) == luid.HighPart)
                    return (Marshal.ReadInt32(buffer, entry + 8) & PrivilegeRemoved) == 0;
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returnLength);
}
