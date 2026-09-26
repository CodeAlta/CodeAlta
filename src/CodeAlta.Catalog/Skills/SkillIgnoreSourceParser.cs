using System.Text;
using XenoAtom.Glob.Ignore;

namespace CodeAlta.Catalog.Skills;

/// <summary>Local parse outcome for one explicitly supplied ignore source, not an effective visibility decision.</summary>
public enum SkillIgnoreSourceStatus
{
    /// <summary>Complete supplied bytes produced a bounded syntactic rule list (possibly empty).</summary>
    Parsed,
    /// <summary>The caller did not assert that the supplied bytes were complete; no parsing was attempted.</summary>
    Incomplete,
    /// <summary>The supplied relative base directory is not canonical or exceeds its path/component bounds.</summary>
    InvalidBase,
    /// <summary>The supplied original byte buffer exceeds its input cap.</summary>
    ByteLimit,
    /// <summary>More than the supported number of physical lines was supplied.</summary>
    LineLimit,
    /// <summary>A physical line exceeds its decoded UTF-16 length bound.</summary>
    LineLengthLimit,
    /// <summary>The bytes cannot be strictly decoded as supported text, or contain a NUL character.</summary>
    InvalidEncoding,
    /// <summary>A line is not supported by the existing Git-ignore parser.</summary>
    InvalidRule,
}

/// <summary>Immutable syntactic evidence for one rule, without a match or filesystem decision.</summary>
/// <param name="Pattern">Pattern after the Git-ignore parser removes leading negation and boundary markers.</param>
/// <param name="RawPattern">Trimmed original rule line, including syntax markers and escapes.</param>
/// <param name="BaseDirectory">Caller-supplied canonical relative source directory.</param>
/// <param name="LineNumber">Physical line number in the supplied complete text.</param>
/// <param name="IsNegated">Whether this rule has a leading unescaped negation marker.</param>
/// <param name="DirectoryOnly">Whether this rule has an unescaped trailing directory separator.</param>
/// <param name="BasenameOnly">Whether this rule applies to a basename rather than an anchored path.</param>
public sealed record SkillIgnoreRule(string Pattern, string RawPattern, string BaseDirectory, int LineNumber,
    bool IsNegated, bool DirectoryOnly, bool BasenameOnly);

/// <summary>Bounded local syntax result; every refusal has an empty rule list.</summary>
public sealed class SkillIgnoreSourceResult
{
    internal SkillIgnoreSourceResult(SkillIgnoreSourceStatus status, IReadOnlyList<SkillIgnoreRule> rules)
    {
        Status = status;
        Rules = rules;
    }

    /// <summary>Fixed parse/refusal code, never effective ignoredness or visibility.</summary>
    public SkillIgnoreSourceStatus Status { get; }

    /// <summary>Read-only rule snapshots in source order only on <see cref="SkillIgnoreSourceStatus.Parsed"/>.</summary>
    public IReadOnlyList<SkillIgnoreRule> Rules { get; }
}

/// <summary>Opt-in in-memory parsing of one explicitly supplied complete ignore-byte source; performs no I/O.</summary>
public static class SkillIgnoreSourceParser
{
    /// <summary>Maximum supplied original byte count; a future file reader must separately establish EOF with a sentinel.</summary>
    public const int MaximumBytes = 32 * 1024;
    /// <summary>Maximum physical lines, including blank and comment lines and any empty line after a final newline.</summary>
    public const int MaximumLines = 256;
    /// <summary>Maximum UTF-16 units per physical line, including a CR before LF.</summary>
    public const int MaximumLineLength = 1024;
    /// <summary>Maximum UTF-16 units in the canonical relative base directory.</summary>
    public const int MaximumBaseLength = RawSkillCandidateReader.MaximumPathLength;
    /// <summary>Maximum directory components in the canonical relative base directory.</summary>
    public const int MaximumBaseDepth = RawSkillCandidateReader.MaximumDepth;

    /// <summary>Parses only caller-supplied complete bytes with the existing XenoAtom Git-ignore syntax parser.</summary>
    /// <remarks>
    /// UTF-8 with/without BOM and BOM-bearing UTF-16/UTF-32 LE/BE are supported with strict decoding. A null base
    /// is invalid; an empty base means source rules relative to the caller's unspecified root. The base uses only
    /// canonical slash-separated nonempty components, not a filesystem path. The caller's completeness assertion
    /// is not EOF proof, absence evidence or file provenance. No source is opened, matched, discovered or trusted;
    /// source order, negation and scope are syntax only. No Git case mode, ancestor pruning, root/global/ignore
    /// acquisition, shadowing, enablement or effective visibility is established. Malformed input yields no rules.
    /// </remarks>
    /// <param name="bytes">Original bytes supplied by the caller, not read by this method.</param>
    /// <param name="baseDirectory">Canonical relative directory for this one source, or empty for its root.</param>
    /// <param name="isComplete">Explicit caller assertion that the supplied bytes are complete, not an EOF check.</param>
    /// <param name="cancellationToken">Checked before and between bounded work; cannot interrupt the synchronous parser.</param>
    /// <returns>Immutable ordered rule snapshots only on a parsed result, otherwise a fixed refusal without partial rules.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled before or between bounded work.</exception>
    public static SkillIgnoreSourceResult Parse(ReadOnlySpan<byte> bytes, string? baseDirectory, bool isComplete,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        static SkillIgnoreSourceResult Refused(SkillIgnoreSourceStatus status) => new(status, Array.Empty<SkillIgnoreRule>());
        if (!isComplete) return Refused(SkillIgnoreSourceStatus.Incomplete);
        if (!ValidBase(baseDirectory)) return Refused(SkillIgnoreSourceStatus.InvalidBase);
        if (bytes.Length > MaximumBytes) return Refused(SkillIgnoreSourceStatus.ByteLimit);

        string text;
        try { text = DecodeStrict(bytes); }
        catch (DecoderFallbackException) { return Refused(SkillIgnoreSourceStatus.InvalidEncoding); }
        if (text.Contains('\0')) return Refused(SkillIgnoreSourceStatus.InvalidEncoding);

        var lineStart = 0;
        var lineCount = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && text[index] != '\n') continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (++lineCount > MaximumLines) return Refused(SkillIgnoreSourceStatus.LineLimit);
            if (index - lineStart > MaximumLineLength) return Refused(SkillIgnoreSourceStatus.LineLengthLimit);
            lineStart = index + 1;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = IgnoreRuleSet.ParseGitIgnore(text, baseDirectory: baseDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            // At most one rule per physical line. The preflight bounds the parser's linear pattern/token work.
            if (parsed.Rules.Count > MaximumLines) return Refused(SkillIgnoreSourceStatus.InvalidRule);
            var rules = new SkillIgnoreRule[parsed.Rules.Count];
            for (var index = 0; index < rules.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rule = parsed.Rules[index];
                rules[index] = new SkillIgnoreRule(rule.PatternText, rule.RawPatternText, rule.BaseDirectory,
                    rule.LineNumber, rule.IsNegated, rule.DirectoryOnly, rule.BasenameOnly);
            }
            return new SkillIgnoreSourceResult(SkillIgnoreSourceStatus.Parsed, Array.AsReadOnly(rules));
        }
        catch (ArgumentException) { return Refused(SkillIgnoreSourceStatus.InvalidRule); }
    }

    private static bool ValidBase(string? baseDirectory)
    {
        if (baseDirectory is null || baseDirectory.Length > MaximumBaseLength) return false;
        if (baseDirectory.Length == 0) return true;
        if (baseDirectory.AsSpan().IndexOfAny('\\', ':', '\0') >= 0) return false;
        var components = baseDirectory.Split('/');
        if (components.Length > MaximumBaseDepth) return false;
        foreach (var component in components)
        {
            if (component.Length is < 1 or > RawSkillCandidateReader.MaximumNameLength ||
                component is "." or ".." || component.Any(char.IsControl)) return false;
        }
        return true;
    }

    private static string DecodeStrict(ReadOnlySpan<byte> bytes)
    {
        var (encoding, bom) = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => ((Encoding)new UTF8Encoding(true, true), 3),
            [0xFF, 0xFE, 0x00, 0x00, ..] => ((Encoding)new UTF32Encoding(false, true, true), 4),
            [0x00, 0x00, 0xFE, 0xFF, ..] => ((Encoding)new UTF32Encoding(true, true, true), 4),
            [0xFF, 0xFE, ..] => ((Encoding)new UnicodeEncoding(false, true, true), 2),
            [0xFE, 0xFF, ..] => ((Encoding)new UnicodeEncoding(true, true, true), 2),
            _ => ((Encoding)new UTF8Encoding(false, true), 0),
        };
        return encoding.GetString(bytes[bom..]);
    }
}
