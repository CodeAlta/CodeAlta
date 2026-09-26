using System.Text;
using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SkillCandidateMetadataReaderTests
{
    private const string Skill = "---\nname: test-skill\ndescription: Über text\n---\n# Body\n";

    [TestMethod]
    public async Task StrictCompleteRead_BindsOnlySuppliedCandidateAndSupportedBoms()
    {
        using var fixture = new Fixture();
        var parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "nested", "test-skill")).FullName;
        var candidate = Path.Combine(parent, "SKILL.md");
        foreach (var (encoding, preamble) in new (Encoding, byte[])[]
        {
            (new UTF8Encoding(false, true), []), (new UTF8Encoding(true, true), [0xEF, 0xBB, 0xBF]),
            (new UnicodeEncoding(false, true, true), [0xFF, 0xFE]),
            (new UnicodeEncoding(true, true, true), [0xFE, 0xFF]),
            (new UTF32Encoding(false, true, true), [0xFF, 0xFE, 0x00, 0x00]),
            (new UTF32Encoding(true, true, true), [0x00, 0x00, 0xFE, 0xFF]),
        })
        {
            var bytes = preamble.Concat(encoding.GetBytes(Skill)).ToArray();
            await File.WriteAllBytesAsync(candidate, bytes);
            var result = await SkillCandidateMetadataReader.ReadAsync(fixture.Root, candidate);
            Assert.AreEqual(SkillCandidateMetadataStatus.Parsed, result.Status, encoding.WebName);
            Assert.AreEqual(bytes.Length, result.BytesRead);
            Assert.AreEqual("test-skill", result.Frontmatter!.Name);
            Assert.AreEqual("Über text", result.Frontmatter.Description);
            Assert.AreEqual(SkillMetadataDiagnostic.None, result.ParserDiagnostic);
        }
        var mismatch = await SkillCandidateMetadataReader.ReadAsync(fixture.Root, candidate, "other-skill");
        Assert.AreEqual(SkillCandidateMetadataStatus.InvalidPath, mismatch.Status);
        Assert.AreEqual(0, mismatch.BytesRead);
        Assert.AreEqual(SkillMetadataDiagnostic.None, mismatch.ParserDiagnostic);
        Assert.IsNull(mismatch.Frontmatter);
        var match = await SkillCandidateMetadataReader.ReadAsync(fixture.Root, candidate, "test-skill");
        Assert.AreEqual(SkillCandidateMetadataStatus.Parsed, match.Status);
        const string differentName = "---\nname: another-skill\ndescription: complete content\n---\n";
        await File.WriteAllTextAsync(candidate, differentName);
        var parsedMismatch = await SkillCandidateMetadataReader.ReadAsync(fixture.Root, candidate, "test-skill");
        Assert.AreEqual(SkillCandidateMetadataStatus.Invalid, parsedMismatch.Status);
        Assert.AreEqual(SkillMetadataDiagnostic.Field, parsedMismatch.ParserDiagnostic);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(differentName), parsedMismatch.BytesRead);
        Assert.IsNull(parsedMismatch.Frontmatter);
        await File.WriteAllTextAsync(candidate, "---\nname: test-skill\ndescription: *unknown\n---");
        var unsupported = await SkillCandidateMetadataReader.ReadAsync(fixture.Root, candidate);
        Assert.AreEqual(SkillCandidateMetadataStatus.Unsupported, unsupported.Status);
        Assert.AreEqual(SkillMetadataDiagnostic.YamlFeature, unsupported.ParserDiagnostic);
        Assert.IsNull(unsupported.Frontmatter);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await SkillCandidateMetadataReader.ReadAsync(fixture.Root,
            candidate, cancellationToken: new CancellationToken(true)));
    }

    [TestMethod]
    public async Task OversizedRootAncestorIsRefusedBeforeFileSystemAccess()
    {
        using var fixture = new Fixture();
        var root = Path.Combine(fixture.Root, new string('x', RawSkillCandidateReader.MaximumNameLength + 1));
        var result = await SkillCandidateMetadataReader.ReadAsync(root, Path.Combine(root, "SKILL.md"));
        Assert.AreEqual(SkillCandidateMetadataStatus.InvalidPath, result.Status);
        Assert.AreEqual(0, result.BytesRead);
        Assert.AreEqual(SkillMetadataDiagnostic.None, result.ParserDiagnostic);
        Assert.IsNull(result.Frontmatter);
    }

    [TestMethod]
    public async Task MalformedBytesAndSentinelNeverYieldReplacementOrPartialMetadata()
    {
        using var fixture = new Fixture();
        var candidate = Path.Combine(fixture.Root, "SKILL.md");
        foreach (var bytes in new byte[][]
        {
            [0xEF, 0xBB], [0xFF], [0xFF, 0xFE, 0x00], [0xEF, 0xBB, 0xBF, 0xC0, 0xAF],
            [0xFF, 0xFE, 0x00, 0xD8], [0xFE, 0xFF, 0xD8, 0x00],
            [0xFF, 0xFE, 0x00, 0x00, 0x00, 0xD8, 0x00, 0x00],
        })
        {
            await File.WriteAllBytesAsync(candidate, bytes);
            var invalid = await SkillCandidateMetadataReader.ReadAsync(fixture.Root, candidate);
            Assert.AreEqual(SkillCandidateMetadataStatus.InvalidEncoding, invalid.Status, Convert.ToHexString(bytes));
            Assert.IsNull(invalid.Frontmatter);
            Assert.AreEqual(bytes.Length, invalid.BytesRead);
        }
        var prefix = Encoding.UTF8.GetBytes(Skill);
        await File.WriteAllBytesAsync(candidate, prefix.Concat(Enumerable.Repeat((byte)'a',
            SkillCandidateMetadataReader.MaximumBytes - prefix.Length)).ToArray());
        var validAtLimit = await SkillCandidateMetadataReader.ReadAsync(fixture.Root, candidate);
        Assert.AreEqual(SkillCandidateMetadataStatus.Parsed, validAtLimit.Status);
        Assert.AreEqual(SkillCandidateMetadataReader.MaximumBytes, validAtLimit.BytesRead);
        await using (var append = new FileStream(candidate, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) append.WriteByte(0xFF);
        var over = await SkillCandidateMetadataReader.ReadAsync(fixture.Root, candidate);
        Assert.AreEqual(SkillCandidateMetadataStatus.TooLarge, over.Status);
        Assert.AreEqual(SkillCandidateMetadataReader.MaximumBytes + 1, over.BytesRead);
        Assert.AreEqual(SkillMetadataDiagnostic.None, over.ParserDiagnostic);
        Assert.IsNull(over.Frontmatter);
        var longFrontmatter = "---\nname: test-skill\ndescription: " + new string('a', SkillMetadataParser.MaximumFrontmatterCharacters) + "\n---\n";
        await File.WriteAllTextAsync(candidate, longFrontmatter);
        var parserLimit = await SkillCandidateMetadataReader.ReadAsync(fixture.Root, candidate);
        Assert.AreEqual(SkillCandidateMetadataStatus.TooLarge, parserLimit.Status);
        Assert.AreEqual(SkillMetadataDiagnostic.FrontmatterLimit, parserLimit.ParserDiagnostic);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(longFrontmatter), parserLimit.BytesRead);
        Assert.IsNull(parserLimit.Frontmatter);
    }

    [TestMethod]
    public async Task PathAndCancellationBudgetsRefuseBeforeReading()
    {
        using var fixture = new Fixture();
        var deep = Path.Combine(new[] { fixture.Root }.Concat(Enumerable.Repeat("child", RawSkillCandidateReader.MaximumDepth + 1)).ToArray());
        var tooManyAncestors = Path.Combine(new[] { fixture.Root }.Concat(Enumerable.Repeat("d", RawSkillCandidateReader.MaximumRootComponents)).ToArray());
        foreach (var (root, candidate, expected) in new (string, string, string?)[]
        {
            (fixture.Root, Path.Combine(deep, "SKILL.md"), null),
            (tooManyAncestors, Path.Combine(tooManyAncestors, "SKILL.md"), null),
            (fixture.Root, Path.Combine(fixture.Root, new string('x', RawSkillCandidateReader.MaximumNameLength + 1), "SKILL.md"), null),
            (fixture.Root, Path.Combine(fixture.Root, "SKILL.md"), new string('x', RawSkillCandidateReader.MaximumNameLength + 1)),
        })
        {
            var result = await SkillCandidateMetadataReader.ReadAsync(root, candidate, expected);
            Assert.AreEqual(SkillCandidateMetadataStatus.InvalidPath, result.Status);
            Assert.AreEqual(0, result.BytesRead);
            Assert.AreEqual(SkillMetadataDiagnostic.None, result.ParserDiagnostic);
            Assert.IsNull(result.Frontmatter);
        }
        var allowed = Directory.CreateDirectory(Path.Combine(new[] { fixture.Root }
            .Concat(Enumerable.Repeat("child", RawSkillCandidateReader.MaximumDepth)).ToArray())).FullName;
        var allowedCandidate = Path.Combine(allowed, "SKILL.md");
        await File.WriteAllTextAsync(allowedCandidate, Skill);
        Assert.AreEqual(SkillCandidateMetadataStatus.Parsed,
            (await SkillCandidateMetadataReader.ReadAsync(fixture.Root, allowedCandidate)).Status);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await SkillCandidateMetadataReader.ReadAsync(
            null, null, cancellationToken: new CancellationToken(true)));
    }

    [TestMethod]
    public async Task ExplicitScopeAndAttributesRefuseInvalidMissingDirectoriesAndObservedLinks()
    {
        using var fixture = new Fixture();
        var candidate = Path.Combine(fixture.Root, "test-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
        await File.WriteAllTextAsync(candidate, Skill);
        var foreign = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-other-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            foreach (var (root, path) in new (string?, string?)[]
            {
                (null, candidate), (fixture.Root, null), ("relative", candidate), (fixture.Root, "SKILL.md"),
                (Path.GetPathRoot(fixture.Root), candidate), (fixture.Root + Path.DirectorySeparatorChar, candidate),
                (fixture.Root, Path.Combine(fixture.Root, "..", Path.GetFileName(fixture.Root), "test-skill", "SKILL.md")),
                (fixture.Root, Path.Combine(foreign, "SKILL.md")), (fixture.Root, Path.Combine(fixture.Root + "-sibling", "SKILL.md")),
                (fixture.Root, Path.Combine(fixture.Root, ".git", "SKILL.md")),
                (fixture.Root, Path.Combine(fixture.Root, "test-skill", "AGENTS.md")),
                (new string('x', RawSkillCandidateReader.MaximumPathLength + 1), candidate),
                (fixture.Root, new string('x', RawSkillCandidateReader.MaximumPathLength + 1)),
                (OperatingSystem.IsWindows() ? @"\\?\C:\Windows" : "//server/share", candidate),
            })
            {
                var rejected = await SkillCandidateMetadataReader.ReadAsync(root, path);
                Assert.AreEqual(SkillCandidateMetadataStatus.InvalidPath, rejected.Status, $"{root} | {path}");
                Assert.AreEqual(0, rejected.BytesRead);
                Assert.IsNull(rejected.Frontmatter);
            }
            Assert.AreEqual(SkillCandidateMetadataStatus.Missing,
                (await SkillCandidateMetadataReader.ReadAsync(fixture.Root, Path.Combine(fixture.Root, "missing", "SKILL.md"))).Status);
            var notDirectory = Path.Combine(fixture.Root, "file");
            await File.WriteAllTextAsync(notDirectory, "data");
            Assert.AreEqual(SkillCandidateMetadataStatus.NotDirectory,
                (await SkillCandidateMetadataReader.ReadAsync(fixture.Root, Path.Combine(notDirectory, "SKILL.md"))).Status);
            var namedDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "folder", "SKILL.md")).FullName;
            Assert.AreEqual(SkillCandidateMetadataStatus.NotDirectory,
                (await SkillCandidateMetadataReader.ReadAsync(fixture.Root, namedDirectory)).Status);
            try
            {
                var linkedParent = Path.Combine(fixture.Root, "linked");
                Directory.CreateSymbolicLink(linkedParent, Path.GetDirectoryName(candidate)!);
                Assert.AreEqual(SkillCandidateMetadataStatus.Linked,
                    (await SkillCandidateMetadataReader.ReadAsync(fixture.Root, Path.Combine(linkedParent, "SKILL.md"))).Status);
                var linkedFile = Path.Combine(fixture.Root, "shortcut", "SKILL.md");
                Directory.CreateDirectory(Path.GetDirectoryName(linkedFile)!);
                File.CreateSymbolicLink(linkedFile, candidate);
                Assert.AreEqual(SkillCandidateMetadataStatus.Linked,
                    (await SkillCandidateMetadataReader.ReadAsync(fixture.Root, linkedFile)).Status);
                var linkedRoot = Path.Combine(fixture.Root, "root-link");
                Directory.CreateSymbolicLink(linkedRoot, Path.GetDirectoryName(candidate)!);
                Assert.AreEqual(SkillCandidateMetadataStatus.Linked,
                    (await SkillCandidateMetadataReader.ReadAsync(linkedRoot, Path.Combine(linkedRoot, "SKILL.md"))).Status);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            { /* Symlink creation requires host support; all non-link cases above remain authoritative. */ }
        }
        finally { Directory.Delete(foreign); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-candidate-skill-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
