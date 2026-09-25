using CodeAlta.Catalog.Skills;
using SharpYaml;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SkillMetadataParserTests
{
    [TestMethod]
    public void Parse_AcceptsLiteralSupportedForms_WithoutReadingOrInventingDirectoryIdentity()
    {
        const string text = "\uFEFF---\r\nname: über-tool\r\ndescription: >-\r\n  Useful Unicode\r\n  description\r\nlicense: MIT\r\ncompatibility: desktop\r\nallowed-tools: shell\r\nmetadata:\r\n  owner: 'team'\r\n  channel: stable\r\n---\r\n# Unparsed body\r\n";
        var result = SkillMetadataParser.Parse(text);
        Assert.AreEqual(SkillMetadataStatus.Parsed, result.Status);
        Assert.AreEqual(SkillMetadataDiagnostic.None, result.Diagnostic);
        Assert.IsNotNull(result.Frontmatter);
        Assert.AreEqual("über-tool", result.Frontmatter.Name);
        Assert.AreEqual("Useful Unicode description", result.Frontmatter.Description);
        Assert.AreEqual("MIT", result.Frontmatter.License);
        Assert.AreEqual("desktop", result.Frontmatter.Compatibility);
        Assert.AreEqual("shell", result.Frontmatter.AllowedTools);
        Assert.AreEqual("team", result.Frontmatter.Metadata["owner"]);
        Assert.AreEqual("stable", result.Frontmatter.Metadata["channel"]);
        var legacy = YamlSerializer.Deserialize<Dictionary<string, object?>>(text[4..text.IndexOf("---", 5, StringComparison.Ordinal)]);
        Assert.AreEqual(legacy!["name"], result.Frontmatter.Name);
        Assert.AreEqual(legacy["description"], result.Frontmatter.Description);
        Assert.AreEqual(SkillMetadataStatus.Parsed,
            SkillMetadataParser.Parse("---\nname: test-skill\ndescription: |\n  First\n  second\n---\nBody").Status);
        var quoted = SkillMetadataParser.Parse("---\nname: test-skill\ndescription: 'true: literal'\nlicense: null\nmetadata: {}\n---");
        Assert.AreEqual(SkillMetadataStatus.Parsed, quoted.Status);
        Assert.AreEqual("true: literal", quoted.Frontmatter!.Description);
        Assert.IsNull(quoted.Frontmatter.License);
    }

    [TestMethod]
    public void Parse_RefusesMissingInvalidAndUnsupported_WithoutPartialMetadata()
    {
        foreach (var (text, status, diagnostic) in new (string, SkillMetadataStatus, SkillMetadataDiagnostic)[]
        {
            ("name: test\n", SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.FrontmatterMissing),
            ("---\nname: test\n", SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.FrontmatterMissing),
            ("---\n---", SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.RequiredField),
            ("---\nname: test\n---\n", SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.RequiredField),
            ("---\nname: TEST\ndescription: ok\n---", SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.Field),
            ("---\nname: test\ndescription: true\n---", SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.Field),
            ("---\nname: test\ndescription: [a, b]\n---", SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.Structure),
            ("---\nname: test\ndescription: " + new string('[', 100) + "x" + new string(']', 100) + "\n---",
                SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.Structure),
            ("---\nname: test\ndescription: ok\nunknown: value\n---", SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.Field),
            ("---\nname: test\nname: test\ndescription: ok\n---", SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.DuplicateKey),
            ("---\nname: test\ndescription: ok\nmetadata: {owner: a, owner: b}\n---", SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.DuplicateKey),
            ("---\nname: &a test\ndescription: *a\n---", SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.YamlFeature),
            ("---\nname: test\ndescription: *missing\n---", SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.YamlFeature),
            ("---\nname: !!str test\ndescription: ok\n---", SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.YamlFeature),
            ("---\nname: test\ndescription: !thing [a]\n---", SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.YamlFeature),
            ("---\n? [name]\n: test\ndescription: ok\n---", SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.Structure),
            ("---\nname: test\ndescription: ok\n...\nname: other\n---", SkillMetadataStatus.Unsupported, SkillMetadataDiagnostic.YamlFeature),
            ("---\nname: \"unterminated\n---", SkillMetadataStatus.Invalid, SkillMetadataDiagnostic.Syntax),
        })
        {
            var result = SkillMetadataParser.Parse(text);
            Assert.AreEqual(status, result.Status, text);
            Assert.AreEqual(diagnostic, result.Diagnostic, text);
            Assert.IsNull(result.Frontmatter, text);
        }
    }

    [TestMethod]
    public void Parse_RefusesWorkAndOutputOverrun_WithoutTruncatingIntoValidMetadata()
    {
        const string valid = "---\nname: test\ndescription: ok\n---\n";
        foreach (var text in new[]
        {
            valid + new string('a', SkillMetadataParser.MaximumTextCharacters),
            "---\nname: test\ndescription: " + new string('a', SkillMetadataParser.MaximumFrontmatterCharacters) + "\n---",
            "---\nname: test\ndescription: " + new string('é', SkillMetadataParser.MaximumTextBytes) + "\n---",
            "---\nname: test\ndescription: " + new string('a', SkillMetadataParser.MaximumScalarCharacters + 1) + "\n---",
            "---\nname: test\ndescription: ok\nmetadata:\n" + string.Concat(Enumerable.Range(0, SkillMetadataParser.MaximumFields + 1)
                .Select(i => $"  x{i}: v\n")) + "---",
            "---\nname: test\ndescription: [" + string.Join(',', Enumerable.Repeat("a", SkillMetadataParser.MaximumTokens)) + "]\n---",
        })
        {
            var result = SkillMetadataParser.Parse(text);
            Assert.AreEqual(SkillMetadataStatus.TooLarge, result.Status, text[..Math.Min(80, text.Length)]);
            Assert.IsNull(result.Frontmatter);
            Assert.AreNotEqual(SkillMetadataDiagnostic.None, result.Diagnostic);
        }
        Assert.AreEqual(SkillMetadataStatus.TooLarge,
            SkillMetadataParser.Parse("---\nname: test\ndescription: ok\n---\n" + new string('é', SkillMetadataParser.MaximumTextCharacters)).Status);
        var header = "---\nname: test\ndescription: ok\n";
        var atFrontmatterLimit = header + "#" + new string('x', SkillMetadataParser.MaximumFrontmatterCharacters -
            "name: test\ndescription: ok\n".Length - 2) + "\n---";
        Assert.AreEqual(SkillMetadataStatus.Parsed, SkillMetadataParser.Parse(atFrontmatterLimit).Status);
        Assert.AreEqual(SkillMetadataDiagnostic.FrontmatterLimit,
            SkillMetadataParser.Parse(atFrontmatterLimit.Replace("\n---", "x\n---", StringComparison.Ordinal)).Diagnostic);
        var exactLength = valid + new string('a', SkillMetadataParser.MaximumTextCharacters - valid.Length);
        Assert.AreEqual(SkillMetadataStatus.Parsed, SkillMetadataParser.Parse(exactLength).Status);
        Assert.AreEqual(SkillMetadataDiagnostic.TextLimit, SkillMetadataParser.Parse(exactLength + "a").Diagnostic);
        var byteLimitOnly = valid + new string('é', SkillMetadataParser.MaximumTextBytes / 2);
        Assert.AreEqual(SkillMetadataDiagnostic.TextLimit, SkillMetadataParser.Parse(byteLimitOnly).Diagnostic);
        var output = SkillMetadataParser.Parse("---\nname: test\ndescription: ok\nmetadata:\n" +
            string.Concat(Enumerable.Range(0, 9).Select(i => $"  key{i}: '{new string('a', 500)}'\n")) + "---");
        Assert.AreEqual(SkillMetadataStatus.TooLarge, output.Status);
        Assert.AreEqual(SkillMetadataDiagnostic.OutputLimit, output.Diagnostic);
        Assert.IsNull(output.Frontmatter);
        var tokens = SkillMetadataParser.Parse("---\nname: test\ndescription: [" +
            string.Join(',', Enumerable.Repeat("a", SkillMetadataParser.MaximumTokens)) + "]\n---");
        Assert.AreEqual(SkillMetadataDiagnostic.WorkLimit, tokens.Diagnostic);
        Assert.Throws<OperationCanceledException>(() => SkillMetadataParser.Parse(valid, new CancellationToken(true)));
        Assert.Throws<ArgumentNullException>(() => SkillMetadataParser.Parse(null!));
    }
}
