using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CodeAlta.Fleet.Update.Service;

/// <summary>
/// Sets the account and the password a Windows service logs on as, through the API of the Service Control Manager: the password
/// goes to Windows, which keeps it as a secret of the system, and never on a command line, in a file or in a log. The account is
/// given the right to log on as a service (<c>SeServiceLogonRight</c>), which the console of services gives and <c>sc.exe</c> does not.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsServiceLogon
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint PolicyCreateAccount = 0x00000010;
    private const uint PolicyLookupNames = 0x00000800;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// Gives a folder, in one write, the administrators as its owner and a protected access list: SYSTEM and the administrators
    /// with full control, the account with modify, inherited by what it holds. Nothing it allowed before remains.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="account">The account, such as <c>NT SERVICE\altanode</c>.</param>
    /// <exception cref="UnauthorizedAccessException">This process may not change the folder (not elevated).</exception>
    /// <exception cref="System.Security.Principal.IdentityNotMappedException">The account does not exist.</exception>
    public static void ProtectFolder(string folder, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        const System.Security.AccessControl.InheritanceFlags inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit
            | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
        var administrators = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        var service = (System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(account).Translate(typeof(System.Security.Principal.SecurityIdentifier));
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        foreach (var (identity, rights) in new[] { (system, System.Security.AccessControl.FileSystemRights.FullControl),
            (administrators, System.Security.AccessControl.FileSystemRights.FullControl), (service, System.Security.AccessControl.FileSystemRights.Modify) })
        {
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity, rights, inherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        }

        new DirectoryInfo(folder).SetAccessControl(security);
    }

    /// <summary>Makes a service log on as an account, with its password, and gives the account the right to.</summary>
    /// <param name="serviceName">The service.</param>
    /// <param name="account">The account: <c>.\name</c>, <c>DOMAIN\name</c> or <c>name@domain</c>.</param>
    /// <param name="password">Its password.</param>
    /// <exception cref="Win32Exception">Windows refused (the account does not exist, this process is not elevated).</exception>
    public static void Set(string serviceName, string account, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentNullException.ThrowIfNull(password);
        GrantServiceLogonRight(account);
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == 0) throw new Win32Exception();
        try
        {
            var service = OpenServiceW(manager, serviceName, ServiceChangeConfig);
            if (service == 0) throw new Win32Exception();
            try
            {
                if (!ChangeServiceConfigW(service, ServiceNoChange, ServiceNoChange, ServiceNoChange, null, null, 0, null, account, password, null))
                    throw new Win32Exception();
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static void GrantServiceLogonRight(string account)
    {
        // A local account named .\name is looked up by its name on this machine.
        var name = account.StartsWith(@".\", StringComparison.Ordinal) ? account[2..] : account;
        var sidSize = 0;
        var domainSize = 0;
        LookupAccountNameW(null, name, null, ref sidSize, null, ref domainSize, out _);
        if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer) throw new Win32Exception();
        var sid = new byte[sidSize];
        var domain = new char[domainSize];
        if (!LookupAccountNameW(null, name, sid, ref sidSize, domain, ref domainSize, out _)) throw new Win32Exception();

        var attributes = new LsaObjectAttributes { Length = Marshal.SizeOf<LsaObjectAttributes>() };
        var status = LsaOpenPolicy(0, ref attributes, PolicyCreateAccount | PolicyLookupNames, out var policy);
        if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        var right = Marshal.StringToHGlobalUni("SeServiceLogonRight");
        try
        {
            var text = new LsaUnicodeString { Length = (ushort)("SeServiceLogonRight".Length * 2), MaximumLength = (ushort)(("SeServiceLogonRight".Length + 1) * 2), Buffer = right };
            status = LsaAddAccountRights(policy, sid, ref text, 1);
            if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        }
        finally
        {
            Marshal.FreeHGlobal(right);
            LsaClose(policy);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenServiceW(nint manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(nint service, uint serviceType, uint startType, uint errorControl, string? binaryPathName, string? loadOrderGroup,
        nint tagId, string? dependencies, string? serviceStartName, string? password, string? displayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupAccountNameW(string? systemName, string accountName, byte[]? sid, ref int sidSize, char[]? domainName, ref int domainSize, out int use);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaOpenPolicy(nint systemName, ref LsaObjectAttributes attributes, uint desiredAccess, out nint policy);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaAddAccountRights(nint policy, byte[] sid, ref LsaUnicodeString rights, uint count);

    [LibraryImport("advapi32.dll")]
    private static partial uint LsaClose(nint policy);

    [LibraryImport("advapi32.dll")]
    private static partial int LsaNtStatusToWinError(uint status);
}
