using System.Text;
using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SkillIgnoreSourceReaderTests
{
    [TestMethod]
    public async Task ExplicitFileAndBaseRemainSeparateAndEmptyIsNotMissing()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "# comment\r\n/hidden/\r\n!hidden/keep\r\n");
        var parsed = await SkillIgnoreSourceReader.ReadAsync(fixture.Root, source, "unrelated/base");
        Assert.AreEqual(SkillIgnoreSourceReadStatus.Parsed, parsed.Status);
        Assert.AreEqual(SkillIgnoreSourceStatus.Parsed, parsed.ParserStatus);
        Assert.AreEqual(Encoding.UTF8.GetByteCount("# comment\r\n/hidden/\r\n!hidden/keep\r\n"), parsed.BytesRead);
        Assert.AreEqual(2, parsed.Rules.Count);
        Assert.AreEqual("unrelated/base", parsed.Rules[0].BaseDirectory);
        Assert.IsTrue(parsed.Rules[1].IsNegated);
        Assert.Throws<NotSupportedException>(() => ((IList<SkillIgnoreRule>)parsed.Rules)[0] = parsed.Rules[1]);

        await File.WriteAllBytesAsync(source, []);
        var empty = await SkillIgnoreSourceReader.ReadAsync(fixture.Root, source, "");
        Assert.AreEqual(SkillIgnoreSourceReadStatus.Parsed, empty.Status);
        Assert.AreEqual(0, empty.BytesRead);
        Assert.AreEqual(0, empty.Rules.Count);
        var missing = await SkillIgnoreSourceReader.ReadAsync(fixture.Root, Path.Combine(fixture.Root, "missing"), "");
        Assert.AreEqual(SkillIgnoreSourceReadStatus.Missing, missing.Status);
        Assert.IsNull(missing.ParserStatus);
        Assert.AreEqual(0, missing.BytesRead);
        Assert.AreEqual(0, missing.Rules.Count);

        await File.WriteAllBytesAsync(source, [0xFF, 0xFE, (byte)'x', 0x00]);
        var utf16 = await SkillIgnoreSourceReader.ReadAsync(fixture.Root, source, "");
        Assert.AreEqual(SkillIgnoreSourceReadStatus.Parsed, utf16.Status);
        Assert.AreEqual(4, utf16.BytesRead);
        Assert.AreEqual("x", utf16.Rules[0].Pattern);
    }

    [TestMethod]
    public async Task ExactByteCapRequiresEofAndSentinelRefusesBeforeDecode()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "custom-ignore");
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("#" + new string('a', 1022) + "\n", 32)));
        Assert.AreEqual(SkillIgnoreSourceParser.MaximumBytes, bytes.Length);
        await File.WriteAllBytesAsync(source, bytes);
        var exact = await SkillIgnoreSourceReader.ReadAsync(fixture.Root, source, "");
        Assert.AreEqual(SkillIgnoreSourceReadStatus.Parsed, exact.Status);
        Assert.AreEqual(bytes.Length, exact.BytesRead);
        Assert.AreEqual(0, exact.Rules.Count);

        bytes[^1] = 0xFF;
        await File.WriteAllBytesAsync(source, bytes);
        var invalid = await SkillIgnoreSourceReader.ReadAsync(fixture.Root, source, "");
        Assert.AreEqual(SkillIgnoreSourceReadStatus.ParserRefused, invalid.Status);
        Assert.AreEqual(SkillIgnoreSourceStatus.InvalidEncoding, invalid.ParserStatus);
        Assert.AreEqual(bytes.Length, invalid.BytesRead);
        Assert.AreEqual(0, invalid.Rules.Count);

        await File.WriteAllBytesAsync(source, bytes.Append((byte)0xFF).ToArray());
        var overflow = await SkillIgnoreSourceReader.ReadAsync(fixture.Root, source, "");
        Assert.AreEqual(SkillIgnoreSourceReadStatus.ByteLimit, overflow.Status);
        Assert.IsNull(overflow.ParserStatus);
        Assert.AreEqual(SkillIgnoreSourceParser.MaximumBytes + 1, overflow.BytesRead);
        Assert.AreEqual(0, overflow.Rules.Count);
    }

    [TestMethod]
    public async Task ParserRefusalsReturnCompleteBytesButNoPartialRules()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, ".gitignore");
        foreach (var (text, expected) in new[]
        {
            ("valid\n[unterminated", SkillIgnoreSourceStatus.InvalidRule),
            ("valid\n" + new string('a', SkillIgnoreSourceParser.MaximumLineLength + 1), SkillIgnoreSourceStatus.LineLengthLimit),
        })
        {
            await File.WriteAllTextAsync(source, text);
            var result = await SkillIgnoreSourceReader.ReadAsync(fixture.Root, source, "");
            Assert.AreEqual(SkillIgnoreSourceReadStatus.ParserRefused, result.Status);
            Assert.AreEqual(expected, result.ParserStatus);
            Assert.AreEqual(Encoding.UTF8.GetByteCount(text), result.BytesRead);
            Assert.AreEqual(0, result.Rules.Count);
        }
    }

    [TestMethod]
    public async Task PathsAndBaseAreValidatedBeforeAnyFileAccess()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "missing");
        foreach (var invalidBase in new string?[] { null, "a//b", "../other", new string('x', 256), "a/b/c/d/e/f/g" })
        {
            var result = await SkillIgnoreSourceReader.ReadAsync(fixture.Root, source, invalidBase);
            AssertRefused(result, SkillIgnoreSourceReadStatus.InvalidBase);
        }
        var rootAncestor = Path.Combine(fixture.Root, new string('x', RawSkillCandidateReader.MaximumNameLength + 1));
        var deep = Path.Combine(new[] { fixture.Root }.Concat(Enumerable.Repeat("child", RawSkillCandidateReader.MaximumDepth + 1)).ToArray());
        var tooManyAncestors = Path.Combine(new[] { fixture.Root }.Concat(Enumerable.Repeat("d", RawSkillCandidateReader.MaximumRootComponents)).ToArray());
        foreach (var (root, path) in new (string?, string?)[]
        {
            (null, source), (fixture.Root, null), ("relative", source), (fixture.Root, "relative"),
            (Path.GetPathRoot(fixture.Root), source), (fixture.Root + Path.DirectorySeparatorChar, source),
            (fixture.Root, Path.Combine(fixture.Root, "..", Path.GetFileName(fixture.Root), "missing")),
            (fixture.Root, Path.Combine(fixture.Root + "-sibling", "missing")),
            (fixture.Root, Path.Combine(deep, "missing")),
            (rootAncestor, Path.Combine(rootAncestor, "missing")),
            (tooManyAncestors, Path.Combine(tooManyAncestors, "missing")),
            (fixture.Root, Path.Combine(fixture.Root, new string('x', 256), "missing")),
            (new string('x', RawSkillCandidateReader.MaximumPathLength + 1), source),
            (fixture.Root, new string('x', RawSkillCandidateReader.MaximumPathLength + 1)),
            (OperatingSystem.IsWindows() ? @"\\?\C:\Windows" : "//server/share", source),
        })
        {
            AssertRefused(await SkillIgnoreSourceReader.ReadAsync(root, path, ""), SkillIgnoreSourceReadStatus.InvalidPath);
        }
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await SkillIgnoreSourceReader.ReadAsync(
            null, null, null, new CancellationToken(true)));
    }

    [TestMethod]
    public async Task DirectoryDepthAttributesAndLinksAreRefusedWithoutRules()
    {
        using var fixture = new Fixture();
        var allowed = Directory.CreateDirectory(Path.Combine(new[] { fixture.Root }
            .Concat(Enumerable.Repeat("child", RawSkillCandidateReader.MaximumDepth)).ToArray())).FullName;
        var source = Path.Combine(allowed, "exclude");
        await File.WriteAllTextAsync(source, "rule");
        Assert.AreEqual(SkillIgnoreSourceReadStatus.Parsed,
            (await SkillIgnoreSourceReader.ReadAsync(fixture.Root, source, "")).Status);
        var maxName = new string('a', RawSkillCandidateReader.MaximumNameLength);
        var maxComponent = Path.Combine(fixture.Root, maxName);
        await File.WriteAllTextAsync(maxComponent, "rule");
        Assert.AreEqual(SkillIgnoreSourceReadStatus.Parsed,
            (await SkillIgnoreSourceReader.ReadAsync(fixture.Root, maxComponent, "")).Status);

        var dataFile = Path.Combine(fixture.Root, "file");
        await File.WriteAllTextAsync(dataFile, "data");
        AssertRefused(await SkillIgnoreSourceReader.ReadAsync(fixture.Root, Path.Combine(dataFile, "exclude"), ""),
            SkillIgnoreSourceReadStatus.NotDirectory);
        var namedDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "exclude")).FullName;
        AssertRefused(await SkillIgnoreSourceReader.ReadAsync(fixture.Root, namedDirectory, ""),
            SkillIgnoreSourceReadStatus.NotDirectory);

        var link = Path.Combine(fixture.Root, "linked");
        try { Directory.CreateSymbolicLink(link, allowed); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { /* Host may forbid links. */ }
        if (Directory.Exists(link))
        {
            AssertRefused(await SkillIgnoreSourceReader.ReadAsync(fixture.Root, Path.Combine(link, "exclude"), ""),
                SkillIgnoreSourceReadStatus.Linked);
            AssertRefused(await SkillIgnoreSourceReader.ReadAsync(link, Path.Combine(link, "exclude"), ""),
                SkillIgnoreSourceReadStatus.Linked);
        }
        var fileLink = Path.Combine(fixture.Root, "file-link");
        try { File.CreateSymbolicLink(fileLink, source); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { /* Host may forbid links. */ }
        if (File.Exists(fileLink))
            AssertRefused(await SkillIgnoreSourceReader.ReadAsync(fixture.Root, fileLink, ""), SkillIgnoreSourceReadStatus.Linked);
    }

    private static void AssertRefused(SkillIgnoreSourceReadResult result, SkillIgnoreSourceReadStatus expected)
    {
        Assert.AreEqual(expected, result.Status);
        Assert.AreEqual(0, result.BytesRead);
        Assert.IsNull(result.ParserStatus);
        Assert.AreEqual(0, result.Rules.Count);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-ignore-source-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
