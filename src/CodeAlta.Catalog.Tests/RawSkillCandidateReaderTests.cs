using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class RawSkillCandidateReaderTests
{
    [TestMethod]
    public void RejectsUnscopedInputsAndCancellationBeforeReading()
    {
        using var fixture = new Fixture();
        foreach (var path in new string?[] { null, "", " ", ".", "relative", "~", Path.GetPathRoot(fixture.Root),
            new string('x', RawSkillCandidateReader.MaximumPathLength + 1),
            OperatingSystem.IsWindows() ? @"\\server\share\skills" : "//server/share/skills",
            Path.Combine(fixture.Root, "..", Path.GetFileName(fixture.Root)), Path.Combine(fixture.Root, ".git", "skills") })
            Assert.AreEqual(RawSkillCandidateStatus.Invalid, RawSkillCandidateReader.Scan(path).Status, path);
        if (OperatingSystem.IsWindows())
            Assert.AreEqual(RawSkillCandidateStatus.Invalid, RawSkillCandidateReader.Scan(@"\\?\C:\Windows").Status);
        Assert.Throws<OperationCanceledException>(() => RawSkillCandidateReader.Scan(fixture.Root, new CancellationToken(true)));
        Assert.AreEqual(RawSkillCandidateStatus.Missing, RawSkillCandidateReader.Scan(Path.Combine(fixture.Root, "missing")).Status);
        var file = Path.Combine(fixture.Root, "regular");
        File.WriteAllText(file, "not read");
        Assert.AreEqual(RawSkillCandidateStatus.NotDirectory, RawSkillCandidateReader.Scan(file).Status);
        Assert.AreEqual(RawSkillCandidateStatus.NotDirectory, RawSkillCandidateReader.Scan(Path.Combine(file, "child")).Status);
    }

    [TestMethod]
    public void RawHiddenAndCaseInsensitiveNestedCandidatesAreSortedOnlyWithinObservedTree()
    {
        using var fixture = new Fixture();
        var nested = Path.Combine(fixture.Root, "b", "deep");
        var hidden = Path.Combine(fixture.Root, ".hidden");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(hidden);
        File.WriteAllText(Path.Combine(nested, "skill.MD"), "unparsed");
        File.WriteAllText(Path.Combine(hidden, "SKILL.md"), "unparsed");
        File.WriteAllText(Path.Combine(fixture.Root, "SKILL.md"), "unparsed");
        File.WriteAllText(Path.Combine(nested, ".gitignore"), "SKILL.md");
        var result = RawSkillCandidateReader.Scan(fixture.Root);
        Assert.AreEqual(RawSkillCandidateStatus.Complete, result.Status);
        CollectionAssert.AreEqual(new[] { Path.Combine(fixture.Root, ".hidden", "SKILL.md"),
            Path.Combine(nested, "skill.MD"), Path.Combine(fixture.Root, "SKILL.md") }, result.CandidatePaths.ToArray());
        Assert.AreEqual(RawSkillCandidateDiagnostics.None, result.Diagnostics);
        Assert.IsTrue(result.EntriesVisited >= 6);
        Assert.IsTrue(result.DirectoriesOpened >= 3);
    }

    [TestMethod]
    public void NonmatchingHighCardinalityConsumesGlobalEntryBudgetIncludingSentinel()
    {
        using var fixture = new Fixture();
        for (var i = 0; i <= RawSkillCandidateReader.MaximumEntries + 1; i++)
            File.WriteAllText(Path.Combine(fixture.Root, $"other-{i:D4}"), "");
        var result = RawSkillCandidateReader.Scan(fixture.Root);
        Assert.AreEqual(RawSkillCandidateStatus.Incomplete, result.Status);
        Assert.IsTrue(result.Diagnostics.HasFlag(RawSkillCandidateDiagnostics.EntryLimit));
        Assert.AreEqual(RawSkillCandidateReader.MaximumEntries + 1, result.EntriesVisited);
        Assert.AreEqual(0, result.CandidatePaths.Count);
    }

    [TestMethod]
    public void CandidateDepthQueueAndDirectoryWorkAreIndependentlyBounded()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < RawSkillCandidateReader.MaximumCandidates + 3; i++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, $"candidate-{i:D3}"));
            File.WriteAllText(Path.Combine(directory.FullName, "SKILL.md"), "");
        }
        var candidates = RawSkillCandidateReader.Scan(fixture.Root);
        Assert.AreEqual(RawSkillCandidateStatus.Incomplete, candidates.Status);
        Assert.IsTrue(candidates.Diagnostics.HasFlag(RawSkillCandidateDiagnostics.CandidateLimit));
        Assert.AreEqual(RawSkillCandidateReader.MaximumCandidates, candidates.CandidatePaths.Count);
        Assert.IsTrue(candidates.CandidatePaths.Sum(path => path.Length) <= RawSkillCandidateReader.MaximumTotalCandidateCharacters);
        CollectionAssert.AreEqual(candidates.CandidatePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal).ToArray(), candidates.CandidatePaths.ToArray());

        using var queueFixture = new Fixture();
        for (var i = 0; i < RawSkillCandidateReader.MaximumPendingDirectories + 2; i++)
            Directory.CreateDirectory(Path.Combine(queueFixture.Root, $"empty-{i:D3}"));
        var queued = RawSkillCandidateReader.Scan(queueFixture.Root);
        Assert.AreEqual(RawSkillCandidateStatus.Incomplete, queued.Status);
        Assert.IsTrue(queued.Diagnostics.HasFlag(RawSkillCandidateDiagnostics.PendingDirectoryLimit));
        Assert.IsTrue(queued.DirectoriesOpened <= RawSkillCandidateReader.MaximumDirectories);

        using var depthFixture = new Fixture();
        var child = depthFixture.Root;
        for (var i = 0; i <= RawSkillCandidateReader.MaximumDepth; i++)
            child = Directory.CreateDirectory(Path.Combine(child, $"d{i}")).FullName;
        File.WriteAllText(Path.Combine(child, "SKILL.md"), "");
        var deep = RawSkillCandidateReader.Scan(depthFixture.Root);
        Assert.AreEqual(RawSkillCandidateStatus.Incomplete, deep.Status);
        Assert.IsTrue(deep.Diagnostics.HasFlag(RawSkillCandidateDiagnostics.DepthLimit));
        Assert.AreEqual(0, deep.CandidatePaths.Count);

        using var workFixture = new Fixture();
        var frontier = new List<string> { workFixture.Root };
        for (var depth = 0; depth < RawSkillCandidateReader.MaximumDepth; depth++)
            frontier = frontier.SelectMany(parent => Enumerable.Range(0, 2)
                .Select(i => Directory.CreateDirectory(Path.Combine(parent, $"child{i}")).FullName)).ToList();
        var work = RawSkillCandidateReader.Scan(workFixture.Root);
        Assert.AreEqual(RawSkillCandidateStatus.Incomplete, work.Status);
        Assert.IsTrue(work.Diagnostics.HasFlag(RawSkillCandidateDiagnostics.DirectoryLimit));
        Assert.AreEqual(RawSkillCandidateReader.MaximumDirectories, work.DirectoriesOpened);
    }

    [TestMethod]
    public void AggregateCandidateCharactersAndChildPathsAreBounded()
    {
        using var fixture = new Fixture();
        var deepRoot = fixture.Root;
        for (var i = 0; i < 4; i++)
            deepRoot = Directory.CreateDirectory(Path.Combine(deepRoot, $"part{i}-" + new string('a', 130))).FullName;
        for (var i = 0; i < RawSkillCandidateReader.MaximumCandidates; i++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(deepRoot, $"child{i:D2}-" + new string('b', 100)));
            File.WriteAllText(Path.Combine(directory.FullName, "SKILL.md"), "");
        }
        var chars = RawSkillCandidateReader.Scan(fixture.Root);
        Assert.AreEqual(RawSkillCandidateStatus.Incomplete, chars.Status);
        Assert.IsTrue(chars.Diagnostics.HasFlag(RawSkillCandidateDiagnostics.CandidateCharactersLimit));
        Assert.IsTrue(chars.CandidatePaths.Sum(path => path.Length) <= RawSkillCandidateReader.MaximumTotalCandidateCharacters);
        Assert.IsTrue(chars.CandidatePaths.Count < RawSkillCandidateReader.MaximumCandidates);

        // All individually constructed paths are bounded even if an existing child exceeds that bound.
        var longChild = Directory.CreateDirectory(Path.Combine(deepRoot, "long-" + new string('c', 240)));
        var oversized = Directory.CreateDirectory(Path.Combine(longChild.FullName, "deeper-" + new string('d', 240)));
        File.WriteAllText(Path.Combine(oversized.FullName, "SKILL.md"), "");
        var paths = RawSkillCandidateReader.Scan(deepRoot);
        Assert.IsTrue(paths.Diagnostics.HasFlag(RawSkillCandidateDiagnostics.PathLimit));
        Assert.IsTrue(paths.CandidatePaths.All(path => path.Length <= RawSkillCandidateReader.MaximumPathLength));
    }

    [TestMethod]
    public void DirectoryDenialDoesNotCertifyEmptyWhenEnforcedByHost()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var denied = Directory.CreateDirectory(Path.Combine(fixture.Root, "denied")).FullName;
        var mode = File.GetUnixFileMode(denied);
        try
        {
            File.SetUnixFileMode(denied, UnixFileMode.None);
            var result = RawSkillCandidateReader.Scan(denied);
            if (result.Status != RawSkillCandidateStatus.Complete)
                Assert.AreEqual(RawSkillCandidateStatus.Denied, result.Status);
        }
        finally { File.SetUnixFileMode(denied, mode); }
    }

    [TestMethod]
    public void FixedMetadataAndLinkedEntriesNeverRecurseAndReportOmissions()
    {
        using var fixture = new Fixture();
        foreach (var metadata in new[] { ".git", ".hg", ".svn", ".jj", ".sl" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, metadata));
            File.WriteAllText(Path.Combine(directory.FullName, "SKILL.md"), "");
        }
        var result = RawSkillCandidateReader.Scan(fixture.Root);
        Assert.AreEqual(RawSkillCandidateStatus.Incomplete, result.Status);
        Assert.IsTrue(result.Diagnostics.HasFlag(RawSkillCandidateDiagnostics.MetadataDirectory));
        Assert.AreEqual(0, result.CandidatePaths.Count);
        Assert.AreEqual(1, result.DirectoriesOpened);
        var link = Path.Combine(fixture.Root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(fixture.Root, ".git"));
            var linked = RawSkillCandidateReader.Scan(fixture.Root);
            Assert.IsTrue(linked.Diagnostics.HasFlag(RawSkillCandidateDiagnostics.LinkedEntry));
            Assert.AreEqual(RawSkillCandidateStatus.ReadError, RawSkillCandidateReader.Scan(link).Status);
            Assert.AreEqual(RawSkillCandidateStatus.ReadError, RawSkillCandidateReader.Scan(Path.Combine(link, "child")).Status);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // The fixed-metadata test still runs on hosts without symbolic-link permission.
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-raw-skills-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
