using System.Runtime.InteropServices;
using System.Text;
using CodeAlta.Credentials.Providers;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class KeychainProviderTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task A_generic_credential_of_Windows_is_read_by_its_target_and_listed_without_its_value()
    {
        if (!OperatingSystem.IsWindows()) return;
        var wide = $"CodeAlta.Tests/{Guid.NewGuid():N}";
        var narrow = $"CodeAlta.Tests/{Guid.NewGuid():N}";
        try
        {
            Write(wide, Encoding.Unicode.GetBytes("keychain-value-utf16-é"), "alice");
            Write(narrow, Encoding.UTF8.GetBytes("keychain-value-utf8-é"), null);
            var provider = ProviderHarness.Create(KeychainProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?>());
            Assert.AreEqual(CredentialProviderState.Ready, (await provider.GetStatusAsync(CancellationToken.None)).State);
            Assert.AreEqual("keychain-value-utf16-é", (await provider.ReadAsync(wide, false, CancellationToken.None)).Value);
            Assert.AreEqual("keychain-value-utf8-é", (await provider.ReadAsync(narrow, false, CancellationToken.None)).Value);

            var page = await provider.BrowseAsync(new CredentialBrowseRequest(null), CancellationToken.None);
            var listed = page.Entries.Single(entry => entry.Reference == wide);
            Assert.AreEqual((wide, CredentialBrowseEntryKind.Field, "alice"), (listed.Label, listed.Kind, listed.Detail));
            Assert.IsTrue(page.Entries.Any(entry => entry.Reference == narrow));
            ProviderHarness.AssertHoldsNone(page, "keychain-value-utf16", "keychain-value-utf8");
        }
        finally
        {
            CredDeleteW(wide, 1, 0);
            CredDeleteW(narrow, 1, 0);
        }

        var provider2 = ProviderHarness.Create(KeychainProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?>());
        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.ReadFailsAsync(provider2, wide)).Failure);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void A_blob_is_read_as_the_text_it_was_written()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.AreEqual("abcd", WindowsCredentials.Decode(Encoding.UTF8.GetBytes("abcd")), "an even ASCII text is not read as UTF-16");
        Assert.AreEqual("abcd", WindowsCredentials.Decode(Encoding.Unicode.GetBytes("abcd")));
        Assert.AreEqual("日本語", WindowsCredentials.Decode(Encoding.Unicode.GetBytes("日本語")));
        Assert.AreEqual("日本語", WindowsCredentials.Decode(Encoding.UTF8.GetBytes("日本語")));
        Assert.IsNull(WindowsCredentials.Decode([0xFF, 0xD8, 0x00]), "an odd blob that is not UTF-8 is no text");
    }

    [TestMethod]
    public void A_utf16_text_without_zero_bytes_is_not_read_as_utf8_control_characters()
    {
        if (!OperatingSystem.IsWindows()) return;
        // "ЖЖ" in UTF-16LE is 16 04 16 04: valid UTF-8 too, of two control characters.
        Assert.AreEqual("ЖЖ", WindowsCredentials.Decode(Encoding.Unicode.GetBytes("ЖЖ")));
        Assert.AreEqual("пароль", WindowsCredentials.Decode(Encoding.Unicode.GetBytes("пароль")));
        Assert.AreEqual("пароль", WindowsCredentials.Decode(Encoding.UTF8.GetBytes("пароль")));
        Assert.AreEqual("ΑΒΓΔ", WindowsCredentials.Decode(Encoding.Unicode.GetBytes("ΑΒΓΔ")));
        Assert.AreEqual("Жa", WindowsCredentials.Decode(Encoding.Unicode.GetBytes("Жa")));
        Assert.AreEqual("ghp_abcdefgh12", WindowsCredentials.Decode(Encoding.UTF8.GetBytes("ghp_abcdefgh12")), "an ASCII token of an even length stays UTF-8");
        Assert.AreEqual("line\none", WindowsCredentials.Decode(Encoding.UTF8.GetBytes("line\none")), "a new line is not a control character that decides");
    }

    [TestMethod]
    public void The_references_of_macOS_and_Linux_name_a_service_an_account_or_attributes()
    {
        Assert.AreEqual(("github", (string?)"alice"), KeychainProvider.ServiceAccount("github/alice"));
        Assert.AreEqual(("github", (string?)null), KeychainProvider.ServiceAccount("github"));
        Assert.AreEqual(CredentialFailure.NotFound, Assert.ThrowsExactly<CredentialProviderException>(() => KeychainProvider.ServiceAccount("github/")).Failure);
        CollectionAssert.AreEqual(new[] { ("service", "github"), ("account", "alice") }, KeychainProvider.LinuxAttributes("github/alice").ToArray());
        CollectionAssert.AreEqual(new[] { ("application", "my app"), ("user", "a=b") }, KeychainProvider.LinuxAttributes("application=my%20app&user=a%3Db").ToArray());
        Assert.ThrowsExactly<CredentialProviderException>(() => KeychainProvider.LinuxAttributes("=value"));
    }

    [TestMethod]
    public async Task Only_Windows_lists_its_keychain()
    {
        var provider = ProviderHarness.Create(KeychainProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?>());
        if (OperatingSystem.IsWindows())
        {
            Assert.IsNotNull(await provider.BrowseAsync(new CredentialBrowseRequest(null), CancellationToken.None));
            return;
        }

        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () => await provider.BrowseAsync(new CredentialBrowseRequest(null), CancellationToken.None));
    }

    private static void Write(string target, byte[] blob, string? userName)
    {
        var targetName = Marshal.StringToHGlobalUni(target);
        var user = userName is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(userName);
        var data = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, data, blob.Length);
            // A credential of the logon session only: nothing is kept on the disk.
            var credential = new Credential { Type = 1, TargetName = targetName, CredentialBlobSize = (uint)blob.Length, CredentialBlob = data, Persist = 1, UserName = user };
            if (!CredWriteW(ref credential, 0)) throw new InvalidOperationException($"CredWriteW failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(targetName);
            if (user != IntPtr.Zero) Marshal.FreeHGlobal(user);
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

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);
}
