using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

/// <summary>
/// The keychain of the system: the generic credentials of the Credential Manager of Windows, the login keychain of macOS
/// (<c>security</c>), or the Secret Service of Linux (<c>secret-tool</c>). The session of the user opens it: CodeAlta asks nothing.
/// </summary>
/// <remarks>
/// A reference is the target name of a credential on Windows; <c>&lt;service&gt;</c> or <c>&lt;service&gt;/&lt;account&gt;</c> on
/// macOS; <c>attribute=value&amp;attribute=value</c>, or <c>&lt;service&gt;/&lt;account&gt;</c>, on Linux.
/// </remarks>
internal sealed class KeychainProvider : ICredentialProvider
{
    internal const string TypeName = "keychain";
    private const string MacProgram = "/usr/bin/security";

    private readonly VaultCommandRunner _runner;

    public KeychainProvider(PluginCredentialProviderContext context, VaultCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>Gets how long a read that may not show a prompt waits: longer means the system asks somebody.</summary>
    internal TimeSpan NonInteractiveTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public static PluginCredentialProviderContribution Contribution(VaultCommandRunner runner) => new()
    {
        Type = TypeName,
        DisplayName = "Keychain of the system",
        Description = "Reads the Credential Manager of Windows, the keychain of macOS or the Secret Service of Linux, opened by your session.",
        Icon = "key",
        ReferenceHint = OperatingSystem.IsWindows() ? "git:https://github.com" : OperatingSystem.IsMacOS() ? "service/account" : "service=myapp&account=alice",
        Create = context => new KeychainProvider(context, runner),
    };

    public ValueTask<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return ValueTask.FromResult(CredentialProviderStatus.Ready() with { Account = "Credential Manager" });
        if (OperatingSystem.IsMacOS())
        {
            return ValueTask.FromResult(File.Exists(MacProgram)
                ? CredentialProviderStatus.Ready() with { Account = "login keychain", ProgramPath = MacProgram }
                : CredentialProviderStatus.Unavailable("The security program of macOS is not found."));
        }

        return ValueTask.FromResult(VaultCommandRunner.FindProgram("secret-tool", null) is { } program
            ? CredentialProviderStatus.Ready() with { Account = "Secret Service", ProgramPath = program }
            : new CredentialProviderStatus(CredentialProviderState.NotInstalled, "secret-tool is not installed: it comes with libsecret.")
            {
                Install = new CredentialProviderInstall(["sudo", "apt", "install", "libsecret-tools"], new Uri("https://wiki.gnome.org/Projects/Libsecret")),
            });
    }

    public ValueTask<CredentialProviderStatus> UnlockAsync(CredentialUnlockContext context, CancellationToken cancellationToken) => GetStatusAsync(cancellationToken);

    public async ValueTask<CredentialValue> ReadAsync(string reference, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (OperatingSystem.IsWindows()) return new CredentialValue(WindowsCredentials.Read(reference.Trim()));
        string program;
        List<string> arguments;
        if (OperatingSystem.IsMacOS())
        {
            program = MacProgram;
            var (service, account) = ServiceAccount(reference);
            arguments = ["find-generic-password", "-s", service];
            if (account is not null) arguments.AddRange(["-a", account]);
            arguments.Add("-w");
        }
        else
        {
            program = VaultCommandRunner.FindProgram("secret-tool", null) ?? throw new CredentialProviderException(CredentialFailure.NotInstalled, "secret-tool is not installed.");
            arguments = ["lookup", .. LinuxAttributes(reference).SelectMany(static pair => new[] { pair.Name, pair.Value })];
        }

        var result = await _runner.RunAsync(program, arguments, new VaultCommandOptions { Timeout = interactive ? TimeSpan.FromMinutes(2) : NonInteractiveTimeout }, cancellationToken).ConfigureAwait(false);
        try
        {
            if (result.TimedOut) throw new CredentialProviderException(CredentialFailure.Locked, "The keychain did not answer in time: is the system asking you to unlock it?");
            // secret-tool ends with 1 and prints nothing for a credential it does not find; security ends with 44.
            if (result.ExitCode != 0 || result.Output.Length == 0)
            {
                var notFound = result.ExitCode is 0 or 44 || result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Error);
                throw VaultText.Failure(notFound ? CredentialFailure.NotFound : CredentialFailure.Failed,
                    notFound ? $"The keychain has no credential {reference}." : $"The keychain did not give {reference}: {result.Summary()}.", result);
            }

            return new CredentialValue(VaultText.TrimOneNewline(result.OutputText()));
        }
        finally
        {
            VaultText.Zero(result.Output);
        }
    }

    public ValueTask<CredentialBrowsePage> BrowseAsync(CredentialBrowseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("The keychain of this system lists nothing: type the reference.");
        if (request.Parent is not null) throw new CredentialProviderException(CredentialFailure.NotFound, "This entry of the Credential Manager is not known.");
        return ValueTask.FromResult(new CredentialBrowsePage([.. WindowsCredentials.List()
            .OrderBy(static credential => credential.Target, StringComparer.CurrentCultureIgnoreCase)
            .Select(static credential => new CredentialBrowseEntry(credential.Target, credential.Target, CredentialBrowseEntryKind.Field) { Reference = credential.Target, Detail = credential.UserName })]));
    }

    internal static (string Service, string? Account) ServiceAccount(string reference)
    {
        var at = reference.IndexOf('/');
        var service = (at < 0 ? reference : reference[..at]).Trim();
        var account = at < 0 ? null : reference[(at + 1)..].Trim();
        if (service.Length == 0 || account is { Length: 0 }) throw new CredentialProviderException(CredentialFailure.NotFound, $"'{reference}' is not a credential of the keychain (service, or service/account).");
        return (service, account);
    }

    internal static IReadOnlyList<(string Name, string Value)> LinuxAttributes(string reference)
    {
        if (!reference.Contains('='))
        {
            var (service, account) = ServiceAccount(reference);
            return account is null ? [("service", service)] : [("service", service), ("account", account)];
        }

        var pairs = new List<(string, string)>();
        foreach (var part in reference.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            var name = equals <= 0 ? string.Empty : Uri.UnescapeDataString(part[..equals]).Trim();
            if (name.Length == 0) throw new CredentialProviderException(CredentialFailure.NotFound, $"'{reference}' is not a list of attributes (name=value&name=value).");
            pairs.Add((name, Uri.UnescapeDataString(part[(equals + 1)..])));
        }

        return pairs;
    }
}

// The generic credentials of the Credential Manager of Windows, read through advapi32.
[SupportedOSPlatform("windows")]
internal static class WindowsCredentials
{
    private const uint GenericType = 1;
    private const int NotFound = 1168;

    public static string Read(string target)
    {
        if (!CredReadW(target, GenericType, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) throw new CredentialProviderException(CredentialFailure.NotFound, $"The Credential Manager has no generic credential {target}.");
            throw new CredentialProviderException(CredentialFailure.Failed, $"The Credential Manager cannot read {target}: {new Win32Exception(error).Message}");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                throw new CredentialProviderException(CredentialFailure.NotFound, $"The generic credential {target} holds nothing.");
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Decode(bytes) ?? throw new CredentialProviderException(CredentialFailure.Failed, $"The generic credential {target} does not hold a text.");
            }
            finally
            {
                VaultText.Zero(bytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    // The target names and user names of the generic credentials: their blobs are never read.
    public static IReadOnlyList<(string Target, string? UserName)> List()
    {
        if (!CredEnumerateW(null, 0, out var count, out var array))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return [];
            throw new CredentialProviderException(CredentialFailure.Failed, $"The Credential Manager cannot be listed: {new Win32Exception(error).Message}");
        }

        try
        {
            var list = new List<(string, string?)>(count);
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.ReadIntPtr(array, index * IntPtr.Size);
                if (item == IntPtr.Zero) continue;
                var credential = Marshal.PtrToStructure<Credential>(item);
                if (credential.Type != GenericType || Marshal.PtrToStringUni(credential.TargetName) is not { Length: > 0 } target) continue;
                list.Add((target, Marshal.PtrToStringUni(credential.UserName) is { Length: > 0 } user ? user : null));
            }

            return list;
        }
        finally
        {
            CredFree(array);
        }
    }

    // The blob has no encoding of its own. The tools of Windows write UTF-16LE: cmdkey, the Credential Manager of the Control Panel,
    // the Git Credential Manager (Encoding.Unicode), Python's keyring; others write UTF-8: keytar of Node, go-keyring (the GitHub
    // CLI). The rule, in order:
    // 1. an even length that holds a zero byte and is valid UTF-16 is UTF-16 (a Latin text: UTF-8 never has a zero byte);
    // 2. an even length, valid UTF-16 without control characters, whose UTF-8 reading is invalid or has control characters, is
    //    UTF-16 (a Cyrillic, Greek or CJK text: "ЖЖ" in UTF-16 is 16 04 16 04, valid UTF-8 of two control characters);
    // 3. valid UTF-8 is UTF-8 (an ASCII token of an even length is also valid UTF-16, of CJK characters nobody meant);
    // 4. else UTF-16 when it is valid. What stays ambiguous is a UTF-16 text whose every byte is printable ASCII, such as a single
    //    CJK character (中 is 2D 4E, "-N"): it is read as UTF-8.
    internal static string? Decode(byte[] bytes)
    {
        var utf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var even = bytes.Length % 2 == 0;
        var wide = even ? TryDecode(utf16, bytes)?.TrimEnd('\0') : null;
        if (wide is not null && Array.IndexOf(bytes, (byte)0) >= 0) return wide;
        var narrow = TryDecode(utf8, bytes);
        if (wide is not null && !HasControl(wide) && (narrow is null || HasControl(narrow))) return wide;
        return narrow ?? wide;
    }

    private static bool HasControl(string text)
    {
        foreach (var character in text)
        {
            if (char.IsControl(character) && character is not ('\t' or '\r' or '\n')) return true;
        }

        return false;
    }

    private static string? TryDecode(Encoding encoding, byte[] bytes)
    {
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerateW(string? filter, uint flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr buffer);
}
