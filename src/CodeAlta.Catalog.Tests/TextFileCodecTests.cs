using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class TextFileCodecTests
{
    [TestMethod]
    [DataRow(65001, false)]
    [DataRow(65001, true)]
    [DataRow(1200, true)]
    [DataRow(1201, true)]
    [DataRow(12000, true)]
    [DataRow(12001, true)]
    public async Task SaveAsync_RoundTripsExactBytes(int codePage, bool bom)
    {
        using var temp = new TemporaryFile();
        var encoding = codePage == 65001 ? new UTF8Encoding(bom) : Encoding.GetEncoding(codePage);
        const string text = "héllo 🌍\r\nsecond\nthird\rfourth";
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        await File.WriteAllBytesAsync(temp.Path, bytes);
        var store = new TextFileCodec();
        var snapshot = await store.LoadAsync(temp.Path);

        Assert.AreEqual(text, snapshot.Text);
        Assert.AreEqual(codePage, snapshot.Encoding.CodePage);
        Assert.AreEqual(bom, snapshot.HasByteOrderMark);
        var result = await store.SaveAsync(Request(temp.Path, snapshot, text));

        Assert.IsFalse(result.IsConflict);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(temp.Path));
        Assert.AreEqual(snapshot.Revision, result.Snapshot!.Revision);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("one\n")]
    [DataRow("one\r\n")]
    [DataRow("one\r")]
    [DataRow("no final newline")]
    public async Task SaveAsync_PreservesEmptyAndFinalNewline(string text)
    {
        using var temp = new TemporaryFile();
        await File.WriteAllTextAsync(temp.Path, text);
        var store = new TextFileCodec();
        var snapshot = await store.LoadAsync(temp.Path);
        var result = await store.SaveAsync(Request(temp.Path, snapshot, text));
        Assert.IsFalse(result.IsConflict);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(text), await File.ReadAllBytesAsync(temp.Path));
    }

    [TestMethod]
    public async Task SaveAsync_RejectsSameTimestampEditAndStaleOverwriteConfirmation()
    {
        using var temp = new TemporaryFile();
        await File.WriteAllTextAsync(temp.Path, "first");
        var store = new TextFileCodec();
        var snapshot = await store.LoadAsync(temp.Path);
        await File.WriteAllTextAsync(temp.Path, "other");
        File.SetLastWriteTimeUtc(temp.Path, snapshot.LastWriteTimeUtc.UtcDateTime);

        var conflict = await store.SaveAsync(Request(temp.Path, snapshot, "my edits"));
        Assert.IsTrue(conflict.IsConflict);
        Assert.IsNull(conflict.Snapshot);
        Assert.AreEqual("other", await File.ReadAllTextAsync(temp.Path));

        await File.WriteAllTextAsync(temp.Path, "third");
        var staleConfirmation = await store.SaveAsync(Request(temp.Path, snapshot, "my edits") with { ExpectedRevision = conflict.CurrentRevision });
        Assert.IsTrue(staleConfirmation.IsConflict);
        Assert.AreEqual("third", await File.ReadAllTextAsync(temp.Path));
        var confirmed = await store.SaveAsync(Request(temp.Path, snapshot, "my edits") with { ExpectedRevision = staleConfirmation.CurrentRevision });
        Assert.IsFalse(confirmed.IsConflict);
        Assert.AreEqual("my edits", await File.ReadAllTextAsync(temp.Path));
    }

    [TestMethod]
    public async Task SaveAsync_DetectsDeletionAndExternalCreation()
    {
        using var temp = new TemporaryFile();
        await File.WriteAllTextAsync(temp.Path, "original");
        var store = new TextFileCodec();
        var snapshot = await store.LoadAsync(temp.Path);
        File.Delete(temp.Path);
        var deleted = await store.SaveAsync(Request(temp.Path, snapshot, "edited"));
        Assert.IsTrue(deleted.IsConflict);
        Assert.AreEqual(TextFileRevision.Missing, deleted.CurrentRevision);
        Assert.IsFalse(File.Exists(temp.Path));

        await File.WriteAllTextAsync(temp.Path, "external creation");
        var created = await store.SaveAsync(Request(temp.Path, snapshot, "edited") with { ExpectedRevision = TextFileRevision.Missing });
        Assert.IsTrue(created.IsConflict);
        Assert.AreEqual("external creation", await File.ReadAllTextAsync(temp.Path));
    }

    [TestMethod]
    public async Task SaveAsync_MissingRevisionCreatesNewFileButDiffersFromEmpty()
    {
        using var temp = new TemporaryFile();
        var store = new TextFileCodec();
        Assert.AreEqual(TextFileRevision.Missing, await store.GetRevisionAsync(temp.Path));
        var result = await store.SaveAsync(new TextFileSaveRequest(temp.Path, "", Encoding.UTF8, false, TextFileRevision.Missing));
        Assert.IsFalse(result.IsConflict);
        Assert.AreNotEqual(TextFileRevision.Missing, result.CurrentRevision);
        Assert.AreEqual(0L, new FileInfo(temp.Path).Length);
    }

    [TestMethod]
    public async Task SaveAsync_OnlyOneConcurrentSaveOfARevisionSucceeds()
    {
        using var temp = new TemporaryFile();
        await File.WriteAllTextAsync(temp.Path, "original");
        var store = new TextFileCodec();
        var snapshot = await store.LoadAsync(temp.Path);
        var results = await Task.WhenAll(
            store.SaveAsync(Request(temp.Path, snapshot, "first")),
            store.SaveAsync(Request(temp.Path, snapshot, "second")));
        Assert.AreEqual(1, results.Count(static result => result.IsConflict));
    }

    [TestMethod]
    public async Task SaveAsync_CancellationAndInvalidEncodingLeaveOriginalIntact()
    {
        using var temp = new TemporaryFile();
        await File.WriteAllTextAsync(temp.Path, "original");
        var store = new TextFileCodec();
        var snapshot = await store.LoadAsync(temp.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => store.SaveAsync(Request(temp.Path, snapshot, "edited"), cancellation.Token));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(Request(temp.Path, snapshot, "edited") with { Encoding = Encoding.ASCII, HasByteOrderMark = true }));
        await Assert.ThrowsExactlyAsync<EncoderFallbackException>(() => store.SaveAsync(Request(temp.Path, snapshot, "invalid \uD800")));
        Assert.AreEqual("original", await File.ReadAllTextAsync(temp.Path));
        Assert.AreEqual(1, Directory.GetFiles(temp.Directory).Length);
    }

    [TestMethod]
    public async Task SaveAsync_ReadOnlyDestinationFailureLeavesOriginalAndNoStagingFile()
    {
        using var temp = new TemporaryFile();
        await File.WriteAllTextAsync(temp.Path, "original");
        var store = new TextFileCodec();
        var snapshot = await store.LoadAsync(temp.Path);
        File.SetAttributes(temp.Path, File.GetAttributes(temp.Path) | FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.SaveAsync(Request(temp.Path, snapshot, "edited")));
            Assert.AreEqual("original", await File.ReadAllTextAsync(temp.Path));
            Assert.AreEqual(1, Directory.GetFiles(temp.Directory).Length);
        }
        finally
        {
            File.SetAttributes(temp.Path, FileAttributes.Normal);
        }
    }

    [TestMethod]
    public async Task LoadAsync_RevisionIncludesBomAndRejectsInvalidUtf8()
    {
        using var temp = new TemporaryFile();
        await File.WriteAllTextAsync(temp.Path, "text", new UTF8Encoding(false));
        var store = new TextFileCodec();
        var plain = await store.LoadAsync(temp.Path);
        await File.WriteAllTextAsync(temp.Path, "text", new UTF8Encoding(true));
        var bom = await store.LoadAsync(temp.Path);
        Assert.AreEqual(plain.Text, bom.Text);
        Assert.AreNotEqual(plain.Revision, bom.Revision);
        await File.WriteAllBytesAsync(temp.Path, [0xFF]);
        await Assert.ThrowsExactlyAsync<DecoderFallbackException>(() => store.LoadAsync(temp.Path));
    }

    [TestMethod]
    public void ResolvePath_PreservesTrustedAbsoluteAndOrderedRelativeLookup()
    {
        using var first = new TemporaryFile();
        using var second = new TemporaryFile();
        File.WriteAllText(second.Path, "second");
        Assert.AreEqual(second.Path, TextFileCodec.ResolvePath("file.txt", [first.Directory, second.Directory]));
        File.WriteAllText(first.Path, "first");
        Assert.AreEqual(first.Path, TextFileCodec.ResolvePath("file.txt", [first.Directory, second.Directory]));
        Assert.AreEqual(second.Path, TextFileCodec.ResolvePath(second.Path, [first.Directory]));
        Assert.AreEqual(Path.Combine(first.Directory, "missing.txt"), TextFileCodec.ResolvePath("missing.txt", [first.Directory]));
    }

    [TestMethod]
    public async Task SaveAsync_FollowsFileSymbolicLinkWithoutReplacingLink()
    {
        using var temp = new TemporaryFile();
        await File.WriteAllTextAsync(temp.Path, "original");
        var link = Path.Combine(temp.Directory, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, temp.Path);
        }
        // ERROR_PRIVILEGE_NOT_HELD: Windows without Developer Mode or elevation cannot create symbolic links.
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070522))
        {
            Assert.Inconclusive("Creating a temporary symbolic link requires platform permission.");
        }

        var store = new TextFileCodec();
        var snapshot = await store.LoadAsync(link);
        var result = await store.SaveAsync(Request(link, snapshot, "edited"));
        Assert.IsFalse(result.IsConflict);
        Assert.IsNotNull(new FileInfo(link).LinkTarget);
        Assert.AreEqual("edited", await File.ReadAllTextAsync(temp.Path));
    }

    [TestMethod]
    public async Task SaveAsync_PreservesUnixModeBits()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix mode bits are only available on Unix.");
            return;
        }

        using var temp = new TemporaryFile();
        await File.WriteAllTextAsync(temp.Path, "original");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(temp.Path, mode);
        var store = new TextFileCodec();
        var snapshot = await store.LoadAsync(temp.Path);
        Assert.IsFalse((await store.SaveAsync(Request(temp.Path, snapshot, "edited"))).IsConflict);
        Assert.AreEqual(mode, File.GetUnixFileMode(temp.Path));
    }

    [TestMethod]
    public async Task SaveAsync_WindowsStagingUsesOriginalRestrictiveDaclBeforeWritingAndAfterClosing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows DACL creation is only available on Windows.");
            return;
        }

        using var temp = new TemporaryFile();
        var directory = new DirectoryInfo(temp.Directory);
        var directorySecurity = directory.GetAccessControl(AccessControlSections.Access);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            everyone, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(directorySecurity);

        await File.WriteAllTextAsync(temp.Path, "original");
        var original = new FileInfo(temp.Path);
        var owner = (SecurityIdentifier)original.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))!;
        var restricted = new FileSecurity();
        restricted.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        restricted.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        original.SetAccessControl(restricted);
        var expectedDacl = GetDaclBytes(original.GetAccessControl(AccessControlSections.Access));

        var stagingPath = Path.Combine(temp.Directory, "stage.tmp");
        await using (var stream = TextFileCodec.CreateStagingFile(stagingPath, temp.Path))
        {
            // Inspect the actual file before any edited bytes are written, not a later ACL repair.
            var atCreation = new FileInfo(stagingPath).GetAccessControl(AccessControlSections.Access);
            Assert.IsTrue(atCreation.AreAccessRulesProtected, "Staging must not inherit the directory's additional reader grants.");
            Assert.IsTrue(expectedDacl.AsSpan().SequenceEqual(GetDaclBytes(atCreation)), "Staging must be created with the original DACL.");
            await stream.WriteAsync(Encoding.UTF8.GetBytes("edited private contents"));
        }

        var afterClose = new FileInfo(stagingPath).GetAccessControl(AccessControlSections.Access);
        Assert.IsTrue(afterClose.AreAccessRulesProtected);
        Assert.IsTrue(expectedDacl.AsSpan().SequenceEqual(GetDaclBytes(afterClose)), "The original DACL must still protect staged bytes after the handle closes.");
        Assert.AreEqual("edited private contents", await File.ReadAllTextAsync(stagingPath));
        File.Delete(stagingPath);

        // Exercise the production replacement path too: restrictive staging must not prevent File.Replace.
        var store = new TextFileCodec();
        var snapshot = await store.LoadAsync(temp.Path);
        Assert.IsFalse((await store.SaveAsync(Request(temp.Path, snapshot, "edited private contents"))).IsConflict);
        Assert.AreEqual("edited private contents", await File.ReadAllTextAsync(temp.Path));
        var afterReplace = original.GetAccessControl(AccessControlSections.Access);
        Assert.IsTrue(afterReplace.AreAccessRulesProtected);
        Assert.IsTrue(expectedDacl.AsSpan().SequenceEqual(GetDaclBytes(afterReplace)), "Replacement must retain the target's original DACL.");
        Assert.AreEqual(1, Directory.GetFiles(temp.Directory).Length);
    }

    [TestMethod]
    public void CreateStagingFile_WindowsAclReadFailureDoesNotFallBackToDirectoryPermissions()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows DACL creation is only available on Windows.");
            return;
        }

        using var temp = new TemporaryFile();
        var stagingPath = Path.Combine(temp.Directory, "stage.tmp");
        Assert.ThrowsExactly<FileNotFoundException>(() =>
        {
            using var stream = TextFileCodec.CreateStagingFile(stagingPath, temp.Path);
        });
        Assert.IsFalse(File.Exists(stagingPath), "Failure to read an original DACL must not create a permissive staging file.");
        Assert.AreEqual(0, Directory.GetFiles(temp.Directory).Length);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] GetDaclBytes(FileSecurity security)
    {
        // Compare ACEs structurally; Windows may change descriptor auto-inheritance flags on creation.
        var dacl = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl;
        Assert.IsNotNull(dacl);
        var bytes = new byte[dacl.BinaryLength];
        dacl.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static TextFileSaveRequest Request(string path, TextFileSnapshot snapshot, string text)
        => new(path, text, snapshot.Encoding, snapshot.HasByteOrderMark, snapshot.Revision);

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile()
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CodeAlta.TextFileTests.{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, "file.txt");
        }

        public string Directory { get; }
        public string Path { get; }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
