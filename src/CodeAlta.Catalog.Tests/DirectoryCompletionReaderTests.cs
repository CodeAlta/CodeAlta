namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class DirectoryCompletionReaderTests
{
    [TestMethod]
    public void RejectsUnscopedOrOversizedInputsWithoutReadingFilesystem()
    {
        using var fixture = new Fixture();
        foreach (var path in new string?[] { null, "", " ", ".", "relative/path", "~", Path.GetPathRoot(fixture.Root),
            new string('x', DirectoryCompletionReader.MaximumDirectoryLength + 1),
            OperatingSystem.IsWindows() ? @"\\server\share\folder" : "//server/share/folder" })
            Assert.AreEqual(DirectoryCompletionStatus.Invalid, DirectoryCompletionReader.Read(path, "").Status, path);
        Assert.AreEqual(DirectoryCompletionStatus.Invalid, DirectoryCompletionReader.Read(fixture.Root, "a/b").Status);
        Assert.AreEqual(DirectoryCompletionStatus.Invalid, DirectoryCompletionReader.Read(fixture.Root, null).Status);
        Assert.AreEqual(DirectoryCompletionStatus.Invalid, DirectoryCompletionReader.Read(fixture.Root, "bad\0name").Status);
        Assert.AreEqual(DirectoryCompletionStatus.Invalid, DirectoryCompletionReader.Read(fixture.Root, new string('x', DirectoryCompletionReader.MaximumPrefixLength + 1)).Status);
        if (OperatingSystem.IsWindows())
            Assert.AreEqual(DirectoryCompletionStatus.Invalid, DirectoryCompletionReader.Read(@"\\?\C:\Windows", "").Status);
        Assert.AreEqual(DirectoryCompletionStatus.Invalid, DirectoryCompletionReader.Read(Path.Combine(fixture.Root, "..", Path.GetFileName(fixture.Root)), "").Status);
        Assert.Throws<OperationCanceledException>(() => DirectoryCompletionReader.Read(fixture.Root, "", new CancellationToken(true)));
    }

    [TestMethod]
    public void DistinguishesMissingFileAndCompleteLiteralPrefixResults()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "éclair"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Echo"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "empty"));
        File.WriteAllText(Path.Combine(fixture.Root, "e-file"), "not read by the reader");
        Assert.AreEqual(DirectoryCompletionStatus.Missing, DirectoryCompletionReader.Read(Path.Combine(fixture.Root, "missing"), "").Status);
        Assert.AreEqual(DirectoryCompletionStatus.NotDirectory, DirectoryCompletionReader.Read(Path.Combine(fixture.Root, "e-file"), "").Status);
        Assert.AreEqual(DirectoryCompletionStatus.NotDirectory, DirectoryCompletionReader.Read(Path.Combine(fixture.Root, "e-file", "child"), "").Status);
        var result = DirectoryCompletionReader.Read(fixture.Root, "e");
        Assert.AreEqual(DirectoryCompletionStatus.Complete, result.Status);
        CollectionAssert.AreEqual(new[] { Path.Combine(fixture.Root, "Echo"), Path.Combine(fixture.Root, "empty") }, result.Directories.ToArray());
        Assert.AreEqual(4, result.EntriesVisited);
        Assert.AreEqual(DirectoryCompletionStatus.Complete, DirectoryCompletionReader.Read(fixture.Root, "é").Status);
        CollectionAssert.AreEqual(new[] { Path.Combine(fixture.Root, "éclair") }, DirectoryCompletionReader.Read(fixture.Root, "é").Directories.ToArray());
        Assert.AreEqual(0, DirectoryCompletionReader.Read(fixture.Root, "xyz").Directories.Count);
    }

    [TestMethod]
    public void CountsNonmatchingEntriesAndNeverCertifiesAFilteredSubsetAsComplete()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < DirectoryCompletionReader.MaximumEntries + 2; i++)
            File.WriteAllText(Path.Combine(fixture.Root, $"other-{i:D4}"), "");
        var result = DirectoryCompletionReader.Read(fixture.Root, "wanted");
        Assert.AreEqual(DirectoryCompletionStatus.Incomplete, result.Status);
        Assert.AreEqual(DirectoryCompletionReader.MaximumEntries + 1, result.EntriesVisited);
        Assert.AreEqual(0, result.Directories.Count);
    }

    [TestMethod]
    public void ResultCapAndOrderingAreBoundedToObservedSubset()
    {
        using var fixture = new Fixture();
        for (var i = DirectoryCompletionReader.MaximumResults + 1; i >= 0; i--)
            Directory.CreateDirectory(Path.Combine(fixture.Root, $"match-{i:D3}"));
        var result = DirectoryCompletionReader.Read(fixture.Root, "match-");
        Assert.AreEqual(DirectoryCompletionStatus.Incomplete, result.Status);
        Assert.AreEqual(DirectoryCompletionReader.MaximumResults, result.Directories.Count);
        Assert.IsTrue(result.EntriesVisited <= DirectoryCompletionReader.MaximumEntries + 1);
        CollectionAssert.AreEqual(result.Directories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal).ToArray(), result.Directories.ToArray());
        Assert.IsTrue(result.Directories.Sum(value => value.Length) <= DirectoryCompletionReader.MaximumTotalResultCharacters);
    }

    [TestMethod]
    public void ExactResultLimitCanBeCompleteWhenTheWholeDirectoryWasVisited()
    {
        using var fixture = new Fixture();
        for (var i = DirectoryCompletionReader.MaximumResults - 1; i >= 0; i--)
            Directory.CreateDirectory(Path.Combine(fixture.Root, $"match-{i:D3}"));
        var result = DirectoryCompletionReader.Read(fixture.Root, "match-");
        Assert.AreEqual(DirectoryCompletionStatus.Complete, result.Status);
        Assert.AreEqual(DirectoryCompletionReader.MaximumResults, result.EntriesVisited);
        CollectionAssert.AreEqual(Enumerable.Range(0, DirectoryCompletionReader.MaximumResults)
            .Select(i => Path.Combine(fixture.Root, $"match-{i:D3}")).ToArray(), result.Directories.ToArray());
    }

    [TestMethod]
    public void HiddenAndLinkedChildrenAreOmittedWithoutClaimingCompletion()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "visible"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".hidden"));
        var hidden = DirectoryCompletionReader.Read(fixture.Root, "");
        Assert.AreEqual(DirectoryCompletionStatus.Incomplete, hidden.Status);
        Assert.IsTrue(hidden.OmittedUnsafeEntries);
        CollectionAssert.AreEqual(new[] { Path.Combine(fixture.Root, "visible") }, hidden.Directories.ToArray());
        var link = Path.Combine(fixture.Root, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(fixture.Root, "visible"));
            var result = DirectoryCompletionReader.Read(fixture.Root, "");
            Assert.AreEqual(DirectoryCompletionStatus.Incomplete, result.Status);
            Assert.IsTrue(result.OmittedUnsafeEntries);
            Assert.IsFalse(result.Directories.Contains(link));
            Assert.AreEqual(DirectoryCompletionStatus.ReadError, DirectoryCompletionReader.Read(link, "").Status);
            Assert.AreEqual(DirectoryCompletionStatus.ReadError, DirectoryCompletionReader.Read(Path.Combine(link, "child"), "").Status);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Some hosts cannot create symbolic links; the hidden omission still runs.
        }
    }

    [TestMethod]
    public void AccessDenialIsDistinctWhenTheLocalTestHostEnforcesUnixModes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var denied = Path.Combine(fixture.Root, "denied");
        Directory.CreateDirectory(denied);
        var original = File.GetUnixFileMode(denied);
        try
        {
            File.SetUnixFileMode(denied, UnixFileMode.None);
            // Privileged test hosts can still enumerate; do not infer denial from mode bits alone.
            var result = DirectoryCompletionReader.Read(denied, "");
            if (result.Status != DirectoryCompletionStatus.Complete)
                Assert.AreEqual(DirectoryCompletionStatus.Denied, result.Status);
        }
        finally { File.SetUnixFileMode(denied, original); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-completion-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
